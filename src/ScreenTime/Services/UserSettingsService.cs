using System.IO;
using System.Text.Json;
using ScreenTime.Options;

namespace ScreenTime.Services;

/// <summary>
/// 用户偏好设置的持久化与运行时访问。设置变更后触发 <see cref="Changed"/> 事件,
/// 让 TrackingService / ReminderService 等订阅者即时生效。
/// 首次运行(无 settings.json)时,从 appsettings.json 读取初始默认值。
/// </summary>
public sealed class UserSettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenTime", "settings.json");

    // appsettings.json 位于可执行文件同目录,作为首次运行的默认值来源。
    private static readonly string AppSettingsPath = Path.Combine(
        AppContext.BaseDirectory, "appsettings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private readonly object _lock = new();
    private UserSettings _current;

    public UserSettingsService()
    {
        _current = Load();
    }

    public UserSettings Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    public event Action? Changed;

    /// <summary>
    /// 用一份新的设置覆盖当前值并持久化,然后触发 Changed。
    /// </summary>
    public void Save(UserSettings updated)
    {
        lock (_lock)
        {
            _current = updated;
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(updated, JsonOpts));
        }
        Changed?.Invoke();
    }

    private static UserSettings Load()
    {
        // 1. 优先读取用户持久化的 settings.json
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<UserSettings>(json, JsonOpts);
                if (s != null) return s;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.LogWarning("用户配置文件加载失败,回退到默认值", ex);
        }

        // 2. 首次运行:从 appsettings.json 读取初始默认值
        var defaults = LoadFromAppSettings();
        return defaults ?? new UserSettings();
    }

    /// <summary>
    /// 从 appsettings.json 解析配置段作为初始默认值。
    /// 文件格式:{"Tracking":{"PollIntervalSec":3,"IdleThresholdSec":60},"Reminder":{...},"Retention":{...}}
    /// </summary>
    private static UserSettings? LoadFromAppSettings()
    {
        try
        {
            if (!File.Exists(AppSettingsPath)) return null;

            var json = File.ReadAllText(AppSettingsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var settings = new UserSettings();

            if (root.TryGetProperty("Tracking", out var tracking))
            {
                if (tracking.TryGetProperty("PollIntervalSec", out var poll))
                    settings.PollIntervalSec = poll.GetInt32();
                if (tracking.TryGetProperty("IdleThresholdSec", out var idle))
                    settings.IdleThresholdSec = idle.GetInt32();
            }

            if (root.TryGetProperty("Reminder", out var reminder))
            {
                if (reminder.TryGetProperty("Enabled", out var enabled))
                    settings.ReminderEnabled = enabled.GetBoolean();
                if (reminder.TryGetProperty("IntervalMin", out var interval))
                    settings.ReminderIntervalMin = interval.GetInt32();
            }

            if (root.TryGetProperty("Retention", out var retention))
            {
                if (retention.TryGetProperty("Days", out var days))
                    settings.RetentionDays = days.GetInt32();
            }

            return settings;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.LogWarning("appsettings.json 加载失败,使用硬编码默认值", ex);
            return null;
        }
    }
}
