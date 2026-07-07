using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ScreenTime.Services;
using ScreenTime.ViewModels;

namespace ScreenTime.Views;

/// <summary>
/// 主窗口(无边框、macOS 风格 chrome)代码后置。
/// - 左上角红/黄圆点为关闭/最小化按钮(绿点装饰);
/// - 标题栏区域可拖拽移动窗口;
/// - 双击标题栏切换自定义最大化(避免 WindowStyle=None + Maximized 覆盖任务栏);
/// - 顶部分段选择器切换今日/历史/设置面板;
/// - 关闭按钮只是隐藏到托盘,程序通过托盘菜单退出。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private System.Windows.Threading.DispatcherTimer? _refreshTimer;
    private bool _forceClose;
    private bool _isMaximized;
    private double _restoreLeft, _restoreTop, _restoreWidth, _restoreHeight;

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
        // 首次加载数据(异步,不阻塞窗口显示)
        _ = _viewModel.RefreshAsync();

        // 每 30 秒自动刷新今日数据
        _refreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _refreshTimer.Tick += async (_, _) => await _viewModel.RefreshAsync();
        _refreshTimer.Start();
    }

    /// <summary>
    /// 标题栏拖拽:左键按下并移动时移动窗口;双击切换最大化。
    /// 仅绑定在标题栏 Grid 上(不绑定到 Window),避免在内容区点击时误触发拖拽,
    /// 也避免与按钮等子元素的鼠标事件冲突。
    /// </summary>
    private void TitleBar_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount >= 2)
        {
            ToggleMaximize();
            return;
        }

        try { DragMove(); }
        catch (Exception ex) { AppLogger.Instance.LogWarning("DragMove 异常", ex); }
    }

    /// <summary>
    /// 自定义最大化:不使用 WindowState.Maximized(它在 WindowStyle=None 下会覆盖任务栏),
    /// 而是手动把窗口尺寸设为工作区大小,并去掉外边距和圆角。还原时恢复。
    /// </summary>
    private void ToggleMaximize()
    {
        if (!_isMaximized)
        {
            // 保存还原尺寸
            _restoreLeft = Left;
            _restoreTop = Top;
            _restoreWidth = Width;
            _restoreHeight = Height;

            var work = SystemParameters.WorkArea;
            OuterBorder.Margin = new Thickness(0);
            WindowShell.CornerRadius = new CornerRadius(0);

            Left = work.Left;
            Top = work.Top;
            Width = work.Width;
            Height = work.Height;
            _isMaximized = true;
        }
        else
        {
            OuterBorder.Margin = new Thickness(12);
            WindowShell.CornerRadius = new CornerRadius(12);

            Left = _restoreLeft;
            Top = _restoreTop;
            Width = _restoreWidth;
            Height = _restoreHeight;
            _isMaximized = false;
        }
    }

    private void CloseBtn_OnClick(object sender, RoutedEventArgs e)
    {
        // macOS 风格:关闭按钮只是隐藏窗口到托盘
        Hide();
    }

    private void MinBtn_OnClick(object sender, RoutedEventArgs e)
    {
        // 若当前处于自定义最大化状态,先还原到普通尺寸再最小化,
        // 这样从托盘恢复时窗口是正常大小,避免 _isMaximized 状态与实际尺寸不一致。
        if (_isMaximized)
        {
            ToggleMaximize();
        }
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
