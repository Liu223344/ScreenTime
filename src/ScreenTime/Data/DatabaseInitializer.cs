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

        // 启用 WAL 提升并发写入性能
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }
    }

    private static string GetDbPath(string connectionString)
    {
        // 简单解析 "Data Source=path" 中的 path
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            if (p.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
            {
                return p.Substring("Data Source=".Length).Trim('"', '\'');
            }
        }
        return "screentime.db";
    }
}
