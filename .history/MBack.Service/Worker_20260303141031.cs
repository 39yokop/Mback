using System.IO;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Net.Mail;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Text;
using System.Diagnostics;
using System.Security.AccessControl; // ファイル所有者取得用
using System.Security.Principal;     // ユーザー識別用
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MBack.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly string _configDir;
    private readonly string _configPath;
    private AppSettings _settings = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, PendingBackup> _pendingBackups = new();
    
    private DateTime _lastCleanupDate = DateTime.MinValue;
    private DateTime _lastFullScanDate = DateTime.MinValue;
    private DateTime _lastErrorMailTime = DateTime.MinValue;
    private DateTime _lastReportDate = DateTime.MinValue;

    private int _dailySuccessCount = 0;
    private int _dailyErrorCount = 0;

    private const int MAX_FILE_HISTORY = 50;
    private const int MAX_TRASH_HISTORY = 10;

    // ランサムウェア対策（サーキットブレーカー）
    private bool _isCircuitBreakerTripped = false;
    private readonly ConcurrentQueue<DateTime> _eventTimes = new();
    private const int RANSOMWARE_SECONDS = 60;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
        _configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MBack");
        if (!Directory.Exists(_configDir)) Directory.CreateDirectory(_configDir);
        _configPath = Path.Combine(_configDir, "appsettings.json");

        string oldPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        if (!File.Exists(_configPath) && File.Exists(oldPath)) {
            try { File.Copy(oldPath, _configPath); } catch { }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadSettings();
        MountAllNetworkDrives();
        StartWatchers();
        MigrateOldLogs(); // 古いjsonlログの清掃

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            bool isMaintenance = IsMaintenanceTime(now);

            if (!_isCircuitBreakerTripped && !isMaintenance)
            {
                ProcessPendingBackups();
            }

            // 深夜0時：クリーンアップ
            if (now.Hour == 0 && _lastCleanupDate.Date != now.Date)
            {
                CleanupOldLogs();
                CleanupOldTrash();
                _dailySuccessCount = 0;
                _dailyErrorCount = 0;
                _lastCleanupDate = now;
            }

            // 深夜4時：NAS再接続 ＆ フルスキャン
            if (now.Hour == 4 && _lastFullScanDate.Date != now.Date)
            {
                MountAllNetworkDrives();
                RunFullScan();
                _lastFullScanDate = now;
            }

            // 朝8時：日報
            if (now.Hour == 8 && _settings.SendDailySummary && _lastReportDate.Date != now.Date)
            {
                SendDailySummary();
                _lastReportDate = now.Date;
            }

            await Task.Delay(10000, stoppingToken);
        }
    }

    // --- NASマウント・メンテナンス関連 ---

    private void MountAllNetworkDrives()
    {
        foreach (var pair in _settings.BackupSettings)
        {
            MountDrive(pair.Destination, pair.UserName, pair.Password);
            MountDrive(pair.Source, pair.UserName, pair.Password);
        }
    }

    private void MountDrive(string path, string user, string pass)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass)) return;
        if (!path.StartsWith(@"\\")) return;

        try {
            var psi = new ProcessStartInfo("net", $"use \"{path}\" \"{pass}\" /user:\"{user}\"") {
                CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi)?.WaitForExit(10000);
            _logger.LogInformation($"Mounted network drive: {path}");
        } catch (Exception ex) {
            _logger.LogError($"Failed to mount {path}: {ex.Message}");
        }
    }

    private bool IsMaintenanceTime(DateTime now)
    {
        if (string.IsNullOrWhiteSpace(_settings.MaintenanceStart) || string.IsNullOrWhiteSpace(_settings.MaintenanceEnd)) return false;
        if (_settings.MaintenanceStart == "00:00" && _settings.MaintenanceEnd == "00:00") return false;

        if (DateTime.TryParseExact(_settings.MaintenanceStart, "HH:mm", null, System.Globalization.DateTimeStyles.None, out var start) &&
            DateTime.TryParseExact(_settings.MaintenanceEnd, "HH:mm", null, System.Globalization.DateTimeStyles.None, out var end))
        {
            var time = now.TimeOfDay;
            var startTime = start.TimeOfDay;
            var endTime = end.TimeOfDay;

            if (startTime <= endTime) return time >= startTime && time <= endTime;
            else return time >= startTime || time <= endTime;
        }
        return false;
    }

    private void SendDailySummary()
    {
        if (_settings.MailConfig == null || !_settings.MailConfig.Enabled) return;
        string msg = $"MBack 日報サマリー\n\n【昨日〜現在までの稼働状況】\n成功: {_dailySuccessCount} 件\nエラー: {_dailyErrorCount} 件\n\n正常に稼働しています。";
        try {
            MailNotifier.SendMail(_settings.MailConfig, "【MBack】稼働サマリー（日報）", msg);
        } catch (Exception ex) {
            _logger.LogError($"Summary mail failed: {ex.Message}");
        }
    }

    // --- 検知・バックアップ・お掃除コアロジック ---

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
            string emergencyFile = Path.Combine(_configDir, "emergency.txt");
            if (!File.Exists(emergencyFile)) File.WriteAllText(emergencyFile, "TRIPPED");
            string msg = $"【緊急警告】短時間に大量の変更を検知したため停止しました。";
            NotifyError("SYSTEM_EMERGENCY", msg);
            return true;
        }
        return false;
    }

    private void RunFullScan()
    {
        foreach (var pair in _settings.BackupSettings)
        {
            // 事前スクリプト実行
            if (!string.IsNullOrWhiteSpace(pair.PreCommand) && File.Exists(pair.PreCommand))
            {
                try {
                    var psi = new ProcessStartInfo(pair.PreCommand) { CreateNoWindow = true, UseShellExecute = false };
                    Process.Start(psi)?.WaitForExit(300000); 
                } catch { }
            }
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
                    QueueBackup(file, pair, true); // フルスキャンは顔パス
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
        Interlocked.Increment(ref _dailyErrorCount); 
        HistoryLogger.Log("Error", sourcePath, 0, message, "System");
        if (_settings.MailConfig != null && _settings.MailConfig.Enabled)
        {
            if (sourcePath == "SYSTEM_EMERGENCY" || (DateTime.Now - _lastErrorMailTime).TotalMinutes > 60)
            {
                try {
                    MailNotifier.SendErrorMail(_settings.MailConfig, sourcePath, message);
                    _lastErrorMailTime = DateTime.Now;
                } catch { }
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
            if ((now - pending.FirstDetected).TotalHours >= 24) // 24時間ルール
            {
                if (_pendingBackups.TryRemove(sourcePath, out _))
                {
                    NotifyError(sourcePath, "ファイルが24時間ロックされているためスキップしました。");
                }
                continue;
            }
            bool isDatabase = sourcePath.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase) ||
                              sourcePath.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase);
            int quietSeconds = isDatabase ? 300 : 10;
            if ((now - pending.LastDetected).TotalSeconds >= quietSeconds)
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
            var watcher = new FileSystemWatcher(pair.Source) { IncludeSubdirectories = true };
            watcher.Changed += (s, e) => QueueBackup(e.FullPath, pair);
            watcher.Created += (s, e) => QueueBackup(e.FullPath, pair);
            watcher.Deleted += (s, e) => OnFileDeleted(e.FullPath, pair);
            watcher.Renamed += (s, e) => { OnFileDeleted(e.OldFullPath, pair); QueueBackup(e.FullPath, pair); };
            watcher.EnableRaisingEvents = true;
            _watchers[pair.Source] = watcher;
        }
    }

    private void QueueBackup(string sourcePath, BackupPair pair, bool isFullScan = false)
    {
        if (IsMaintenanceTime(DateTime.Now) || IsExcluded(sourcePath) || Directory.Exists(sourcePath)) return;
        if (!isFullScan && CheckForRansomware()) return;
        _pendingBackups.AddOrUpdate(sourcePath,
            path => new PendingBackup { Pair = pair, FirstDetected = DateTime.Now, LastDetected = DateTime.Now },
            (path, existing) => { existing.LastDetected = DateTime.Now; return existing; });
    }

    private void PerformBackup(string sourcePath, BackupPair pair)
    {
        try {
            if (!File.Exists(sourcePath)) return;

            // ★ファイル保存者（所有者）の取得ロジック
            string ownerName = "System";
            try {
                var fileSecurity = File.GetAccessControl(sourcePath);
                var owner = fileSecurity.GetOwner(typeof(NTAccount));
                ownerName = owner?.ToString() ?? "System";
            } catch { }

            string relativePath = Path.GetRelativePath(pair.Source, sourcePath);
            string destPath = Path.Combine(pair.Destination, relativePath);
            string? destDir = Path.GetDirectoryName(destPath);
            if (destDir != null && !Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

            if (File.Exists(destPath)) {
                RotateHistory(destPath, MAX_FILE_HISTORY);
                File.SetAttributes(destPath, FileAttributes.Normal);
            }
            File.Copy(sourcePath, destPath, true);
            
            Interlocked.Increment(ref _dailySuccessCount); 
            // 時刻・サイズ・ユーザーをDBに記録
            HistoryLogger.Log("Copy", sourcePath, new FileInfo(sourcePath).Length, "", ownerName);
        } catch (IOException) {
            QueueBackup(sourcePath, pair); 
        } catch (Exception ex) {
            NotifyError(sourcePath, ex.Message);
        }
    }

    private void OnFileDeleted(string sourcePath, BackupPair pair)
    {
        if (IsMaintenanceTime(DateTime.Now) || IsExcluded(sourcePath) || CheckForRansomware()) return;
        _pendingBackups.TryRemove(sourcePath, out _);
        try {
            string relativePath = Path.GetRelativePath(pair.Source, sourcePath);
            string trashPath = Path.Combine(pair.Destination, "_TRASH_", relativePath);
            string? trashDir = Path.GetDirectoryName(trashPath);
            if (trashDir != null && !Directory.Exists(trashDir)) Directory.CreateDirectory(trashDir);
            if (File.Exists(trashPath)) RotateHistory(trashPath, MAX_TRASH_HISTORY);

            string currentBackPath = Path.Combine(pair.Destination, relativePath);
            if (File.Exists(currentBackPath)) {
                File.SetAttributes(currentBackPath, FileAttributes.Normal);
                File.Move(currentBackPath, trashPath, true);
                HistoryLogger.Log("Delete", sourcePath, 0, "Moved to Trash", "System");
            }
        } catch { }
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

    private void CleanupOldLogs() => HistoryLogger.Cleanup(_settings.LogRetentionDays);

    private void MigrateOldLogs()
    {
        try {
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MBack", "Reports");
            if (Directory.Exists(logDir)) Directory.Delete(logDir, true);
        } catch { }
    }

    private void CleanupOldTrash()
    {
        var threshold = DateTime.Now.AddDays(-_settings.LogRetentionDays);
        foreach (var pair in _settings.BackupSettings) {
            string trashBase = Path.Combine(pair.Destination, "_TRASH_");
            if (Directory.Exists(trashBase)) DeleteOldFilesInDirectory(trashBase, threshold);
        }
    }

    private void DeleteOldFilesInDirectory(string dir, DateTime threshold)
    {
        try {
            foreach (var file in Directory.GetFiles(dir)) {
                if (File.GetLastWriteTime(file) < threshold) {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
            }
            foreach (var subDir in Directory.GetDirectories(dir)) {
                DeleteOldFilesInDirectory(subDir, threshold);
                if (Directory.GetFiles(subDir).Length == 0 && Directory.GetDirectories(subDir).Length == 0) Directory.Delete(subDir);
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
                        BackupSettings = s.BackupSettings, GlobalExclusions = s.GlobalExclusions,
                        LogRetentionDays = s.LogRetentionDays > 0 ? s.LogRetentionDays : 60,
                        RansomwareThreshold = s.RansomwareThreshold > 0 ? s.RansomwareThreshold : 2000,
                        MaintenanceStart = s.MaintenanceStart ?? "00:00", MaintenanceEnd = s.MaintenanceEnd ?? "00:00",
                        SendDailySummary = s.SendDailySummary, MailConfig = s.MailConfig
                    };
                }
            } catch { }
        }
    }
}

// --- 通信・定義クラス群（全ロジック復元） ---

public static class MailNotifier
{
    public static void SendMail(MailSettings config, string subject, string body)
    {
        if (!config.Enabled || string.IsNullOrEmpty(config.ToAddress)) return;

        // POP-before-SMTP 復元
        if (config.UsePopBeforeSmtp && !string.IsNullOrEmpty(config.PopServer)) DoPopAuth(config);
        
        using var client = new SmtpClient(config.SmtpServer, config.SmtpPort) { 
            EnableSsl = config.SmtpSsl,
            Credentials = new NetworkCredential(config.UserName, config.Password) 
        };
        using var mail = new MailMessage(string.IsNullOrWhiteSpace(config.FromAddress) ? config.ToAddress : config.FromAddress, config.ToAddress) {
            Subject = subject, Body = body
        };
        client.Send(mail);
    }

    public static void SendErrorMail(MailSettings config, string filePath, string errorMessage)
    {
        string subject = "【緊急警告】MBack バックアップエラー";
        string body = $"異常が発生しました。\n\n対象: {filePath}\n内容: {errorMessage}\n時刻: {DateTime.Now}";
        SendMail(config, subject, body);
    }

    private static void DoPopAuth(MailSettings config) {
        try {
            using var client = new TcpClient(config.PopServer, config.PopPort); 
            Stream stream = client.GetStream();
            if (config.PopSsl) { var ssl = new SslStream(stream); ssl.AuthenticateAsClient(config.PopServer); stream = ssl; }
            using var reader = new StreamReader(stream, Encoding.ASCII); 
            using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
            reader.ReadLine(); writer.WriteLine($"USER {config.UserName}"); reader.ReadLine();
            writer.WriteLine($"PASS {config.Password}"); reader.ReadLine(); 
            writer.WriteLine("QUIT"); reader.ReadLine();
        } catch { }
    }
}

public class PendingBackup { public BackupPair Pair { get; set; } = new(); public DateTime FirstDetected { get; set; } public DateTime LastDetected { get; set; } }
public class AppSettings { public List<BackupPair> BackupSettings { get; set; } = new(); public List<string> GlobalExclusions { get; set; } = new(); public int LogRetentionDays { get; set; } = 60; public int RansomwareThreshold { get; set; } = 2000; public string MaintenanceStart { get; set; } = "00:00"; public string MaintenanceEnd { get; set; } = "00:00"; public bool SendDailySummary { get; set; } = false; public MailSettings MailConfig { get; set; } = new(); }
public class AppSettingsRaw { public List<BackupPair> BackupSettings { get; set; } = new(); public List<string> GlobalExclusions { get; set; } = new(); public int LogRetentionDays { get; set; } = 60; public int RansomwareThreshold { get; set; } = 2000; public string MaintenanceStart { get; set; } = "00:00"; public string MaintenanceEnd { get; set; } = "00:00"; public bool SendDailySummary { get; set; } = false; public MailSettings MailConfig { get; set; } = new(); }
public class MailSettings { public bool Enabled { get; set; } = false; public string ToAddress { get; set; } = ""; public string FromAddress { get; set; } = ""; public string SmtpServer { get; set; } = ""; public int SmtpPort { get; set; } = 587; public bool SmtpSsl { get; set; } = true; public string UserName { get; set; } = ""; public string Password { get; set; } = ""; public bool UsePopBeforeSmtp { get; set; } = false; public string PopServer { get; set; } = ""; public int PopPort { get; set; } = 110; public bool PopSsl { get; set; } = false; }
public class BackupPair { public string Source { get; set; } = ""; public string Destination { get; set; } = ""; public string UserName { get; set; } = ""; public string Password { get; set; } = ""; public string PreCommand { get; set; } = ""; }