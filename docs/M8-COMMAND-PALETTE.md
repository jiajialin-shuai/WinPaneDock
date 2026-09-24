# M8 Command Palette

状态：2026-09-23 完成。

Ctrl+Shift+P 或工具栏搜索按钮打开独立 WPF Popup。独立窗口可覆盖官方 TerminalControl 的原生 HWND；同窗口 WPF 浮层会被终端盖住。输入搜索、上下键选择、Enter 执行、Esc 关闭，也可双击执行。

命令：New Workspace、New Group with Terminal、Split Right、Split Down、Close Terminal、Close All Terminals、Rename Workspace、Open Project、Restart Terminal、Focus Next Pane，以及每个 Workspace 的 Switch Workspace。命令面板和 M9 CLI 共用 `ExecuteGuiCommand` 分发入口，再调用现有 UI 动作。

验收：`Cmux.Spike.Terminal.exe --palette-smoke` 执行 Split Right → Close Terminal，Pane 数 1→2→1，退出码 0；Ctrl+Shift+P 实际窗口截图见 `artifacts/m8-command-palette.png`。
