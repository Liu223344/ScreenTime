using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using ScreenTime.Data;
using ScreenTime.Services;
// 注意:本文件同时使用 System.Drawing(生成图标位图)和 System.Windows.Media(返回 ImageSource)。
// 为避免 Color/FontStyle/Brushes 等类型在两个命名空间间歧义,这里不导入 System.Windows.Media,
// 而在用到时完全限定为 System.Windows.Media.xxx。

namespace ScreenTime.Tray;

/// <summary>
/// 系统托盘图标管理:右键菜单、双击显示主窗口、休息提醒气泡通知、tooltip 显示今日活跃时长。
/// 图标用 System.Drawing 代码生成(蓝底白字 "S"),不依赖外部 .ico 文件。
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly UsageRepository _repo;
    private readonly ReminderService _reminder;
    private readonly AutoStartService _autoStart;

    private TaskbarIcon? _icon;
    private MenuItem? _muteItem;
    private MenuItem? _autoStartItem;
    private bool _disposed;

    public event Action? ShowMainWindowRequested;
    public event Action? ExitRequested;

    public TrayIconManager(UsageRepository repo, ReminderService reminder, AutoStartService autoStart)
    {
        _repo = repo;
        _reminder = reminder;
        _autoStart = autoStart;
    }

    public void Initialize()
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "ScreenTime",
            IconSource = GenerateIconSource(),
        };
        _icon.TrayLeftMouseDoubleClick += (_, _) => ShowMainWindowRequested?.Invoke();
        _icon.ContextMenu = BuildContextMenu();

        // 强制创建托盘图标(确保图标立即在系统托盘注册)
        try
        {
            _icon.ForceCreate();
        }
        catch
        {
            // ForceCreate 在某些环境下可能抛异常,忽略以保证启动不中断
        }

        // 订阅提醒事件,在 UI 线程显示气泡
        _reminder.ReminderTriggered += OnReminder;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var showItem = new MenuItem { Header = "显示主窗口" };
        showItem.Click += (_, _) => ShowMainWindowRequested?.Invoke();
        menu.Items.Add(showItem);

        menu.Items.Add(new Separator());

        _muteItem = new MenuItem { Header = "今日暂停提醒", IsCheckable = true, IsChecked = _reminder.IsMuted };
        _muteItem.Click += (_, _) =>
        {
            _reminder.SetMuted(_muteItem.IsChecked);
            _muteItem.Header = _muteItem.IsChecked ? "恢复提醒" : "今日暂停提醒";
        };
        menu.Items.Add(_muteItem);

        _autoStartItem = new MenuItem { Header = "开机自启", IsCheckable = true, IsChecked = _autoStart.IsEnabled() };
        _autoStartItem.Click += (_, _) =>
        {
            _autoStart.SetEnabled(_autoStartItem.IsChecked);
        };
        menu.Items.Add(_autoStartItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void OnReminder(string message)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            // H.NotifyIcon.Wpf v2.1.3: ShowNotification(title, message, NotificationIcon icon, ...)
            try
            {
                _icon?.ShowNotification("该休息啦", message, NotificationIcon.Info);
            }
            catch
            {
                // 通知失败不应影响后台运行
            }
        });
    }

    /// <summary>
    /// 刷新 tooltip 显示今日活跃时长。可由 UI 定时器或主窗口激活时调用。
    /// </summary>
    public void RefreshTooltip()
    {
        if (_icon == null) return;
        string today = DateTime.Today.ToString("yyyy-MM-dd");
        var summary = _repo.GetTodaySummary(today);
        string text = $"ScreenTime - 今日活跃 {FormatDuration(summary.ActiveSeconds)}";
        _icon.ToolTipText = text;
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        long minutes = seconds / 60;
        if (minutes < 60) return $"{minutes}m";
        long hours = minutes / 60;
        long mins = minutes % 60;
        return $"{hours}h {mins}m";
    }

    /// <summary>
    /// 生成 32x32 蓝底白字 "S" 图标,转为 ImageSource 供 TaskbarIcon 使用。
    /// 注意:本文件不导入 System.Windows.Media / System.Windows.Media.Imaging,
    /// 因为它们会与 System.Drawing 的 Color/FontStyle/Brushes 冲突。这里使用完全限定名。
    /// </summary>
    private static System.Windows.Media.ImageSource GenerateIconSource()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var bgBrush = new SolidBrush(Color.SteelBlue);
            g.FillRectangle(bgBrush, 0, 0, 32, 32);
            using var font = new Font("Segoe UI", 18, System.Drawing.FontStyle.Bold);
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("S", font, Brushes.White, new RectangleF(0, 0, 32, 32), sf);
        }
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            // 释放原生图标句柄
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reminder.ReminderTriggered -= OnReminder;
        _icon?.Dispose();
        _icon = null;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
