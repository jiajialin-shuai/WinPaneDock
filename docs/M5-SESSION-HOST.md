# M5 SessionHost

状态：2026-09-23 完成 M5.1–M5.4 和 ping 连续性 Gate。

`Cmux.Terminal.ConptyProcess` 持有原 ConPTY/OpenConsole 代码，不引用 WPF。`Cmux.SessionHost.exe` 独立持有会话，并在当前用户的命名管道上处理 create、list、input、resize、close、attach。WPF `ConptyConnection` 实现原有 ITerminalConnection，转发输入和输出。关闭 GUI 时 Detach，显式关闭 Pane 时 Close。布局中的 SessionId 用于下次启动重连。

验证：

```powershell
dotnet build spikes/M0.Terminal.Wpf/M0.Terminal.Wpf.csproj -c Release
pwsh -NoProfile -File scripts/M5.SessionHost-Smoke.ps1
pwsh -NoProfile -File scripts/M5.App-Reattach-Smoke.ps1
```

本机结果：构建 0 警告、0 错误；独立宿主断开客户端后保持同一 Shell PID，重连读到断开期间两个标记；应用运行 `ping -t localhost` 后关闭 30 秒，Shell PID 不变，输出缓存从 241 增至 931 字符，重新打开应用后仍是同一 SessionId/PID。M1 生命周期 50 次、M2 Tab/Pane、M3 三 Shell 恢复、M4 agent event 回归均通过。

已知限制：每个 Session 缓存最多 100 万字符，长时间运行后重连只重放尾部输出，无法保证完整恢复复杂 TUI 屏幕。SessionHost 是当前用户单实例，目前没有宿主崩溃自动恢复；重新启动宿主会重新创建 Shell。运行中的宿主锁住发布目录中的 DLL，更新该目录前需先退出宿主。当前用户事件管道支持应用重开后继续接收 `cmux notify`，同时运行多个 GUI 实例时事件接收顺序尚未定义。
