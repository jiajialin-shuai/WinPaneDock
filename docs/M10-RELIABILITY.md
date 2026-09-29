# M10 Reliability / Release

状态：2026-09-29 部分完成。**当前源码版本以 `Directory.Build.props` 的 `CmuxAppVersion` 为唯一来源**，本文不重复该版本号；打包时注入清单。本机注册并运行的版本因机器而异，用 `Get-AppxPackage -Name Cmux.Windows` 查询。MSIX 由 `CN=cmux-dev` 自签名开发证书签署，仅供本机测试。正式分发签名与完整键盘到终端渲染延迟验收尚未完成。用户配置与布局位于 `%LOCALAPPDATA%\cmux\`。日志位置与排障入口见 [日志与排障](LOGGING.md)；已安装的旧版本仍使用 `logs\app.log`。

本轮源码已加入隔离实例握手、布局语义校验、显式关闭意图提交与孤立会话回收、IPC deadline/有序输入/会话 lease、输出积压预算与批量合并；未聚焦 Pane 延迟 Attach，状态扫描移到后台并按 worktree 归并 Git 查询。统一回归入口为 `scripts/Test.ps1`，源码与开发包已同步，尚未替换本机已注册版本。性能基线脚本 `scripts/M12.Performance-Baseline.ps1` 只读采样，不打开或修改会话。

## 终端引擎来源

`OpenConsole.exe` 在构建时由 `scripts/Copy-OpenConsole.ps1` 解析，来源依次为：显式路径（`-OpenConsolePath` 或 `CMUX_OPENCONSOLE_PATH`）、`packaging/.openconsole` 缓存、本机已安装的最新 Windows Terminal 包。与 `packaging/terminal-engine.json` 记录的参考组合不一致时给出警告并继续构建，实际使用的来源与 SHA-256 会写入包内 `build-metadata.txt`；需要严格可复现的发布构建时传 `-RequireReferenceBuild`。缺少 OpenConsole 时的错误信息会直接给出安装与覆盖方式。

## 会话与恢复

正常关闭 GUI 会 Detach，SessionHost 和 Shell 继续运行；显式关闭 Terminal、Group 或 Workspace 才结束对应 Shell。显式关闭还会清除布局中的启动命令，重开应用不会复活已关闭的会话。`Close All Terminals` 立即保存空终端布局，并保留 Workspace、Group 和空 Pane。强制结束 GUI 后可按持久化 SessionId 重连；Shell 自然退出后，SessionHost 延迟清理 Session 与 ConPTY 句柄。

`scripts/M5.App-Reattach-Smoke.ps1 -Crash -DetachedSeconds 5` 验证 `ping -t localhost` 的 Shell PID 不变，断开期间仍缓存输出；`scripts/M5.SessionHost-Smoke.ps1` 验证 Shell 自然退出和重连缓存。Attach 客户端断开时，SessionHost 释放对应输出订阅。每个会话最多缓存 100 万字符，长时间运行后无法保证复杂 TUI 完整恢复；宿主崩溃后没有自动恢复原 Shell。

## 安装与当前验证

`scripts/M10.Build-Msix.ps1` 发布自包含 App、SessionHost、CLI，复制 OpenConsole、清单和图标，产出 `artifacts/WinPaneDock-<version>-x64-unsigned.msix`（`<version>` 取自 `Directory.Build.props`）。`scripts/M10.Sign-Dev-Msix.ps1` 生成同版本号的 `-dev.msix`；签名验证为 Valid。安装包显示名称已改为 WinPaneDock，内部包身份与 `cmux.exe` 别名保持兼容。安装用 `powershell.exe -NoProfile -File scripts\M10.Activate-Dev-Update.ps1`；安装别名 `cmux.exe` 可用，`cmux list` 已读到运行中的工作区。升级前需先确认 GUI 与 SessionHost 的活动会话。

本机注册版本以 `Get-AppxPackage -Name Cmux.Windows` 的输出为准，其 `InstallLocation` 即 GUI 与 SessionHost 所在目录。源码与开发包不自动替换已注册版本。

激活脚本先检查是否有 GUI 或活动 Session；只在两者都不存在时停止旧 SessionHost 并更新。用户在这次更新前明确要求运行脚本并打开应用，按其要求显式关闭了当时的一条会话，确认 SessionHost 列表及布局启动命令均为零后完成安装。此验证是当时的更新证据，不表示当前会话数量恒为零。开发证书只供本地测试；其他测试机需先信任 `artifacts/cmux-dev.cer`，不能分发私钥。

此前测得单次冷启动到窗口句柄可见 461ms；100 次单字符 SessionHost IPC 往返中位数 0.17ms、P95 0.33ms、最大值 2.6ms。真实 GUI 经 CLI 切换 Workspace 40 次 P95 73.5ms，双 Pane 聚焦 40 次 P95 90.1ms。这些是此前版本的本机测量，未覆盖当前版本的完整键盘到渲染延迟。

### 输出卡顿排查记录

2026-09-24 对本机 0.1.6.1 做只读采样时，6 个活动会话中一个 Codex 会话的 `OpenConsole.exe` 累计写出约 181 MB、约 22 万次；SessionHost 累计写出约 451 MB，GUI 累计读入约 648 MB。GUI 当时约 124 个线程、1,500 个句柄。证据更符合高输出量经过 JSON/Named Pipe/终端渲染链路造成的积压，而不是子 agent 数量本身直接创建了 GUI 线程。源码已通过 64 KiB 输出合并、订阅积压字符预算、异步有序输入和后台状态扫描降低该风险；仍需在安装新包后做同机前后对比。

## 待完成

- 完整键盘输入到终端渲染的延迟量化验收。
- 正式发布者签名与分发验证。Microsoft Store 分发可由商店签名；自行分发需使用适用的受信任签名服务或 CA 证书，并将 Manifest Publisher 改为正式身份。
