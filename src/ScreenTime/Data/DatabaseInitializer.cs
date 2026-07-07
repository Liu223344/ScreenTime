using System.IO;
using Microsoft.Data.Sqlite;

namespace ScreenTime.Data;

/// <summary>
/// 启动时初始化 SQLite 数据库 schema。线程安全:幂等执行。
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly string _connectionString;

    public DatabaseInitializer(string connectionString)
    {
        _connectionString = connectionString;
    }

    public void Initialize()
    {
        // 确保目录存在
        string? dir = Path.GetDirectoryName(GetDbPath(_connectionString));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS app_usage (
                  id            INTEGER PRIMARY KEY AUTOINCREMENT,
                  date          TEXT NOT NULL,
                  process_name  TEXT NOT NULL,
                  window_title  TEXT,
                  duration_sec  INTEGER NOT NULL,
                  started_at    TEXT NOT NULL,
                  ended_at      TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_app_usage_date ON app_usage(date);
                CREATE INDEX IF NOT EXISTS idx_app_usage_proc ON app_usage(date, process_name);
                CREATE INDEX IF NOT EXISTS idx_app_usage_start ON app_usage(started_at);

                CREATE TABLE IF NOT EXISTS daily_summary (
                  date           TEXT PRIMARY KEY,
                  active_sec     INTEGER NOT NULL DEFAULT 0,
                  idle_sec       INTEGER NOT NULL DEFAULT 0,
                  locked_sec     INTEGER NOT NULL DEFAULT 0
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // 启用 WAL 提升并发写入性能;WAL 是数据库级持久属性,设一次永久有效。
        // synchronous=NORMAL 在 WAL 模式下兼顾安全与性能(仅可能在系统崩溃时丢最后一段事务,
        // 不会损坏数据库)。busy_timeout=5000 让并发写等待 5 秒而非立即抛 SQLITE_BUSY。
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA busy_timeout=5000;
                PRAGMA wal_autocheckpoint=1000;
                """;
            pragma.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 退出时执行 WAL checkpoint 并截断 -wal 文件,防止其无限增长。
    /// </summary>
    public void Checkpoint()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    private static string GetDbPath(string connectionString)
    {
        // 用 SqliteConnectionStringBuilder 解析,正确处理引号包裹的路径
        // (路径可能含分号、空格等特殊字符,手动 Split 会出错)。
        var builder = new SqliteConnectionStringBuilder(connectionString);
        return string.IsNullOrEmpty(builder.DataSource) ? "screentime.db" : builder.DataSource;
    }
}
