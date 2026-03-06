using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace MBack.Service;

/// <summary>
/// 実行履歴をSQLiteに保存するクラス。
/// 「いつ」「誰が」「何を」したかを記録する。
/// </summary>
public static class HistoryLogger
{
    private static readonly string DbPath;
    private static readonly object _lock = new object();

    static HistoryLogger()
    {
        // サービスと設定ツールで共有するパス (ProgramData)
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MBack", "Database");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        DbPath = Path.Combine(dir, "history.db");
        InitializeDatabase();
    }

    private static void InitializeDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        // 書き込み速度向上のためのWALモード設定
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        // テーブル作成（Userカラムを追加）
        string sql = @"
            CREATE TABLE IF NOT EXISTS LogEntries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Time DATETIME NOT NULL,
                Type TEXT NOT NULL,
                Path TEXT NOT NULL,
                Size INTEGER,
                Message TEXT,
                User TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_log_time ON LogEntries(Time);
            CREATE INDEX IF NOT EXISTS idx_log_path ON LogEntries(Path);";

        using (var command = new SqliteCommand(sql, connection))
        {
            command.ExecuteNonQuery();
        }

        // 既存のDBにUserカラムがない場合の救済措置
        try {
            using var checkCmd = new SqliteCommand("ALTER TABLE LogEntries ADD COLUMN User TEXT;", connection);
            checkCmd.ExecuteNonQuery();
        } catch { /* すでにカラムがある場合は何もしない */ }
    }

    /// <summary>
    /// ログをデータベースに書き込む
    /// </summary>
    public static void Log(string type, string path, long size, string msg = "", string user = "")
    {
        lock (_lock)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={DbPath}");
                connection.Open();

                string sql = "INSERT INTO LogEntries (Time, Type, Path, Size, Message, User) VALUES (@time, @type, @path, @size, @msg, @user)";
                using var command = new SqliteCommand(sql, connection);
                
                command.Parameters.AddWithValue("@time", DateTime.Now);
                command.Parameters.AddWithValue("@type", type);
                command.Parameters.AddWithValue("@path", path);
                command.Parameters.AddWithValue("@size", size);
                command.Parameters.AddWithValue("@msg", msg ?? "");
                command.Parameters.AddWithValue("@user", user ?? "System");

                command.ExecuteNonQuery();
            }
            catch { }
        }
    }

    public static void Cleanup(int days)
    {
        lock (_lock)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={DbPath}");
                connection.Open();
                string sql = "DELETE FROM LogEntries WHERE Time < @threshold";
                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@threshold", DateTime.Now.AddDays(-days));
                command.ExecuteNonQuery();
                using var vacuumCmd = new SqliteCommand("VACUUM", connection);
                vacuumCmd.ExecuteNonQuery();
            }
            catch { }
        }
    }
}