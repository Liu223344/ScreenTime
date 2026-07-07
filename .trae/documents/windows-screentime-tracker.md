# Windows 屏幕使用时间软件 - 实现计划

## Summary

为 Windows 打造一个**轻量级、可视化**的屏幕使用时间追踪工具,后台静默运行,实时统计电脑活跃/闲置时长、前台应用使用时长,通过图表展示每日/每周使用情况,并支持休息提醒。技术栈采用 **C# WPF (.NET 8)**,数据存储使用 **SQLite**。

## Current State Analysis

- 工作区现状:仅有一个 [README.md](file:///workspace/README.md),内容为 `# ScreenTime`,即项目名占位。
- 这是**全新项目**,无任何既有代码、依赖或约定需要兼容。
- 目标平台:Windows 10/11 x64。
- 关键约束:轻量(内存占用 <50MB,启动 <1s,安装包/单文件 <30MB)、低打扰(后台静默)、可视化(图表)。

## Assumptions & Decisions

| 决策项 | 选择 | 理由 |
|---|---|---|
| 技术栈 | C# + WPF (.NET 8) | 原生 Windows、二进制小、Win32 API 调用方便、托盘应用成熟 |
| 数据存储 | SQLite (Microsoft.Data.Sqlite) | 单文件、无服务、聚合查询友好、轻量 |
| 图表库 | OxyPlot.Wpf | 轻量、纯 .NET、无需 WebView,比 LiveCharts 体积小 |
| 托盘图标 | H.NotifyIcon.Wpf | 现代 WPF 托盘库,替代过时的 WinForms NotifyIcon |
| 发布方式 | 单文件自包含 (`PublishSingleFile` + `SelfContained`) | 用户无需装 .NET 运行时,双击即用 |
| 轮询频率 | 每 3 秒采样一次 | 平衡精度与 CPU 占用 |
| 闲置阈值 | 默认 60 秒无输入视为闲置 | 可在设置中调整 |
| 数据保留 | 默认 90 天,可配置 | 避免数据库无限增长 |

## Proposed Changes

### 1. 项目骨架

创建 .NET 8 WPF 解决方案:

```
/workspace
├── ScreenTime.sln
├── src/ScreenTime/
│   ├── ScreenTime.csproj          # WPF + 单文件发布配置
│   ├── App.xaml / App.xaml.cs     # 入口、单实例检查、托盘初始化
│   ├── appsettings.json           # 可配置项(闲置阈值、提醒间隔、保留天数)
│   └── Assets/
│       └── tray-icon.ico          # 托盘图标(占位用代码生成或内嵌资源)
```

**ScreenTime.csproj 关键配置**:
- `TargetFramework=net8.0-windows`
- `UseWPF=true`
- `PublishSingleFile=true`、`SelfContained=true`、`RuntimeIdentifier=win-x64`
- `IncludeNativeLibrariesForSelfExtract=true`(把 SQLite 原生库打进单文件)
- NuGet 依赖:`Microsoft.Data.Sqlite`、`OxyPlot.Wpf`、`H.NotifyIcon.Wpf`、`Microsoft.Extensions.Hosting`(轻量 DI/配置)

### 2. Win32 互操作层

`src/ScreenTime/Native/Win32/NativeMethods.cs` — 集中所有 P/Invoke 声明:

- `GetLastInputInfo(ref LASTINPUTINFO)` + `GetTickCount64()` → 计算空闲时长(闲置检测)
- `GetForegroundWindow()` → 当前前台窗口句柄
- `GetWindowThreadProcessId(IntPtr, out uint)` → 由窗口句柄拿 PID
- `GetWindowText(IntPtr, StringBuilder, int)` → 拿窗口标题
- `SystemParametersInfo` 用于查询屏幕尺寸等(可选)
- `Microsoft.Win32.SystemEvents.SessionSwitch` 事件订阅锁屏/解锁(`SessionSwitchReason.SessionLock`/`SessionUnlock`)

### 3. 数据模型与仓储层

`src/ScreenTime/Data/DatabaseInitializer.cs` — 启动时建表:

```sql
CREATE TABLE IF NOT EXISTS app_usage (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  date          TEXT NOT NULL,            -- YYYY-MM-DD(本地时区)
  process_name  TEXT NOT NULL,
  window_title  TEXT,
  duration_sec  INTEGER NOT NULL,
  started_at    TEXT NOT NULL,            -- ISO 8601
  ended_at      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_app_usage_date ON app_usage(date);

CREATE TABLE IF NOT EXISTS daily_summary (
  date           TEXT PRIMARY KEY,        -- YYYY-MM-DD
  active_sec     INTEGER NOT NULL DEFAULT 0,
  idle_sec       INTEGER NOT NULL DEFAULT 0,
  locked_sec     INTEGER NOT NULL DEFAULT 0
);
```

`src/ScreenTime/Data/UsageRepository.cs` — SQLite CRUD + 聚合查询:
- `UpsertDailySummary(date, activeDelta, idleDelta, lockedDelta)`
- `AddAppUsage(date, processName, title, duration, startedAt, endedAt)`
- `GetTodaySummary()`、`GetHourlyActive(date)`、`GetTopApps(date, limit)`、`GetDailyActiveRange(from, to)`
- `PurgeOlderThan(days)` — 定期清理

数据库文件位置:`%LOCALAPPDATA%\ScreenTime\screentime.db`(随用户隔离,无需管理员权限)。

### 4. 追踪服务(核心后台逻辑)

`src/ScreenTime/Services/TrackingService.cs` — `IHostedService` 实现,启动后:
- 每 3 秒 `Tick` 一次(用 `System.Threading.Timer`,不要 `DispatcherTimer`,避免阻塞 UI 线程)
- 每个 Tick:
  1. 通过 `IdleDetectionService` 拿空闲秒数
     - 若 `< IdleThreshold` → 本段时间计入 `active`,记录前台 App
     - 若 `>= IdleThreshold` → 计入 `idle`,不记录 App
  2. 通过 `ForegroundAppService` 拿当前前台进程名 + 窗口标题
  3. 累加到一个内存中的"当前会话"(`CurrentSession`),仅当切换 App 或状态从 active→idle/idle→active/锁定时,才 flush 一次到 SQLite(减少写盘)
  4. flush 时调用 `UsageRepository.AddAppUsage` + `UpsertDailySummary`
- 锁屏事件处理:
  - `SessionLock`:flush 当前会话,开始计 `locked` 时长
  - `SessionUnlock`:结束 locked 时段,重新开始 active/idle 检测
- 应用退出时(`App.OnExit`):强制 flush

`src/ScreenTime/Services/IdleDetectionService.cs` — 封装 `GetLastInputInfo`,返回 `TimeSpan`。
`src/ScreenTime/Services/ForegroundAppService.cs` — 封装 `GetForegroundWindow` → PID → `Process.GetProcessById` 拿进程名(注意兜底:系统进程、访问被拒时返回 "Unknown")。
`src/ScreenTime/Services/SessionService.cs` — 订阅 `SystemEvents.SessionSwitch`,转发锁屏/解锁/注销事件给 `TrackingService`。

### 5. 提醒服务

`src/ScreenTime/Services/ReminderService.cs` — 另一个 `IHostedService`:
- 每 N 分钟(默认 45 分钟,可配置)触发一次托盘气泡通知 + 可选声音
- 使用 H.NotifyIcon.Wpf 的 `TaskbarIcon.ShowNotification`(无弹窗打扰)
- 提醒文案随机化(避免疲劳)

### 6. 托盘 UI 层

`src/ScreenTime/Tray/TrayIconManager.cs`:
- 程序启动时只显示托盘图标,主窗口默认隐藏
- 双击托盘 → 显示主窗口
- 右键菜单:`显示` / `今日暂停提醒` / `开机自启` / `退出`
- 开机自启:写 `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`(无需管理员权限)
- 托盘图标 tooltip 显示今日累计活跃时长,如 "今日 4h 23m"

### 7. 主窗口可视化

`src/ScreenTime/Views/MainWindow.xaml` + `MainWindowViewModel.cs`(MVVM,不引入重型框架,手写 `INotifyPropertyChanged` + `RelayCommand`):

主窗口分 3 个 Tab:

**Tab 1 - 今日**
- 大字号显示「今日活跃时长」(active_sec)
- 次要显示:闲置时长、锁屏时长、活跃占比百分比
- 24 小时柱状图(OxyPlot `ColumnSeries`),X 轴 0-23 时,Y 轴活跃秒数 → 一眼看出今天哪个时段用得多
- Top 5 应用横向条形图(进程名 + 时长 + 占比百分比)

**Tab 2 - 历史**
- 最近 7 天折线图(OxyPlot `LineSeries`),展示每天活跃时长趋势
- 下方表格:每天一行(date / active / idle / locked / 最常用 App)

**Tab 3 - 设置**
- 闲置阈值(秒,滑块 30~300)
- 提醒间隔(分钟,数字框)
- 数据保留天数(默认 90)
- 开机自启(复选框)
- 「立即清理旧数据」按钮
- 「导出 CSV」按钮(可选,简单实现)

### 8. 配置与启动

`src/ScreenTime/appsettings.json`:
```json
{
  "Tracking": { "PollIntervalSec": 3, "IdleThresholdSec": 60 },
  "Reminder": { "Enabled": true, "IntervalMin": 45 },
  "Retention": { "Days": 90 }
}
```
`App.xaml.cs`:
- 用 `Microsoft.Extensions.Hosting.Host` 装配:`TrackingService`、`ReminderService` 作为 `IHostedService` 注册
- 单实例检查(`Mutex`),已运行则激活已存在的主窗口后退出
- 注册 `SystemEvents.SessionSwitch`(注意 WPF 下需在 STA 主线程或显式转线程)
- `ShutdownMode=OnExplicitShutdown`(只能从托盘退出,关窗口时只是隐藏)

### 9. README 更新

更新 [README.md](file:///workspace/README.md):项目简介、构建命令、使用说明、数据存储位置、隐私声明(所有数据仅本地存储,不上传任何服务器)。

## 文件清单(待创建)

| 文件 | 作用 |
|---|---|
| `ScreenTime.sln` | 解决方案 |
| `src/ScreenTime/ScreenTime.csproj` | 项目文件 + 发布配置 |
| `src/ScreenTime/App.xaml(.cs)` | 入口、DI 装配、单实例、会话事件 |
| `src/ScreenTime/appsettings.json` | 默认配置 |
| `src/ScreenTime/Native/Win32/NativeMethods.cs` | P/Invoke 声明 |
| `src/ScreenTime/Data/DatabaseInitializer.cs` | 建表 |
| `src/ScreenTime/Data/UsageRepository.cs` | SQLite CRUD + 聚合 |
| `src/ScreenTime/Services/TrackingService.cs` | 核心追踪 |
| `src/ScreenTime/Services/IdleDetectionService.cs` | 闲置检测 |
| `src/ScreenTime/Services/ForegroundAppService.cs` | 前台应用检测 |
| `src/ScreenTime/Services/SessionService.cs` | 锁屏/解锁事件 |
| `src/ScreenTime/Services/ReminderService.cs` | 休息提醒 |
| `src/ScreenTime/Tray/TrayIconManager.cs` | 托盘图标 + 右键菜单 + 自启 |
| `src/ScreenTime/Views/MainWindow.xaml(.cs)` | 主窗口布局 |
| `src/ScreenTime/ViewModels/MainViewModel.cs` | 数据绑定 |
| `src/ScreenTime/ViewModels/RelayCommand.cs` | 简易 ICommand |
| `src/ScreenTime/ViewModels/ViewModelBase.cs` | INotifyPropertyChanged |
| `src/ScreenTime/Assets/tray-icon.ico` | 托盘图标资源 |

## Verification Steps

实现完成后按以下顺序验证:

1. **构建**:`cd /workspace && dotnet build src/ScreenTime/ScreenTime.csproj -c Release` 必须通过。
2. **数据库初始化**:首次运行后在 `%LOCALAPPDATA%\ScreenTime\screentime.db` 生成文件,两张表存在。
3. **追踪准确性**:运行 1 分钟,操作电脑若干秒 → 今日活跃时长有累计;静置 70 秒 → 闲置时长累计,且期间不计活跃。
4. **前台 App 识别**:在不同窗口(浏览器/编辑器/资源管理器)切换各 10 秒 → Top Apps 列表正确显示对应进程名和占比。
5. **锁屏处理**:Win+L 锁屏 → 重新解锁后,锁屏期间既不计活跃也不计闲置,而是计入 locked。
6. **托盘交互**:关闭主窗口 → 程序仍在托盘运行;双击托盘 → 主窗口重新显示;右键「退出」→ 进程完全结束。
7. **图表渲染**:今日 Tab 24 小时柱状图、Top 5 应用条形图、历史 Tab 7 天折线图均正常绘制。
8. **提醒**:把间隔设为 1 分钟,1 分钟后出现托盘气泡通知。
9. **单文件发布**:`dotnet publish src/ScreenTime/ScreenTime.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` → 产出单个 exe(目标 <30MB),拷贝到干净 Win11 机器可双击运行。
10. **开机自启**:勾选设置后,注册表 `HKCU\...\Run` 出现 `ScreenTime` 项;重启登录后托盘图标自动出现。
11. **隐私**:用 Process Monitor 或网络抓包确认无任何对外网络请求(纯本地)。

## Notes

- 不引入 MVVM 框架(Prism/CommunityToolkit.Mvvm 也不引入),手写最小 MVVM,保持依赖最小。
- 不做云同步、不做账户系统、不做跨设备,聚焦"单机轻量"。
- 图表用 OxyPlot 而非 WebView2/Electron 路线,避免引入浏览器运行时。
- 进程名获取对系统进程(如 `SearchUI.exe`、`ShellExperienceHost.exe`)做白名单友好名映射(可选增强,放在 `Services/AppNameResolver.cs`,首版可跳过)。
