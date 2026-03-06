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

    // ランサムウェア対策（サーキットブレーカー）の設定
    private bool _isCircuitBreakerTripped = false;
    private readonly ConcurrentQueue<DateTime> _eventTimes = new();
    private const int RANSOMWARE_SECONDS = 60;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
        string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MBack");
        if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
        _configPath = Path.Combine(configDir, "appsettings.json");

        // 旧バージョンからの設定データ引き継ぎ
        string oldPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        if (!File.Exists(_configPath) && File.Exists(oldPath))
        {
            try { File.Copy(oldPath, _configPath); } catch { }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadSettings();
        StartWatchers();

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_isCircuitBreakerTripped)
            {
                ProcessPendingBackups();

                var now = DateTime.Now;
                if (now.Hour == 0 && _lastCleanupDate.Date != now.Date)
                {
                    CleanupOldLogs();
                    _lastCleanupDate = now;
                }
                if (now.Hour == 1 && _lastFullScanDate.Date != now.Date)
                {
                    RunFullScan();
                    _lastFullScanDate = now;
                }
            }
            await Task.Delay(10000, stoppingToken);
        }
    }

    private bool CheckForRansomware()
    {
        if (_isCircuitBreakerTripped) return true;

        var now = DateTime.Now;
        _eventTimes.Enqueue(now);

        while (_eventTimes.TryPeek(out DateTime oldest) && (now - oldest).TotalSeconds > RANSOMWARE_SECONDS)
        {
            _eventTimes.TryDequeue(out _);
        }

        if (_eventTimes.Count >= _settings.RansomwareThreshold)
        {
            _isCircuitBreakerTripped = true;
            _pendingBackups.Clear(); 
            
            string msg = $"【緊急警告】{RANSOMWARE_SECONDS}秒間に{_settings.RansomwareThreshold}件以上のファイル変更を検知しました。\n" +
                         "ランサムウェア感染、または大規模な誤操作の可能性があるため、" +
                         "MBackのバックアップ処理を緊急停止（サーキットブレーカー発動）しました。\n" +
                         "安全が確認されるまでバックアップは再開されません。(サービスの再起動で復旧します)";
                         
            NotifyError("SYSTEM_EMERGENCY", msg);
            _logger.LogCritical(msg);
            return true;
        }
        return false;
    }

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
                if (!File.Exists(destPath) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(destPath)) {
                    QueueBackup(file, pair);
                }
            }
            foreach (var subDir in Directory.GetDirectories(dir)) {
                if (IsExcluded(subDir)) continue;
                ScanDirectory(subDir, pair);
            }
        } catch { }
    }

    private void NotifyError(string sourcePath, string message)
    {
        HistoryLogger.Log("Error", sourcePath, 0, message);
        _logger.LogError($"Backup Error: {sourcePath} - {message}");

        if (_settings.MailConfig != null && _settings.MailConfig.Enabled)
        {
            bool isEmergency = sourcePath == "SYSTEM_EMERGENCY";
            if (isEmergency || (DateTime.Now - _lastErrorMailTime).TotalMinutes > 60)
            {
                try {
                    MailNotifier.SendErrorMail(_settings.MailConfig, sourcePath, message);
                    _lastErrorMailTime = DateTime.Now;
                } catch (Exception ex) {
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
        
        if (CheckForRansomware()) return;

        _pendingBackups.AddOrUpdate(
            sourcePath,
            path => new PendingBackup { Pair = pair, FirstDetected = DateTime.Now, LastDetected = DateTime.Now },
            (path, existing) => { existing.LastDetected = DateTime.Now; return existing; }
        );
    }

    private void PerformBackup(string sourcePath, BackupPair pair)
    {
        try {
            if (!File.Exists(sourcePath)) return;

            string relativePath = Path.GetRelativePath(pair.Source, sourcePath);
            string destPath = Path.Combine(pair.Destination, relativePath);
            string? destDir = Path.GetDirectoryName(destPath);

            if (destDir != null && !Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

            if (File.Exists(destPath)) {
                RotateHistory(destPath, MAX_FILE_HISTORY);
                File.SetAttributes(destPath, FileAttributes.Normal);
            }
            File.Copy(sourcePath, destPath, true);
            HistoryLogger.Log("Copy", sourcePath, new FileInfo(sourcePath).Length);
        } catch (IOException) {
            QueueBackup(sourcePath, pair);
        } catch (Exception ex) {
            NotifyError(sourcePath, ex.Message);
        }
    }

    private void OnFileDeleted(string sourcePath, BackupPair pair)
    {
        if (IsExcluded(sourcePath)) return;
        
        if (CheckForRansomware()) return;

        _pendingBackups.TryRemove(sourcePath, out _);

        try {
            string relativePath = Path.GetRelativePath(pair.Source, sourcePath);
            string trashBase = Path.Combine(pair.Destination, "_TRASH_");
            string trashPath = Path.Combine(trashBase, relativePath);
            
            string? trashDir = Path.GetDirectoryName(trashPath);
            if (trashDir != null && !Directory.Exists(trashDir)) Directory.CreateDirectory(trashDir);

            if (File.Exists(trashPath)) RotateHistory(trashPath, MAX_TRASH_HISTORY);

            string currentBackPath = Path.Combine(pair.Destination, relativePath);
            if (File.Exists(currentBackPath)) {
                File.SetAttributes(currentBackPath, FileAttributes.Normal);
                File.Move(currentBackPath, trashPath, true);
                HistoryLogger.Log("Delete", sourcePath, 0, "Moved to Trash");
            }
        } catch (Exception ex) {
            NotifyError(sourcePath, "削除/ゴミ箱移動エラー: " + ex.Message);
        }
    }

    private void RotateHistory(string baseFilePath, int maxHistory)
    {
        try {
            string oldestPath = $"{baseFilePath}.v{maxHistory}";
            if (File.Exists(oldestPath)) File.Delete(oldestPath);

            for (int i = maxHistory - 1; i >= 1; i--) {
                string oldVer = $"{baseFilePath}.v{i}";
                string newVer = $"{baseFilePath}.v{i + 1}";
                if (File.Exists(oldVer)) File.Move(oldVer, newVer, true);
            }
            if (File.Exists(baseFilePath)) File.Move(baseFilePath, $"{baseFilePath}.v1", true);
        } catch { }
    }

    private void CleanupOldLogs()
    {
        try {
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MBack", "Reports");
            if (!Directory.Exists(logDir)) return;
            var threshold = DateTime.Now.AddDays(-_settings.LogRetentionDays);
            foreach (var file in Directory.GetFiles(logDir, "report-*.jsonl")) {
                if (File.GetCreationTime(file) < threshold) File.Delete(file);
            }
        } catch { }
    }

    private bool IsExcluded(string path) => _settings.GlobalExclusions.Any(ex => path.Contains(ex, StringComparison.OrdinalIgnoreCase));

    private void LoadSettings()
    {
        if (File.Exists(_configPath)) {
            try {
                var json = File.ReadAllText(_configPath);
                var s = JsonSerializer.Deserialize<AppSettingsRaw>(json);
                if (s != null) {
                    _settings = new AppSettings { 
                        BackupSettings = s.BackupSettings, 
                        GlobalExclusions = s.GlobalExclusions,
                        LogRetentionDays = s.LogRetentionDays > 0 ? s.LogRetentionDays : 60,
                        RansomwareThreshold = s.RansomwareThreshold > 0 ? s.RansomwareThreshold : 2000,
                        MailConfig = s.MailConfig
                    };
                }
            } catch { }
        }
    }
}

// --- 以下、通信クラスとデータ定義 ---
public static class MailNotifier
{
    public static void SendErrorMail(MailSettings config, string filePath, string errorMessage)
    {
        if (!config.Enabled || string.IsNullOrEmpty(config.ToAddress)) return;

        // ★ TLS1.2 / TLS1.3 を強制
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

        if (config.UsePopBeforeSmtp && !string.IsNullOrEmpty(config.PopServer)) DoPopAuth(config);
        
        using var client = new SmtpClient(config.SmtpServer, config.SmtpPort);
        client.EnableSsl = config.SmtpSsl;
        if (!string.IsNullOrEmpty(config.UserName)) client.Credentials = new NetworkCredential(config.UserName, config.Password);
        
        using var mail = new MailMessage(string.IsNullOrWhiteSpace(config.FromAddress) ? config.ToAddress : config.FromAddress, config.ToAddress);
        mail.Subject = "【緊急警告】MBack バックアップエラー";
        mail.Body = $"MBackサービスで異常が発生しました。\n\n【対象】\n{filePath}\n\n【内容】\n{errorMessage}\n\n【時刻】\n{DateTime.Now}";
        
        client.Send(mail);
    }

    private static void DoPopAuth(MailSettings config) {
        using var client = new TcpClient(config.PopServer, config.PopPort); Stream stream = client.GetStream();
        if (config.PopSsl) { var ssl = new SslStream(stream); ssl.AuthenticateAsClient(config.PopServer); stream = ssl; }
        using var reader = new StreamReader(stream, Encoding.ASCII); using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
        reader.ReadLine(); writer.WriteLine($"USER {config.UserName}"); reader.ReadLine();
        writer.WriteLine($"PASS {config.Password}"); reader.ReadLine(); writer.WriteLine("QUIT"); reader.ReadLine();
    }
}

public class PendingBackup { public BackupPair Pair { get; set; } = new(); public DateTime FirstDetected { get; set; } public DateTime LastDetected { get; set; } }

public class AppSettings { public List<BackupPair> BackupSettings { get; set; } = new(); public List<string> GlobalExclusions { get; set; } = new(); public int LogRetentionDays { get; set; } = 60; public int RansomwareThreshold { get; set; } = 2000; public MailSettings MailConfig { get; set; } = new(); }
public class AppSettingsRaw { public List<BackupPair> BackupSettings { get; set; } = new(); public List<string> GlobalExclusions { get; set; } = new(); public int LogRetentionDays { get; set; } = 60; public int RansomwareThreshold { get; set; } = 2000; public MailSettings MailConfig { get; set; } = new(); }

public class MailSettings { public bool Enabled { get; set; } = false; public string ToAddress { get; set; } = ""; public string FromAddress { get; set; } = ""; public string SmtpServer { get; set; } = ""; public int SmtpPort { get; set; } = 587; public bool SmtpSsl { get; set; } = true; public string UserName { get; set; } = ""; public string Password { get; set; } = ""; public bool UsePopBeforeSmtp { get; set; } = false; public string PopServer { get; set; } = ""; public int PopPort { get; set; } = 110; public bool PopSsl { get; set; } = false; }
public class BackupPair { public string Source { get; set; } = ""; public string Destination { get; set; } = ""; }