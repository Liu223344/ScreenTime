namespace ScreenTime.Models;

/// <summary>
/// 历史表格中的一行:某天的关键统计 + 当天最常用应用。
/// </summary>
public sealed class HistoryRow
{
    public string Date { get; set; } = string.Empty;
    public long ActiveSeconds { get; set; }
    public long IdleSeconds { get; set; }
    public long LockedSeconds { get; set; }
    public string TopApp { get; set; } = string.Empty;
    public long TopAppSeconds { get; set; }
}
