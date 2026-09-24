# M0 Terminal Feasibility Spike — 进度报告

更新：2026-09-23。结论：**WPF + 官方 TerminalControl + OpenConsole 路线可行；M0 Gate 九项全部通过。下一阶段可以开始 M1。**

## 技术路线

WinUI 3 的 TerminalControl 没有可直接消费的官方控件包，本地 Spike 采用路线图允许的 WPF fallback：`CI.Microsoft.Terminal.Wpf 1.25.260303002`。渲染、VT、字体由官方控件负责；`ConptyConnection` 只接管句柄、管道和进程生命周期。架构决定见 `ADR-0001-m0-openconsole-host.md`。

在当前 Windows 10 19045，系统 `CreatePseudoConsole` 启动 inbox `conhost.exe` 时，原探针未观察到鼠标模式。切换到 Windows Terminal 安装的 `OpenConsole.exe` 后，直接写入的 DECSET 探针及原始 VT 输入探针均得到预期结果。原 `m0-decset-probe.ps1` 把 `Write-Host` 放在模式切换之间，会影响结果，现改为连续写入；鼠标探针显式开启 VT input 并直接 `ReadFile`。

WPF host 在窗口激活时注册 Ctrl+V 热键，在窗口失活和关闭时注销；热键把剪贴板纯文本送入现有连接。官方 WPF 包装层未提供可直接调用的粘贴快捷键。`m0-41-selection-retry.png` 显示文本选择，右键后从剪贴板读回 `clipboard-ok`，对应 `m0-42-copy.png`。

## 构建与复现

要求：Windows x64、.NET 8 SDK、PowerShell 7、已安装 x64 Windows Terminal。构建时 `Copy-OpenConsole.ps1` 从本机 Windows Terminal 安装复制 `OpenConsole.exe` 到输出目录。

```powershell
dotnet build .\spikes\M0.Terminal.Wpf\M0.Terminal.Wpf.csproj
.\spikes\M0.Terminal.Wpf\bin\Debug\net8.0-windows\Cmux.Spike.Terminal.exe
```

指定启动命令可用 `--cmd`，例如启动 DECSET 探针：

```powershell
.\spikes\M0.Terminal.Wpf\bin\Debug\net8.0-windows\Cmux.Spike.Terminal.exe --cmd 'pwsh.exe -NoProfile -NoExit -File .\scripts\m0-decset-probe.ps1'
```

鼠标探针同理使用 `m0-mouse-probe.ps1`，随后运行 `M0.Post.ps1 -Match 'cmux M0 spike' -Action FocusOn/Click/Wheel/Drag`。截图使用 `M0.CaptureWindow.ps1`。窗口关闭可用 `.CloseMainWindow()`，然后检查该进程的子进程是否仍存活。

## Gate 结果

| Gate | 状态 | 证据 / 说明 |
|---|---|---|
| pwsh 正常运行 | ✅ | `m0-25-term-inherited.png`：从 `TERM=dumb` 环境启动后正常执行 `echo term-ok` |
| TUI 正常渲染 | ✅ | `m0-24-codex-tui.png`：Codex 交互界面正常显示 |
| TUI 鼠标点击 | ✅ | `m0-43-real-mouse-tui.png`：真实鼠标输入探针收到 `ESC[<0;…M/m`；Codex TUI 也已启动渲染 |
| 滚轮 | ✅ | `m0-47-user-wheel.log`：用户实体滚轮在终端获得前台焦点后，收到 `ESC[<64;…M` 与 `ESC[<65;…M`；失焦时只滚动终端历史，不向 TUI 上报 |
| Nerd Font | ✅ | `m0-01-startup.png`、`m0-02-colors.png`：Cascadia Code NF 与提示符图标 |
| Unicode | ✅ | `m0-02-colors.png`：中日韩文、符号与 emoji |
| Resize 无明显错乱 | ✅ | `m0-19-resize.png`：窗口改为 800×500，状态由 29×97 变为 19×69，渲染正常 |
| Ctrl+C / Ctrl+V | ✅ | `m0-30-ctrl-c-real-click.png`：Ctrl+C 中断 `ping -t`；`m0-39-ctrl-v-executed.png`：Ctrl+V 粘贴并执行 `echo clipboard-ok` |
| 关闭 Terminal 不导致 App Crash | ✅ | `m0-26-exit-no-crash.png`：`exit` 后 App 仍响应；鼠标探针实例 `CloseMainWindow()` 成功，App、OpenConsole、pwsh 均退出，无新增异常日志 |

附加结果：`m0-13-decset-direct.png` 验证模式 7；`m0-18-mouse-raw-events.png` 验证 SGR 点击、滚轮、拖动与焦点事件；`m0-02-colors.png` 包含 ANSI 256、TrueColor、Unicode；`m0-03-scrollback*.png` 记录回滚。早期 Ctrl+C 注入失败是因为终端子窗口未通过真实点击聚焦。

`m0-50-alt-active.png` 与 `m0-51-alt-restored.png` 验证 alternate screen 进入与退出，原屏内容恢复。

## 已知限制与下一步

1. Codex TUI 的鼠标点击与滚轮效果未单独取证；支持鼠标的交互式探针已验证实体点击、滚轮、拖动与焦点事件。
2. M0 构建依赖本机 Windows Terminal 安装；发布打包方案留待后续阶段。
3. 前台窗口未聚焦时滚轮进入 Scrollback；TUI 鼠标上报必须在终端获得焦点后验收。
