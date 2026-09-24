# ADR-0010: Waiting notifications route by focus path

状态：已采纳（2026-09-23）。

只有显式事件使 Agent 从 Working 转为 Waiting 且对应 Pane 不在前台时，应用显示 Windows Shell 通知。通知保存 Workspace/Group/Pane/Session 路径；点击后通过 Core FocusManager 选择对应控件并激活窗口。当前已打包应用仍使用系统通知区域的 NotifyIcon balloon；Windows App SDK Toast 尚未接入，路由逻辑可沿用。
