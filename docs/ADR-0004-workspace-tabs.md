# ADR-0004: One terminal control per tab

状态：已采纳（2026-09-23）。

M2.2 在 Core 中给每个 Workspace 增加 Tabs 和 ActiveTabId；WPF 层为每个 Tab 持有独立的官方 `TerminalControl` 和 `ConptyConnection`。切换 Workspace / Tab 只切换显示的控件，后台连接继续运行。关闭 Tab 或 Workspace 时关闭其所有连接。

原因：官方控件的 Connection setter 在替换旧连接时会重置屏幕，无法在多个 Tab 间共享一个控件且保留终端内容。每 Tab 一控件保持输入和渲染链路不变，Workspace Core 仍不依赖具体 UI Host。
