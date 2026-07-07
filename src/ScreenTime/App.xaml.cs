using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ScreenTime.Data;
using ScreenTime.Services;
using ScreenTime.Tray;
using ScreenTime.ViewModels;
using ScreenTime.Views;

namespace ScreenTime;

/// <summary>
/// 应用入口。负责:单实例检查、DI 容器装配、数据库初始化、托盘图标、
/// 启动 TrackingService/ReminderService、托盘 tooltip 定时刷新、退出流程。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = "Local\\ScreenTime_SingleInstance_v1";

    private static readonly Mutex SingleInstanceMutex = new(false, SingleInstanceMutexName);

    private ServiceProvider? _services;
    private TrayIconManager? _tray;
    private MainWindow? _mainWindow;
    private System.Windows.Threading.DispatcherTimer? _tooltipTimer;
    private TrackingService? _tracking;
    private ReminderService? _reminder;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ----- 单实例检查 -----
        if (!SingleInstanceMutex.WaitOne(0, false))
        {
            // 已有实例在运行,直接退出(用户可见托盘图标)
            Shutdown(0);
            return;
        }

        // ----- DI 装配 -----
        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        // ----- 数据库初始化 -----
        // 若初始化失败(磁盘只读、路径权限不足等),提示用户后退出,
        // 避免未捕获异常导致进程静默崩溃、托盘不出现。
        var dbInit = _services.GetRequiredService<DatabaseInitializer>();
        try
        {
            dbInit.Initialize();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"数据库初始化失败,程序无法启动。\n\n路径:{DbPaths.DatabasePath}\n错误:{ex.Message}",
                "ScreenTime 启动错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // 启动时按当前保留天数做一次清理(防止用户长期不开机)
        var settings = _services.GetRequiredService<UserSettingsService>();
        var repo = _services.GetRequiredService<UsageRepository>();
        try
        {
            string cutoff = DateTime.Today.AddDays(-settings.Current.RetentionDays).ToString("yyyy-MM-dd");
            repo.PurgeOlderThan(cutoff);
        }
        catch
        {
            // 清理失败不阻塞启动
        }

        // ----- 托盘 -----
        _tray = _services.GetRequiredService<TrayIconManager>();
        _tray.Initialize();
        _tray.ShowMainWindowRequested += ShowMainWindow;
        _tray.ExitRequested += ExitFromTray;

        // ----- 启动后台服务 -----
        _tracking = _services.GetRequiredService<TrackingService>();
        _reminder = _services.GetRequiredService<ReminderService>();
        _tracking.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _reminder.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        // ----- 托盘 tooltip 定时刷新 -----
        _tooltipTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1),
        };
        _tooltipTimer.Tick += (_, _) => _tray.RefreshTooltip();
        _tooltipTimer.Start();
        _tray.RefreshTooltip();

        // ----- 首次启动显示主窗口 -----
        ShowMainWindow();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // 数据
        services.AddSingleton(new DatabaseInitializer(DbPaths.ConnectionString));
        services.AddSingleton(new UsageRepository(DbPaths.ConnectionString));

        // 服务(单例,生命周期由 App 管理)
        services.AddSingleton<UserSettingsService>();
        services.AddSingleton<IdleDetectionService>();
        services.AddSingleton<ForegroundAppService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<AutoStartService>();
        services.AddSingleton<ReminderService>();
        services.AddSingleton<TrackingService>();

        // UI
        services.AddSingleton<TrayIconManager>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private void ShowMainWindow()
    {
        if (_services == null) return;
        _mainWindow ??= _services.GetRequiredService<MainWindow>();
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Activate();
    }

    private void ExitFromTray()
    {
        if (_exiting) return;
        _exiting = true;

        _tooltipTimer?.Stop();

        // 强制关闭主窗口(绕过 Hide 拦截)
        _mainWindow?.ForceClose();

        // 停止后台服务(TrackingService.StopAsync 会 flush 最后一段会话)
        try
        {
            _reminder?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch { /* 忽略退出时的异常 */ }
        try
        {
            _tracking?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch { /* 忽略退出时的异常 */ }

        _tray?.Dispose();
        _services?.Dispose();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 兜底:若用户通过其他方式触发退出(如系统关机),也尝试 flush
        if (!_exiting)
        {
            try
            {
                _tracking?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch { }
            _tray?.Dispose();
            _services?.Dispose();
        }
        base.OnExit(e);
    }
}
