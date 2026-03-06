using System.IO;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Net.Mail;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MBack.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly string _configPath;
    private AppSettings _settings = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    
    private readonly ConcurrentDictionary<string, PendingBackup> _pendingBackups = new();
    
    private DateTime _lastCleanupDate = DateTime.MinValue;
    private DateTime _lastFullScanDate = DateTime.MinValue;
    private DateTime _lastErrorMailTime = DateTime.MinValue;

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

        while (!stoppingToken.IsCancellationRequested)
        {
            ProcessPendingBackups();

            var now = DateTime.Now;

            // 毎日深夜0時にログの掃除
            if (now.Hour == 0 && _lastCleanupDate.Date != now.Date)
            {
                CleanupOldLogs();
                _lastCleanupDate = now;
            }

            // ★毎日深夜1時に「定期フルスキャン」を実行（取りこぼし対策）
            if (now.Hour == 1 && _lastFullScanDate.Date != now.Date)
            {
                RunFullScan();
                _lastFullScanDate = now;
            }
            
            await Task.Delay(10000, stoppingToken);
        }
    }

    // --- 定期フルスキャン機能 ---
    private void RunFullScan()
    {
        _logger.LogInformation("Starting daily full scan...");
        foreach (var pair in _settings.BackupSettings)
        {
            if (!Directory.Exists(pair.Source)) continue;
            ScanDirectory(pair.Source, pair);
        }
    }

    private void ScanDirectory(string dir, BackupPair pair)
    {
        try {
            foreach (var file in Directory.GetFiles(dir)) {
                if (IsExcluded(file)) continue;

                string relativePath = Path.GetRelativePath(pair.Source, file);
                string destPath = Path.Combine(pair.Destination, relativePath);

                // バックアップ先に存在しない、または元ファイルの方が新しい場合はキューに入れる
                if (!File.Exists(destPath) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(destPath)) {
                    QueueBackup(file, pair);
                }
            }
            // サブフォルダも再帰的にチェック
            foreach (var subDir in Directory.GetDirectories(dir)) {
                if (IsExcluded(subDir)) continue;
                ScanDirectory(subDir, pair);
            }
        } catch { } // アクセス権エラーなどはスキップ
    }

    // --- エラー通知機能（メール送信） ---
    private void NotifyError(string sourcePath, string message)
    {
        HistoryLogger.Log("Error", sourcePath, 0, message);
        _logger.LogError($"Backup Error: {sourcePath} - {message}");

        // 1時間に1回だけメールを送る（スパム防止）
        if (_settings.MailConfig != null && _settings.MailConfig.Enabled)
        {
            if ((DateTime.Now - _lastErrorMailTime).TotalMinutes > 60)
            {
                try
                {
                    MailNotifier.SendErrorMail(_settings.MailConfig, sourcePath, message);
                    _lastErrorMailTime = DateTime.Now;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Mail send failed: {ex.Message}");
                }
            }
        }
    }

    private void ProcessPendingBackups()
    {
        var now = DateTime.Now;
        foreach (var kvp in _pendingBackups)
        {
            string sourcePath = kvp.Key;
            var pending = kvp.Value;

            bool isDatabase = sourcePath.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase) ||
                              sourcePath.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) ||
                              sourcePath.EndsWith(".laccdb", StringComparison.OrdinalIgnoreCase);

            int quietSeconds = isDatabase ? 300 : 10;
            int forceMinutes = isDatabase ? 30 : 15;

            if ((now - pending.LastDetected).TotalSeconds >= quietSeconds || 
                (now - pending.FirstDetected).TotalMinutes >= forceMinutes)
            {
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

            watcher.Changed += (s, e) => QueueBackup(e.FullPath, pair);
            watcher.Created += (s, e) => QueueBackup(e.FullPath, pair);
            watcher.Deleted += (s, e) => OnFileDeleted(e.FullPath, pair);
            watcher.Renamed += (s, e) => {
                OnFileDeleted(e.OldFullPath, pair);
                QueueBackup(e.FullPath, pair);
            };

            watcher.EnableRaisingEvents = true;
            _watchers[pair.Source] = watcher;
        }
    }

    private void QueueBackup(string sourcePath, BackupPair pair)
    {
        if (IsExcluded(sourcePath)) return;
        if (Directory.Exists(sourcePath)) return;

        _pendingBackups.AddOrUpdate(
            sourcePath,
            path => new PendingBackup { Pair = pair, FirstDetected = DateTime.Now, LastDetected = DateTime.Now },
            (path, existing) => { existing.LastDetected = DateTime.Now; return existing; }
        );
    }

    private void PerformBackup(string sourcePath, BackupPair pair)
    {
        try
        {
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
            // ロックされている場合はキューに戻して後でリトライ
            QueueBackup(sourcePath, pair);
        }
        catch (Exception ex)
        {
            // それ以外の深刻なエラーはメール通知判定へ
            NotifyError(sourcePath, ex.Message);
        }
    }

    private void OnFileDeleted(string sourcePath, BackupPair pair)
    {
        if (IsExcluded(sourcePath)) return;
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
            NotifyError(sourcePath, "削除/ゴミ箱移動エラー: " + ex.Message);
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
            if (File.Exists(baseFilePath)) File.Move(baseFilePath, $"{baseFilePath}.v1", true);
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
                        LogRetentionDays = s.LogRetentionDays,
                        MailConfig = s.MailConfig // ★メール設定を読み込み
                    };
                }
            }
            catch { }
        }
    }
}

// --- メール送信ロジック ---
public static class MailNotifier
{
    public static void SendErrorMail(MailSettings config, string filePath, string errorMessage)
    {
        if (!config.Enabled || string.IsNullOrEmpty(config.ToAddress)) return;

        // 1. POP before SMTP の処理（必要な場合のみ）
        if (config.UsePopBeforeSmtp && !string.IsNullOrEmpty(config.PopServer))
        {
            DoPopAuth(config);
        }

        // 2. SMTP メール送信
        using var client = new SmtpClient(config.SmtpServer, config.SmtpPort);
        client.EnableSsl = config.SmtpSsl;
        if (!string.IsNullOrEmpty(config.UserName))
        {
            client.Credentials = new NetworkCredential(config.UserName, config.Password);
        }

        using var mail = new MailMessage(config.FromAddress, config.ToAddress);
        mail.Subject = "【警告】MBack バックアップエラー";
        mail.Body = $"MBackサービスでバックアップエラーが発生しました。\n\n【対象ファイル】\n{filePath}\n\n【エラー内容】\n{errorMessage}\n\n【発生時刻】\n{DateTime.Now}";
        
        client.Send(mail);
    }

    // 生のTCP通信でPOP3認証を行うプロ向けの裏技
    private static void DoPopAuth(MailSettings config)
    {
        using var client = new TcpClient(config.PopServer, config.PopPort);
        Stream stream = client.GetStream();
        if (config.PopSsl)
        {
            var ssl = new SslStream(stream);
            ssl.AuthenticateAsClient(config.PopServer);
            stream = ssl;
        }
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

        reader.ReadLine(); // サーバー応答
        writer.WriteLine($"USER {config.UserName}");
        reader.ReadLine();
        writer.WriteLine($"PASS {config.Password}");
        reader.ReadLine();
        writer.WriteLine("QUIT");
        reader.ReadLine();
    }
}

// --- データ定義 ---
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
    public MailSettings MailConfig { get; set; } = new();
}

public class AppSettingsRaw
{
    public List<BackupPair> BackupSettings { get; set; } = new();
    public List<string> GlobalExclusions { get; set; } = new();
    public int LogRetentionDays { get; set; } = 60;
    public MailSettings MailConfig { get; set; } = new();
}

public class MailSettings
{
    public bool Enabled { get; set; } = false;
    public string ToAddress { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string SmtpServer { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpSsl { get; set; } = true;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UsePopBeforeSmtp { get; set; } = false;
    public string PopServer { get; set; } = "";
    public int PopPort { get; set; } = 110;
    public bool PopSsl { get; set; } = false;
}

public class BackupPair
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
}