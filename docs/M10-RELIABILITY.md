# M10 Reliability / Release

状态：2026-09-24 部分完成。本机当前注册并运行的版本是 **0.1.6.0**，MSIX 仍由 `CN=cmux-dev` 自签名开发证书签署；正式分发签名与完整键盘到终端渲染延迟验收尚未完成。用户配置与布局位于 `%LOCALAPPDATA%\cmux\`，应用日志写入其 `logs\app.log`。

## 会话与恢复

正常关闭 GUI 会 Detach，SessionHost 和 Shell 继续运行；显式关闭 Terminal、Group 或 Workspace 才结束对应 Shell。显式关闭还会清除布局中的启动命令，重开应用不会复活已关闭的会话。`Close All Terminals` 立即保存空终端布局，并保留 Workspace、Group 和空 Pane。强制结束 GUI 后可按持久化 SessionId 重连；Shell 自然退出后，SessionHost 延迟清理 Session 与 ConPTY 句柄。

`scripts/M5.App-Reattach-Smoke.ps1 -Crash -DetachedSeconds 5` 验证 `ping -t localhost` 的 Shell PID 不变，断开期间仍缓存输出；`scripts/M5.SessionHost-Smoke.ps1` 验证 Shell 自然退出和重连缓存。Attach 客户端断开时，SessionHost 释放对应输出订阅。每个会话最多缓存 100 万字符，长时间运行后无法保证复杂 TUI 完整恢复；宿主崩溃后没有自动恢复原 Shell。

## 安装与当前验证

`scripts/M10.Build-Msix.ps1` 发布自包含 App、SessionHost、CLI，复制 OpenConsole、清单和图标，生成 `artifacts/cmux-0.1.6.0-x64-unsigned.msix`。`scripts/M10.Sign-Dev-Msix.ps1` 生成 `artifacts/cmux-0.1.6.0-x64-dev.msix`；签名验证为 Valid。本机通过 `powershell.exe -NoProfile -File scripts\M10.Activate-Dev-Update.ps1` 安装成功，AppsFolder 启动后的 GUI 路径位于 `Cmux.Windows_0.1.6.0_x64__xtne161mbahd6`，窗口响应正常。安装别名 `cmux.exe` 可用，`cmux list` 已读到运行中的工作区。

激活脚本先检查是否有 GUI 或活动 Session；只在两者都不存在时停止旧 SessionHost 并更新。用户在这次更新前明确要求运行脚本并打开应用，按其要求显式关闭了当时的一条会话，确认 SessionHost 列表及布局启动命令均为零后完成安装。此验证是当时的更新证据，不表示当前会话数量恒为零。开发证书只供本地测试；其他测试机需先信任 `artifacts/cmux-dev.cer`，不能分发私钥。

0.1.6.0 安装版的 `cmux doctor` 在 2026-09-24 返回 0，必需项全部通过，包括配置 JSON 与 SessionHost 管道；可选程序或配置缺失时会显示 WARN。此前测得单次冷启动到窗口句柄可见 461ms；100 次单字符 SessionHost IPC 往返中位数 0.17ms、P95 0.33ms、最大值 2.6ms。真实 GUI 经 CLI 切换 Workspace 40 次 P95 73.5ms，双 Pane 聚焦 40 次 P95 90.1ms。这些是此前版本的本机测量，未覆盖当前版本的完整键盘到渲染延迟。

## 待完成

- 完整键盘输入到终端渲染的延迟量化验收。
- 正式发布者签名与分发验证。Microsoft Store 分发可由商店签名；自行分发需使用适用的受信任签名服务或 CA 证书，并将 Manifest Publisher 改为正式身份。
