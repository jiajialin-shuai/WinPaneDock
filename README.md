# WinPaneDock

Windows 桌面终端工作区管理器。WPF 界面嵌入官方 Windows Terminal 控件，ConPTY 会话由独立 SessionHost 持有；项目不实现终端模拟器。当前安装包和命令仍使用开发期间的 `cmux` 名称。开发进度与尚未完成的项目见 [路线图](CMUX-Windows-ROADMAP.md)，关键实现记录位于 `docs/`。

项目目前处于开发阶段，0.1.6.0 开发版仅在作者的 Windows x64 电脑上验证过。仓库中的源码不是可直接安装的正式发布版本。

## 使用

- 首次启动自动建立 Group 并运行 PowerShell 7。顶部可选 Profile；`+ Group` 与 Split 使用当前选择启动独立终端。
- 终端标题栏的 × 或 **Close Terminal** 结束单个会话；**Close Group** 结束整组；命令面板 `Close All Terminals` 结束全部会话。关闭应用窗口只断开 GUI，Shell 留在后台。
- Workspace 的 **Root Directory** 应用后作为默认启动目录；Profile 显式设置的 `startingDirectory` 优先。右上角可切换 Night、Day、Gray 主题，并打开设置面板。
- 用户配置和布局写入 `%LOCALAPPDATA%\cmux\`。Profile 见 `profiles.json`，终端设置见 `terminal-settings.json`，主题见 `ui-theme.txt`，布局见 `workspace-state.json`。

## 开发与安装

源码构建需要 Windows x64、.NET 8 SDK、PowerShell 7，以及已安装的 x64 Windows Terminal（构建脚本从本机安装中复制 `OpenConsole.exe`）。首次构建还需要访问 NuGet。运行时默认 Profile 使用 PowerShell 7；其他 Shell 可在 `%LOCALAPPDATA%\cmux\profiles.json` 中配置。

```powershell
dotnet build spikes/M0.Terminal.Wpf/M0.Terminal.Wpf.csproj -c Release
```

构建成功后，可从 `spikes/M0.Terminal.Wpf/bin/Release/net8.0-windows/` 启动 `Cmux.Spike.Terminal.exe`。若要在本机制作和安装开发测试用 MSIX，还需要 Windows SDK 的 `makeappx.exe` 与 `signtool.exe`：

```powershell
pwsh -NoProfile -File scripts/M10.Build-Msix.ps1
pwsh -NoProfile -File scripts/M10.Sign-Dev-Msix.ps1
powershell.exe -NoProfile -File scripts/M10.Activate-Dev-Update.ps1
```

本机安装版本与验证证据以 [M10 发布状态](docs/M10-RELIABILITY.md) 为准。签名证书 `CN=cmux-dev` 仅供本地测试，不适合面向普通用户分发；安装脚本会在 GUI 或活动会话仍在运行时拒绝更新。不要为安装测试包而结束正在使用的会话。Windows 安装别名 `cmux.exe` 可用，`cmux list` 已在安装版验证。正式分发签名和完整键盘到渲染延迟验收仍待完成。

源码入口在 `spikes/M0.Terminal.Wpf/`，纯数据模型在 `src/Cmux.Core/`，ConPTY 和宿主在 `src/Cmux.Terminal/`、`src/Cmux.SessionHost/`，CLI 在 `src/Cmux.Cli/`。打包脚本在 `scripts/`，清单和图标在 `packaging/`。界面限制与验收见 [M6 界面记录](docs/M6-UI-POLISH.md)。

`artifacts/` 保存本机生成的截图、预览构建和 MSIX，不进入源码仓库；阶段文档提到的部分本机截图因此不会随仓库提供。`external/terminal/` 是单独检出的 Microsoft Windows Terminal 源码，本项目构建不依赖该目录。

本项目自有代码采用 [MIT 许可证](LICENSE)。通过 NuGet 获取的终端控件及 Windows Terminal 组件遵循各自的许可条款。
