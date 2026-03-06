using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace MBack.Service;

/// <summary>
/// バックアップの実行履歴を管理するクラス
/// テキストログから SQLite データベースへ移行し、高速化と検索性を向上
/// </summary>
public static class HistoryLogger
{
    private static readonly string DbPath;
    private static readonly object _lock = new object();

    static HistoryLogger()
    {
        // ★最重要ポイント：サービスとUIで同じDBを見るために「CommonApplicationData (ProgramData)」を使用
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MBack", "Database");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        DbPath = Path.Combine(dir, "history.db");
        InitializeDatabase();
    }

    /// <summary>
    /// データベースの初期設定（テーブルとインデックスの作成）
    /// </summary>
    private static void InitializeDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        // 読み書きの衝突を防ぐための設定（WALモード）
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        // ログ保存用のテーブル。インデックスを貼ることで検索を爆速にする
        string sql = @"
            CREATE TABLE IF NOT EXISTS LogEntries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Time DATETIME NOT NULL,
                Type TEXT NOT NULL,
                Path TEXT NOT NULL,
                Size INTEGER,
                Message TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_log_time ON LogEntries(Time);
            CREATE INDEX IF NOT EXISTS idx_log_path ON LogEntries(Path);";

        using (var command = new SqliteCommand(sql, connection))
        {
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// ログを記録する（コピー成功、削除、エラーなど）
    /// </summary>
    public static void Log(string type, string path, long size, string msg = "")
    {
        lock (_lock)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={DbPath}");
                connection.Open();

                string sql = "INSERT INTO LogEntries (Time, Type, Path, Size, Message) VALUES (@time, @type, @path, @size, @msg)";
                using var command = new SqliteCommand(sql, connection);
                
                // パラメーターを使って安全に書き込み（SQLインジェクション対策）
                command.Parameters.AddWithValue("@time", DateTime.Now);
                command.Parameters.AddWithValue("@type", type);
                command.Parameters.AddWithValue("@path", path);
                command.Parameters.AddWithValue("@size", size);
                command.Parameters.AddWithValue("@msg", msg ?? "");

                command.ExecuteNonQuery();
            }
            catch
            {
                // ログ自体のエラーで本体を止めないための安全策
            }
        }
    }

    /// <summary>
    /// 古くなったデータを削除し、データベースを最適化する
    /// </summary>
    public static void Cleanup(int days)
    {
        lock (_lock)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={DbPath}");
                connection.Open();

                // 指定した日数より古いレコードを削除
                string sql = "DELETE FROM LogEntries WHERE Time < @threshold";
                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@threshold", DateTime.Now.AddDays(-days));
                command.ExecuteNonQuery();

                // 消した後の「スカスカ」な領域を詰めてファイルサイズを小さくする
                using var vacuumCmd = new SqliteCommand("VACUUM", connection);
                vacuumCmd.ExecuteNonQuery();
            }
            catch { }
        }
    }
}