using Microsoft.Win32;
using System.Windows.Threading;

namespace ScreenTime.Services;

/// <summary>
/// 会话事件转发:订阅 Windows 锁屏/解锁/注销事件,通过回调通知 TrackingService。
/// 必须在 UI 线程(有消息循环的 STA 线程)上调用 Start,事件才会触发。
/// </summary>
public sealed class SessionService : IDisposable
{
    public enum SessionState { Active, Locked }

    public event Action<SessionState>? StateChanged;

    private bool _subscribed;

    public void Start()
    {
        if (_subscribed) return;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _subscribed = true;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        // SystemEvents 在订阅线程上触发回调,WPF 主线程是 STA + Dispatcher,可安全调用。
        var state = e.Reason switch
        {
            SessionSwitchReason.SessionLock
            or SessionSwitchReason.SessionLogoff
            or SessionSwitchReason.RemoteDisconnect
            or SessionSwitchReason.ConsoleDisconnect => SessionState.Locked,
            SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.SessionLogon
            or SessionSwitchReason.RemoteConnect
            or SessionSwitchReason.ConsoleConnect => SessionState.Active,
            _ => SessionState.Active,
        };
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _subscribed = false;
        }
    }
}
