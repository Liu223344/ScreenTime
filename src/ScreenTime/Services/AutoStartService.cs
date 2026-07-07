using Microsoft.Win32;

namespace ScreenTime.Services;

/// <summary>
/// 管理「开机自启」开关:写 / 读 / 删除 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 下的项。
/// 仅操作当前用户 hive,无需管理员权限。
/// </summary>
public sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ScreenTime";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) != null;
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            // 单文件发布时 Assembly.Location 返回空字符串(IL3000),优先用 Environment.ProcessPath。
            // 兜底用 AppContext.BaseDirectory + 可执行文件名,保证单文件场景也能注册自启。
            string exePath = Environment.ProcessPath
                ?? System.IO.Path.Combine(AppContext.BaseDirectory, "ScreenTime.exe");
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }
}
