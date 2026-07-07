# ScreenTime

一个为 Windows 打造的**轻量级、可视化**屏幕使用时间追踪工具。后台静默运行,实时统计电脑活跃/闲置时长、前台应用使用时长,通过图表展示每日/每周使用情况,并支持休息提醒。所有数据仅本地存储,**不上传任何服务器**。

## 功能

- **活跃/闲置/锁屏时长统计**:基于 Win32 `GetLastInputInfo` 与 `SessionSwitch` 事件,精确区分三态。
- **前台应用时长**:记录当前前台窗口对应的进程,统计每个 App 的使用时长与占比。
- **可视化图表**:
  - 今日 24 小时活跃柱状图
  - 今日 Top 5 应用横向条形图
  - 最近 7 天活跃趋势折线图
  - 历史明细表格
- **休息提醒**:每 N 分钟托盘气泡提醒,可"今日暂停"。
- **开机自启**:写入 `HKCU\...\Run`,无需管理员权限。
- **数据保留与导出**:可配置保留天数,支持导出近 30 天 CSV。
- **单文件发布**:自包含单 exe,无需安装 .NET 运行时,双击即用。

## 技术栈

- C# + WPF (.NET 8)
- SQLite(`Microsoft.Data.Sqlite`)
- OxyPlot.Wpf(图表)
- H.NotifyIcon.Wpf(托盘图标)
- 手写最小 MVVM(不引入框架)

## 构建

需要 .NET 8 SDK(Windows)。

```powershell
# 调试构建
dotnet build src\ScreenTime\ScreenTime.csproj -c Debug

# 单文件自包含发布(产出单 exe,约 25-30MB)
dotnet publish src\ScreenTime\ScreenTime.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## 使用

1. 双击发布的 `ScreenTime.exe` 启动。
2. 程序默认在系统托盘运行(蓝底白字 "S" 图标)。
3. 双击托盘图标打开主窗口查看今日数据与图表。
4. 关闭主窗口只会隐藏到托盘,程序持续在后台追踪。
5. 右键托盘图标可:显示主窗口 / 今日暂停提醒 / 开机自启 / 退出。

## 数据存储位置

- 数据库:`%LOCALAPPDATA%\ScreenTime\screentime.db`(SQLite)
- 用户设置:`%LOCALAPPDATA%\ScreenTime\settings.json`

## 隐私声明

所有数据仅本地存储在本机 SQLite 文件中,程序**不会发起任何对外网络请求**,不含任何遥测、账户或云同步功能。可通过 Process Monitor 或网络抓包验证。

## 配置

默认配置(可在主窗口「设置」Tab 修改并持久化到 `settings.json`):

| 项目 | 默认值 |
|---|---|
| 轮询间隔 | 3 秒 |
| 闲置阈值 | 60 秒 |
| 提醒间隔 | 45 分钟 |
| 数据保留 | 90 天 |

`appsettings.json` 中的配置项仅作为初始默认值的参考;运行时实际使用 `settings.json` 中的用户偏好。
