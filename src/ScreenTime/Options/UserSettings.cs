namespace ScreenTime.Options;

/// <summary>
/// 用户偏好设置(可运行时修改,持久化到 %LOCALAPPDATA%\ScreenTime\settings.json)。
/// </summary>
public sealed class UserSettings
{
    public int IdleThresholdSec { get; set; } = 60;
    public int PollIntervalSec { get; set; } = 3;
    public bool ReminderEnabled { get; set; } = true;
    public int ReminderIntervalMin { get; set; } = 45;
    public int RetentionDays { get; set; } = 90;
    public bool AutoStartWithWindows { get; set; } = false;
}
