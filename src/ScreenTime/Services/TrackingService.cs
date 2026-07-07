using Microsoft.Extensions.Hosting;
using ScreenTime.Data;
using ScreenTime.Options;

namespace ScreenTime.Services;

/// <summary>
/// 屏幕使用时间追踪核心服务。
///
/// 维护一个有限状态机:Active(app) / Idle / Locked。
/// 每 PollIntervalSec 秒采样一次,根据"上次状态"和"本次状态"判断是否要 flush
/// 一段连续会话到 SQLite(状态变化时 flush,减少写盘)。
/// IdleThresholdSec 实时从 <see cref="UserSettingsService"/> 读取,无需重启即可生效。
/// </summary>
public sealed class TrackingService : IHostedService, IDisposable
{
    private enum State { Active, Idle, Locked }

    private readonly IdleDetectionService _idle;
    private readonly ForegroundAppService _foreground;
    private readonly UsageRepository _repo;
    private readonly UserSettingsService _settings;
    private readonly SessionService _session;

    private Timer? _timer;
    private readonly object _stateLock = new();

    private State _currentState = State.Idle;
    private string _currentProcess = string.Empty;
    private string _currentTitle = string.Empty;
    private DateTime _sessionStart;
    private long _sessionSeconds;

    private readonly int _tickIntervalSec;
    private bool _disposed;

    public TrackingService(
        IdleDetectionService idle,
        ForegroundAppService foreground,
        UsageRepository repo,
        UserSettingsService settings,
        SessionService session)
    {
        _idle = idle;
        _foreground = foreground;
        _repo = repo;
        _settings = settings;
        _session = session;
        _tickIntervalSec = Math.Max(1, _settings.Current.PollIntervalSec);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 必须在主线程(UI 线程)上订阅会话事件,以便 SystemEvents 在消息循环上触发。
        _session.StateChanged += OnSessionStateChanged;
        _session.Start();

        _sessionStart = DateTime.Now;
        _currentState = State.Idle;

        _timer = new Timer(OnTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(_tickIntervalSec));
        return Task.CompletedTask;
    }

    private void OnTick(object? state)
    {
        lock (_stateLock)
        {
            if (_currentState == State.Locked)
            {
                // 锁屏期间不采样,等解锁事件
                return;
            }

            int threshold = Math.Max(1, _settings.Current.IdleThresholdSec);
            bool isIdle = _idle.IsIdle(threshold);
            if (isIdle)
            {
                TransitionTo(State.Idle, processName: string.Empty, title: string.Empty);
            }
            else
            {
                var (proc, title) = _foreground.GetForegroundApp();
                TransitionTo(State.Active, proc, title);
            }
        }
    }

    /// <summary>
    /// 状态转移:累计当前 tick 时长,必要时 flush 上一会话。
    /// </summary>
    private void TransitionTo(State newState, string processName, string title)
    {
        // 把本 tick 时长累加到当前会话
        _sessionSeconds += _tickIntervalSec;

        bool sameActive = _currentState == State.Active && newState == State.Active
                          && _currentProcess == processName;
        bool sameIdle = _currentState == State.Idle && newState == State.Idle;

        if (sameActive || sameIdle)
        {
            // 状态未变,仅累加,等待下次 tick
            return;
        }

        // 状态变化:flush 旧会话,开始新会话
        FlushCurrentSession();

        _currentState = newState;
        _currentProcess = processName;
        _currentTitle = title;
        _sessionStart = DateTime.Now;
        _sessionSeconds = 0;
    }

    private void OnSessionStateChanged(SessionService.SessionState state)
    {
        lock (_stateLock)
        {
            if (state == SessionService.SessionState.Locked)
            {
                FlushCurrentSession();
                _currentState = State.Locked;
                _sessionStart = DateTime.Now;
                _sessionSeconds = 0;
            }
            else
            {
                FlushCurrentSession();
                _currentState = State.Idle;
                _currentProcess = string.Empty;
                _currentTitle = string.Empty;
                _sessionStart = DateTime.Now;
                _sessionSeconds = 0;
            }
        }
    }

    private void FlushCurrentSession()
    {
        if (_sessionSeconds <= 0)
        {
            return;
        }

        DateTime end = _sessionStart.AddSeconds(_sessionSeconds);
        string date = _sessionStart.ToString("yyyy-MM-dd");
        string startedAt = _sessionStart.ToString("o");
        string endedAt = end.ToString("o");

        switch (_currentState)
        {
            case State.Active:
                _repo.AddAppUsage(date, _currentProcess, _currentTitle,
                                  _sessionSeconds, startedAt, endedAt);
                _repo.UpsertDailySummary(date, activeDelta: _sessionSeconds, idleDelta: 0, lockedDelta: 0);
                break;
            case State.Idle:
                _repo.UpsertDailySummary(date, activeDelta: 0, idleDelta: _sessionSeconds, lockedDelta: 0);
                break;
            case State.Locked:
                _repo.UpsertDailySummary(date, activeDelta: 0, idleDelta: 0, lockedDelta: _sessionSeconds);
                break;
        }

        _sessionSeconds = 0;
        _sessionStart = end;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_timer != null)
        {
            await _timer.DisposeAsync();
            _timer = null;
        }
        _session.StateChanged -= OnSessionStateChanged;
        _session.Dispose();

        lock (_stateLock)
        {
            FlushCurrentSession();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _session.Dispose();
    }
}
