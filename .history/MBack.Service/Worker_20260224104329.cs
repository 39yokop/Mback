using System.IO;
using System.Text.Json;
using System.Collections.Concurrent; // ★追加: キュー管理用
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MBack.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly string _configPath;
    private AppSettings _settings = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    
    // バックアップ待機用のキュー
    private readonly ConcurrentDictionary<string, PendingBackup> _pendingBackups = new();
    private DateTime _lastCleanupDate = DateTime.MinValue;

    // 世代管理の設定
    private const int MAX_FILE_HISTORY = 50;
    private const int MAX_TRASH_HISTORY = 10;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
        _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadSettings();
        StartWatchers();

        // メインループ：10秒ごとにキューをチェックする
        while (!stoppingToken.IsCancellationRequested)
        {
            ProcessPendingBackups();

            // 毎日深夜0時台に1回だけログのクリーンアップを実行
            if (DateTime.Now.Hour == 0 && _lastCleanupDate.Date != DateTime.Now.Date)
            {
                CleanupOldLogs();
                _lastCleanupDate = DateTime.Now;
            }
            
            await Task.Delay(10000, stoppingToken); // 10秒待機
        }
    }

    // --- 待機キューの処理（★今回のメイン機能） ---
    private void ProcessPendingBackups()
    {
        var now = DateTime.Now;

        foreach (var kvp in _pendingBackups)
        {
            string sourcePath = kvp.Key;
            var pending = kvp.Value;

            // ファイル拡張子ごとの特別ルールを設定
            bool isDatabase = sourcePath.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase) ||
                              sourcePath.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase);

            // Accessファイルは「5分放置」または「30分経過」でバックアップ
            // 通常ファイルは「10秒放置」または「15分経過」でバックアップ
            int quietSeconds = isDatabase ? 300 : 10;
            int forceMinutes = isDatabase ? 30 : 15;

            bool isQuiet = (now - pending.LastDetected).TotalSeconds >= quietSeconds;
            bool isMaxDelayed = (now - pending.FirstDetected).TotalMinutes >= forceMinutes;

            // 条件を満たしたらバックアップ実行！
            if (isQuiet || isMaxDelayed)
            {
                // キューから取り出してバックアップ処理へ
                if (_pendingBackups.TryRemove(sourcePath, out _))
                {
                    PerformBackup(sourcePath, pending.Pair);
                }
            }
        }
    }

    private void StartWatchers()
    {
        foreach (var pair in _settings.BackupSettings)
        {
            if (!Directory.Exists(pair.Source)) continue;

            var watcher = new FileSystemWatcher(pair.Source)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName
            };

            // イベント発生時は即座にコピーせず、キューに登録するだけ
            watcher.Changed += (s, e) => QueueBackup(e.FullPath, pair);
            watcher.Created += (s, e) => QueueBackup(e.FullPath, pair);
            
            watcher.Deleted += (s, e) => OnFileDeleted(e.FullPath, pair);
            watcher.Renamed += (s, e) => {
                OnFileDeleted(e.OldFullPath, pair);
                QueueBackup(e.FullPath, pair);
            };

            watcher.EnableRaisingEvents = true;
            _watchers[pair.Source] = watcher;
            _logger.LogInformation($"Monitoring started: {pair.Source}");
        }
    }

    // --- バックアップの予約 ---
    private void QueueBackup(string sourcePath, BackupPair pair)
    {
        if (IsExcluded(sourcePath)) return;
        if (Directory.Exists(sourcePath)) return;

        // キューに追加。既にキューにあれば「最後の変更時間」だけ更新する
        _pendingBackups.AddOrUpdate(
            sourcePath,
            path => new PendingBackup { Pair = pair, FirstDetected = DateTime.Now, LastDetected = DateTime.Now },
            (path, existing) => { existing.LastDetected = DateTime.Now; return existing; }
        );
    }

    // --- 実際のバックアップ処理（即時実行からここへ移動） ---
    private void PerformBackup(string sourcePath, BackupPair pair)
    {
        try
        {
            // バックアップ直前にファイルが消えていたり、アクセス拒否（ロック中）ならスキップ
            if (!File.Exists(sourcePath)) return;

            string relativePath = Path.GetRelativePath(pair.Source, sourcePath);
            string destPath = Path.Combine(pair.Destination, relativePath);
            string? destDir = Path.GetDirectoryName(destPath);

            if (destDir != null && !Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

            if (File.Exists(destPath))
            {
                RotateHistory(destPath, MAX_FILE_HISTORY);
                File.SetAttributes(destPath, FileAttributes.Normal);
            }
            
            File.Copy(sourcePath, destPath, true);
            HistoryLogger.Log("Copy", sourcePath, new FileInfo(sourcePath).Length);
        }
        catch (IOException)
        {
            // Accessファイルなどがロック（使用中）でコピーできない場合
            // 再びキューに戻して、後でもう一度リトライさせる
            QueueBackup(sourcePath, pair);
        }
        catch (Exception ex)
        {
            HistoryLogger.Log("Error", sourcePath, 0, ex.Message);
        }
    }

    private void OnFileDeleted(string sourcePath, BackupPair pair)
    {
        if (IsExcluded(sourcePath)) return;

        // もしバックアップ待機中だったファイルが削除されたら、キューから消す
        _pendingBackups.TryRemove(sourcePath, out _);

        try
        {
            string relativePath = Path.GetRelativePath(pair.Source, sourcePath);
            string trashBase = Path.Combine(pair.Destination, "_TRASH_");
            string trashPath = Path.Combine(trashBase, relativePath);
            
            string? trashDir = Path.GetDirectoryName(trashPath);
            if (trashDir != null && !Directory.Exists(trashDir)) Directory.CreateDirectory(trashDir);

            if (File.Exists(trashPath)) RotateHistory(trashPath, MAX_TRASH_HISTORY);

            string currentBackPath = Path.Combine(pair.Destination, relativePath);
            if (File.Exists(currentBackPath))
            {
                File.SetAttributes(currentBackPath, FileAttributes.Normal);
                File.Move(currentBackPath, trashPath, true);
                HistoryLogger.Log("Delete", sourcePath, 0, "Moved to Trash");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Delete error: {ex.Message}");
        }
    }

    private void RotateHistory(string baseFilePath, int maxHistory)
    {
        try 
        {
            string oldestPath = $"{baseFilePath}.v{maxHistory}";
            if (File.Exists(oldestPath)) File.Delete(oldestPath);

            for (int i = maxHistory - 1; i >= 1; i--)
            {
                string oldVer = $"{baseFilePath}.v{i}";
                string newVer = $"{baseFilePath}.v{i + 1}";
                if (File.Exists(oldVer)) File.Move(oldVer, newVer, true);
            }
            if (File.Exists(baseFilePath))
            {
                File.Move(baseFilePath, $"{baseFilePath}.v1", true);
            }
        }
        catch { }
    }

    private void CleanupOldLogs()
    {
        try
        {
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MBack", "Reports");
            if (!Directory.Exists(logDir)) return;

            var threshold = DateTime.Now.AddDays(-_settings.LogRetentionDays);
            foreach (var file in Directory.GetFiles(logDir, "report-*.jsonl"))
            {
                if (File.GetCreationTime(file) < threshold) File.Delete(file);
            }
        }
        catch { }
    }

    private bool IsExcluded(string path) => _settings.GlobalExclusions.Any(ex => path.Contains(ex, StringComparison.OrdinalIgnoreCase));

    private void LoadSettings()
    {
        if (File.Exists(_configPath))
        {
            try
            {
                var json = File.ReadAllText(_configPath);
                var s = JsonSerializer.Deserialize<AppSettingsRaw>(json);
                if (s != null)
                {
                    _settings = new AppSettings 
                    { 
                        BackupSettings = s.BackupSettings,
                        GlobalExclusions = s.GlobalExclusions,
                        LogRetentionDays = s.LogRetentionDays
                    };
                }
            }
            catch { }
        }
    }
}

// 待機中のバックアップ情報を保持するクラス
public class PendingBackup
{
    public BackupPair Pair { get; set; } = new();
    public DateTime FirstDetected { get; set; }
    public DateTime LastDetected { get; set; }
}

public class AppSettings
{
    public List<BackupPair> BackupSettings { get; set; } = new();
    public List<string> GlobalExclusions { get; set; } = new();
    public int LogRetentionDays { get; set; } = 60;
}

public class AppSettingsRaw
{
    public List<BackupPair> BackupSettings { get; set; } = new();
    public List<string> GlobalExclusions { get; set; } = new();
    public int LogRetentionDays { get; set; } = 60;
}

public class BackupPair
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
}