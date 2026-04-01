using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace MBack.Service;

/// <summary>
/// 実行履歴をSQLiteに保存するクラス。
/// 「いつ」「誰が」「何を」したかを記録する。
///
/// ★耐障害化の方針：
/// .NETの static コンストラクタは「一度失敗すると以降の呼び出しが
/// 全て TypeInitializationException を投げ続ける」という仕様上の罠がある。
/// これを回避するため、初期化の成否を _isAvailable フラグで管理し、
/// DB操作に失敗してもバックアップ本業に影響を与えない構造にしている。
/// </summary>
public static class HistoryLogger
{
    private static readonly string DbPath;
    private static readonly object _lock = new object();

    // ★耐障害化: DBが使える状態かどうかを管理するフラグ
    // false の場合、Log()等を呼んでも何もせず黙って返る（サービスを止めない）
    private static bool _isAvailable = false;

    static HistoryLogger()
    {
        try
        {
            // サービスと設定ツールで共有するパス (ProgramData\MBack)
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MBack",
                "Database");

            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            DbPath = Path.Combine(dir, "history.db");

            InitializeDatabase();

            // ここまで例外が出なければDBは正常に使える
            _isAvailable = true;
        }
        catch (Exception ex)
        {
            // ★ここで握り潰す。static ctorから例外を外に出すと
            // TypeInitializationExceptionとなり、以降の全呼び出しが死ぬ。
            // サービスのログ(Serilog)には書けないため、Windowsイベントログに記録する。
            try
            {
                System.Diagnostics.EventLog.WriteEntry(
                    "MBackService",
                    $"HistoryLogger の初期化に失敗しました。SQLiteログは無効化されます。\n詳細: {ex}",
                    System.Diagnostics.EventLogEntryType.Warning);
            }
            catch { /* イベントログへの書き込みも失敗した場合は完全に無視 */ }

            // DbPath が未代入の場合にコンパイルエラーになるため、ダミー値を設定
            DbPath = "";
            _isAvailable = false;
        }
    }

    /// <summary>
    /// DBのテーブルとインデックスを初期化する（初回起動時に実行される）
    /// </summary>
    private static void InitializeDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        // 書き込みと読み取りの衝突を防ぐWALモード
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        // テーブル作成（Userカラムを含む最新スキーマ）
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

        // 既存のDBにUserカラムがない場合、ここで追加する（マイグレーション）
        // すでにカラムがある場合はエラーが出るので、そのまま無視する
        try
        {
            using var checkCmd = new SqliteCommand("ALTER TABLE LogEntries ADD COLUMN User TEXT;", connection);
            checkCmd.ExecuteNonQuery();
        }
        catch { /* すでにカラムがある場合はエラーを無視 */ }
    }

    /// <summary>
    /// ログを記録する。DBが使えない状態でも例外を投げず黙って返る。
    /// </summary>
    /// <param name="type">ログの種類 (Copy / Delete / Error など)</param>
    /// <param name="path">対象ファイルのパス</param>
    /// <param name="size">ファイルサイズ (バイト)</param>
    /// <param name="msg">補足メッセージ</param>
    /// <param name="user">操作したWindowsユーザー名</param>
    public static void Log(string type, string path, long size, string msg = "", string user = "")
    {
        // ★耐障害化: DBが使えない状態なら何もせず返る（バックアップ本業を止めない）
        if (!_isAvailable) return;

        lock (_lock)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={DbPath}");
                connection.Open();

                string sql = "INSERT INTO LogEntries (Time, Type, Path, Size, Message, User) " +
                             "VALUES (@time, @type, @path, @size, @msg, @user)";
                using var command = new SqliteCommand(sql, connection);

                command.Parameters.AddWithValue("@time", DateTime.Now);
                command.Parameters.AddWithValue("@type", type);
                command.Parameters.AddWithValue("@path", path);
                command.Parameters.AddWithValue("@size", size);
                command.Parameters.AddWithValue("@msg", msg ?? "");
                command.Parameters.AddWithValue("@user", user ?? "System");

                command.ExecuteNonQuery();
            }
            catch { /* DB書き込みエラーはバックアップ本業に影響させない */ }
        }
    }

    /// <summary>
    /// 指定日数より古いログをDBから削除してディスクを節約する。
    /// </summary>
    /// <param name="days">保存する日数。これより古いログは削除される。</param>
    public static void Cleanup(int days)
    {
        // ★耐障害化: DBが使えない状態なら何もせず返る
        if (!_isAvailable) return;

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

                // 削除後にVACUUMしてDBファイルのサイズを実際に縮小する
                using var vacuumCmd = new SqliteCommand("VACUUM", connection);
                vacuumCmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    /// <summary>
    /// DBが正常に使える状態かどうかを外部から確認できるプロパティ。
    /// Worker.cs の起動チェック等で使用可能。
    /// </summary>
    public static bool IsAvailable => _isAvailable;
}
