# M7 Git / Project Context

状态：2026-09-23；M7.2 完成，M7.1 在 PowerShell 和显式 Shell 上报范围内完成。

`GitProjectContext.ReadAsync` 在后台进程调用 `git status --short --branch`，3 秒超时。Workspace 侧栏每 5 秒更新根目录 branch 和 dirty 标记；当前 Pane 状态栏显示其已上报 cwd 的 Git 状态。Git 不在 UI 线程执行。

`cmux cwd <directory>` 沿用 M4 当前用户事件管道，验证 Workspace/Pane/Session ID 后更新 Pane cwd。内置 PowerShell/PowerShell 7 启动时加载 `cmux-prompt.ps1`，在目录变化后的 prompt 自动上报；其他 Shell 可自行调用 `cmux cwd`，目前无自动 hook。

验证：

```powershell
dotnet build spikes/M0.Terminal.Wpf/M0.Terminal.Wpf.csproj -c Debug
pwsh -NoProfile -File scripts/M7.Git-Smoke.ps1
spikes/M0.Terminal.Wpf/bin/Debug/net8.0-windows/Cmux.Spike.Terminal.exe --cwd-smoke
```

本机结果：临时 Git 仓库 `main` clean → dirty 识别通过；PowerShell 7 切换到 TEMP 后 Pane cwd 更新通过；侧栏截图见 `artifacts/m7-git-sidebar.png`。
