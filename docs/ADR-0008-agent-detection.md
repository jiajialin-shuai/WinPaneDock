# ADR-0008: Detect agents from process ancestry

状态：已采纳（2026-09-23）。

M4.1 使用 Windows Toolhelp 进程快照，从每个 Terminal Shell PID 向下遍历子进程。优先按可执行文件名识别 Codex、Claude、Grok、Gemini；其次可用明确的启动命令或终端标题提示。探测结果记录来源，不解析终端输出文字。进程枚举封装在 Core AgentDetector，WPF 仅提供根 PID 和 Pane 元数据。

原因：同机可能同时运行多个 Agent，必须按 Pane 的进程祖先关系归属；终端输出包含历史文字，不能作为状态真相。
