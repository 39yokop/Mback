using System;
using System.IO;
using System.Text.Json;

namespace MBack.Service;

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