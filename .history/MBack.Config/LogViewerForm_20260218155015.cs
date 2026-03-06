using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MBack.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly string _configPath;
    private AppSettings _settings = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    
    // 世代管理の設定
    private const int MAX_FILE_HISTORY = 50;  // 通常ファイルの履歴数
    private const int MAX_TRASH_HISTORY = 10; // ゴミ箱の履歴数

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
        _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadSettings();
        StartWatchers();

        while (!stoppingToken.IsCancellationRequested)
        {
            // 毎日深夜0時にログのクリーンアップを実行
            if (DateTime.Now.Hour == 0 && DateTime.Now.Minute == 0)
            {
                CleanupOldLogs();
            }
            await Task.Delay(60000, stoppingToken); // 1分おきにチェック
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

            watcher.Changed += (s, e) => OnFileChanged(e.FullPath, pair);
            watcher.Created += (s, e) => OnFileChanged(e.FullPath, pair);
            watcher.Deleted += (s, e) => OnFileDeleted(e.FullPath, pair);
            watcher.Renamed += (s, e) => {
                OnFileDeleted(e.OldFullPath, pair);
                OnFileChanged(e.FullPath, pair);
            };

            watcher.EnableRaisingEvents = true;
            _watchers[pair.Source] = watcher;
            _logger.LogInformation($"Monitoring started: {pair.Source}");
        }
    }

    private void OnFileChanged(string sourcePath, BackupPair pair)
    {
        if (IsExcluded(sourcePath)) return;
        if (Directory.Exists(sourcePath)) return;

        try
        {
            string relativePath = Path.GetRelativePath(pair.Source, sourcePath);
            string destPath = Path.Combine(pair.Destination, relativePath);
            string? destDir = Path.GetDirectoryName(destPath);

            if (destDir != null && !Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

            // 1. 既存のバックアップがあるなら世代交代（.v1 -> .v2 ...）
            if (File.Exists(destPath))
            {
                RotateHistory(destPath, MAX_FILE_HISTORY);
            }

            // 2. 最新をコピー
            // 読み取り専用属性がついていると上書きエラーになることがあるので解除
            if (File.Exists(destPath))
            {
                File.SetAttributes(destPath, FileAttributes.Normal);
            }
            File.Copy(sourcePath, destPath, true);
            
            HistoryLogger.Log("Copy", sourcePath, new FileInfo(sourcePath).Length);
        }
        catch (Exception ex)
        {
            HistoryLogger.Log("Error", sourcePath, 0, ex.Message);
        }
    }

    private void OnFileDeleted(string sourcePath, BackupPair pair)
    {
        if (IsExcluded(sourcePath)) return;

        try
        {
            string relativePath = Path.GetRelativePath(pair.Source, sourcePath);
            // ゴミ箱用フォルダ "_TRASH_" を作成
            string trashBase = Path.Combine(pair.Destination, "_TRASH_");
            string trashPath = Path.Combine(trashBase, relativePath);
            
            string? trashDir = Path.GetDirectoryName(trashPath);
            if (trashDir != null && !Directory.Exists(trashDir)) Directory.CreateDirectory(trashDir);

            // ゴミ箱内での世代管理（10世代）
            if (File.Exists(trashPath))
            {
                RotateHistory(trashPath, MAX_TRASH_HISTORY);
            }

            // バックアップ先の現役ファイルをゴミ箱へ移動
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

    // 履歴ローテーション (.v49 -> .v50, ..., .v1 -> .v2, 原本 -> .v1)
    private void RotateHistory(string baseFilePath, int maxHistory)
    {
        try 
        {
            // 古い順にリネーム (.vMax -> 消去)
            string oldestPath = $"{baseFilePath}.v{maxHistory}";
            if (File.Exists(oldestPath)) File.Delete(oldestPath);

            for (int i = maxHistory - 1; i >= 1; i--)
            {
                string oldVer = $"{baseFilePath}.v{i}";
                string newVer = $"{baseFilePath}.v{i + 1}";
                if (File.Exists(oldVer)) File.Move(oldVer, newVer, true);
            }
            // 現在のファイルを .v1 に
            if (File.Exists(baseFilePath))
            {
                File.Move(baseFilePath, $"{baseFilePath}.v1", true);
            }
        }
        catch { /* ローテーション失敗は無視して進む */ }
    }

    private void CleanupOldLogs()
    {
        try
        {
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MBack", "Reports");
            if (!Directory.Exists(logDir)) return;

            // 設定日数を過ぎたログを削除
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
                var s = JsonSerializer.Deserialize<AppSettingsRaw>(json); // AppSettingsRawを使用
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

// サービス内で使うための簡易クラス定義
// (Config側のAppSettings.csと重複しないよう、ここでは内部クラス的に扱うか、
//  プロジェクトが分かれているのでそのままでOKです)
public class AppSettings
{
    public List<BackupPair> BackupSettings { get; set; } = new();
    public List<string> GlobalExclusions { get; set; } = new();
    public int LogRetentionDays { get; set; } = 60; // デフォルト60日
}

// Worker.csに必要な定義
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

public static class HistoryLogger
{
    private static object _lock = new object();
    public static void Log(string type, string path, long size, string msg = "")
    {
        lock (_lock)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MBack", "Reports");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                
                string file = Path.Combine(dir, $"report-{DateTime.Now:yyyyMMdd}.jsonl");
                var entry = new { Time = DateTime.Now, Type = type, Path = path, Size = size, Message = msg };
                File.AppendAllText(file, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
            catch { }
        }
    }
}