# ADR-0009: Explicit agent events via named pipe

状态：已采纳（2026-09-23）。

每个 Terminal Shell 启动时获得 `CMUX_WORKSPACE_ID`、`CMUX_PANE_ID`、`CMUX_SESSION_ID` 和 `CMUX_PIPE_NAME`。`cmux notify <working|waiting|completed|error>` CLI 从环境变量读取路由信息，通过当前 UI 进程的本地命名管道发送一条 JSON 事件。应用验证 Workspace/Pane/Session 对应关系后更新运行状态。

CLI 与传输结构不引用 WPF；UI 关闭时管道消失，CLI 返回明确错误。M5 Session Daemon 可接管同一事件协议，不需依赖终端输出解析。
