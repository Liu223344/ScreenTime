using System.ComponentModel;
using System.Windows;
using ScreenTime.ViewModels;

namespace ScreenTime.Views;

/// <summary>
/// 主窗口代码后置。关闭按钮(右上角 X)只是隐藏窗口,程序通过托盘菜单退出。
/// 内置一个 30 秒定时器自动刷新今日数据,保证用户长时间挂着窗口时图表会更新。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private System.Windows.Threading.DispatcherTimer? _refreshTimer;
    private bool _forceClose;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        // 每 30 秒自动刷新今日数据
        _refreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _refreshTimer.Tick += (_, _) => _viewModel.Refresh();
        _refreshTimer.Start();
    }

    private void MainWindow_OnStateChanged(object? sender, EventArgs e)
    {
        // 最小化时直接隐藏到托盘
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    /// <summary>
    /// 拦截关闭按钮:除非是从托盘菜单触发的强制退出,否则只是隐藏窗口。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _refreshTimer?.Stop();
        base.OnClosing(e);
    }

    /// <summary>
    /// 由 App 在收到托盘「退出」时调用,真正关闭窗口并让应用退出。
    /// </summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }
}
