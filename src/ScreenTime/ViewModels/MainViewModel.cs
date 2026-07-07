using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using ScreenTime.Data;
using ScreenTime.Models;
using ScreenTime.Options;
using ScreenTime.Services;

namespace ScreenTime.ViewModels;

/// <summary>
/// 主窗口数据上下文。负责今日数据、3 个图表、历史表格、设置面板的数据与命令。
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly UsageRepository _repo;
    private readonly UserSettingsService _settings;
    private readonly ReminderService _reminder;
    private readonly AutoStartService _autoStart;

    // ---------- 今日数据 ----------

    private string _todayActiveDisplay = "0m";
    private string _todayIdleDisplay = "0m";
    private string _todayLockedDisplay = "0m";
    private string _todayActiveRatioDisplay = "0%";
    private long _todayActiveSeconds;

    public string TodayActiveDisplay
    {
        get => _todayActiveDisplay;
        private set => SetProperty(ref _todayActiveDisplay, value);
    }
    public string TodayIdleDisplay
    {
        get => _todayIdleDisplay;
        private set => SetProperty(ref _todayIdleDisplay, value);
    }
    public string TodayLockedDisplay
    {
        get => _todayLockedDisplay;
        private set => SetProperty(ref _todayLockedDisplay, value);
    }
    public string TodayActiveRatioDisplay
    {
        get => _todayActiveRatioDisplay;
        private set => SetProperty(ref _todayActiveRatioDisplay, value);
    }

    /// <summary>
    /// 今日活跃秒数,供托盘 tooltip 等场景读取。
    /// </summary>
    public long TodayActiveSeconds
    {
        get => _todayActiveSeconds;
        private set => SetProperty(ref _todayActiveSeconds, value);
    }

    // ---------- 图表 ----------

    private PlotModel? _hourlyChart;
    private PlotModel? _topAppsChart;
    private PlotModel? _historyChart;

    public PlotModel? HourlyChart { get => _hourlyChart; private set => SetProperty(ref _hourlyChart, value); }
    public PlotModel? TopAppsChart { get => _topAppsChart; private set => SetProperty(ref _topAppsChart, value); }
    public PlotModel? HistoryChart { get => _historyChart; private set => SetProperty(ref _historyChart, value); }

    // ---------- 历史表格 ----------

    public ObservableCollection<HistoryRow> HistoryRows { get; } = new();

    // ---------- 设置 ----------

    private int _idleThresholdSec;
    private bool _reminderEnabled;
    private int _reminderIntervalMin;
    private int _retentionDays;
    private bool _autoStartWithWindows;
    private bool _isReminderMuted;

    public int IdleThresholdSec { get => _idleThresholdSec; set => SetProperty(ref _idleThresholdSec, value); }
    public bool ReminderEnabled { get => _reminderEnabled; set => SetProperty(ref _reminderEnabled, value); }
    public int ReminderIntervalMin { get => _reminderIntervalMin; set => SetProperty(ref _reminderIntervalMin, value); }
    public int RetentionDays { get => _retentionDays; set => SetProperty(ref _retentionDays, value); }
    public bool AutoStartWithWindows
    {
        get => _autoStartWithWindows;
        set => SetProperty(ref _autoStartWithWindows, value);
    }
    public bool IsReminderMuted
    {
        get => _isReminderMuted;
        set
        {
            if (SetProperty(ref _isReminderMuted, value))
            {
                _reminder.SetMuted(value);
            }
        }
    }

    // ---------- 命令 ----------

    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand PurgeNowCommand { get; }
    public RelayCommand ExportCsvCommand { get; }

    public MainViewModel(UsageRepository repo, UserSettingsService settings,
                         ReminderService reminder, AutoStartService autoStart)
    {
        _repo = repo;
        _settings = settings;
        _reminder = reminder;
        _autoStart = autoStart;

        RefreshCommand = new RelayCommand(async () => await RefreshAsync());
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        PurgeNowCommand = new RelayCommand(PurgeNow);
        ExportCsvCommand = new RelayCommand(ExportCsv);

        LoadSettingsIntoFields();
        // 不在构造函数中触发 Refresh,改为在 MainWindow.OnLoaded 中异步调用,
        // 避免 DI 解析阶段同步执行 5+ 次 DB 查询阻塞启动。
    }

    // ---------- 数据刷新 ----------

    /// <summary>
    /// 从数据库重新加载今日 + 历史 + 图表。DB 查询在 ThreadPool 线程执行,
    /// 图表构建与属性更新切回 UI 线程,避免阻塞界面。
    /// </summary>
    public async Task RefreshAsync()
    {
        string today = DateTime.Today.ToString("yyyy-MM-dd");
        string from = DateTime.Today.AddDays(-6).ToString("yyyy-MM-dd");

        // 在后台线程执行所有 DB 查询
        var (summary, hourly, topApps, historyRows) = await Task.Run(() =>
        {
            var s = _repo.GetTodaySummary(today);
            var h = _repo.GetHourlyActive(today);
            var t = _repo.GetTopApps(today, 5);
            var r = _repo.GetDailyHistory(from, today);
            return (s, h, t, r);
        });

        // 切回 UI 线程更新绑定属性与构建图表
        TodayActiveSeconds = summary.ActiveSeconds;
        TodayActiveDisplay = FormatDuration(summary.ActiveSeconds);
        TodayIdleDisplay = FormatDuration(summary.IdleSeconds);
        TodayLockedDisplay = FormatDuration(summary.LockedSeconds);
        TodayActiveRatioDisplay = (summary.ActiveRatio * 100).ToString("0") + "%";

        HourlyChart = BuildHourlyChart(hourly);
        TopAppsChart = BuildTopAppsChart(topApps);

        HistoryRows.Clear();
        foreach (var r in historyRows) HistoryRows.Add(r);
        HistoryChart = BuildHistoryChart();
    }

    /// <summary>
    /// 同步刷新(兼容旧调用方,内部调用异步版本)。
    /// </summary>
    public void Refresh()
    {
        RefreshAsync().GetAwaiter().GetResult();
    }

    // ---------- 设置 ----------

    private void LoadSettingsIntoFields()
    {
        var s = _settings.Current;
        IdleThresholdSec = s.IdleThresholdSec;
        ReminderEnabled = s.ReminderEnabled;
        ReminderIntervalMin = s.ReminderIntervalMin;
        RetentionDays = s.RetentionDays;
        AutoStartWithWindows = _autoStart.IsEnabled();
        _isReminderMuted = _reminder.IsMuted;
        OnPropertyChanged(nameof(IsReminderMuted));
    }

    private void SaveSettings()
    {
        var updated = new UserSettings
        {
            IdleThresholdSec = Math.Clamp(IdleThresholdSec, 10, 600),
            PollIntervalSec = _settings.Current.PollIntervalSec, // 不通过 UI 改
            ReminderEnabled = ReminderEnabled,
            ReminderIntervalMin = Math.Clamp(ReminderIntervalMin, 1, 480),
            RetentionDays = Math.Clamp(RetentionDays, 7, 3650),
            AutoStartWithWindows = AutoStartWithWindows,
        };
        _settings.Save(updated);

        if (AutoStartWithWindows != _autoStart.IsEnabled())
        {
            _autoStart.SetEnabled(AutoStartWithWindows);
        }

        // 同步内存字段(Clamp 后的值)
        LoadSettingsIntoFields();
        MessageBox.Show("设置已保存", "ScreenTime", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void PurgeNow()
    {
        int days = Math.Clamp(RetentionDays, 7, 3650);
        string cutoff = DateTime.Today.AddDays(-days).ToString("yyyy-MM-dd");
        int affected = _repo.PurgeOlderThan(cutoff);
        Refresh();
        MessageBox.Show($"已清理 {affected} 条 {cutoff} 之前的记录", "ScreenTime",
                        MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportCsv()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            FileName = $"screentime_{DateTime.Today:yyyyMMdd}.csv",
        };
        if (dlg.ShowDialog() != true) return;

        string from = DateTime.Today.AddDays(-29).ToString("yyyy-MM-dd");
        string to = DateTime.Today.ToString("yyyy-MM-dd");
        var rows = _repo.GetAllAppUsage(from, to);

        using var writer = new StreamWriter(dlg.FileName, false, new System.Text.UTF8Encoding(true));
        writer.WriteLine("date,process_name,window_title,duration_sec,started_at,ended_at");
        foreach (var r in rows)
        {
            string title = (r.Title ?? "").Replace("\"", "\"\"");
            writer.WriteLine($"{r.Date},{r.ProcessName},\"{title}\",{r.Seconds},{r.StartedAt},{r.EndedAt}");
        }
        MessageBox.Show($"已导出 {rows.Count} 条记录到:\n{dlg.FileName}", "ScreenTime",
                        MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------- 图表构建 ----------
    // 配色采用 Apple 系统色:#007AFF (蓝) / #FF9500 (橙) / #AF52DE (紫) / #34C759 (绿) / #FF3B30 (红) / #5AC8FA (青)
    // 文字色:#1D1D1F (主) / #6E6E73 (次) / #A1A1A6 (三)
    private static readonly OxyColor AppleBlue = OxyColor.FromRgb(0x00, 0x7A, 0xFF);
    private static readonly OxyColor AppleOrange = OxyColor.FromRgb(0xFF, 0x95, 0x00);
    private static readonly OxyColor AppleGreen = OxyColor.FromRgb(0x34, 0xC7, 0x59);
    private static readonly OxyColor AppleRed = OxyColor.FromRgb(0xFF, 0x3B, 0x30);
    private static readonly OxyColor AppleTeal = OxyColor.FromRgb(0x5A, 0xC8, 0xFA);
    private static readonly OxyColor ApplePurple = OxyColor.FromRgb(0xAF, 0x52, 0xDE);
    private static readonly OxyColor ApplePink = OxyColor.FromRgb(0xFF, 0x2D, 0x55);
    private static readonly OxyColor AppleTextPrimary = OxyColor.FromRgb(0x1D, 0x1D, 0x1F);
    private static readonly OxyColor AppleTextSecondary = OxyColor.FromRgb(0x6E, 0x6E, 0x73);
    private static readonly OxyColor AppleTextTertiary = OxyColor.FromRgb(0xA1, 0xA1, 0xA6);
    private static readonly OxyColor AppleHairline = OxyColor.FromRgb(0xE5, 0xE5, 0xEA);
    private static readonly OxyColor AppleChartBg = OxyColors.White;

    /// <summary>
    /// 应用统一的 Apple 风格样式到 PlotModel。
    /// 注意:OxyPlot 2.1.x 中 legend 颜色属性已移到独立的 Legend 对象上,
    /// PlotModel 本身不再有 LegendTextColor/LegendBorderColor。本项目不显示图例,故无需设置。
    /// </summary>
    private static void ApplyAppleTheme(PlotModel model)
    {
        model.Background = AppleChartBg;
        model.PlotAreaBackground = AppleChartBg;
        model.TextColor = AppleTextPrimary;
        model.TitleColor = AppleTextPrimary;
        model.SubtitleColor = AppleTextSecondary;
        model.PlotAreaBorderColor = AppleHairline;
        model.PlotAreaBorderThickness = new OxyThickness(0, 0, 0, 1); // 仅底部细线
        model.IsLegendVisible = false;
    }

    private static LinearAxis MakeAppleLinearAxis(AxisPosition pos, string? title = null, string? unit = null)
    {
        var axis = new LinearAxis
        {
            Position = pos,
            Title = title,
            TitleColor = AppleTextSecondary,
            TitleFontSize = 11,
            TextColor = AppleTextTertiary,
            FontSize = 11,
            MajorGridlineStyle = pos == AxisPosition.Left ? LineStyle.Solid : LineStyle.None,
            MajorGridlineColor = AppleHairline,
            MajorGridlineThickness = 0.6,
            MinorGridlineStyle = LineStyle.None,
            TickStyle = TickStyle.None,
            AxislineStyle = pos == AxisPosition.Bottom ? LineStyle.Solid : LineStyle.None,
            AxislineColor = AppleHairline,
            AxislineThickness = 1,
            Minimum = 0,
        };
        if (!string.IsNullOrEmpty(unit))
        {
            axis.StringFormat = "0";
            axis.Title = string.IsNullOrEmpty(title) ? unit : $"{title} ({unit})";
        }
        return axis;
    }

    private static CategoryAxis MakeAppleCategoryAxis(AxisPosition pos, IEnumerable<string> labels)
    {
        var axis = new CategoryAxis
        {
            Position = pos,
            TextColor = AppleTextTertiary,
            FontSize = 11,
            TickStyle = TickStyle.None,
            MajorStep = 1,
            MinorStep = 1,
            AxislineStyle = pos == AxisPosition.Bottom ? LineStyle.Solid : LineStyle.None,
            AxislineColor = AppleHairline,
            AxislineThickness = 1,
            MajorGridlineStyle = LineStyle.None,
            MinorGridlineStyle = LineStyle.None,
            // OxyPlot 2.1.x: GapWidth 是 CategoryAxis 的属性(控制类别间距),不是 series 的。
            GapWidth = 0.2,
        };
        foreach (var lbl in labels) axis.Labels.Add(lbl);
        return axis;
    }

    private PlotModel BuildHourlyChart(List<HourlyActivePoint> hourly)
    {
        var model = new PlotModel { Title = null };
        ApplyAppleTheme(model);

        // OxyPlot 2.1.x 已移除 ColumnSeries/ColumnItem。改用 AreaSeries(带填充的折线)
        // 展示 24 小时趋势,视觉上更接近 macOS 健康 App 的活动曲线。
        var area = new AreaSeries
        {
            Color = AppleBlue,
            Fill = OxyColor.FromAColor(56, AppleBlue),
            StrokeThickness = 2,
        };
        for (int i = 0; i < 24; i++)
        {
            double minutes = hourly[i].ActiveSeconds / 60.0;
            area.Points.Add(new DataPoint(i, minutes));
            area.Points2.Add(new DataPoint(i, 0));
        }
        model.Series.Add(area);

        // X 轴:小时(数值轴,0-23,每 2 小时一个主刻度)
        var xAxis = MakeAppleLinearAxis(AxisPosition.Bottom);
        xAxis.Minimum = 0;
        xAxis.Maximum = 23;
        xAxis.MajorStep = 2;
        xAxis.MinorStep = 1;
        xAxis.StringFormat = "0";
        model.Axes.Add(xAxis);
        model.Axes.Add(MakeAppleLinearAxis(AxisPosition.Left, unit: "分钟"));

        return model;
    }

    private PlotModel BuildTopAppsChart(List<AppUsageRecord> top)
    {
        // Apple 调色板,顺序应用
        var palette = new[] { AppleBlue, AppleTeal, AppleGreen, AppleOrange, ApplePink, ApplePurple };

        var model = new PlotModel { Title = null };
        ApplyAppleTheme(model);

        // OxyPlot 2.1.x:BarSeries 为水平条形(CategoryAxis 在 Y 轴)。
        // BarWidth 控制条形粗细(类别单位),GapWidth 在 CategoryAxis 上控制类别间距。
        var series = new BarSeries
        {
            FillColor = AppleBlue,
            StrokeColor = AppleBlue,
            StrokeThickness = 0,
            BarWidth = 0.65,
        };
        // 反转使最大的在顶部
        var reversed = top.AsEnumerable().Reverse().ToList();
        var categoryAxis = MakeAppleCategoryAxis(AxisPosition.Left, Enumerable.Empty<string>());
        for (int i = 0; i < reversed.Count; i++)
        {
            var app = reversed[i];
            var color = palette[i % palette.Length];
            series.Items.Add(new BarItem
            {
                Value = app.DurationSeconds / 60.0,
                Color = color,
            });
            categoryAxis.Labels.Add(AppFriendlyName(app.ProcessName));
        }
        model.Series.Add(series);
        model.Axes.Add(categoryAxis);
        model.Axes.Add(MakeAppleLinearAxis(AxisPosition.Bottom, unit: "分钟"));

        return model;
    }

    private PlotModel BuildHistoryChart()
    {
        var model = new PlotModel { Title = null };
        ApplyAppleTheme(model);

        // OxyPlot 2.1.x:LineSeries.Smooth 已移除,改用 InterpolationAlgorithm 做平滑;
        // 7 天趋势用直线即可,足够清晰。
        var series = new LineSeries
        {
            Color = AppleBlue,
            StrokeThickness = 2.5,
            MarkerType = MarkerType.Circle,
            MarkerSize = 6,
            MarkerFill = AppleBlue,
            MarkerStroke = OxyColors.White,
            MarkerStrokeThickness = 2,
        };

        // HistoryRows 是按 date DESC 排的,折线图按时间升序更直观
        var ordered = HistoryRows.Reverse().ToList();
        var categoryAxis = MakeAppleCategoryAxis(AxisPosition.Bottom, Enumerable.Empty<string>());
        foreach (var row in ordered)
        {
            series.Points.Add(new DataPoint(categoryAxis.Labels.Count, row.ActiveSeconds / 3600.0));
            // 用 MM-DD 作为标签
            DateTime dt = DateTime.TryParse(row.Date, out var parsed) ? parsed : DateTime.Today;
            categoryAxis.Labels.Add(dt.ToString("MM-dd"));
        }

        // 在折线下方填充淡色面积,呼应 macOS 健康图表风格
        var areaSeries = new AreaSeries
        {
            Color = OxyColors.Transparent,
            Fill = OxyColor.FromAColor(40, AppleBlue),
            StrokeThickness = 0,
        };
        for (int i = 0; i < series.Points.Count; i++)
        {
            var p = series.Points[i];
            areaSeries.Points.Add(p);
            areaSeries.Points2.Add(new DataPoint(p.X, 0));
        }
        model.Series.Add(areaSeries);
        model.Series.Add(series);
        model.Axes.Add(categoryAxis);
        model.Axes.Add(MakeAppleLinearAxis(AxisPosition.Left, unit: "小时"));

        return model;
    }

    // ---------- 辅助 ----------

    private static string FormatDuration(long seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        long minutes = seconds / 60;
        if (minutes < 60) return $"{minutes}m";
        long hours = minutes / 60;
        long mins = minutes % 60;
        return $"{hours}h {mins}m";
    }

    private static string AppFriendlyName(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return "Unknown";
        // 去掉 .exe 后缀,首字母大写
        string name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return name;
    }
}
