# M5 SessionHost

状态：2026-09-24 完成 M5.1–M5.4、ping 连续性 Gate，并加入协议 v2、lease、deadline、输出积压和隔离回归。

`Cmux.Terminal.ConptyProcess` 持有原 ConPTY/OpenConsole 代码，不引用 WPF。`Cmux.SessionHost.exe` 独立持有会话，并在当前用户的命名管道上处理 create、list、input、resize、close、attach。WPF `ConptyConnection` 实现原有 ITerminalConnection，转发输入和输出。关闭 GUI 时 Detach，显式关闭 Pane 时 Close。布局中的 SessionId 用于下次启动重连。

当前源码协议为 v2：开发/测试可用 `CMUX_INSTANCE_ID` 或 `--instance-id` 派生独立的 SessionHost、GUI 和事件管道；`identity` 握手返回 PID、可执行路径、版本、协议版本和实例 ID。每个 Session 还带随机 lease，重启后旧输入/关闭请求会被拒绝。只有 `identity` 和 `list` 允许协议 0 以便诊断，其余命令强制协议 v2 并要求 lease，避免同用户的其他进程用低版本协议绕过栅栏。客户端输入使用单消费者有序队列和 16 KiB 分片，resize 合并为最新尺寸；首行请求和首次响应有 deadline。宿主按订阅字符预算限制慢消费者，输出以最多 64 KiB 批量发送，超过预算只断开输出连接，不结束 Shell；未聚焦的 Pane 延迟 Attach，避免无效输出订阅。

重连回放会剔除终端能力查询和 OSC 4/10/11/12/17 颜色查询，避免新建的 TerminalControl 再次回答旧查询，把颜色回复注入仍在运行的 TUI。已被 TUI 回显成连续可见文本的颜色回复也会从回放中剔除。`scripts/M5.Replay-Filter-Smoke.ps1` 覆盖查询、回显残留，以及正常颜色设置和标题设置的保留。

写超时必须真正取消写。`NamedPipeProtocol.WriteLineAsync` 曾把 token 只包在 `WaitAsync` 外面，实际的 `StreamWriter.WriteLineAsync` 没有 token，deadline 到期时底层管道写仍在进行中；随后的 `writer.Dispose()` flush 会在同一个 `PipeStream` 上发起第二次写，撞上 .NET 的并发写保护并抛 `The stream is currently in use by a previous operation on the stream.`。后果是 attach 输出流被打断，GUI 读到半截帧后 `JsonException` 退出读循环，且不会重连，表现为终端永久不再刷新。现在 token 直接传给 `StreamWriter.WriteLineAsync`，`SessionHostClient` 的两处内联副本也改为复用该 helper。`scripts/M5.Output-Framing-Smoke.ps1` 用 12000 行、每行都带引号/右花括号/反斜杠的 ~336 KB 输出，刻意让消费端在前 20 帧停顿共 3 秒制造背压，逐帧校验 JSON 可解析且标记完整有序，并断言宿主日志不出现并发写异常和 attach 写 deadline。该用例已纳入 `scripts/Test.ps1` 的 `output frame alignment`。

验证：

```powershell
dotnet build spikes/M0.Terminal.Wpf/M0.Terminal.Wpf.csproj -c Release
pwsh -NoProfile -File scripts/M5.SessionHost-Smoke.ps1
pwsh -NoProfile -File scripts/M5.Output-Backpressure-Smoke.ps1
pwsh -NoProfile -File scripts/M5.Output-Framing-Smoke.ps1
pwsh -NoProfile -File scripts/M5.Ipc-Deadline-Smoke.ps1
pwsh -NoProfile -File scripts/M5.Lease-Fence-Smoke.ps1
pwsh -NoProfile -File scripts/M5.Input-Chunk-Smoke.ps1
pwsh -NoProfile -File scripts/M5.Close-Persist-Smoke.ps1
pwsh -NoProfile -File scripts/M5.App-Reattach-Smoke.ps1 -Crash -DetachedSeconds 5
pwsh -NoProfile -File scripts/M10.Reattach-Smoke.ps1
```

本机结果：构建 0 警告、0 错误；独立宿主断开客户端后保持同一 Shell PID，重连读到断开期间两个标记；应用运行 `ping -t localhost` 后关闭 30 秒，Shell PID 不变，输出缓存从 241 增至 931 字符，重新打开应用后仍是同一 SessionId/PID。M1 生命周期 50 次、M2 Tab/Pane、M3 三 Shell 恢复、M4 agent event 回归均通过。

输出流会自愈。attach 管道不是持久的：宿主写 deadline（5s）、坏帧、客户端积压溢出或宿主重启都会终结它，而 Session 本身是持久的。此前 `ReadOutputAsync` 一旦退出该 Pane 就永久停止刷新。现在读循环区分三种结局：正常收尾、管道可恢复、会话已消失；可恢复时按指数退避重连 attach（上限 6 次、250ms→4s），宿主会重放断开期间的输出，Pane 自行恢复。判为永久失败的情况是 session not found、lease 失效、身份或协议不匹配。

重连不能要整份重放。Session 的重放缓冲上限是 1,000,000 字符；重连时若照单全收，宿主要把这 1 MB 序列化成**一整行** JSON（控制字符转义后每个占 6 字节，线上膨胀到数 MB），5 秒写 deadline 必然超时，于是每次重试都必然失败、6 次预算耗尽后 Pane 永久死亡。协议因此给 `HostRequest` 加了 `IncludeReplay` 与 `ReplayLimit`：进程内重连只取尾部 256 KiB（GUI 自己已保留 512 KiB 本地尾巴），首次 attach 仍取完整重放但使用独立的 30s deadline，重连用 10s。`scripts/M10.Reattach-Smoke.ps1` 会先把重放缓冲灌到 1 MB 以上饱和、确认真的送出了超过 1,000,000 字符，再断开管道并断言重放有界、一次成功、输出恢复。`EnqueueOutputBatch` 积压溢出改为丢弃管道让读循环重连，而不是直接终止。

启动首屏会补发。`TerminalContainer` 在原生终端窗口建好之前到达的输出一律丢弃（`Connection_TerminalOutput` 里先比 `IntPtr.Zero` 再决定要不要 `TerminalSendOutput`），而新建 Pane 的控件在第一次布局前并没有这个窗口。grok、opencode 这类只画一次就静默的 TUI 因此整屏不可见，直到用户碰巧触发重绘（比如 split 改变尺寸）。`ConptyConnection` 现在保留最近 512 KiB 输出尾巴，绑定连接时若控件尚未 `IsLoaded` 就打标记，`Loaded` 之后以 `ApplicationIdle` 优先级补发一次（`terminal.output.replayed`）。绑定逻辑收敛到 `AttachControlConnection`，`Boot`、`FocusPane` 和门都用同一条路径。`scripts/M10.Replay-Smoke.ps1` 强制复现这个时序（不等任何 dispatcher 轮次就绑定），并校验补发确实发生。

坏帧仍然会发生（`terminal.output.malformed`），根因未定位：坏行以完整行到达但起点不在帧边界（`BytePositionInLine` 0/1/107），是读指针错位而非尾部截断。现在错误日志带原始行前 96 字符的转义摘要、帧计数和行长度；0.1.6.6 实测样例为 `frames=13 chars=9366 head=500\u2500\u2500...`，即读指针落进了 JSON 数字中间。配合宿主的 `session.output.metrics` 与客户端的 `terminal.output.metrics` 可直接比对批次与字符数，判断丢帧还是重帧。`scripts/M10.Reattach-Smoke.ps1` 用 `--reattach-smoke` 门主动丢弃 attach 管道，要求同一 shell 上输出恢复。

已知限制：每个 Session 缓存最多 100 万字符，长时间运行后重连只重放尾部输出，无法保证完整恢复复杂 TUI 屏幕。SessionHost 是当前用户单实例，目前没有宿主崩溃自动恢复；宿主进程死亡时客户端重连会连续失败并最终停用该 Pane，需要重开。`Boot()` 里的 `Terminal.Connection` setter 会在 UI 线程上同步 `GetAwaiter().GetResult()` 等待 Attach，Attach 变慢时界面会短暂无响应，待单独立项。
