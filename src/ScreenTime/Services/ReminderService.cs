using Microsoft.Extensions.Hosting;
using ScreenTime.Options;

namespace ScreenTime.Services;

/// <summary>
/// 休息提醒服务:每 N 分钟触发一次 <see cref="ReminderTriggered"/> 事件,
/// 由订阅方(如托盘管理器)负责显示通知。
/// 间隔实时跟随 <see cref="UserSettingsService"/>。
/// </summary>
public sealed class ReminderService : IHostedService, IDisposable
{
    private static readonly string[] Messages =
    {
        "该休息一下啦,起来活动活动 🚶",
        "盯屏太久了,看看远处放松眼睛 👀",
        "提醒:久坐伤身,站起来拉伸一下吧",
        "短暂休息能提升专注力,小憩一下吧",
        "记得喝水 💧 顺便活动下肩膀",
    };

    private readonly UserSettingsService _settings;
    private Timer? _timer;
    private bool _muted; // 「今日暂停提醒」用

    public event Action<string>? ReminderTriggered;

    public ReminderService(UserSettingsService settings)
    {
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
    }

    public bool IsMuted => _muted;

    public void SetMuted(bool muted)
    {
        _muted = muted;
        ReconfigureTimer();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ReconfigureTimer();
        return Task.CompletedTask;
    }

    private void OnSettingsChanged()
    {
        ReconfigureTimer();
    }

    private void ReconfigureTimer()
    {
        if (_timer == null)
        {
            _timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        if (_muted || !_settings.Current.ReminderEnabled)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        int minutes = Math.Max(1, _settings.Current.ReminderIntervalMin);
        _timer.Change(TimeSpan.FromMinutes(minutes), TimeSpan.FromMinutes(minutes));
    }

    private void OnTick(object? state)
    {
        if (_muted || !_settings.Current.ReminderEnabled) return;
        // Random.Shared 是线程安全的(.NET 6+);System.Random 实例在 ThreadPool
        // 并发调用下可能返回 0 或损坏内部状态。
        string msg = Messages[Random.Shared.Next(Messages.Length)];
        ReminderTriggered?.Invoke(msg);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_timer != null)
        {
            await _timer.DisposeAsync();
            _timer = null;
        }
        _settings.Changed -= OnSettingsChanged;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _settings.Changed -= OnSettingsChanged;
    }
}
