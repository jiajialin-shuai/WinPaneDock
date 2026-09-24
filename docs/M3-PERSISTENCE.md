# M3 Session & Workspace Persistence

状态：2026-09-23，M3.1–M3.3 完成。

应用状态存放于 `%LOCALAPPDATA%\cmux\workspace-state.json`。保存 Workspace 名称、根目录、置顶顺序、活动项；每个 Group 的标题、活动 Pane；Pane 二叉树的方向和比例；每个 Terminal 叶子的 Profile、命令行、工作目录和标题。进程 ID 与终端屏幕缓冲区不保存。重新启动时，仅对保留启动命令的终端重连 SessionHost 或重建 Shell；显式关闭会清除该命令，空 Pane 不自动启动。

修改后 700ms debounce 保存；关闭应用时立即刷新。先写同目录 `.tmp`，再替换主文件；仅在旧主文件有效时更新 `.bak`。主文件无效时从 `.bak` 加载。`--layout-path <path>` 可指定隔离测试文件。

验收：`scripts/M3.Layout-Smoke.ps1` 验证快照往返、比例/命令/工作目录保存和损坏主文件后的备份恢复；`scripts/M3.App-Restore-Smoke.ps1` 用隔离文件启动程序，恢复两个 Workspace 和三个 SessionHost Shell，截图 `artifacts/m3-restored-layout.png`。构建 0 警告、0 错误。M5 起，已有 SessionId 会优先重连后台 Shell，宿主不存在时才创建新 Shell。
