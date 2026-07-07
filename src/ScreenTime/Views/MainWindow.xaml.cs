using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ScreenTime.ViewModels;

namespace ScreenTime.Views;

/// <summary>
/// 主窗口(无边框、macOS 风格 chrome)代码后置。
/// - 左上角红/黄圆点为关闭/最小化按钮(绿点装饰);
/// - 标题栏区域可拖拽移动窗口;
/// - 双击标题栏切换最大化;
/// - 顶部分段选择器切换今日/历史/设置面板;
/// - 关闭按钮只是隐藏到托盘,程序通过托盘菜单退出。
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

        // 顶部分段选择器切换面板
        TabToday.Checked += (_, _) => SwitchPanel(TodayPanel);
        TabHistory.Checked += (_, _) => SwitchPanel(HistoryPanel);
        TabSettings.Checked += (_, _) => SwitchPanel(SettingsPanel);
    }

    private void SwitchPanel(UIElement visible)
    {
        TodayPanel.Visibility = ReferenceEquals(visible, TodayPanel) ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = ReferenceEquals(visible, HistoryPanel) ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = ReferenceEquals(visible, SettingsPanel) ? Visibility.Visible : Visibility.Collapsed;
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
        // 无边框窗口最大化时,避免遮住任务栏:调整为工作区大小
        if (WindowState == WindowState.Maximized)
        {
            var workArea = SystemParameters.WorkArea;
            // 在最大化时移除外边距,避免圆角边距叠加
            Margin = new Thickness(0);
            Width = workArea.Width;
            Height = workArea.Height;
            Left = workArea.Left;
            Top = workArea.Top;
        }
        else
        {
            Margin = new Thickness(0);
        }
    }

    /// <summary>
    /// 标题栏拖拽:在标题栏区域按下并移动时移动窗口。
    /// </summary>
    private void TitleBar_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.ClickCount >= 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            try { DragMove(); } catch { /* DragMove 可能因按钮释放时机抛异常,忽略 */ }
        }
    }

    private void CloseBtn_OnClick(object sender, RoutedEventArgs e)
    {
        // macOS 风格:关闭按钮只是隐藏窗口到托盘
        Hide();
    }

    private void MinBtn_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// 拦截关闭:除非是从托盘菜单触发的强制退出,否则只是隐藏到托盘。
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
