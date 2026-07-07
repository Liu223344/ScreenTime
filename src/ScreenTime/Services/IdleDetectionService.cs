using ScreenTime.Native.Win32;

namespace ScreenTime.Services;

/// <summary>
/// 闲置检测:封装 GetLastInputInfo,返回自最后一次键鼠输入以来的时间。
/// </summary>
public sealed class IdleDetectionService
{
    public TimeSpan GetIdleTime() => NativeMethods.GetIdleTime();

    public bool IsIdle(int idleThresholdSec)
    {
        return GetIdleTime().TotalSeconds >= idleThresholdSec;
    }
}
