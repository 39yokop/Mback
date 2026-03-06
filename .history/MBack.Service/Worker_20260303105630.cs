using System.IO;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Net.Mail;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Text;
using System.Diagnostics;
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

    // ランサムウェア対策
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

        // 起動時に一度だけ古いテキストログを掃除する（SQLite移行に伴う整理）
        MigrateOldLogs();

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            bool isMaintenance = IsMaintenanceTime(now);

            if (!_isCircuitBreakerTripped && !isMaintenance)
            {
                ProcessPendingBackups();
            }

            // 深夜0時：ログ（DB）と「ゴミ箱」のクリーンアップ、日報リセット
            if (now.Hour == 0 && _lastCleanupDate.Date != now.Date)
            {
                CleanupOldLogs(); // SQLite側の古いデータを消すように修正
                CleanupOldTrash();
                _dailySuccessCount = 0;
                _dailyErrorCount = 0;
                _lastCleanupDate = now;
            }

            // 深夜4時：NAS再マウント ＆ フルスキャン ＆ 事前スクリプト実行
            if (now.Hour == 4 && _lastFullScanDate.Date != now.Date)
            {
                MountAllNetworkDrives();
                RunFullScan();
                _lastFullScanDate = now;
            }

            // 朝8時：日報サマリーメール送信
            if (now.Hour == 8 && _settings.SendDailySummary && _lastReportDate.Date != now.Date)
            {
                SendDailySummary();
                _lastReportDate = now.Date;
            }

            await Task.Delay(10000, stoppingToken);
        }
    }

    // --- マウント・メンテナンス・日報関連 ---

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
        string msg = $"MBack 日報サマリー\n\n【昨日〜現在までの稼働状況】\nバックアップ成功: {_dailySuccessCount} 件\nエラー発生: {_dailyErrorCount} 件\n\n正常に稼働しています。";
        try {
            MailNotifier.SendMail(_settings.MailConfig, "【MBack】稼働サマリー（日報）", msg);
        } catch (Exception ex) {
            _logger.LogError($"Summary mail failed: {ex.Message}");
        }
    }

    // --- バックアップ・検知コアロジック ---

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

            string msg = $"【緊急警告】{RANSOMWARE_SECONDS}秒間に{_settings.RansomwareThreshold}件以上のファイル変更を検知しました。\n" +
                         "ランサムウェア感染、または大規模な誤操作の可能性があるため、MBackを緊急停止（サーキットブレーカー発動）しました。\n" +
                         "設定ツールからサービスを再起動するまでバックアップは再開されません。";
                         
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
            if (!string.IsNullOrWhiteSpace(pair.PreCommand) && File.Exists(pair.PreCommand))
            {
                try {
                    _logger.LogInformation($"Running PreCommand: {pair.PreCommand}");
                    var psi = new ProcessStartInfo(pair.PreCommand) { CreateNoWindow = true, UseShellExecute = false };
                    Process.Start(psi)?.WaitForExit(300000); 
                } catch (Exception ex) {
                    _logger.LogError($"PreCommand failed: {ex.Message}");
                }
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
                    QueueBackup(file, pair, true);
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

            if ((now - pending.FirstDetected).TotalHours >= 24)
            {
                if (_pendingBackups.TryRemove(sourcePath, out _))
                {
                    NotifyError(sourcePath, "ファイルが24時間以上ロックされているため、バックアップをスキップしました。");
                }
                continue;
            }

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

    private void QueueBackup(string sourcePath, BackupPair pair, bool isFullScan = false)
    {
        if (IsMaintenanceTime(DateTime.Now)) return; 
        if (IsExcluded(sourcePath)) return;
        if (Directory.Exists(sourcePath)) return;
        
        if (!isFullScan && CheckForRansomware()) return;

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
            
            Interlocked.Increment(ref _dailySuccessCount); 
            HistoryLogger.Log("Copy", sourcePath, new FileInfo(sourcePath).Length);
        } catch (IOException) {
            QueueBackup(sourcePath, pair); 
        } catch (Exception ex) {
            NotifyError(sourcePath, ex.Message);
        }
    }

    private void OnFileDeleted(string sourcePath, BackupPair pair)
    {
        if (IsMaintenanceTime(DateTime.Now)) return; 
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

    // ★修正：SQLiteデータベース内の古いデータを掃除するように変更
    private void CleanupOldLogs()
    {
        try {
            _logger.LogInformation("Cleaning up old database logs...");
            // SQLite内の指定日数より古いレコードを削除
            HistoryLogger.Cleanup(_settings.LogRetentionDays);
        } catch (Exception ex) {
            _logger.LogError($"Log cleanup failed: {ex.Message}");
        }
    }

    // ★追加：古いテキスト形式のログ（.jsonl）を掃除する（移行用）
    private void MigrateOldLogs()
    {
        try {
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MBack", "Reports");
            if (Directory.Exists(logDir)) {
                _logger.LogInformation("Migrating/Cleaning up old .jsonl log files...");
                foreach (var file in Directory.GetFiles(logDir, "report-*.jsonl")) {
                    File.Delete(file);
                }
                // フォルダ自体が空になったら削除
                if (Directory.GetFiles(logDir).Length == 0) Directory.Delete(logDir);
            }
        } catch { }
    }

    private void CleanupOldTrash()
    {
        try {
            var threshold = DateTime.Now.AddDays(-_settings.LogRetentionDays);
            foreach (var pair in _settings.BackupSettings) {
                string trashBase = Path.Combine(pair.Destination, "_TRASH_");
                if (!Directory.Exists(trashBase)) continue;
                
                DeleteOldFilesInDirectory(trashBase, threshold);
            }
        } catch (Exception ex) {
            _logger.LogError($"Trash cleanup failed: {ex.Message}");
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
                if (Directory.GetFiles(subDir).Length == 0 && Directory.GetDirectories(subDir).Length == 0) {
                    Directory.Delete(subDir);
                }
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
                        MaintenanceStart = s.MaintenanceStart ?? "00:00",
                        MaintenanceEnd = s.MaintenanceEnd ?? "00:00",
                        SendDailySummary = s.SendDailySummary,
                        MailConfig = s.MailConfig
                    };
                }
            } catch { }
        }
    }
}
// (以下、MailNotifierクラスなどは変更なしのため、既存のものをそのまま継承)