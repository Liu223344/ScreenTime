using Microsoft.Data.Sqlite;
using ScreenTime.Models;
using ScreenTime.Services;

namespace ScreenTime.Data;

/// <summary>
/// SQLite 仓储层。每次操作打开/关闭一个连接,Microsoft.Data.Sqlite 的连接池
/// 已足够轻量,避免长时间持锁阻塞 UI 线程的读取。
/// </summary>
public sealed class UsageRepository
{
    private readonly string _connectionString;

    public UsageRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// 打开连接并设置连接级 PRAGMA(busy_timeout 是连接级属性,每次新连接都需设置)。
    /// </summary>
    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    // ---------- 写入 ----------

    /// <summary>
    /// 累加某天的汇总值(原子 upsert)。写入失败时记录日志但不抛出,
    /// 避免异常中断 TrackingService 状态机。
    /// </summary>
    public void UpsertDailySummary(string date, long activeDelta, long idleDelta, long lockedDelta)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO daily_summary(date, active_sec, idle_sec, locked_sec)
                VALUES(@date, @a, @i, @l)
                ON CONFLICT(date) DO UPDATE SET
                  active_sec = active_sec + @a,
                  idle_sec   = idle_sec   + @i,
                  locked_sec = locked_sec + @l;
                """;
            cmd.Parameters.AddWithValue("@date", date);
            cmd.Parameters.AddWithValue("@a", activeDelta);
            cmd.Parameters.AddWithValue("@i", idleDelta);
            cmd.Parameters.AddWithValue("@l", lockedDelta);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            AppLogger.Instance.LogWarning($"UpsertDailySummary 失败 (date={date})", ex);
        }
    }

    /// <summary>
    /// 追加一条应用使用记录。写入失败时记录日志但不抛出。
    /// </summary>
    public void AddAppUsage(string date, string processName, string? windowTitle,
                            long durationSeconds, string startedAt, string endedAt)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO app_usage(date, process_name, window_title, duration_sec, started_at, ended_at)
                VALUES(@date, @proc, @title, @dur, @start, @end);
                """;
            cmd.Parameters.AddWithValue("@date", date);
            cmd.Parameters.AddWithValue("@proc", processName);
            cmd.Parameters.AddWithValue("@title", (object?)windowTitle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@dur", durationSeconds);
            cmd.Parameters.AddWithValue("@start", startedAt);
            cmd.Parameters.AddWithValue("@end", endedAt);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            AppLogger.Instance.LogWarning($"AddAppUsage 失败 (date={date}, proc={processName})", ex);
        }
    }

    // ---------- 读取 ----------

    public DailySummary? GetSummary(string date)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT date, active_sec, idle_sec, locked_sec FROM daily_summary WHERE date=@date;";
        cmd.Parameters.AddWithValue("@date", date);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new DailySummary
        {
            Date = r.GetString(0),
            ActiveSeconds = r.GetInt64(1),
            IdleSeconds = r.GetInt64(2),
            LockedSeconds = r.GetInt64(3),
        };
    }

    public DailySummary GetTodaySummary(string date)
    {
        return GetSummary(date) ?? new DailySummary { Date = date };
    }

    /// <summary>
    /// 按小时聚合某天的活跃时长。
    /// 通过 started_at 字段中的 ISO 8601 时间(包含 'T'HH)解析小时。
    /// </summary>
    public List<HourlyActivePoint> GetHourlyActive(string date)
    {
        var result = new List<HourlyActivePoint>(24);
        for (int i = 0; i < 24; i++) result.Add(new HourlyActivePoint { Hour = i });

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        // started_at 形如 "2026-07-07T14:23:01";取第 11-13 位的小时数字。
        cmd.CommandText = """
            SELECT CAST(SUBSTR(started_at, 12, 2) AS INTEGER) AS h,
                   SUM(duration_sec) AS s
            FROM app_usage
            WHERE date=@date
            GROUP BY h;
            """;
        cmd.Parameters.AddWithValue("@date", date);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!r.IsDBNull(0) && !r.IsDBNull(1))
            {
                int h = r.GetInt32(0);
                if (h >= 0 && h < 24)
                {
                    result[h].ActiveSeconds = r.GetInt64(1);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 取某天使用时长 Top N 的应用。
    /// </summary>
    public List<AppUsageRecord> GetTopApps(string date, int limit)
    {
        var list = new List<AppUsageRecord>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT process_name, SUM(duration_sec) AS s
            FROM app_usage
            WHERE date=@date
            GROUP BY process_name
            ORDER BY s DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@date", date);
        cmd.Parameters.AddWithValue("@limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AppUsageRecord
            {
                Date = date,
                ProcessName = r.GetString(0),
                DurationSeconds = r.GetInt64(1),
            });
        }
        return list;
    }

    /// <summary>
    /// 取 [from, to] 区间内每天的汇总(含 Top App)。
    /// </summary>
    public List<HistoryRow> GetDailyHistory(string from, string to)
    {
        var list = new List<HistoryRow>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.date, s.active_sec, s.idle_sec, s.locked_sec,
                   t.process_name, t.s
            FROM daily_summary s
            LEFT JOIN (
              SELECT date, process_name, s AS s
              FROM (
                SELECT date, process_name, SUM(duration_sec) AS s,
                       ROW_NUMBER() OVER (PARTITION BY date ORDER BY SUM(duration_sec) DESC) AS rn
                FROM app_usage
                WHERE date BETWEEN @from AND @to
                GROUP BY date, process_name
              )
              WHERE rn = 1
            ) t ON t.date = s.date
            WHERE s.date BETWEEN @from AND @to
            ORDER BY s.date DESC;
            """;
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new HistoryRow
            {
                Date = r.GetString(0),
                ActiveSeconds = r.GetInt64(1),
                IdleSeconds = r.GetInt64(2),
                LockedSeconds = r.GetInt64(3),
                TopApp = r.IsDBNull(4) ? "-" : r.GetString(4),
                TopAppSeconds = r.IsDBNull(5) ? 0 : r.GetInt64(5),
            });
        }
        return list;
    }

    // ---------- 导出 ----------

    /// <summary>
    /// 取 [from, to] 区间内所有应用使用记录(用于 CSV 导出)。
    /// </summary>
    public List<(string Date, string ProcessName, string? Title, long Seconds, string StartedAt, string EndedAt)>
        GetAllAppUsage(string from, string to)
    {
        var list = new List<(string, string, string?, long, string, string)>();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT date, process_name, window_title, duration_sec, started_at, ended_at
            FROM app_usage
            WHERE date BETWEEN @from AND @to
            ORDER BY started_at;
            """;
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((
                r.GetString(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.GetInt64(3),
                r.GetString(4),
                r.GetString(5)));
        }
        return list;
    }

    // ---------- 清理 ----------

    public int PurgeOlderThan(string cutoffDate)
    {
        using var conn = OpenConnection();
        int affected = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM app_usage WHERE date < @c;";
            cmd.Parameters.AddWithValue("@c", cutoffDate);
            affected += cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM daily_summary WHERE date < @c;";
            cmd.Parameters.AddWithValue("@c", cutoffDate);
            affected += cmd.ExecuteNonQuery();
        }
        return affected;
    }
}
