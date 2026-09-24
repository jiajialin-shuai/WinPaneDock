# ADR-0001 — M0 使用 Windows Terminal 的 OpenConsole 主机

状态：接受用于 M0 Spike（2026-09-23）

## 背景

WPF 官方 TerminalControl 已能渲染，但系统 `CreatePseudoConsole` 在当前 Windows 10 19045 上启动 inbox `conhost.exe`。原 M0 探针无法确认私有 DECSET 与 SGR 鼠标链路。路线图允许 WinUI 3 受阻后使用 WPF fallback，同时禁止自研终端渲染和 VT 解析。

## 决定

M0 的 `ConptyConnection` 按 Windows Terminal `winconpty` 的句柄与进程启动流程，启动本机 Windows Terminal 安装中的 `OpenConsole.exe`，继续用官方 WPF TerminalControl 渲染。构建时复制本机二进制到输出目录，不提交该二进制。Shell 客户端仍通过 `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` 附着。

关键参数以官方源码为准：`PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002`，传入 server、input、output、signal 四个句柄；resize 信号为三个 `ushort`（8、列、行）。先前状态文档中的 `0x00020012` 是错误记录，会使 `UpdateProcThreadAttribute` 返回错误 24。

## 验证与限制

- `m0-13-decset-direct.png` 显示关闭自动换行后长行截断，恢复后重新折行。
- `m0-18-mouse-raw-events.png` 显示 SGR 点击、滚轮、拖动和焦点序列。
- `m0-24-codex-tui.png` 显示 Codex TUI 启动与渲染。
- 本地 Windows Terminal 安装是 M0 构建依赖；未决定发布包装和二进制再分发方式。
- Ctrl+C 和 Ctrl+V 已通过真实点击聚焦后的快捷键验收；实体滚轮在聚焦后也上报 SGR 64/65。M0 Gate 九项全部通过。

参考实现：[Windows Terminal winconpty.cpp](https://github.com/microsoft/terminal/blob/main/src/winconpty/winconpty.cpp)、[winconpty.h](https://github.com/microsoft/terminal/blob/main/src/winconpty/winconpty.h)、[DeviceHandle.cpp](https://github.com/microsoft/terminal/blob/main/src/server/DeviceHandle.cpp)。
