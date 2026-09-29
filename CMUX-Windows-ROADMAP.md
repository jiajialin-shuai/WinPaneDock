# cmux for Windows — Development Roadmap

> Product goal: **Windows Terminal 的终端质感 + cmux 的管理能力**
>
> Primary use case: 在一个 Windows 桌面应用中集中管理多个项目、多个 Terminal、多个 AI Coding Agent（Codex / Claude Code / Grok / Gemini CLI 等），同时保持成熟 Terminal 的渲染、鼠标/TUI、字体与 VT 兼容能力。

---

## 0. 产品边界

### 核心目标

1. 使用成熟 Windows Terminal 技术栈承载真正的终端内容，而不是自己实现 Terminal Emulator。
2. 提供 cmux 式 Workspace / Tab / Pane / Project 管理体验。
3. 支持 PowerShell、CMD、Git Bash、WSL 以及任意 CLI/TUI。
4. TUI 鼠标交互必须完整，包括点击、滚轮、拖动和 SGR Mouse Reporting。
5. 支持 AI Agent 状态识别：Working / Waiting / Idle / Completed / Error。
6. 应用关闭后可以恢复布局；后续阶段支持真正的后台持久 Session。
7. UI 风格以 Windows 11 / Fluent 为基准，不复制 cmux 的 macOS 外观。

### 第一阶段明确不做

- 不自己实现 VT Parser。
- 不自己实现字体渲染器。
- 不自己实现 ConPTY。
- 不做 SSH 客户端。
- 不做 IDE。
- 不做远程云同步。
- 不在 MVP 中实现插件市场。
- 不在 MVP 中实现复杂 Terminal Transcript / AI 内容理解。

---

# 1. 目标架构

```text
┌────────────────────────────────────────────────────────────┐
│                       App Shell                            │
│                  WinUI 3 / Windows 11 UI                  │
├───────────────┬────────────────────────────────────────────┤
│ Workspace Bar │               Surface                     │
│               │                                            │
│ Project A     │   ┌────────────────────────────────────┐   │
│ ├ Codex  ●    │   │                                    │   │
│ ├ Claude ◉    │   │          Terminal Pane             │   │
│ └ Server ✓    │   │                                    │   │
│               │   ├────────────────────────────────────┤   │
│ Project B     │   │          Terminal Pane             │   │
│ └ PowerShell  │   │                                    │   │
│               │   └────────────────────────────────────┘   │
├───────────────┴────────────────────────────────────────────┤
│ Working 2     Waiting 1     Idle 2     Error 0             │
└────────────────────────────────────────────────────────────┘
                           │
                           ▼
                  Terminal Host Adapter
                           │
              ┌────────────┴────────────┐
              │                         │
      Windows Terminal Core        ConPTY Connection
              │                         │
              └────────────┬────────────┘
                           ▼
        PowerShell / CMD / Git Bash / WSL / TUI / Agent
```

---

# 2. 技术栈

## 目标技术栈

- UI Shell: **WinUI 3 + Windows App SDK**
- Main language: **C#**
- Terminal layer: **Microsoft Windows Terminal TerminalControl / TerminalCore**
- Process layer: **ConPTY**
- Native bridge when required: **C++/WinRT**
- Persistence: JSON for MVP, SQLite only when data complexity requires it
- IPC: Named Pipe
- Logging: Microsoft.Extensions.Logging
- Tests: xUnit + integration smoke tests

## TerminalControl 技术门槛

不要默认 TerminalControl 已经可以像普通 NuGet 控件一样直接使用。

项目开始时必须先完成技术 Spike：

### Preferred Path

```text
WinUI 3
  ↓
Microsoft.Terminal.Control
  ↓
TerminalCore
  ↓
ConptyConnection
```

### Fallback Path

如果 WinUI 3 集成成本明显过高：

```text
WPF Shell
  ↓
Microsoft.Terminal.Wpf.TerminalControl
```

或使用基于 Windows Terminal backend 的成熟封装进行 MVP 验证。

**产品架构不能依赖具体 UI Host。**

必须定义：

```text
ITerminalHost
ITerminalSession
```

让 Terminal Engine 可以被替换，而 Workspace / Pane / Agent 层不需要重写。

---

# 3. 核心数据模型

```text
Workspace
├── id
├── name
├── rootDirectory
├── tabs[]
└── metadata

Tab
├── id
├── title
└── rootPane

PaneNode
├── type: terminal | split
├── orientation
├── ratio
├── childA
├── childB
└── terminalSessionId

TerminalSession
├── id
├── profile
├── cwd
├── command
├── processId
├── title
├── agentType
├── agentStatus
└── runtimeState

AgentStatus
├── Unknown
├── Idle
├── Working
├── Waiting
├── Completed
└── Error
```

Pane 必须使用 **Tree Model**，不要把布局保存成二维坐标。

例如：

```text
Horizontal
├── Terminal A
└── Vertical
    ├── Terminal B
    └── Terminal C
```

这样可以自然支持无限嵌套 Split。

---

# 4. Repository 建议结构

```text
src/
├── Cmux.App/
│   ├── Views/
│   ├── ViewModels/
│   ├── Controls/
│   └── Themes/
│
├── Cmux.Core/
│   ├── Workspaces/
│   ├── Layout/
│   ├── Sessions/
│   ├── Agents/
│   └── Git/
│
├── Cmux.Terminal/
│   ├── ITerminalHost.cs
│   ├── ITerminalSession.cs
│   ├── WindowsTerminal/
│   └── Conpty/
│
├── Cmux.SessionHost/
│   └── Background session daemon
│
├── Cmux.Cli/
│
└── Cmux.Tests/
```

依赖方向必须保持：

```text
App
 ↓
Core
 ↓
Interfaces

Terminal Adapter ──→ Interfaces
SessionHost      ──→ Interfaces
```

`Cmux.Core` 不允许依赖 WinUI / WPF。

---

# 5. Roadmap

# M0 — Terminal Feasibility Spike

目标：先证明“Windows Terminal 的终端质感”真的能嵌进我们的应用。

> 2026-09-23 状态：**M0 完成，Gate 九项全部通过**。M0.1–M0.4 已完成；实体鼠标点击、滚轮、拖动、Ctrl+C/V、Resize、颜色、Unicode、alternate screen、关闭清理均有证据。详见 `docs/M0-REPORT.md`。下一次工作可开始 M1。

### TASK M0.1 — Minimal Terminal Host

创建只有一个窗口、一个 Terminal 的最小 Demo。

要求能够启动：

```text
pwsh.exe
```

### TASK M0.2 — 基础输入输出

验证：

- 键盘输入
- IME
- Ctrl+C
- Ctrl+V
- Terminal resize
- Scrollback
- 文本选择
- Unicode
- Emoji
- Nerd Font

### TASK M0.3 — TUI Mouse

必须验证完整鼠标协议。

测试对象至少包含：

- 一个支持鼠标的 TUI
- Codex / Claude Code 中至少一个实际 Agent
- 滚轮
- 点击
- 拖动
- Pane 内文本选择

重点验证：

```text
SGR Mouse Reporting
VT Mouse Event
Wheel Event
Focus Event
```

### TASK M0.4 — TrueColor / ANSI

运行 ANSI / TrueColor 测试。

必须支持：

```text
24-bit color
ANSI escape
cursor movement
alternate screen
```

### M0 Gate

只有以下全部满足，才允许进入 M1：

```text
[x] pwsh 正常运行
[x] TUI 正常渲染
[x] TUI 鼠标点击正常
[x] 滚轮正常
[x] Nerd Font 正常
[x] Unicode 正常
[x] Resize 无明显错乱
[x] Ctrl+C / Ctrl+V 正常
[x] 关闭 Terminal 不导致 App Crash
```

如果 WinUI 3 + TerminalControl 连续投入后仍无法稳定满足以上条件：

**立即切换 WPF TerminalControl 路线。**

不要为了坚持框架选择而重写 Terminal Renderer。

---

# M1 — Terminal MVP

目标：把单 Terminal 变成真正可日常使用的 Terminal。

### TASK M1.1 — Profile

> 2026-09-23 状态：**已完成**。启动前可选择六种 Profile；配置存放于 `%LOCALAPPDATA%\cmux\profiles.json`，`Custom` 可编辑 `command`、`args`、`startingDirectory`。五种内置 Shell 和 Custom 已完成本机启动验证，详见 `docs/M1.1-PROFILE.md`。

支持 Profile：

```text
PowerShell
PowerShell 7
CMD
Git Bash
WSL
Custom
```

Profile 数据结构：

```json
{
  "name": "PowerShell",
  "command": "pwsh.exe",
  "args": [],
  "startingDirectory": ""
}
```

### TASK M1.2 — Terminal Settings

> 2026-09-23 状态：**部分完成（5/7）**。字体、字号、光标、配色、留白已通过 `%LOCALAPPDATA%\cmux\terminal-settings.json` 生效。当前 WPF 控件无背景透明度与 Scrollback 长度接口；原生层将历史长度固定为 9001 行。限制与验收见 `docs/M1.2-SETTINGS.md`。

支持：

- Font family
- Font size
- Cursor style
- Background opacity
- Color scheme
- Padding
- Scrollback size

### TASK M1.3 — Terminal Lifecycle

> 2026-09-23 状态：**已完成**。同一控件支持 Create / Start / Resize / Focus / Close / Kill / Restart。50 次循环（含重启与强制结束）通过，0 失败、无 OpenConsole 残留；见 `docs/M1.3-LIFECYCLE.md`。M1 Gate 的循环稳定性已通过，M1.2 仍有两项原生控件限制。

实现：

```text
Create
Start
Resize
Focus
Close
Kill
Restart
```

### M1 验收

连续创建和关闭 50 个 Terminal：

- App 不崩溃
- 无明显 Handle 泄漏
- 无僵尸 ConPTY
- Terminal resize 稳定

---

# M2 — cmux 基础管理能力

目标：开始形成真正的 cmux 体验。

### TASK M2.1 — Workspace Sidebar

> 2026-09-23 状态：**已完成**。左栏可新建、删除、重命名、切换、置顶和上下移动 Workspace；纯数据操作在独立 `Cmux.Core`，见 `docs/M2.1-WORKSPACE.md`。Workspace 与终端会话的归属在 M2.2 建立。

左栏：

```text
WORKSPACES

SuperScale
UnityProject
Server
Tools
```

功能：

- 新建
- 删除
- Rename
- 切换
- Pin
- Reorder

### TASK M2.2 — Tabs

> 2026-09-23 状态：**已完成**。每个 Workspace 可新建、选择、关闭 Tabs；每个 Tab 持有独立 TerminalControl / ConptyConnection，切换后屏幕和后台 Shell 保留。见 `docs/M2.2-TABS.md`。

> 2026-09-24 交互修正：UI 将 Tab 明确标为 Group，Split 后的终端有独立标题栏和关闭按钮；旧布局标题自动转换。Workspace Root 可粘贴并应用，默认 Profile 的新终端从该目录启动。

Workspace 内支持 Tabs。

```text
SuperScale

[ Codex ] [ Server ] [ Git ]
```

### TASK M2.3 — Split Pane

> 2026-09-23 状态：**已完成**。Pane Tree 支持 Split Right / Down、Close Pane、Focus Pane、拖动分隔条改变比例；Alt+D、Alt+Shift+D 和 Alt+方向键已绑定。三 Pane 启动、方向聚焦、调整比例、关闭清理通过，详见 `docs/M2.3-PANES.md`。

实现：

```text
Split Right
Split Down
Close Pane
Focus Pane
Resize Pane
```

核心操作：

```text
Alt + D         Split Right
Alt + Shift+D   Split Down

Alt + Arrow     Focus Pane
```

快捷键最终允许自定义。

### TASK M2.4 — Focus Model

> 2026-09-23 状态：**已完成**。Core FocusManager 显式记录 Workspace → Tab → Pane → TerminalSession 焦点路径；UI 选择、Pane 点击及方向聚焦均经它更新，不依赖 WPF 默认焦点作为状态来源。见 `docs/M2.4-FOCUS.md`。

必须建立统一 FocusManager。

不能依赖 UI Framework 默认 Focus。

```text
Workspace
  ↓
Tab
  ↓
Pane
  ↓
Terminal
```

### M2 验收

> 2026-09-23：四 Pane 同时启动 Codex、Claude、PowerShell、Python Dev Server，进程均存活，窗口截图见 `artifacts/m2-gate-four-panes.png`，测试结束无 OpenConsole 残留。Codex 显示更新提示、Claude 显示工作目录信任提示；尚未经过这些交互继续执行。方向焦点切换逻辑已在 Pane smoke 验证，实体鼠标/键盘跨 Pane 操作尚未单独取证。

一个 Workspace 中至少可以稳定运行：

```text
Codex
Claude
PowerShell
Dev Server
```

并在 Pane 间通过鼠标和键盘切换。

---

# M3 — Session & Workspace Persistence

目标：关掉应用后回来，工作台还在。

### TASK M3.1 — Layout Persistence

> 2026-09-23 状态：**已完成**。JSON 快照保存 Workspace、Tab、Pane Tree、比例、工作目录、Profile/命令和标题，见 `docs/M3-PERSISTENCE.md`。

保存：

```text
Workspace
Tab
Pane Tree
Split Ratio
cwd
Profile
Title
```

### TASK M3.2 — App Restore

> 2026-09-23 状态：**已完成**。启动时恢复 Workspace / Tab / Pane 布局，并为有启动信息的 Pane 重建 Shell。两 Workspace、三 Shell 应用级验收通过，截图见 `artifacts/m3-restored-layout.png`。

重新启动时恢复：

```text
Workspace
Tab
Pane layout
cwd
```

MVP 可以重新创建 Shell。

这一阶段 **不要求进程继续运行**。

### TASK M3.3 — Crash Safe Save

> 2026-09-23 状态：**已完成**。700ms debounce、同目录临时文件原子替换、有效旧快照备份；主文件损坏时从备份恢复。见 `docs/M3-PERSISTENCE.md`。

状态保存必须：

- debounce
- atomic write
- backup last-known-good

避免配置文件损坏导致整个 Workspace 丢失。

---

# M4 — Agent Awareness

这是产品和普通 Terminal Manager 拉开差距的关键阶段。

目标状态：

```text
● Working
◉ Waiting
○ Idle
✓ Completed
× Error
```

### TASK M4.1 — Agent Detection

> 2026-09-23 状态：**已完成**。按 Pane 的 Shell PID 遍历进程树，识别 Codex / Claude / Grok / Gemini，并记录来源；四 Pane 实测 Codex、Claude 均由 ProcessTree 正确归属，其他 Pane 未误报。详见 `docs/M4.1-AGENT-DETECTION.md`。

首先识别：

```text
codex
claude
grok
gemini
```

检测来源：

1. Process tree
2. Executable name
3. Terminal title
4. Explicit integration

禁止仅依赖输出文字猜状态。

### TASK M4.2 — Explicit Agent Events

> 2026-09-23 状态：**已完成**。终端进程获得四个 CMUX 环境变量；`cmux notify working|waiting|completed|error` 通过本机命名管道按 Workspace/Pane/Session ID 上报，见 `docs/M4.2-AGENT-EVENTS.md`。

提供 CLI：

```text
cmux notify working
cmux notify waiting
cmux notify completed
cmux notify error
```

以及环境变量：

```text
CMUX_SESSION_ID
CMUX_WORKSPACE_ID
CMUX_PANE_ID
```

Agent / Hook 可以主动上报状态。

### TASK M4.3 — Waiting Notification

> 2026-09-24 状态：**部分完成**。后台 Pane 的 Working→Waiting 显示 Windows 通知区域提醒，点击可定位原 Pane；自动验收通过。当前 MSIX 已打包，但通知仍使用 NotifyIcon balloon；正式 Windows Toast 尚未接入，见 `docs/M4.3-NOTIFICATION.md`。

当 Agent 从：

```text
Working
   ↓
Waiting
```

并且 Pane 不在前台：

Windows Toast：

```text
Claude is waiting
SuperScale / Backend
```

点击通知定位到对应 Workspace / Pane。

### TASK M4.4 — Sidebar Status

> 2026-09-23 状态：**已完成**。侧栏逐 Pane 显示 Agent/终端及状态，Workspace 按 Error > Waiting > Working > Completed > Idle 聚合；四 Pane 和 Waiting 状态截图见 `artifacts/m4.4-sidebar-status.png`、`artifacts/m4.4-waiting-status.png`。

Sidebar：

```text
SuperScale
├─ Codex       ●
├─ Claude      ◉
└─ Server      ○
```

Workspace 级状态按优先级聚合：

```text
Error
Waiting
Working
Completed
Idle
```

---

# M5 — Session Daemon

目标：UI 关闭不等于 Terminal 进程死亡。

架构：

```text
Cmux.App
   │
Named Pipe
   │
Cmux.SessionHost
   │
ConPTY
   │
Process
```

### TASK M5.1

独立 SessionHost.exe。

> 2026-09-23 状态：**已完成**。`Cmux.SessionHost.exe` 是独立进程，持有 ConPTY/OpenConsole 和 Shell；通过当前用户命名管道提供 create/list/input/resize/close/attach，最多缓存每个会话 100 万字符用于重连。独立服务断开客户端后继续运行的验收见 `docs/M5-SESSION-HOST.md`。

### TASK M5.2

App 创建 Session 时：

```text
App
 ↓
SessionHost
 ↓
ConPTY
 ↓
pwsh / codex / claude
```

> 2026-09-23 状态：**已完成**。WPF `ConptyConnection` 已改为 SessionHost 客户端；官方 TerminalControl 的 ITerminalConnection 接口保持不变。

### TASK M5.3

关闭 App：

```text
UI Dies
SessionHost Lives
Terminal Lives
```

> 2026-09-23 状态：**已完成**。GUI 关闭时先 Detach 控件连接，SessionHost 和 Shell 保留；显式 Close/Kill Pane 仍结束对应会话。

### TASK M5.4

重新打开：

```text
App
 ↓
Query SessionHost
 ↓
Reattach
```

> 2026-09-23 状态：**已完成**。布局恢复时按持久化 SessionId 查询并重连；宿主不存在时创建新 Shell。30 秒 ping 连续性验收通过，见 `docs/M5-SESSION-HOST.md`。

### M5 验收

运行：

```text
ping -t localhost
```

关闭整个 GUI。

30 秒后重新打开。

输出必须连续，没有重新启动进程。

> 2026-09-23 本机结果：**PASS**。关闭 GUI 30 秒后，`ping -t localhost` 的 Shell PID 保持不变，缓存输出由 241 增至 931 字符；重开 GUI 后同一 SessionId/PID。验收脚本 `scripts/M5.App-Reattach-Smoke.ps1`。

---

# M6 — Visual Quality

这一阶段才集中做“漂亮”。

> 2026-09-23 状态：**部分完成**。侧栏 240px 且可折叠；外围深色控件、圆角按钮、Tab 图标/标题/关闭按钮、轻量 Pane 焦点边界和简洁状态栏已实装，窗口截图见 `artifacts/m3-restored-layout.png`。Windows 10 深色标题栏已验证；Mica/圆角窗口在 Windows 11 启用但本机未验证。Tab 的独立状态、完整 cwd/branch 展示、原生菜单与动画仍待完善。

> 2026-09-24：输入框和按钮圆角/高度统一，主次操作重新排列，右上角提供设置面板和 Night/Day/Gray 切换；自制图标用于标题栏和安装包，WPF 滚动条使用圆角 Thumb。见 `docs/M6-UI-POLISH.md`。

原则：

> Terminal 内容区域尽可能接近 Windows Terminal。
> 外围管理 UI 使用 Windows 11 Fluent，而不是强行复制 macOS cmux。

### Sidebar

默认约：

```text
220–260 px
```

支持 Collapse。

### Terminal Chrome

减少无意义边框。

Pane 仅在 Focus 时显示轻量边界。

### Tab

显示：

```text
Icon
Title
cwd / branch
Agent Status
Close
```

### Fluent

使用：

- Mica
- Acrylic where appropriate
- Rounded corners
- Native context menus
- Native animations
- Windows accent color

避免：

- 大量卡片
- 厚边框
- 大面积渐变
- Web Dashboard 风格

目标是：

```text
VS Code 密度
+
Windows Terminal 终端
+
cmux 信息结构
```

---

# M7 — Git / Project Context

### TASK M7.1

Terminal cwd 改变时更新：

```text
Project name
cwd
Git branch
Git dirty
```

> 2026-09-23 状态：**部分完成**。`cmux cwd <directory>` 可把当前 Pane 的 cwd 上报到 UI；内置 PowerShell/PowerShell 7 在 prompt 自动上报，改变目录的应用级验收通过。当前 Pane 的 Git branch/dirty 由后台查询并显示在状态栏。CMD、Git Bash、WSL 和任意 Custom Shell 尚无自动 cwd hook。

### TASK M7.2

Sidebar 可展示：

```text
SuperScale
main *
```

> 2026-09-23 状态：**已完成**。Workspace 根目录每 5 秒异步读取 `git status --short --branch`，侧栏显示 branch/dirty；Git 命令不在 UI 线程执行。干净/脏工作树测试通过，见 `docs/M7-GIT-CONTEXT.md`。

不要在 UI 线程执行 Git。

---

# M8 — Command Palette

> 2026-09-23 状态：**已完成**。Ctrl+Shift+P 和工具栏按钮打开可搜索命令面板；十类命令可执行，Split/Close Pane 自动验收通过。命令面板与 M9 CLI 共用 `ExecuteGuiCommand` 分发入口；截图见 `artifacts/m8-command-palette.png`。

快捷键：

```text
Ctrl + Shift + P
```

命令包括：

```text
New Workspace
New Terminal
Split Right
Split Down
Close Pane
Rename Workspace
Open Project
Restart Terminal
Focus Next Pane
Switch Workspace
```

所有 UI 操作应该优先实现为：

```text
Command
```

再由：

```text
Keyboard
Menu
Command Palette
CLI
```

调用同一个 Command。

禁止为四种入口写四份业务逻辑。

---

# M9 — CLI / IPC

> 2026-09-23 状态：**已完成路线图列出的命令**。`cmux new`、`workspace <name>`、`split right|down`、`run <command>`、`focus <pane>`、`list` 通过当前用户 GUI 命名管道调用 M8 命令入口；`notify` 和 `cwd` 仍使用会话事件管道。真实 GUI/CLI 联动和 `run cmd.exe` Shell PID 验证通过，详见 `docs/M9-CLI-IPC.md`。

目标：

```powershell
cmux new
cmux workspace SuperScale
cmux split right
cmux run codex
cmux focus 3
cmux notify waiting
cmux list
```

使用 Named Pipe 控制正在运行的 GUI。

这将成为后续 Agent 自动控制 cmux 的基础。

---

# M10 — Reliability / Release

> 2026-09-24 状态：**部分完成**。GUI 强制结束后 SessionHost/Shell 继续运行，重新打开仍是同一 PID；Shell 自然退出后 Session 自动清理，Attach 客户端断开后输出订阅会释放。`cmux doctor` 必需项全通过；冷启动到窗口句柄 461ms，Workspace 切换 P95 73.5ms、Pane 聚焦 P95 90.1ms（含 CLI 启动），单字符 IPC 往返 P95 0.33ms。自包含 MSIX 已通过 MakeAppx 打包，并使用自签名开发证书在本机安装、启动和运行 CLI；正式发布者签名与键盘到渲染延迟验收尚未完成，见 `docs/M10-RELIABILITY.md`。

> 2026-09-24 现役版本与运行证据以 [`docs/M10-RELIABILITY.md`](docs/M10-RELIABILITY.md) 为准，源码版本号以 `Directory.Build.props` 的 `CmuxAppVersion` 为唯一来源；本文件各段引用的版本号是当时测量的历史记录，不代表当前状态。Group 与 Split 默认启动所选 Profile，首次启动默认 PowerShell 7，显式关闭清除恢复命令，Night/Day/Gray 三主题与界面修正已包含。正式签名和完整键盘到渲染延迟验收仍待完成。界面限制见 `docs/M6-UI-POLISH.md`。

> 2026-09-28 状态：**部分完成**。按 [`docs/OPTIMIZATION-REVIEW-2026-09-24.md`](docs/OPTIMIZATION-REVIEW-2026-09-24.md) 实施 O1–O12（详见该文第 7、8 节）。本轮修正并复核的可靠性缺陷：恢复期间焦点回退抛错导致 `_restoring` 卡死而布局永不落盘；延迟 Attach 绕过主题应用；关闭期间丢弃分支遗留已释放连接；`StopTransport` 关闭后误报渲染过慢；`Start()` 不等待在途准备；SessionHost 协议 0 可绕过 lease 栅栏（现仅 `identity`/`list` 放行）；`LayoutStore` 主文件缺失时无恢复提示。统一入口 `scripts/Test.ps1` 的桌面回归门已按 `-Configuration` 参数化，Debug 与 Release 均全绿。O9 第一步完成：8 套内置门迁至 `MainWindow.Smoke.cs`，`MainWindow.xaml.cs` 由 2196 行降至约 1899 行，9 个启动开关收敛为 `SmokeOptions`。源码/开发包版本升至 **0.1.6.4**，本机已注册并运行 **0.1.6.3**；0.1.6.4 尚未安装。仍待完成：安装版前后性能对比、O9 第二步、内置门 `--tab-smoke`/`--cwd-smoke`/`--palette-smoke` 的既有失败、正式分发签名与完整键盘到渲染延迟验收。

必须完成：

### Process

- orphan cleanup
- crash recovery
- graceful shutdown

### Performance

目标：

```text
App cold start       < 3 s
Workspace switch     < 150 ms subjective response
Pane focus           instant
Terminal typing      no perceptible latency
```

### Installer

优先：

```text
MSIX
```

后续：

```text
winget
```

### Diagnostics

提供：

```text
cmux doctor
```

检查：

```text
OS
runtime
Terminal engine
ConPTY
PowerShell
Git Bash
WSL
Codex
Claude
config
session daemon
```

---

# 6. MVP Cut Line

第一版真正可以发布的 MVP 到：

```text
M0 Terminal Spike
M1 Terminal
M2 Workspace / Tab / Pane
M3 Persistence
M4 Agent Awareness
M6 Visual Quality
```

M5 Session Daemon 可以放到 v0.2。

---

# 7. Version Plan

## v0.0.1 — Terminal Spike

只有一个 Terminal。

目标：

> Terminal 体验证明。

---

## v0.1 — Workspace MVP

包含：

```text
Workspace
Tabs
Split Pane
Profiles
Persistence
```

目标：

> 可以替代日常 Windows Terminal。

---

## v0.2 — Agent Workspace

加入：

```text
Codex / Claude detection
Working / Waiting / Completed
Windows notifications
Agent sidebar state
```

目标：

> 比普通 Terminal Manager 更适合 AI Coding。

---

## v0.3 — Persistent Sessions

加入：

```text
Session daemon
Detach
Reattach
Crash recovery
```

目标：

> 接近 tmux / cmux 的 Session 能力。

---

## v0.4 — Automation

加入：

```text
CLI
IPC
Hooks
Agent integrations
Command Palette
```

目标：

> 让 AI Agent 本身也能控制 cmux。

---

## v1.0

要求：

- 稳定
- 无明显输入延迟
- TUI 鼠标完整
- Session 稳定
- Workspace 恢复可靠
- Installer 完整
- 可长期替代 Windows Terminal

---

# 8. 测试矩阵

每个 Release 必须运行：

| 类型 | 测试 |
|---|---|
| Shell | PowerShell 7 |
| Shell | Windows PowerShell |
| Shell | CMD |
| Shell | Git Bash |
| Shell | WSL |
| TUI | Codex |
| TUI | Claude Code |
| TUI | Vim / Neovim |
| TUI | lazygit |
| Rendering | ANSI 256 |
| Rendering | TrueColor |
| Rendering | Unicode |
| Rendering | Nerd Font |
| Input | IME |
| Input | Mouse |
| Input | Wheel |
| Input | Clipboard |
| Layout | Split |
| Layout | Resize |
| Lifecycle | Close |
| Lifecycle | Restore |

---

# 9. Agent 开发规则

每个开发 Agent 必须遵循：

1. 先读取 `ROADMAP.md`。
2. 每次只处理一个 Milestone 或明确 Task。
3. 不提前实现后续阶段功能。
4. Terminal Engine 与 Workspace Core 必须解耦。
5. 不自己实现 VT Renderer / PTY。
6. 不为了 UI 效果破坏 Terminal 输入链路。
7. 每个 Task 必须包含可复现验收方式。
8. 修改架构前必须更新 Architecture Decision Record。
9. 完成任务后运行相关测试。
10. 在 Roadmap 中更新任务状态。

---

# 10. Agent 每次工作的标准输出

Agent 完成工作后必须输出：

```text
Implemented
- ...

Files changed
- ...

Tests
- ...

Manual verification
- ...

Known issues
- ...

Next recommended task
- ...
```

禁止只说：

```text
Done
```

---

# 11. 第一条 Agent Prompt

以下是 2026-09-22 启动 M0 时的历史提示模板，并非当前开发范围限制。现役状态以本路线图各阶段记录和 `README.md` 为准。

```text
阅读仓库中的 ROADMAP.md。

当前只执行 M0 — Terminal Feasibility Spike，不进入 M1 或后续阶段。

目标：
验证 Windows Terminal 的 TerminalControl / TerminalCore 是否能稳定嵌入我们的 Windows 桌面应用，并达到接近 Windows Terminal 的真实使用体验。

优先技术路线：
WinUI 3 + Windows App SDK + Windows Terminal TerminalControl + ConPTY。

但不要假设 TerminalControl 是一个可以直接安装的普通控件。先研究当前 Windows Terminal 源码中的 TerminalControl、TerminalCore、TerminalConnection、ConptyConnection 的依赖和构建方式。

如果 WinUI 3 路线存在明显阻塞，记录原因和证据，再验证官方 WpfTerminalControl 路线。不要自行编写 Terminal Emulator。

本阶段必须验证：
- pwsh 启动
- 键盘输入
- Ctrl+C
- Ctrl+V
- Resize
- Scrollback
- Unicode
- Nerd Font
- 24-bit TrueColor
- Alternate Screen
- TUI Mouse Reporting
- 鼠标点击
- 滚轮
- 文本选择
- 至少一个 AI Agent TUI

输出：
1. 可运行 Demo
2. 技术路线结论
3. 已知限制
4. 构建说明
5. 测试结果
6. 是否满足 M0 Gate

只有 M0 Gate 全部通过，才允许建议开始 M1。
```

---

# 12. 最重要的架构原则

整个项目只坚持一句话：

> **我们开发的是 Terminal Workspace Manager，不是 Terminal Emulator。**

任何时候如果 Agent 开始自己处理：

```text
glyph rendering
VT parsing
cursor rendering
mouse protocol encoding
ConPTY implementation
```

应该先停止并确认是否已经偏离架构。

我们应该把研发资源集中在：

```text
Workspace
Pane
Session
Agent
Automation
UX
```

这才是 cmux for Windows 真正有价值的部分。
