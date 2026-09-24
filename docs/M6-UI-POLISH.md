# M6 UI polish — 2026-09-24

针对用户截图：输入框统一为 7px 圆角，与按钮一致；顶栏按钮统一为 34px 高。主操作保留 Profile、+ Group、Split 和 Close，较少使用的启动、重启、强制结束、Workspace 管理及配置编辑收进右上角设置面板。侧栏增加红点和 CMUX 标识，图标改为几何终端提示符，标题栏及 MSIX 图块共用同一套素材。

右上角支持 Night、Day、Gray 三种主题，选择会写入 `%LOCALAPPDATA%\cmux\ui-theme.txt`。主题作用于应用外框和终端背景、前景、ANSI 色表；终端字号和字体仍按 `terminal-settings.json` 设置。默认使用系统自带 Segoe UI；桌面包无需联网获取字体。WPF 列表和嵌入终端的滚动条使用圆角 Thumb，保留滚动轨道的分页点击。

验证：Release 编译零警告；Tab、Pane、Palette smoke 退出码均为 0；隔离布局的真实窗口中，首次启动产生一个 PowerShell 7，+ Group 后再 Split Right 产生两个独立 PowerShell 7；Day 主题通过 UI Automation 切换并写入配置，窗口保持运行。隔离测试创建的 Shell 均已显式关闭。

限制：终端内部滚动条需要在控件加载后显式设置 WPF 样式；UI Automation 测得其布局宽度仍为 17px，圆角 Thumb 样式已设置，但窄轨道效果尚未目测验收。未改动终端渲染器。0.1.6.0 开发包已在本机安装并启动；发布状态见 `M10-RELIABILITY.md`。
