namespace ScreenTime.Models;

/// <summary>
/// 一天的汇总数据。
/// </summary>
public sealed class DailySummary
{
    public string Date { get; set; } = string.Empty; // YYYY-MM-DD
    public long ActiveSeconds { get; set; }
    public long IdleSeconds { get; set; }
    public long LockedSeconds { get; set; }

    public long TotalSeconds => ActiveSeconds + IdleSeconds + LockedSeconds;

    public double ActiveRatio =>
        TotalSeconds > 0 ? (double)ActiveSeconds / TotalSeconds : 0;
}
