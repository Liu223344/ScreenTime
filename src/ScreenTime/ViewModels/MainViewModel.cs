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

        RefreshCommand = new RelayCommand(Refresh);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        PurgeNowCommand = new RelayCommand(PurgeNow);
        ExportCsvCommand = new RelayCommand(ExportCsv);

        LoadSettingsIntoFields();
        Refresh();
    }

    // ---------- 数据刷新 ----------

    /// <summary>
    /// 从数据库重新加载今日 + 历史 + 图表。可在 UI 线程调用。
    /// </summary>
    public void Refresh()
    {
        string today = DateTime.Today.ToString("yyyy-MM-dd");

        var summary = _repo.GetTodaySummary(today);
        TodayActiveSeconds = summary.ActiveSeconds;
        TodayActiveDisplay = FormatDuration(summary.ActiveSeconds);
        TodayIdleDisplay = FormatDuration(summary.IdleSeconds);
        TodayLockedDisplay = FormatDuration(summary.LockedSeconds);
        TodayActiveRatioDisplay = (summary.ActiveRatio * 100).ToString("0") + "%";

        HourlyChart = BuildHourlyChart(today);
        TopAppsChart = BuildTopAppsChart(today);

        LoadHistory();
        HistoryChart = BuildHistoryChart();
    }

    private void LoadHistory()
    {
        string to = DateTime.Today.ToString("yyyy-MM-dd");
        string from = DateTime.Today.AddDays(-6).ToString("yyyy-MM-dd");
        var rows = _repo.GetDailyHistory(from, to);
        HistoryRows.Clear();
        foreach (var r in rows) HistoryRows.Add(r);
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

    private PlotModel BuildHourlyChart(string date)
    {
        var hourly = _repo.GetHourlyActive(date);

        var model = new PlotModel { Title = "今日各小时活跃时长", TitleFontSize = 14 };
        var series = new ColumnSeries
        {
            FillColor = OxyColors.SteelBlue,
            StrokeColor = OxyColors.SteelBlue,
        };
        for (int i = 0; i < 24; i++)
        {
            // 转换为分钟显示更直观
            series.Items.Add(new ColumnItem { Value = hourly[i].ActiveSeconds / 60.0 });
        }
        model.Series.Add(series);

        var categoryAxis = new CategoryAxis
        {
            Position = AxisPosition.Bottom,
            Title = "小时",
            MajorStep = 1,
            MinorStep = 1,
            Minimum = -0.5,
            Maximum = 23.5,
        };
        for (int i = 0; i < 24; i++) categoryAxis.Labels.Add(i.ToString());
        model.Axes.Add(categoryAxis);

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = "分钟",
            Minimum = 0,
        });

        return model;
    }

    private PlotModel BuildTopAppsChart(string date)
    {
        var top = _repo.GetTopApps(date, 5);

        var model = new PlotModel { Title = "今日 Top 5 应用", TitleFontSize = 14 };
        var series = new BarSeries
        {
            FillColor = OxyColors.CadetBlue,
            StrokeColor = OxyColors.CadetBlue,
        };
        var categoryAxis = new CategoryAxis { Position = AxisPosition.Left };
        foreach (var app in top)
        {
            series.Items.Add(new BarItem { Value = app.DurationSeconds / 60.0 });
            categoryAxis.Labels.Add(AppFriendlyName(app.ProcessName));
        }
        model.Series.Add(series);
        model.Axes.Add(categoryAxis);
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = "分钟",
            Minimum = 0,
        });
        return model;
    }

    private PlotModel BuildHistoryChart()
    {
        var model = new PlotModel { Title = "最近 7 天活跃时长", TitleFontSize = 14 };
        var series = new LineSeries
        {
            Color = OxyColors.OrangeRed,
            MarkerType = MarkerType.Circle,
            MarkerSize = 5,
            MarkerFill = OxyColors.OrangeRed,
        };

        // HistoryRows 是按 date DESC 排的,折线图按时间升序更直观
        var ordered = HistoryRows.Reverse().ToList();
        var categoryAxis = new CategoryAxis { Position = AxisPosition.Bottom, Title = "日期" };
        foreach (var row in ordered)
        {
            series.Points.Add(new DataPoint(categoryAxis.Labels.Count, row.ActiveSeconds / 3600.0));
            // 用 MM-DD 作为标签
            DateTime dt = DateTime.TryParse(row.Date, out var parsed) ? parsed : DateTime.Today;
            categoryAxis.Labels.Add(dt.ToString("MM-dd"));
        }
        model.Series.Add(series);
        model.Axes.Add(categoryAxis);
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = "小时",
            Minimum = 0,
        });
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
