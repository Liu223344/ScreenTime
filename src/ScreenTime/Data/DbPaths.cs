namespace ScreenTime.Data;

/// <summary>
/// 统一管理数据库文件路径与连接字符串。文件位于 %LOCALAPPDATA%\ScreenTime\screentime.db。
/// </summary>
public static class DbPaths
{
    public static string DatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenTime", "screentime.db");

    public static string ConnectionString => $"Data Source={DatabasePath}";
}
