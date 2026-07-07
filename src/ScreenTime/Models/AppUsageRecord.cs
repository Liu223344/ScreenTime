namespace ScreenTime.Models;

/// <summary>
/// 某应用在某天的累计使用记录(已聚合)。
/// </summary>
public sealed class AppUsageRecord
{
    public string Date { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public long DurationSeconds { get; set; }
}
