using System.Diagnostics;
using ScreenTime.Native.Win32;

namespace ScreenTime.Services;

/// <summary>
/// 前台应用检测:返回当前前台窗口对应的进程名 + 窗口标题。
/// 对系统进程、访问被拒等情况做兜底处理。
/// </summary>
public sealed class ForegroundAppService
{
    public (string ProcessName, string WindowTitle) GetForegroundApp()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return ("Unknown", string.Empty);
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        string processName = "Unknown";
        if (pid != 0)
        {
            try
            {
                // Process.GetProcessById 返回的对象持有 OS 句柄,必须 Dispose,
                // 否则每次 tick(默认 3 秒)都会泄漏,长期运行耗尽句柄。
                using var proc = Process.GetProcessById((int)pid);
                processName = string.IsNullOrEmpty(proc.ProcessName) ? "Unknown" : proc.ProcessName + ".exe";
            }
            catch (ArgumentException)
            {
                // 进程已退出
                processName = "Unknown";
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // 访问被拒(部分系统进程)
                processName = "System";
            }
            catch (Exception ex)
            {
                AppLogger.Instance.LogWarning($"获取进程名失败 (pid={pid})", ex);
                processName = "Unknown";
            }
        }

        string title = NativeMethods.GetWindowTitle(hwnd);
        return (processName, title);
    }
}
