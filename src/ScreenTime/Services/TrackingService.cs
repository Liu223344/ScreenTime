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
///
/// 时长累计使用"实际上次 tick 以来的真实经过时间",而非固定 _tickIntervalSec,
/// 这样可以避免:(1) 首次立即 tick 时多算一个完整间隔;
/// (2) 退出时最后不足一个间隔的时间丢失。同时检测睡眠/休眠造成的大间隔(>60s)
/// 并丢弃该段(因为机器实际未在使用)。
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
    private DateTime _lastTickTime;

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

        var now = DateTime.Now;
        _sessionStart = now;
        _lastTickTime = now;
        _currentState = State.Idle;

        _timer = new Timer(OnTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(_tickIntervalSec));
        return Task.CompletedTask;
    }

    private void OnTick(object? state)
    {
        lock (_stateLock)
        {
            // 锁屏期间不采样(锁屏时长由 SessionSwitch 事件在解锁时一次性结算)
            if (_currentState == State.Locked)
            {
                _lastTickTime = DateTime.Now;
                return;
            }

            var now = DateTime.Now;
            double elapsed = (now - _lastTickTime).TotalSeconds;
            _lastTickTime = now;
            if (elapsed < 0) elapsed = 0;

            // 大间隔(睡眠/休眠/长时间挂起):丢弃当前未结算的部分,重新开始,
            // 不把这段"机器未真正使用"的时间计入任何状态。
            if (elapsed > 60)
            {
                FlushCurrentSession();
                _currentState = State.Idle;
                _currentProcess = string.Empty;
                _currentTitle = string.Empty;
                _sessionStart = now;
                _sessionSeconds = 0;
                // 不累加 elapsed,直接重新探测当前状态
            }
            else
            {
                _sessionSeconds += (long)Math.Round(elapsed);

                // 跨午夜:把当前会话 flush 到昨天(_sessionStart 所在日期),
                // 从今天 00:00 开始新会话。避免长时间运行的会话把今天的数据记到昨天。
                if (now.Date != _sessionStart.Date)
                {
                    FlushCurrentSession();
                    _sessionStart = now.Date;
                    _sessionSeconds = 0;
                    // _currentState / _currentProcess 保持,后续状态探测会自然衔接
                }
            }

            int threshold = Math.Max(1, _settings.Current.IdleThresholdSec);
            bool isIdle = _idle.IsIdle(threshold);
            var (proc, title) = isIdle ? (string.Empty, string.Empty) : _foreground.GetForegroundApp();
            var newState = isIdle ? State.Idle : State.Active;

            bool sameActive = _currentState == State.Active && newState == State.Active
                              && _currentProcess == proc;
            bool sameIdle = _currentState == State.Idle && newState == State.Idle;

            if (sameActive || sameIdle)
            {
                // 状态未变,仅累加,等待下次 tick
                return;
            }

            // 状态变化:flush 旧会话,开始新会话
            FlushCurrentSession();
            _currentState = newState;
            _currentProcess = proc;
            _currentTitle = title;
            _sessionStart = now;
            _sessionSeconds = 0;
        }
    }

    private void OnSessionStateChanged(SessionService.SessionState state)
    {
        lock (_stateLock)
        {
            var now = DateTime.Now;

            if (state == SessionService.SessionState.Locked)
            {
                // 锁屏前:把自上次 tick 以来的部分累加到当前 active/idle 会话,然后 flush
                double elapsed = (now - _lastTickTime).TotalSeconds;
                if (elapsed > 0 && elapsed < 60)
                {
                    _sessionSeconds += (long)Math.Round(elapsed);
                }
                _lastTickTime = now;

                FlushCurrentSession();
                _currentState = State.Locked;
                _currentProcess = string.Empty;
                _currentTitle = string.Empty;
                _sessionStart = now;
                _sessionSeconds = 0;
            }
            else // Unlock
            {
                // 锁屏期间没有 tick 累计,用 (now - _sessionStart) 作为锁屏时长
                double lockedElapsed = (now - _sessionStart).TotalSeconds;
                if (lockedElapsed > 0 && lockedElapsed < 86400) // 上限 24h,防异常
                {
                    _sessionSeconds = (long)Math.Round(lockedElapsed);
                }
                _lastTickTime = now;

                FlushCurrentSession(); // 写入 locked 会话
                _currentState = State.Idle;
                _currentProcess = string.Empty;
                _currentTitle = string.Empty;
                _sessionStart = now;
                _sessionSeconds = 0;
            }
        }
    }

    /// <summary>
    /// 把当前内存会话写入数据库。调用方需持有 _stateLock。
    /// 注意:本方法不重置 _sessionStart / _sessionSeconds,由调用方负责。
    /// </summary>
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

        // 退出前:把自上次 tick 以来的最后一段也累加并 flush
        lock (_stateLock)
        {
            var now = DateTime.Now;
            double elapsed = (now - _lastTickTime).TotalSeconds;
            if (_currentState != State.Locked && elapsed > 0 && elapsed < 60)
            {
                _sessionSeconds += (long)Math.Round(elapsed);
            }
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
