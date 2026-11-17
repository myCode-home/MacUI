# MacUI — Spotlight 搜索 + 前台调度 + 触顶状态栏

轻量的 Windows 桌面增强工具:单进程 WPF(.NET 8)、零第三方依赖、常驻内存约 60MB。

## 功能

| 功能 | 快捷键 / 触发 | 说明 |
|---|---|---|
| **Spotlight 搜索** | `Ctrl+Space`(可改键) | 居中毛玻璃面板,模糊搜索开始菜单应用(.lnk),↑↓ 选择、回车启动、Esc 关闭 |
| **前台调度** | `Ctrl+Tab`(Shift+Tab / ←→ 反向) | 居中低透明度深色 frost 应用条(按 Z 序=最近使用排序);鼠标悬停也可选中;**松开 Ctrl 即切换**;Esc 取消 |
| **触顶状态栏** | 鼠标顶到屏幕上缘 | 全宽毛玻璃菜单栏滑出:输入法(中/EN)+ 运行中应用图标(单击切换)+ WiFi + 音量 + 电量 + 时间 + 设置;鼠标离开 2 秒自动收回 |

托盘图标:设置 / 功能开关 / 开机自启 / 退出。

## 全局接管说明(重要)

两个快捷键均为**系统级全局接管**:

- `Ctrl+Space` 会覆盖系统中英文输入法切换(可在设置改为 `Alt+Space` 等)
- `Ctrl+Tab` 会覆盖浏览器/编辑器内的"切换标签页"(可在 config 改键)

## 构建

需 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)(Windows):

```
dotnet build src/MacUI/MacUI.csproj -c Release
dotnet run --project src/MacUI -- -c Release   # 或直接运行 bin/Release/net8.0-windows/MacUI.exe
```

仅依赖 Windows 自身组件(Win32 / Core Audio / DWM),无任何 NuGet 包。

## 配置

`%APPDATA%\MacUI\config.json`:

- `hotkey`:Spotlight 热键(默认 `Ctrl+Space`)
- `switcherHotkey`:前台调度热键(默认 `Ctrl+Tab`)
- `spotlightEnabled` / `switcherEnabled` / `statusBarEnabled`:功能开关
- `statusBarAutoHideDelay`:状态栏自动收回延迟(毫秒,默认 600)
- `theme`:`system` / `dark` / `light`

## 技术要点

- 全部系统交互走 Win32 P/Invoke 与 DWM:`RegisterHotKey` 全局热键、`SHAppBarMessage`、Core Audio(`IAudioEndpointVolume`)、DWM 缩略图、`SHGetFileInfo` 图标提取 —— 零 NuGet 依赖
- 状态栏/面板为 `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW`:点击不抢焦点、不进任务视图
- 前台调度支持鼠标悬停选中、左右方向键、Shift+Tab 三种选择方式
