# ADR-0011: Move ConPTY process ownership out of WPF

状态：已采纳（2026-09-23）。

M5 将原 `ConptyConnection` 的底层 ConPTY / OpenConsole 逻辑提取为不引用 WPF 的 `Cmux.Terminal.ConptyProcess`。WPF 保留一个实现官方 ITerminalConnection 的薄 Adapter；独立 `Cmux.SessionHost.exe` 直接持有 ConptyProcess，并通过命名管道提供输入、输出、Resize 和会话查询。App 已通过该协议创建和重连会话。

这样关闭 GUI 不会调用 ConPTY 进程清理，同时 Workspace Core 和 Terminal Engine 维持独立边界。
