# ADR-0006: Explicit focus path in Core

状态：已采纳（2026-09-23）。

Core `FocusManager` 保存 Workspace → Tab → Pane → TerminalSession 的完整路径，并通过 WorkspaceManager 验证每次切换。WPF 点击、Tab 选择和快捷键都调用它，再根据路径定位要显示和聚焦的控件。UI 框架焦点事件只提供输入信号，不是状态真相。

原因：HwndHost 的原生窗口焦点与 WPF 控件焦点可能不同步。显式路径使快捷键、Workspace 切换和后续状态恢复使用同一规则。
