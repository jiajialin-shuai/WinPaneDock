# 终端与 CLI 主题

WinPaneDock 的 Night、Day、Gray 切换会更新窗口、终端默认前景/背景色和 ANSI 调色板。CLI 程序可以自行输出 24 位 RGB 颜色，因此其内部界面不一定跟随终端配色；正在运行的 TUI 也可能需要重新绘制或重启。WinPaneDock 不会改写这些程序的个人配置。

- OpenCode：在程序内使用 `/theme` 选择 `system`。这个主题会使用终端背景和 ANSI 调色板；若切换 Day 后当前界面未刷新，重新打开 OpenCode。[OpenCode 主题文档](https://opencode.ai/docs/zh-cn/themes/)
- Claude Code：在程序内使用 `/theme`，可选跟随终端的 `auto` 或使用终端调色板的 ANSI 主题。[Claude Code 命令文档](https://code.claude.com/docs/zh-CN/commands)
- Codex CLI：在程序内使用 `/theme` 选择适合浅色背景的主题。[Codex CLI 自定义文档](https://developers.openai.com/docs/cli-customization)
- Grok 等其他 TUI：如程序提供自身主题设置，请在该程序中切换；WinPaneDock 只控制终端提供的颜色。
