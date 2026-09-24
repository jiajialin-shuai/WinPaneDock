# M9 CLI / IPC

状态：2026-09-23 完成路线图列出的命令。

`GuiCommandServer` 在当前用户命名管道接收单行 JSON，切回 WPF Dispatcher 执行 `ExecuteGuiCommand`，并返回结果。`cmux.exe` 支持：

```powershell
cmux new
cmux workspace SuperScale
cmux split right
cmux split down
cmux run codex
cmux focus 3
cmux list
cmux notify waiting
cmux cwd E:\project
```

`workspace <name>` 优先切换已有 Workspace，否则在 CLI 当前目录创建。`focus` 使用当前 Group 内从 1 开始的 Pane 顺序。`new` 新建 Group 并自动启动当前 Profile；`run` 在当前空 Pane 启动命令，若当前 Pane 已运行终端，会新建 Group 后启动指定命令。

验证：真实 GUI 运行时依次执行 `list`、`workspace M9Test`、`split right`、`focus 2`、`list`，各命令得到正确响应；`run cmd.exe` 后 SessionHost 查询到一个存活 Shell PID，关闭应用后显式清理该测试会话。M8 命令面板 Split/Close Pane 回归通过。

当前 GUI 管道按用户命名；同时运行多个 GUI 实例时只有其中一个实例能接收命令，尚未定义实例选择。

MSIX Manifest 为 `cmux.exe` 声明 App Execution Alias；包内终端也会把程序目录加入 PATH。2026-09-24 在 0.1.6.0 安装版中通过 WindowsApps 别名执行 `cmux list`，成功读到当前 Workspace。
