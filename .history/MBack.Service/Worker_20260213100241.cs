using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using System.IO.Enumeration; // これが重要
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MBack.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly BlockingCollection<FileSystemEventArgs> _eventQueue = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceTokens = new();

    private List<BackupPair> _backupPairs = new();
    private List<string> _globalExclusions = new();
    private readonly List<FileSystemWatcher> _activeWatchers = new();
    
    private FileSystemWatcher? _configWatcher;
    private FileSystemWatcher? _triggerWatcher;
    private readonly string _configPath;

    // 設定保存用クラス
    private class BackupPair
    {
        public required string Source { get; set; }
        public required string Destination { get; set; }
    }

    public Worker(ILogger<Worker> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        // 実行ファイルと同じ場所の appsettings.json を絶対パスで指定
        _configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ReloadAndRestartWatchers();
        StartConfigWatcher();
        StartTriggerWatcher(); // 手動実行ボタンの監視

        try
        {
            await ProcessEventQueueAsync(stoppingToken);
        }
        finally
        {
            DisposeWatchers();
        }
    }

    // --- 設定読み込みと初期化 ---
private void ReloadAndRestartWatchers()
    {
        _logger.LogInformation("設定を読み込み、監視体制を更新します...");
        
        DisposeWatchers();

        _globalExclusions = _configuration.GetSection("GlobalExclusions").Get<List<string>>() ?? new List<string>();
        var newPairs = _configuration.GetSection("BackupSettings").Get<BackupPair[]>();

        if (newPairs != null && newPairs.Length > 0)
        {
            _backupPairs = newPairs.ToList();
            _ = PerformInitialSyncAsync();
        }
        else
        {
            _backupPairs = new List<BackupPair>();
            _logger.LogWarning("バックアップ設定がまだありません。待機モードに入ります。");
            return;
        }

        // 監視のセットアップ
        foreach (var pair in _backupPairs)
        {
            // ★修正: ここに try-catch を入れて、1つのペアがダメでも他を動かす
            try
            {
                // ドライブ自体がない場合などに備える
                if (!Directory.Exists(pair.Source)) Directory.CreateDirectory(pair.Source);
                if (!Directory.Exists(pair.Destination)) Directory.CreateDirectory(pair.Destination);

                var watcher = new FileSystemWatcher(pair.Source)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 65536
                };

                watcher.Created += OnFileEvent;
                watcher.Changed += OnFileEvent;
                watcher.Renamed += OnFileEvent;
                watcher.Deleted += OnFileEvent;
                watcher.Error += (s, e) => _logger.LogError(e.GetException(), "監視エラー");

                watcher.EnableRaisingEvents = true;
                _activeWatchers.Add(watcher);
                _logger.LogInformation("監視開始: {src}", pair.Source);
            }
            catch (Exception ex)
            {
                // ★失敗してもログを出して、次のペアの設定に進む（アプリを落とさない）
                _logger.LogError("監視セットアップ失敗 (スキップします): {src} -> {dest}\n理由: {msg}", pair.Source, pair.Destination, ex.Message);
            }
        }
    }

    // --- 初期同期 (完全版: 削除・作成・コピー) ---
    private async Task PerformInitialSyncAsync()
    {
        _logger.LogInformation("--- 初期同期を開始 ---");
        var currentPairs = _backupPairs.ToList();
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 };

        foreach (var pair in currentPairs)
        {
            try
            {
                if (!Directory.Exists(pair.Source)) continue;
                string latestRoot = Path.Combine(pair.Destination, "Latest");
                if (!Directory.Exists(latestRoot)) Directory.CreateDirectory(latestRoot);

                // 1. フォルダ構成の同期
                foreach (var dirPath in Directory.GetDirectories(pair.Source, "*", SearchOption.AllDirectories))
                {
                    if (IsExcluded(dirPath)) continue;
                    string relative = Path.GetRelativePath(pair.Source, dirPath);
                    string destDir = Path.Combine(latestRoot, relative);
                    if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                }

                // 2. ファイルの同期（コピー）
                var sourceFiles = Directory.EnumerateFiles(pair.Source, "*", SearchOption.AllDirectories);
                await Parallel.ForEachAsync(sourceFiles, parallelOptions, async (filePath, token) =>
                {
                    if (IsExcluded(filePath)) return;
                    string relative = Path.GetRelativePath(pair.Source, filePath);
                    string mirrorPath = Path.Combine(latestRoot, relative);

                    bool needCopy = !File.Exists(mirrorPath);
                    if (!needCopy)
                    {
                        var si = new FileInfo(filePath);
                        var di = new FileInfo(mirrorPath);
                        if (si.Length != di.Length || si.LastWriteTime > di.LastWriteTime) needCopy = true;
                    }

                    if (needCopy)
                    {
                        string? d = Path.GetDirectoryName(mirrorPath);
                        if (d != null && !Directory.Exists(d)) Directory.CreateDirectory(d);
                        await CopyFileWithRetryAsync(filePath, mirrorPath);
                        _logger.LogInformation("[初期同期] コピー: {path}", relative);
                    }
                });

                // 3. 削除されたファイルの反映（ゴミ箱へ）
                var mirrorFiles = Directory.GetFiles(latestRoot, "*", SearchOption.AllDirectories);
                foreach (var mirrorPath in mirrorFiles)
                {
                    string relative = Path.GetRelativePath(latestRoot, mirrorPath);
                    string originalSource = Path.Combine(pair.Source, relative);

                    if (!File.Exists(originalSource))
                    {
                        MoveToTrash(pair.Destination, mirrorPath, relative);
                    }
                }
                DeleteEmptyDirectories(latestRoot);
            }
            catch (Exception ex)
            {
                _logger.LogError("初期同期失敗 ({src}): {msg}", pair.Source, ex.Message);
            }
        }
        _logger.LogInformation("--- 初期同期完了 ---");
    }

    // --- イベント処理とバックアップ実行 ---
    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (!_eventQueue.IsAddingCompleted) _eventQueue.TryAdd(e);
    }

    private async Task ProcessEventQueueAsync(CancellationToken token)
    {
        foreach (var e in _eventQueue.GetConsumingEnumerable(token))
        {
            HandleDebounce(e);
        }
    }

    private void HandleDebounce(FileSystemEventArgs e)
    {
        if (_debounceTokens.TryRemove(e.FullPath, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }

        var cts = new CancellationTokenSource();
        _debounceTokens[e.FullPath] = cts;

        Task.Delay(1000, cts.Token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
            {
                _debounceTokens.TryRemove(e.FullPath, out _);
                _ = PerformBackupAsync(e);
            }
        }, TaskScheduler.Default);
    }

    private async Task PerformBackupAsync(FileSystemEventArgs e)
    {
        var currentPairs = _backupPairs.ToList();
        var pair = currentPairs.FirstOrDefault(p => e.FullPath.StartsWith(p.Source, StringComparison.OrdinalIgnoreCase));
        if (pair == null || IsExcluded(e.FullPath)) return;

        try
        {
            string relative = Path.GetRelativePath(pair.Source, e.FullPath);
            string mirrorPath = Path.Combine(pair.Destination, "Latest", relative);

            if (e.ChangeType == WatcherChangeTypes.Deleted)
            {
                MoveToTrash(pair.Destination, mirrorPath, relative);
                return;
            }

            if (File.Exists(e.FullPath))
            {
                if (!await WaitForFileReadyAsync(e.FullPath)) return;
                string? d = Path.GetDirectoryName(mirrorPath);
                if (d != null) Directory.CreateDirectory(d);

                await CopyFileWithRetryAsync(e.FullPath, mirrorPath);

                // 履歴作成
                string histDir = Path.Combine(pair.Destination, "History", Path.GetDirectoryName(relative) ?? "");
                if (!Directory.Exists(histDir)) Directory.CreateDirectory(histDir);
                File.Copy(mirrorPath, Path.Combine(histDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(e.Name)}"), true);
                
                _logger.LogInformation("[リアルタイム同期] {file}", relative);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("バックアップ失敗: {msg}", ex.Message);
        }
    }

    // --- ヘルパーメソッド群 ---
    private void MoveToTrash(string destRoot, string fullPath, string relative)
    {
        try
        {
            string trashRoot = Path.Combine(destRoot, "Trash");
            if (!Directory.Exists(trashRoot)) Directory.CreateDirectory(trashRoot);
            string safeName = relative.Replace(Path.DirectorySeparatorChar, '_');
            string trashPath = Path.Combine(trashRoot, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeName}");

            if (File.Exists(fullPath))
            {
                File.Move(fullPath, trashPath);
                _logger.LogInformation("[ゴミ箱] 移動: {name}", safeName);
            }
        }
        catch {}
    }

    private bool IsExcluded(string path)
    {
        string name = Path.GetFileName(path);
        if (name.StartsWith("~$") || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var pat in _globalExclusions)
        {
            if (FileSystemName.MatchesSimpleExpression(pat, name, ignoreCase: true)) return true;
        }
        return false;
    }

    private async Task CopyFileWithRetryAsync(string src, string dst)
    {
        for (int i = 0; i < 3; i++)
        {
            try { File.Copy(src, dst, true); return; }
            catch (IOException) { await Task.Delay(500); }
        }
    }

    private async Task<bool> WaitForFileReadyAsync(string path)
    {
        for (int i = 0; i < 10; i++)
        {
            try { using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); if (fs.Length > 0) return true; }
            catch { await Task.Delay(500); }
        }
        return false;
    }

    private void DeleteEmptyDirectories(string path)
    {
        foreach (var d in Directory.GetDirectories(path))
        {
            DeleteEmptyDirectories(d);
            if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
        }
    }

    // --- 設定＆トリガー監視 ---
    private void StartConfigWatcher()
    {
        string? dir = Path.GetDirectoryName(_configPath);
        if (dir == null) return;
        _configWatcher = new FileSystemWatcher(dir, Path.GetFileName(_configPath)) { NotifyFilter = NotifyFilters.LastWrite, EnableRaisingEvents = true };
        _configWatcher.Changed += (s, e) => 
        {
            Task.Delay(500).ContinueWith(_ => { ((IConfigurationRoot)_configuration).Reload(); ReloadAndRestartWatchers(); });
        };
    }

    private void StartTriggerWatcher()
    {
        _triggerWatcher = new FileSystemWatcher(AppContext.BaseDirectory, "backup.trigger") 
        { 
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, 
            EnableRaisingEvents = true 
        };
        FileSystemEventHandler handler = (s, e) => 
        {
            _logger.LogInformation("★手動実行トリガー検知！");
            Task.Delay(1000).ContinueWith(_ => PerformInitialSyncAsync());
        };
        _triggerWatcher.Changed += handler;
        _triggerWatcher.Created += handler;
    }

    private void DisposeWatchers()
    {
        foreach (var w in _activeWatchers) w.Dispose();
        _activeWatchers.Clear();
    }
}