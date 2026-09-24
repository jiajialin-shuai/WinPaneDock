# M0 进度总结（Terminal Feasibility Spike）

> 更新时间：2026-09-23 · 状态：**M0 Gate 九项全部通过**。最新结果见 `M0-REPORT.md`；下文是 2026-09-22 的调查快照。

> 下文保留 2026-09-22 的调查快照；第 3、5、7 节的“待实施/未过”记录已由 `M0-REPORT.md` 的最新实测替代。

---

## 一、目标回顾

按 `CMUX-Windows-ROADMAP.md` 执行 M0：验证 Windows Terminal 终端控件能否嵌入自建桌面应用，
并跑通 M0 Gate 的 9 项验收清单（渲染/颜色/Scrollback/鼠标 TUI/Resize/Ctrl+C+V/关窗不 Crash 等）。

规则遵守情况：一次只做一个 Milestone（仅 M0）；不自研 VT Parser/字体渲染/ConPTY；
终端引擎与 Workspace 解耦；每个 Task 有可复现验收脚本。

---

## 二、技术路线（已定案）

| 路线 | 结论 |
|---|---|
| WinUI3 + TerminalControl | ❌ 官方无可用控件包，受阻（Roadmap 允许记录证据走 Fallback） |
| **WPF + 官方 `CI.Microsoft.Terminal.Wpf 1.25.260303002` + 自写 `ITerminalConnection`** | ✅ 当前路线，控件本身工作正常 |

`ConptyConnection.cs`（388 行）负责把 ConPTY 管道接到控件，不实现任何 VT 解析，符合 Roadmap 红线。

---

## 三、Gate 9 项验收进度

| # | 验收项 | 状态 | 证据 |
|---|---|---|---|
| 1 | 启动渲染（彩色/NerdFont/行列） | ✅ | `artifacts/m0-01-startup.png`（29x97） |
| 2 | 颜色/样式 | ✅ | `m0-02-colors.png`、`m0-02-visual-top.png`（3000 行 scrollback） |
| 3 | Scrollback 回滚/回底 | ✅ | `m0-03-scrollback.png`、`m0-03-scrollback-bottom.png`（双向滚轮验过） |
| 4 | IME 中文输入 | ✅ | 意外被真实 SendKeys 验证（`myapp`→`没有app`） |
| 5 | 鼠标上报（TUI 点击/滚轮/拖动） | ❌ **未过** | `m0-04-mouse-probe-*.png`：只有选择行为，无 `ESC[<…M/m` |
| 6 | DECSET 模式透传 | ❌ **未过** | `m0-07-decset-mode7.png`（300 字符仍换行）、`m0-05-decrqm*.png`（DECRQM 无回复） |
| 7 | 窗口 Resize 行列变化 | ⏸ | 等根因修复后连带回归（Resize 链路已通但需整体重验） |
| 8 | Ctrl+C / Ctrl+V | ⏸ | 注入链路已通，待重跑取证 |
| 9 | `exit` 后 App 不崩 / 关窗无孤儿进程 | ⏸ | 待重跑取证 |

辅助成果：`M0.Post.ps1`（PostMessage 注入，不抢前台）修掉两个 bug——PS 逗号优先级导致坐标算错、
负滚轮 delta `Int32` 溢出；`M0.Foreground.ps1` 前台校验（失败 exit 2，安全中止，不把按键打进用户窗口）。

---

## 四、核心根因（本次最关键发现）

**症状**：应用明明发出了 `ESC[?1000h` 等 SGR 鼠标开启序列，控件就是不上报鼠标。

**根因链**：
1. `kernel32!CreatePseudoConsole` 拉起的是系统自带 `C:\Windows\System32\conhost.exe`（Win10 19045）。
2. 这个 inbox conhost **吞掉应用发出的私有 DECSET**（模式 7 / 1000 / 1002 / 1003 / 1004 / 1006 均不生效）。
3. 于是控件内部 `IsTrackingMouseInput()` 永远为 false → 鼠标全部退化为"选择文本"。
4. Windows Terminal 自身正常，是因为它通过内置 `winconpty.cpp` 拉起同目录的
   `OpenConsole.exe --headless`（含上游 issue #15977 修复，里程碑 Terminal v1.20）。

**证据**：
- `m0-07-decset-mode7.png`：`ESC[?7l` 后 300 字符长行仍然换行 → 模式 7 被吞。
- `m0-06-sgr-foreground.png`：前台焦点校验通过，仍只有选择、无 SGR 序列。
- DECRQM 查询（`m0-decrqm` 系列探针）无任何回复。
- 上游 `microsoft/terminal#15977`。

**排除项**：不是焦点问题（`_focused` 已确认打开，前台已校验）；
不是注入问题（`M0.Post.ps1` 消息全部投递成功 exit 0）；
`OpenConsoleProxy.dll` / `TerminalConnection.dll` 无 Conpty 导出，无法直接 P/Invoke。

---

## 五、修复方案（调研已完成，下一步实施）

在 C# 里**复刻官方 `winconpty._CreatePseudoConsole`**，把 console host 换成 `OpenConsole.exe`：

1. `NtOpenFile`（ntdll）开 `\Device\ConDrv\Server` 得 server 句柄
   （`GENERIC_ALL`、`OBJ_CASE_INSENSITIVE|OBJ_INHERIT`、share RWD，失败先
   `NtSetSystemInformation(132)` 确保 ConDrv 已加载再重试）。
2. 建 `\Reference` 子句柄（`GENERIC_READ|WRITE|SYNCHRONIZE`、`FILE_SYNCHRONOUS_IO_NONALERT`）。
3. 建 signal 管道：conhost 侧读端设 inherit，我方保留写端。
4. `CreateProcessW` 拉起：
   `"OpenConsole.exe" --headless --width W --height H --signal 0x… --server 0x…`
   （`STARTUPINFOEX` + `PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002`，
   只继承 server / input / output / signal 四个句柄，`bInheritHandles = true`，
   `STARTF_USESTDHANDLES` 指向 input/output 管道）。
5. HPCON 结构按官方 ABI 打包为 `{hSignal, hPtyReference, hConPtyProcess}` 三个 IntPtr，
   放进堆内存作为 HPCON 值。
6. **Resize**：向 signal 管道写 `ushort[3] = {8, cols, rows}`（`PTY_SIGNAL_RESIZE_WINDOW = 8`）。
7. **Close**：关 `hPtyReference`（引用归零 → OpenConsole 自然退出，避免孤儿），
   等待/必要时终止 `hConPtyProcess`，再关 signal、回收结构。
8. `OpenConsole.exe` 从
   `C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.24.11911.0_x64__8wekyb3d8bbwe\OpenConsole.exe`
   （SHA256 `B7FD936C…F8160`）复制到程序输出目录——WindowsApps 目录直接 Load/CreateProcess 会被 ACL 拒绝。

**已核对的官方源码**（已抓到正文，实现时逐行对照）：
- `src/winconpty/winconpty.cpp`：`_CreatePseudoConsole` / `_ResizePseudoConsole` / `_ClosePseudoConsoleMembers` 全文
  （signal 包格式、4 句柄继承列表、命令行格式 `--signal 0x%tx --server 0x%tx` 均已确认）。
- `src/winconpty/winconpty.h`：HPCON 结构 ABI、`PTY_SIGNAL_*` 常量（RESIZE=8）。
- `src/winconpty/device.h` + `src/server/DeviceHandle.cpp`：`CreateServerHandle`（`\Device\ConDrv\Server`、`GENERIC_ALL`、
  inherit、options=0）与 `CreateClientHandle`（`\Reference`、R/W/SYNC、`FILE_SYNCHRONOUS_IO_NONALERT`）准确参数。

**注意点**：
- 现有 `ProcThreadAttributePseudoconsole = 0x00020016` 是**拉起 shell 客户端**用的，保留；
  新增 `ProcThreadAttributeHandleList = 0x00020002` 是**拉起 OpenConsole** 用的，别混。
- 输入注入、PrintWindow 取证、前台安全校验等全部脚本不动，修复后直接重跑同一套验收。

---

## 六、当前环境快照

- Demo：`Cmux.Spike.Terminal.exe`（旧 pid 86760，改写后需杀掉重启），主窗 hwnd 1116598（3290,340 1100x720），
  终端子窗 hwnd 33232330 类 `HwndTerminalClass`，标题匹配 `cmux M0 spike`。
- 工具脚本：`scripts/M0.Post.ps1`、`M0.Foreground.ps1`、`M0.Restore.ps1`、`M0.CaptureWindow.ps1`、
  `m0-decset-probe.ps1`、`m0-mouse-probe.ps1`、`m0-decrqm.ps1`。
- 证据目录：`artifacts/`（17 张截图）。
- 已知坑：posted focus 操作曾把窗口最小化到 -32000（已用 `M0.Restore.ps1` 恢复）；
  脚本必须用 `pwsh` 跑（GBK/UTF-8 无 BOM 中文注释会让 `powershell.exe` 崩）；
  用户多屏约 6560px，前台锁会拦截 `SetForegroundWindow`，exit 2 是预期安全行为。

---

## 七、接下来的顺序（恢复工作时从这里继续）

1. 改写 `spikes/M0.Terminal.Wpf/ConptyConnection.cs`：按第五节复刻 winconpty，改拉 `OpenConsole.exe`；
   把 OpenConsole 复制进输出目录。
2. `dotnet build` → 杀旧进程重启 Demo。
3. 重跑验收链并存 `artifacts/`：DECSET 探针（模式 7 应截断）、mouse probe + Foreground + FocusOn +
   Click/Wheel/Drag（应出 `ESC[<…M/m`）、选择拖动 + 右键复制、Ctrl+C（`ping -t`）/Ctrl+V、
   Resize 行列变化、`codex`/`claude` TUI + 滚轮、`exit` 不崩、关窗无孤儿 OpenConsole。
4. 补看 `m0-02-colors.png`；在报告里补 WinUI3 受阻证据段（含 conhost DECSET 吞噬证据）。
5. 写 `docs/M0-REPORT.md`（技术路线结论/已知限制/构建说明/测试结果/Gate 9 项勾选）。
6. 按 Roadmap 第 9/10 节更新任务状态并输出标准收尾（Implemented / Files changed / Tests /
   Manual verification / Known issues / Next recommended task）。
