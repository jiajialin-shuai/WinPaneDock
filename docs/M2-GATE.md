# M2 Gate

2026-09-23，`--m2-gate-smoke` 在一个 Workspace / Tab 中建立四 Pane：Codex、Claude、PowerShell、Python `http.server`。四个 Shell PID 均存活，截图见 `artifacts/m2-gate-four-panes.png`；测试退出码 0、无 OpenConsole 残留。截图显示 Codex 更新提示、Claude 目录信任提示、PowerShell 提示符，以及 Dev Server 监听 127.0.0.1:18765。

限制：本次未越过 Codex 更新提示或 Claude 信任提示；鼠标与 Alt+方向键实际跨 Pane 切换尚未独立取证。Core 焦点路径和方向焦点处理通过自动验收。M0 已单独验证实体鼠标事件进入终端。
