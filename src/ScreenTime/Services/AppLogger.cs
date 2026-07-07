using System.IO;
using System.Text;

namespace ScreenTime.Services;

/// <summary>
/// 轻量级文件日志服务。日志写入 %LOCALAPPDATA%\ScreenTime\logs\screentime.log,
/// 按日期轮转,保留最近 7 天。线程安全,开销极低。
/// </summary>
public sealed class AppLogger
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenTime", "logs");

    private static readonly object _lock = new();
    private static AppLogger? _instance;

    /// <summary>
    /// 全局单例,供未使用 DI 的场景访问。
    /// </summary>
    public static AppLogger Instance => _instance ??= new AppLogger();

    private StreamWriter? _writer;
    private DateTime _logDate;

    public AppLogger()
    {
        _logDate = DateTime.Today;
        _writer = OpenWriter(_logDate);
        CleanupOldLogs();
    }

    public void LogInfo(string message) => Write("INFO", message);
    public void LogWarning(string message, Exception? ex = null) => Write("WARN", message, ex);
    public void LogError(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex = null)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
        sb.Append(" [").Append(level).Append("] ");
        sb.Append(message);
        if (ex != null)
        {
            sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        }

        string line = sb.ToString();
        lock (_lock)
        {
            // 跨天则重新打开日志文件
            if (DateTime.Today != _logDate && _writer != null)
            {
                _writer.Dispose();
                _logDate = DateTime.Today;
                _writer = OpenWriter(_logDate);
            }

            try
            {
                _writer?.WriteLine(line);
                _writer?.Flush();
            }
            catch
            {
                // 日志写入失败不应影响程序运行
            }
        }

        System.Diagnostics.Debug.WriteLine(line);
    }

    private static StreamWriter? OpenWriter(DateTime date)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string path = Path.Combine(LogDirectory, $"screentime_{date:yyyyMMdd}.log");
            return new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = false };
        }
        catch
        {
            return null;
        }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;
            var cutoff = DateTime.Today.AddDays(-7);
            foreach (var file in Directory.GetFiles(LogDirectory, "screentime_*.log"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Length >= 19 && DateTime.TryParseExact(name.AsSpan(11, 8), "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out var fileDate) && fileDate < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败不影响启动
        }
    }
}
