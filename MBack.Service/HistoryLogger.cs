using System.Text.Json;

namespace MBack.Service;

// ログの1行ごとのデータ形式
public class HistoryEntry
{
    public DateTime Time { get; set; }
    public string Type { get; set; } = ""; // "Copy", "Delete", "Error"
    public string Path { get; set; } = "";
    public string Message { get; set; } = "";
    public long Size { get; set; }
}

public class HistoryLogger
{
    private readonly string _logDir;
    private readonly int _retentionDays;

    public HistoryLogger(int retentionDays)
    {
        _retentionDays = retentionDays;
        // ログ保存場所: AppData/Local/MBack/Reports
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MBack", "Reports");

        if (!Directory.Exists(_logDir)) Directory.CreateDirectory(_logDir);
    }

    // ログを書き込む (1行ずつのJSONL形式)
    public void Log(string type, string path, string message = "", long size = 0)
    {
        try
        {
            var entry = new HistoryEntry
            {
                Time = DateTime.Now,
                Type = type,
                Path = path,
                Message = message,
                Size = size
            };

            string json = JsonSerializer.Serialize(entry);
            string fileName = $"report-{DateTime.Now:yyyyMMdd}.jsonl";
            string fullPath = Path.Combine(_logDir, fileName);

            // 追記モードで書き込み
            File.AppendAllText(fullPath, json + Environment.NewLine);
        }
        catch { /* ログ書き込みエラーは無視 */ }
    }

    // 古いログを削除する
    public void CleanUpOldLogs()
    {
        try
        {
            if (_retentionDays <= 0) return; // 0なら削除しない設定

            var files = Directory.GetFiles(_logDir, "report-*.jsonl");
            var threshold = DateTime.Now.AddDays(-_retentionDays);

            foreach (var file in files)
            {
                var fi = new FileInfo(file);
                // ファイル名から日付を判断しても良いが、作成日時で簡易判定
                if (fi.CreationTime < threshold)
                {
                    fi.Delete();
                }
            }
        }
        catch { /* 削除失敗は無視 */ }
    }
}