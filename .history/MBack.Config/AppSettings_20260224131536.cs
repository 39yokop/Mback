using System.Collections.Generic;

namespace MBack.Config;

// 設定ファイルのルート
public class AppSettingsRaw
{
    public List<BackupPair> BackupSettings { get; set; } = new();
    public List<string> GlobalExclusions { get; set; } = new();
    public int LogRetentionDays { get; set; } = 60;
    
    // ★追加: メール設定
    public MailSettings MailConfig { get; set; } = new(); 
}

// メール通知用の詳細設定
public class MailSettings
{
    public bool Enabled { get; set; } = false;
    public string ToAddress { get; set; } = "";
    public string FromAddress { get; set; } = "";
    
    // SMTP設定
    public string SmtpServer { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpSsl { get; set; } = true;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    
    // POP before SMTP 設定
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
public class AppSettingsRaw
{
    public List<BackupPair> BackupSettings { get; set; } = new();
    public List<string> GlobalExclusions { get; set; } = new();
    public int LogRetentionDays { get; set; } = 60;
    
    public int RansomwareThreshold { get; set; } = 2000; // ★これを必ず追加！
    
    public MailSettings MailConfig { get; set; } = new();
}