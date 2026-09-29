# 日志与排障

日志目录：`%LOCALAPPDATA%\cmux\logs\`。运行 `cmux logs` 可输出绝对路径。GUI、SessionHost、CLI 分别写入 `gui-*`、`session-host-*`、`cli-*` 的 `.jsonl` 文件。每行是一条 JSON 记录，包含 UTC 时间、级别、组件、进程和线程 ID、事件名、详情和异常。GUI 的旧版 `app.log` 不再追加，新日志从首次运行新版程序时产生。

默认记录 `Info` 及以上级别。启动前设置 `CMUX_LOG_LEVEL=Debug` 可启用调试级别；可选值为 `Debug`、`Info`、`Warning`、`Error`、`Critical`。每个日志文件达到约 5 MiB 后新开一段，启动时清理超过 14 天的 JSONL 日志。写日志失败不会中断终端或 IPC。

排查时先按故障时间查找对应进程的文件，再用 `eventName`、`sessionId`、`workspaceId` 或 `groupId` 串联事件。例如 `terminal.started` 与 SessionHost 的 `session.created` 共用 sessionId；`terminal.closed` 可用 processId 对照。CLI 通信失败记录在 `cli-*`，GUI 未处理异常记录在 `gui-*`。

日志不会主动记录终端输入、输出、命令行或环境变量。异常详情及工作目录相关错误可能包含本机路径；分享日志前请检查并去除敏感信息。
