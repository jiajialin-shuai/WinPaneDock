# ADR-0003: Workspace core separate from terminal host

状态：已采纳（2026-09-23）。

M2.1 的 Workspace 数据和操作放入独立的 `Cmux.Core` .NET 类库，不引用 WPF、Windows Terminal 控件或 ConPTY。WPF Sidebar 只调用 WorkspaceManager 并展示结果。TerminalControl 暂时仍是单实例；M2.2 引入 Tab 和会话归属时再绑定 Workspace，避免在 M2.1 中实现未要求的后台会话保存。

这保持路线图要求的 Terminal Engine 与 Workspace Core 解耦，也为未来替换 WPF Host 留出边界。
