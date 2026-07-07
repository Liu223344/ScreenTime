using System.IO;
using System.Text.Json;
using ScreenTime.Options;

namespace ScreenTime.Services;

/// <summary>
/// 用户偏好设置的持久化与运行时访问。设置变更后触发 <see cref="Changed"/> 事件,
/// 让 TrackingService / ReminderService 等订阅者即时生效。
/// </summary>
public sealed class UserSettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenTime", "settings.json");

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
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<UserSettings>(json, JsonOpts);
                if (s != null) return s;
            }
        }
        catch
        {
            // 损坏的配置文件,回退到默认
        }
        return new UserSettings();
    }
}
