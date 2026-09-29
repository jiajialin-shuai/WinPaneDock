# cmux for Windows

- 定位：Windows Terminal Workspace Manager，不实现 VT 解析、字形渲染或 ConPTY；终端引擎使用官方 WPF TerminalControl。
- 构建：`dotnet build spikes/M0.Terminal.Wpf/M0.Terminal.Wpf.csproj -c Release`。回归统一入口是 `pwsh -NoProfile -File scripts/Test.ps1 -Configuration Release`，加 `-IncludeDesktop` 才跑 WPF 恢复与重连门；改动后跑它，不要只跑单个冒烟脚本。打包与开发签名依次运行 `scripts/M10.Build-Msix.ps1`、`scripts/M10.Sign-Dev-Msix.ps1`。版本号唯一来源是 `Directory.Build.props` 的 `CmuxAppVersion`，打包时注入清单，勿在 `packaging/AppxManifest.xml` 硬编码。
- 目录：`spikes/M0.Terminal.Wpf` 是 GUI；`src/Cmux.Core` 是 Workspace/Pane 模型；`src/Cmux.Terminal` 与 `src/Cmux.SessionHost` 持有会话；`src/Cmux.Cli` 是本机 CLI；`docs/` 记录阶段验收。
- 会话规则：关闭 GUI 只 Detach；显式关闭 Terminal/Group/Workspace 才结束 Shell，并清除该终端的持久化启动信息。测试和更新时不要误关用户现有会话。
- 发布规则：本机 MSIX 使用开发自签名证书，不能作为公众发布凭证；更新前核对 GUI 与 SessionHost 的活动会话。现役事实见 `README.md`、`docs/M10-RELIABILITY.md`，历史进度见 `CMUX-Windows-ROADMAP.md`。
- 当前状态及下一步：以 `docs/M10-RELIABILITY.md` 和路线图各阶段状态为准；正式发布签名和完整键盘到渲染延迟验收仍待完成。
