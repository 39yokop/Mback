namespace MBack.Config;

public class AppSettings
{
    public List<BackupPair> BackupSettings { get; set; } = new();
    public List<string> GlobalExclusions { get; set; } = new();
}

public class BackupPair
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
}