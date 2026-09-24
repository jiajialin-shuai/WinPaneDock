# ADR-0002: Terminal connection lifecycle

状态：已采纳（2026-09-23）。

M1.3 继续使用官方 WPF `TerminalControl` 承载一个可替换的 `ConptyConnection`。创建 Connection 不启动进程；将它赋给 `TerminalControl.Connection` 才启动。关闭时先将控件的 Connection 设为 null 以断开事件，再关闭 ConPTY 和 Shell；重启时新建 Connection 并重新赋值。Resize 仍交给控件和 `ConptyConnection.Resize`，不自建终端渲染或 PTY。

原因：`TerminalContainer.Connection` setter 已实现退订旧连接、重置终端、启动新连接；复用控件可用最小改动完成创建、关闭和重启，并保留 M0 输入链路。若连续 50 次循环显示 native Hwnd 控件存在泄漏，再评估控件重建。
