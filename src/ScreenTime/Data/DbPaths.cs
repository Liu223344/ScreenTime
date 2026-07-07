using System.IO;

namespace ScreenTime.Data;

/// <summary>
/// 统一管理数据库文件路径与连接字符串。文件位于 %LOCALAPPDATA%\ScreenTime\screentime.db。
/// </summary>
public static class DbPaths
{
    public static string DatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenTime", "screentime.db");

    // 路径用双引号包裹,防止用户名含分号等特殊字符破坏连接字符串解析。
    public static string ConnectionString => $"Data Source=\"{DatabasePath}\"";
}
