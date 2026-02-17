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
    private HistoryLogger? _history;
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
        // ★初期化と掃除
        int days = _configuration.GetValue<int>("LogRetentionDays");
        if (days == 0) days = 30; // 設定がない場合のデフォルト
        
        _history = new HistoryLogger(days);
        _history.CleanUpOldLogs(); // 起動時に古いログを消す

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
                _logger.LogError("監視セットアップ失敗 (スキップします): {src} -> {dest}\n理由: {msg}", pair.Source, pair.Destination, ex.Message);
            }
        }
    }

    // --- 初期同期 (完全版: 削除・作成・コピー) ---
    private async Task PerformInitialSyncAsync()
    {
        _logger.LogInformation("--- 初期同期を開始します (大規模対応モード) ---");
        
        // 設定の再読み込み
        var currentPairs = _backupPairs.ToList();

        // 並列処理の設定
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 8 };

        foreach (var pair in currentPairs)
        {
            try
            {
                if (!Directory.Exists(pair.Source))
                {
                    _logger.LogError("ソースが見つかりません: {path}", pair.Source);
                    continue;
                }

                string latestRoot = Path.Combine(pair.Destination, "Latest");
                if (!Directory.Exists(latestRoot)) Directory.CreateDirectory(latestRoot);

                _logger.LogInformation("同期開始: {src} -> {dest}", pair.Source, latestRoot);

                // 1. 【コピー & 更新フェーズ】
                var sourceFileEnum = Directory.EnumerateFiles(pair.Source, "*", SearchOption.AllDirectories);

                await Parallel.ForEachAsync(sourceFileEnum, parallelOptions, async (filePath, token) =>
                {
                    if (IsExcluded(filePath)) return;

                    try
                    {
                        string relativePath = Path.GetRelativePath(pair.Source, filePath);
                        string mirrorPath = Path.Combine(latestRoot, relativePath);

                        bool needCopy = !File.Exists(mirrorPath);
                        
                        // 整合性チェック
                        if (!needCopy)
                        {
                            var si = new FileInfo(filePath);
                            var di = new FileInfo(mirrorPath);
                            if (si.Length != di.Length || si.LastWriteTime > di.LastWriteTime.AddSeconds(2))
                            {
                                needCopy = true;
                            }
                        }

                        if (needCopy)
                        {
                            string? d = Path.GetDirectoryName(mirrorPath);
                            if (d != null && !Directory.Exists(d)) Directory.CreateDirectory(d);
                            
                            await CopyFileWithRetryAsync(filePath, mirrorPath);

                            // ★追加: コピー履歴ログ
                            var fi = new FileInfo(filePath);
                            _history?.Log("Copy", filePath, "", fi.Length);
                            
                            _logger.LogInformation("[更新] {path}", relativePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        // ★追加: エラーログ
                        _history?.Log("Error", filePath, ex.Message);
                        _logger.LogWarning("ファイル処理スキップ: {file} ({msg})", filePath, ex.Message);
                    }
                });

                // 2. 【削除フェーズ】
                _logger.LogInformation("不要ファイルの削除チェックを開始...");

                var mirrorFileEnum = Directory.EnumerateFiles(latestRoot, "*", SearchOption.AllDirectories);

                await Parallel.ForEachAsync(mirrorFileEnum, parallelOptions, async (mirrorPath, token) =>
                {
                    try 
                    {
                        string relativePath = Path.GetRelativePath(latestRoot, mirrorPath);
                        string originalSourcePath = Path.Combine(pair.Source, relativePath);

                        if (!File.Exists(originalSourcePath))
                        {
                            // 削除処理（ログ出力は MoveToTrash 内で行う）
                            MoveToTrash(pair.Destination, mirrorPath, relativePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("削除チェック失敗: {file} ({msg})", mirrorPath, ex.Message);
                    }
                });

                DeleteEmptyDirectories(latestRoot);

            }
            catch (Exception ex)
            {
                _history?.Log("Error", pair.Source, "初期同期全体エラー: " + ex.Message);
                _logger.LogError("初期同期エラー ({src}): {msg}", pair.Source, ex.Message);
            }
        }
        _logger.LogInformation("--- 初期同期が完了しました ---");
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

                // ★追加: リアルタイムコピー履歴ログ
                var fi = new FileInfo(e.FullPath);
                _history?.Log("Copy", e.FullPath, "", fi.Length);

                // 履歴作成 (簡易バージョニング)
                /* ※注: もしバージョニングが不要ならこのブロックは削除してもOKですが、
                   コード