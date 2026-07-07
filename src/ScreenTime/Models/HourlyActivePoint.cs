namespace ScreenTime.Models;

/// <summary>
/// 单个小时的活跃秒数(用于 24 小时柱状图)。
/// </summary>
public sealed class HourlyActivePoint
{
    public int Hour { get; set; }       // 0-23
    public long ActiveSeconds { get; set; }
}
