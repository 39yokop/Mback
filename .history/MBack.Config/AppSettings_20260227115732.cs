using System.Collections.Generic;

namespace MBack.Config;

// 設定ファイルのルート（全設定の入れ物）
public class AppSettingsRaw
{
    // 基本設定
    public List<BackupPair> BackupSettings { get; set; } = new();
    public List<string> GlobalExclusions { get; set; } = new();
    
    // 詳細設定
    public int LogRetentionDays { get; set; } = 60;
    public int RansomwareThreshold { get; set; } = 2000;
    
    // ★新規追加: メンテナンスモード（監視停止時間帯）
    public string MaintenanceStart { get; set; } = "00:00";
    public string MaintenanceEnd { get; set; } = "00:00";
    
    // ★新規追加: 日報（サマリー）メール送信フラグ
    public bool SendDailySummary { get; set; } = false;
    
    // メール設定
    public MailSettings MailConfig { get; set; } = new(); 
}

// バックアップ元のペア設定
public class BackupPair
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    
    // ★新規追加: NAS等ネットワークドライブ自動マウント用の認証情報
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    
    // ★新規追加: バックアップ直前に実行するスクリプト（バッチ等）のパス
    public string PreCommand { get; set; } = "";
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