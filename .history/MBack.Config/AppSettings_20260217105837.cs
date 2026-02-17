namespace MBack.Config;

public class AppSettings
{
    public List<BackupPair> BackupSettings { get; set; } = new();
    public List<string> GlobalExclusions { get; set; } = new();
    // ★追加: ログの保存期間 (デフォルト30日)
    public int LogRetentionDays { get; set; } = 30;
}

public class BackupPair
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
}