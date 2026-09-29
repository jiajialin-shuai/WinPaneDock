# WinPaneDock 项目优化评估

审查日期：2026-09-24  
审查基线：`ad5cb7c` 加本次审查开始时的工作区修改；结论针对当前源码，不等同于已安装版本的运行表现。  
交付范围：代码审查、构建和低影响验证、优化建议；未修改业务代码，未更新安装包或操作现有终端会话。

## 1. 结论与优先顺序

项目已经具备可继续演进的基础：官方 TerminalControl 负责终端能力，独立 SessionHost 保持 Shell 生命周期，Core 保存工作区模型，CLI 通过本机管道通信。布局已有临时文件替换、有效备份和保存防抖；输出回放已有长度限制；管道使用 CurrentUserOnly；日志已有分片与保留期。这些机制应继续保留。

当前最有价值的工作是补齐故障边界，再减少界面线程的阻塞和重复工作。现阶段不建议重写终端引擎、整体迁移 UI 框架或一次性进行完整 MVVM 改造。

发现项的证据分为三类：**已复现**表示本次执行了隔离用例；**代码确认**表示相关路径和条件明确，但未运行真实故障场景；**待测量**表示优化候选，尚不能断言造成了用户可感知的性能问题。

优先级含义：P1 为近期可靠性工作；P2 为随后安排的性能与工程改进。未发现本次证据足以定为 P0 的问题。工作量为熟悉项目的一名开发者的粗估，包含对应回归验证，不是排期承诺。

| 编号 | 优先级 | 优化项 | 证据 | 预期收益 | 粗估 |
|---|---|---|---|---|---|
| O1 | P1 | 显式关闭操作及时持久化 | 代码确认 | 缩小已关闭终端在崩溃后复活的窗口 | 1–2 人日 |
| O2 | P1 | IPC 完整超时、异步执行与输入顺序 | 代码确认 | 避免宿主或客户端异常拖住 GUI/CLI | 2–4 人日 |
| O3 | P1 | 限制实时输出订阅积压 | 代码确认 | 防止慢消费者使宿主内存持续增长 | 1–3 人日 |
| O4 | P1 | 布局语义校验与可靠回退 | 已复现 | 有效备份真正覆盖结构损坏场景 | 1–2 人日 |
| O5 | P1 | 测试实例隔离与宿主身份检查 | 代码确认 | 确保测到当前源码，保护日常会话 | 2–3 人日 |
| O6 | P2 | 状态扫描移出 UI 线程并减少重复遍历 | 代码确认；收益待测量 | 多 Pane 时减少周期性停顿 | 1–2 人日 |
| O7 | P2 | Git 查询按工作树去重并限制并发 | 代码确认；收益待测量 | 减少多目录、多工作区的后台开销 | 1–2 人日 |
| O8 | P2 | 回放缓冲分块管理并说明恢复边界 | 代码确认；收益待测量 | 控制分配成本，降低恢复行为的不确定性 | 1–2 人日 |
| O9 | P2 | 分步拆分 MainWindow 与冒烟代码 | 代码确认 | 降低会话、UI、测试相互影响的修改风险 | 2–4 人日 |
| O10 | P2 | 建立统一测试入口与 Windows CI | 代码确认 | 可重复执行回归，覆盖新增故障边界 | 2–3 人日 |
| O11 | P2 | 固定构建依赖，统一版本来源 | 代码确认 | 让构建、日志和发布证据可以对应 | 1–2 人日 |
| O12 | P2 | 建立当前版本的完整性能基线 | 覆盖缺口确认 | 按数据决定后续优化投入 | 1–2 人日 |

以上项目存在重叠，不应直接把工作量相加。建议先完成 O5 的最小隔离能力，再验证 O1–O4 的故障场景；O10 的测试入口可以与这些修复同步建立。

## 2. 可靠性优先项

### O1：显式关闭操作不能只依赖 700ms 防抖

**依据：** [保存定时器](../spikes/M0.Terminal.Wpf/MainWindow.xaml.cs)、[CloseTerminal](../spikes/M0.Terminal.Wpf/MainWindow.xaml.cs)、[关闭 Pane](../spikes/M0.Terminal.Wpf/MainWindow.xaml.cs)、[关闭 Group](../spikes/M0.Terminal.Wpf/MainWindow.xaml.cs)、[删除 Workspace](../spikes/M0.Terminal.Wpf/MainWindow.xaml.cs)。

普通关闭终端会结束会话、清除内存里的启动信息，然后调用 `MarkLayoutDirty()`。关闭 Pane、Group、Workspace 也经过延迟保存。只有 `CloseAllTerminals()` 在操作末尾显式调用 `SaveLayout()`。

如果关闭已完成但 700ms 保存尚未发生，GUI 被强制结束，磁盘仍可能保留旧启动命令。下次 [RestoreShellsAsync](../spikes/M0.Terminal.Wpf/MainWindow.xaml.cs) 会使用这些命令，而 [ConptyConnection.Start](../spikes/M0.Terminal.Wpf/ConptyConnection.cs) 在旧 SessionId 不存在时创建新 Shell。这与“显式关闭不复活”的会话规则存在边界冲突。本次确认调用路径，未对用户运行中的 GUI 做崩溃实验。

**最小改进：** 为显式关闭操作设置单独的提交路径，完成后立即保存，并让保存失败对用户可见；拖动比例、焦点等普通布局变化继续防抖。需要严格覆盖“宿主已关、保存未完成”的中途崩溃时，再加入可恢复的关闭意图记录，不必先引入通用事务框架。

**相关异常路径：** [ConptyConnection.Close](../spikes/M0.Terminal.Wpf/ConptyConnection.cs) 先设 `_closed`，再发送关闭请求；[ClosePaneConnection](../spikes/M0.Terminal.Wpf/MainWindow.xaml.cs) 也先清空连接引用。关闭 RPC 失败时，界面可能已经失去重试入口，而后台 Shell 仍活着。应保留“关闭失败、可重试”的状态，并让按 SessionId 关闭具备幂等语义。

**验收：** 在隔离实例中分别关闭 Terminal、Pane、Group、Workspace，随后立即强制结束 GUI，重启不能复活已关闭会话；注入保存失败和关闭 RPC 失败，状态应可解释且可重试；正常退出 GUI 仍只 Detach。

### O2：给 IPC 请求完整期限，并让界面不等待网络式往返

**依据：** [SessionHostClient.Send/Attach](../src/Cmux.Terminal/SessionHostClient.cs)、[WriteInput/Resize](../spikes/M0.Terminal.Wpf/ConptyConnection.cs)、[EnsureHost](../spikes/M0.Terminal.Wpf/ConptyConnection.cs)、[GUI 管道](../spikes/M0.Terminal.Wpf/GuiCommandServer.cs)、[事件管道](../spikes/M0.Terminal.Wpf/AgentEventServer.cs)、[CLI 响应读取](../src/Cmux.Cli/Program.cs)。

每次输入或 resize 都会新建管道、序列化请求、同步等回复。连接阶段有超时，`ReadLine()` 响应阶段没有；`EnsureHost()` 还包含同步重试和 `Thread.Sleep(100)`。GUI 设置控件连接时直接触发 Start，因此宿主异常至少会影响启动/恢复路径；输入、resize 的具体回调线程应通过测量确认，但当前接口会阻塞其调用线程。

GUI 和 AgentEvent 服务端各以单连接串行处理请求。客户端连接后不发送换行，服务端只受应用退出取消令牌约束，会占住这一处理循环。SessionHost 虽然并发接受客户端，但首行读取没有期限和长度上限，长时间无效连接也缺少资源约束。

**建议分两步实施：**

1. 先为连接、请求写入和首个响应设置统一 deadline；为服务端首行读取设置合理期限与长度限制，并保留 CurrentUserOnly。Attach 建立后的输出流允许长时间静默，不能套用普通请求超时。超时应关闭对应管道并终止实际 I/O，不能只停止等待、留下后台阻塞任务。
2. 再为每个 Session 建立有序输入队列，必要时复用控制连接；resize 合并为最新尺寸。不要对每个按键独立 `Task.Run`，否则输入顺序和资源使用更难保证。Start 的宿主探测可先异步完成，WPF 控件操作仍回到 Dispatcher。

**验收：** 假宿主“接受连接但不回包”时，GUI 保持可操作，CLI 在约定期限内返回错误；空闲恶意或故障客户端不会长期独占 GUI/事件通道；快速输入、大段粘贴和连续拖动仍保持字符顺序与最终尺寸正确。

### O3：回放有限，不代表实时输出积压有限

**依据：** [HostedSession.Subscribe](../src/Cmux.SessionHost/Program.cs)、[OnOutput](../src/Cmux.SessionHost/Program.cs)、[输出发送循环](../src/Cmux.SessionHost/Program.cs)。

`_replay` 最多保留 100 万字符，但每个订阅者使用 `Channel.CreateUnbounded<string>()`。如果 GUI 读取慢或某个 Attach 客户端持续不读取，管道写入会停住，新的输出仍会进入该订阅队列。当前没有队列容量上限；这是一条独立于回放上限的内存增长路径。本次未做长时间内存压力测试。

**最小改进：** 按积压字节数或字符数设置订阅预算，而不是仅限制消息条数；记录队列峰值与慢消费者断开次数。达到上限时，主动关闭该慢消费者的输出连接，释放积压，保留 Shell 和其他订阅者，并提示需要重新连接。不要阻塞 ConPTY 读取线程来等待某一个 GUI。

终端输出含 ANSI/OSC 控制序列，简单使用 `DropOldest` 会静默丢失状态，不能作为默认处理。重连时应明确当前提供的是有限历史回放；复杂 TUI 的完整状态恢复仍受 O8 所述边界约束。

**验收：** 隔离 Shell 持续输出，测试客户端暂停读取至少 60 秒；宿主内存增长应受已配置预算约束，其他会话继续响应，断开慢客户端不会结束 Shell。

### O4：备份回退需要验证布局结构，而不只是 JSON 语法

**依据：** [LayoutStore.Read](../src/Cmux.Core/LayoutStore.cs)、[WorkspaceManager.Restore](../src/Cmux.Core/WorkspaceManager.cs)。

当前只检查 `Version == 1` 和顶层 `Workspaces != null`。`Tabs`、`RootPane`、节点形态、ID 唯一性、活动项引用和比例没有完整验证。

**本次已复现：** 使用独立合成文件，连续保存两个有效快照以生成备份，然后把主文件的 `workspaces[0].tabs` 改为 `null`，JSON 仍合法。结果如下：

```text
Semantic-invalid main accepted: True; valid backup tabs: 1
Restore failed: NullReferenceException
```

`Load()` 接受主文件，因而不会尝试有效备份，异常发生在后续 Restore。保存时“旧主文件是否有效”的判断也复用这个浅校验，因此结构损坏的旧快照有可能替换原本有效的备份。

**最小改进：** 在读取和覆盖备份前共用布局校验：必要集合和字段、叶子/分支约束、ID 唯一性、比例范围、活动节点引用。对旧版本字段需要兼容时，显式迁移后再校验。主文件结构无效时尝试备份；主备均无效时保留原文件并显示恢复说明，避免直接覆盖证据。当前 Restore 先构建临时列表再替换模型的方式值得保留。

**验收：** 增加 `tabs:null`、空 RootPane、重复 Pane/SessionId、缺少分支子节点、非法比例、悬空活动 ID、主备同时损坏的用例；结构损坏主文件不得污染有效备份。

### O5：让开发测试连接到自己启动的实例

**依据：** [SessionHost 管道名](../src/Cmux.Terminal/SessionHostProtocol.cs)、[宿主单实例互斥](../src/Cmux.SessionHost/Program.cs)、[GUI 管道名](../src/Cmux.Core/GuiCommandProtocol.cs)、[SessionHost 冒烟脚本](../scripts/M5.SessionHost-Smoke.ps1)。

宿主管道和互斥量按用户 SID 固定；GUI 管道按用户名固定。`--layout-path` 只隔离布局，并未隔离 IPC。已有宿主运行时，新构建的 SessionHost 会因互斥量退出，测试仍可能通过固定管道连到旧宿主。因此一次测试通过不一定验证了刚修改的宿主代码。

本次只读检查确认 GUI 与 SessionHost 来自已安装的 `Cmux.Windows_0.1.6.1_x64__xtne161mbahd6`。未运行会建立或关闭真实会话的 GUI/SessionHost 冒烟脚本。

**最小改进：** 增加内部测试用的实例标识，统一派生 SessionHost、GUI、事件管道和互斥量；测试同时使用独立配置/布局路径。增加只读握手返回宿主 PID、可执行文件路径、应用版本、协议版本；脚本确认身份匹配后才创建测试会话。正式实例维持现有默认命名，不应为测试替换或结束日常宿主。

**验收：** 正式 GUI 和开发测试同时运行；测试只连接自己的宿主，清理只涉及本次创建的 SessionId/PID；不兼容协议返回可识别错误，而不是静默连接或结束旧会话。

## 3. 性能与维护成本

### O6：减少 500ms 状态轮询里的同步工作

**依据：** [状态定时器](../spikes/M0.Terminal.Wpf/MainWindow.xaml.cs)、[UpdateStatus](../spikes/M0.Terminal.Wpf/MainWindow.xaml.cs)、[AgentDetector.Detect](../src/Cmux.Core/AgentDetector.cs)、[侧栏签名比较](../spikes/M0.Terminal.Wpf/MainWindow.xaml.cs)。

每个 Tick 在 Dispatcher 上扫描系统进程，然后对全部 Pane 检测 Agent。Detect 为每个父节点再次遍历进程列表；每轮已经共享同一份进程快照，但没有共享父子索引。侧栏已经用签名避免无变化时替换 ItemsSource，不过比较前仍会构造整套数组和字符串，因此不能描述为“每 500ms 必然重建 UI”。

**建议：** 后台采集一次进程快照并建立 PID、ParentPID 索引，禁止重叠扫描；UI 只应用改变的状态。无活动连接时跳过扫描，后台窗口适当降低扫描频率；保留显式事件的及时更新。先测量再决定是否进一步改为可观察集合，避免为了消除少量分配引入大规模绑定改造。

**验收：** 1/4/16 Pane 场景记录 UI Tick P95、空闲 CPU 与分配量；确认 Agent 状态正确、不会因后台扫描结果延迟覆盖新状态。收益以相同机器的前后数据判断。

### O7：Git 状态查询按工作树归并

**依据：** [RefreshGitContextsAsync](../spikes/M0.Terminal.Wpf/MainWindow.xaml.cs)、[GitProjectContext.ReadAsync](../src/Cmux.Core/GitProjectContext.cs)。

当前每 5 秒对不同 cwd 并行执行 `git status --short --branch`。已有目录去重、3 秒超时和防重入，但同一个 Git 工作树中的多个子目录仍可能触发重复查询，目录很多时 `Task.WhenAll` 会同时启动很多进程。目录缓存也没有移除已不再使用项的路径。

**建议：** 缓存“cwd → Git 工作树根目录”的关系，每个工作树每轮查询一次，设置小规模并发上限，清理已无引用的缓存项。区分同一仓库的不同 worktree，不能按共享 `.git` 公共目录全部合并。保留 cwd 变化和用户操作后的及时刷新；移除异步查询外多余的 Task.Run 可作为随手简化，不应把它宣称为主要性能收益。

**验收：** 同一工作树 10 个子目录每轮只产生一次状态查询；不同工作树结果互不覆盖；大型仓库超时不会阻塞界面或堆积下一轮任务。

### O8：回放缓存优化与复杂 TUI 恢复边界分开处理

**依据：** [回放缓冲实现](../src/Cmux.SessionHost/Program.cs)、[FilterReplay](../spikes/M0.Terminal.Wpf/ConptyConnection.cs)、[现有恢复限制](M10-RELIABILITY.md)。

回放在锁内 Append，超过上限后从头 Remove，Attach 时再 ToString 生成完整字符串并经过 JSON 序列化。100 万 UTF-16 字符的内容本身约占 2MB/会话，不包含缓冲容量、序列化、回放副本和订阅队列；不能把它当成每会话总内存上限。

**建议：** 通过基准测试确认这里的分配成本，再考虑按输出块保存的有界队列或环形缓存，减少反复裁剪和锁内复制，维持相同内存预算与输出顺序。

字符截断不保证从完整控制序列边界开始，也不保存终端屏幕状态。现有正则过滤解决的是部分查询重放问题，不能据此承诺复杂 TUI 完整恢复。应增加超限回放、跨块控制序列、宽字符和 alternate screen 的恢复测试，并在必要时提示历史不完整。不要为此把项目扩展成自行实现 VT 解析器；完整恢复能力应另行评估官方控件/宿主能够提供的机制。

**验收：** 相同输出量下比较分配、峰值内存和 Attach 耗时；普通文本顺序正确，有限历史的行为可解释；不因优化重新触发终端设备查询回包。

### O9：以职责为边界逐步缩小 MainWindow

**依据：** [MainWindow](../spikes/M0.Terminal.Wpf/MainWindow.xaml.cs) 本次统计为 1,769 行，其中 [内置冒烟流程](../spikes/M0.Terminal.Wpf/MainWindow.xaml.cs) 约 300 行。窗口同时处理会话、布局恢复、持久化、Git、Agent、通知、命令面板、热键和测试。

**建议：** 先把冒烟驱动迁移到测试入口；随后在修复相关问题时抽出会话协调、状态采集、布局持久化这几个有明确边界的职责。WPF 控件创建、焦点和原生消息处理留在窗口侧。测试不应靠生产窗口继续积累启动开关，也不需要先引入通用事件总线或 DI 容器。

**验收：** 拆分不改变关闭/Detach、恢复焦点、热键和布局保存行为；新增故障测试能够直接覆盖相应服务。先保持现有目录与内部标识，GUI 从 spike 目录迁出可以单独安排，避免和会话修复绑在一起。

## 4. 测试、构建与验收

### O10：把已有冒烟资产变成统一回归入口

**依据：** 当前检索到多份 PowerShell 冒烟脚本，未发现独立测试项目或 `.github` CI 工作流。部分 Core 脚本固定读取 Debug DLL，例如 [布局测试](../scripts/M3.Layout-Smoke.ps1)；另一些使用 Release。仅按 README 构建 Release 后，不一定具备所有脚本所需的当前 Debug 产物。

**建议：** 先统一脚本的 Configuration/AssemblyPath 和一个明确的执行入口，入口负责构建并以非零退出码汇总失败。Core 的布局、Pane、焦点、Agent 聚合可逐步迁入常规 .NET 测试项目；ConPTY 与 WPF 测试在具备桌面能力的独立 Windows 环境运行。CI 的第一阶段可以只做构建和 Core 测试，桌面恢复测试在 O5 完成后接入。

优先新增的回归案例就是 O1–O5 描述的失败条件，而不是为每个简单属性或实现细节增加测试。保留已有冒烟脚本，避免为了形式一致重写所有测试。

**验收：** 从干净检出执行单个命令可以构建并完成规定检查；输出记录版本和配置；失败项返回失败退出码；测试可以在没有真实用户会话的隔离环境重复运行。

### O11：构建可复现，版本信息能对应到运行实例

**依据：** [固定的 WPF 包版本](../spikes/M0.Terminal.Wpf/M0.Terminal.Wpf.csproj)、[OpenConsole 复制脚本](../scripts/Copy-OpenConsole.ps1)、[包清单](../packaging/AppxManifest.xml)、[打包](../scripts/M10.Build-Msix.ps1)、[签名](../scripts/M10.Sign-Dev-Msix.ps1)、[安装激活](../scripts/M10.Activate-Dev-Update.ps1)。

NuGet 的 TerminalControl 版本已固定，但 OpenConsole 每次选本机 Windows Terminal 中版本最高的一份。本次构建使用 WPF 包 `1.25.260303002` 和本机 Windows Terminal `1.24.11911.0` 中的 OpenConsole；版本数字不同不直接证明不兼容，但说明运行组合受开发机安装状态影响。未发现 SDK 的 global.json 或 NuGet 锁文件。

版本 `0.1.6.3` 同时写在 Manifest 和多个脚本中，容易漏改。应用和宿主日志读取程序集版本，项目没有与 MSIX 版本建立统一来源。[路线图“现役版本”段落](../CMUX-Windows-ROADMAP.md) 仍写 0.1.6.0，而 [M10](M10-RELIABILITY.md) 和 README 记录 0.1.6.1。

**建议：** 指定并记录已验收的控件/OpenConsole 组合；构建产物写入来源版本、SHA-256、SDK 和提交标识。为 OpenConsole 提供明确构建输入或匹配校验，不能只依赖“本机最新”。使用适合项目的 SDK 固定策略和依赖锁定；从同一版本来源生成包名、Manifest 与日志构建身份。将“当前状态”集中在 M10，路线图保留历史并链接当前状态。

正式签名与分发验证已经是项目明确的待完成项，应保留为发布门槛。本次没有审核证书、安装包或发布平台规则，也没有把开发证书视为正式分发凭证。

**验收：** 两台干净环境构建的依赖版本与文件哈希可核对；运行日志、握手信息、包清单指向同一构建；开发机安装新 Windows Terminal 后不会无提示改变发布所用引擎。

### O12：用当前版本的完整路径数据决定性能优化

**依据：** [M10 已有性能记录](M10-RELIABILITY.md) 包括旧版本冷启动、CLI 驱动切换和 IPC 往返，但已经明确未覆盖当前版本的完整键盘到渲染延迟。

IPC 往返很快不代表输入到屏幕也很快；包含 CLI 启动的焦点测试也不能单独代表控件焦点切换成本。先补测量，才能判断 O6–O8 哪项最值得做。

| 场景 | 应记录的数据 | 判定方式 |
|---|---|---|
| 无宿主冷启动、已有宿主重连、16 Pane 恢复 | 窗口出现与首个终端可输入的耗时分别记录 | 冷启动继续参考路线图 <3 秒目标；重连单列 |
| 前台空闲与后台运行，1/4/16 Pane | GUI/Host CPU、Private Bytes、句柄、线程、GC 分配 | 同机器比较，并检查随时间是否持续增长 |
| 单字符输入、大段粘贴 | 输入送达、宿主处理、输出到达、实际可见画面各阶段耗时 | 报 P50/P95/P99；不能把输出回调当作渲染完成 |
| 拖动分隔条、切换 Workspace、聚焦 Pane | UI 响应、resize 次数与延迟 | Workspace 参考 <150ms 目标，并注明是否包含 CLI |
| 持续输出、慢消费者、反复 Attach/Detach | 吞吐、队列峰值、峰值内存、恢复耗时、资源回收 | 对照 O3/O8 的预算和行为要求 |

每组固定机器、显示刷新率、字体、主题、输出负载和软件版本，区分冷/热启动；记录重复次数、预热方式和原始数据。具体“按键到可见画面”的通过阈值应在测量方案确定后制定，不从旧 IPC 数字推导。

已有 [DiagnosticLog](../src/Cmux.Core/DiagnosticLog.cs) 的轮转、保留期和不阻断业务策略可继续使用。补充请求耗时、队列积压、恢复原因等汇总指标即可；避免默认逐字记录终端内容，或为了测量把同步文件日志写入每个按键的热路径。

## 5. 建议实施顺序

| 批次 | 范围 | 完成标志 |
|---|---|---|
| 第一批：可靠性 | O5 最小隔离；O4 校验回退；O1 关闭提交；O2 deadline；O3 积压预算 | 故障用例在独立实例可重复通过，不影响日常会话 |
| 第二批：交互性能 | O12 基线；按结果实施 O2 有序异步输入、O6/O7/O8 | 同配置前后对比可说明收益，功能回归通过 |
| 第三批：持续交付 | O9 分步拆分；O10 CI；O11 构建与版本统一 | 干净环境可构建、验证并追踪到具体运行版本 |

如果只做三个业务改进，优先选择 **O4 布局校验回退、O1 显式关闭持久化、O2 IPC 完整超时**；它们分别覆盖已复现的恢复失败、会话规则边界和可能无限等待的路径。O5 是这些真实故障实验的前置保护，应先具备最小可用版本。

## 6. 本次验证记录与限制

环境：Windows，.NET SDK `8.0.425`。源码 Release 构建和 Core Debug 构建均为 0 警告、0 错误。

| 本次执行 | 结果 |
|---|---|
| `dotnet build spikes/M0.Terminal.Wpf/M0.Terminal.Wpf.csproj -c Release --nologo` | 通过 |
| `dotnet build src/Cmux.Core/Cmux.Core.csproj -c Debug --nologo --no-restore` | 通过，为现有 Core 冒烟脚本准备当前产物 |
| `pwsh -NoProfile -File scripts/M2.Workspace-Smoke.ps1` | Workspace、Tab、Pane、Focus 通过 |
| `pwsh -NoProfile -File scripts/M3.Layout-Smoke.ps1` | 布局往返与 JSON 语法损坏回退通过 |
| `pwsh -NoProfile -File scripts/M4.AgentDetection-Smoke.ps1` | Agent 检测与状态优先级通过 |
| `pwsh -NoProfile -File scripts/M5.Replay-Filter-Smoke.ps1` | 设备查询/回显过滤与保留内容检查通过 |
| `pwsh -NoProfile -File scripts/M7.Git-Smoke.ps1` | 临时仓库 clean/dirty 检测通过 |
| 合成布局：有效备份 + 主文件 `tabs:null` | 已复现主文件被接受、Restore 抛 NullReferenceException |

合成布局用例只创建审查专用文件，未修改用户配置。复现方法：用 WorkspaceManager 创建一个工作区和 Group，以 LayoutStore 连续保存两次；仅将主文件的 `workspaces[0].tabs` 改为 null；再次 Load 后调用新的 WorkspaceManager.Restore。有效备份存在，但当前代码不会在这条路径使用它。

未执行 GUI 启动/崩溃、真实会话生命周期、慢消费者压力、安装更新、签名或完整渲染延迟测试。性能条目没有宣称具体提速比例，代码确认的故障窗口也不等于已在当前用户会话中发生。未列入本文的行为不代表已经通过完整审计。

## 7. 实施进展（2026-09-24）

本文的建议与当前架构一致，已按第一批优先级实施并通过统一入口 `scripts/Test.ps1 -Configuration Release`：

- **O5**：`CMUX_INSTANCE_ID`/`--instance-id` 派生 SessionHost、GUI、事件管道和互斥量；新增 v2 `identity` 握手，测试脚本只清理自身 PID/SessionId。
- **O4**：新增共享布局语义校验和损坏证据保护；`M3.Layout-Smoke.ps1` 覆盖语义损坏、重复 ID、缺子节点、非法比例、悬空活动引用和主备同时损坏。
- **O1**：显式关闭路径在关闭 RPC 前先记录并强制提交清除启动信息（包括恢复进行中），关闭 RPC 失败保留连接供重试；下次启动会清理已记录但未成功关闭的孤立会话。`M5.Close-Persist-Smoke.ps1` 验证强制结束 GUI 后不复活。
- **O2**：首行读取/首次响应 deadline、实际管道终止、异步探测避免 UI 死锁、单消费者有序输入队列、16 KiB 代理对安全的输入分片和 resize 合并；`M5.Ipc-Deadline-Smoke.ps1`、`M5.Input-Chunk-Smoke.ps1` 通过。会话创建返回的 lease 由客户端预先携带，重启/迟到请求不能写入新 Shell；`M5.Lease-Fence-Smoke.ps1` 通过。
- **O3/O8**：每个订阅按字符预算限制积压，64 KiB 输出批量发送，回放改为分块有界缓存；未聚焦的 Pane 延迟 Attach，避免后台输出订阅先把 GUI 断开；`M5.Output-Backpressure-Smoke.ps1` 通过。
- **O6/O7**：状态扫描移到后台并共享进程父子索引；Git 查询按 worktree 根目录归并并限制为 4 个并发。
- **O10/O11/O12**：新增统一回归入口、Windows Core CI、SDK/应用版本来源、固定 OpenConsole 版本与 SHA-256、构建元数据和只读性能基线脚本。桌面回归门已按 `-Configuration` 参数化，`-Configuration Debug -IncludeDesktop` 现在真正运行 Debug 二进制，不再对 Release 宿主路径做断言。
- **O9（第一步已完成）**：8 套内置门（lifecycle/tab/pane/m2-gate/event/notification/cwd/palette）迁到 `MainWindow.Smoke.cs`，`MainWindow.xaml.cs` 从 2196 行降到 1867 行；9 个启动开关字段收敛为 `SmokeOptions`，布局读写开关改为派生属性 `AllowsLayoutPersistence`。控件创建、焦点与原生消息处理仍留在窗口侧，未引入事件总线或 DI 容器。

## 8. 复核修复（2026-09-28）

对本轮改动做了一次完整只读复核，逐条对照当前磁盘代码核实，8 条全部属实并已修复：

- `RestoreShellsAsync` 的 `finally` 原先无保护地调用 `FocusTab`/`FocusPane`，用户在恢复多 Shell 期间关闭启动时聚焦的组会抛 `ArgumentException`，导致 `_restoring` 永久为 `true`，此后整个会话的布局永不落盘。焦点回退现在自捕获异常，`_restoring = false` 放在内层 `finally`。
- 延迟 Attach 引入的回归：`FocusPane` 只设置 `Control.Connection`，绕过了 `Boot()` 里的 `Margin`/`SetTheme`，懒附着的 Pane 会以默认主题和内边距渲染。抽出 `ApplyTerminalAppearance`，`Boot()` 与 `FocusPane` 共用。
- 显式关闭发生在 `PrepareHostAsync` 期间时，丢弃分支只 `Dispose()` 而不清 `paneState.Connection`，Pane 会保留已释放连接且 Start 保持禁用。
- `StopTransport` 原本不加锁就完成输出通道并释放刷批定时器，在途回调会对已关闭通道写入并误报"渲染过慢"。新增 `_transportStopped` 标志，定时器在 `_outputGate` 内释放。
- `Start()` 遇到在途准备时原会直接返回，改为共享同一个准备任务，Attach 会等到 lease 和 Shell PID 就绪。
- SessionHost 原按 `ProtocolVersion >= 2` 才要求 lease，协议 0 的请求可完全绕过会话栅栏。现在只有 `identity`/`list` 保持可诊断，其余命令强制协议 v2。
- `LayoutStore` 在主文件缺失、备份有效时不设置恢复提示，且把同一个异常同时当作主文件和备份错误上报。
- 两个桌面冒烟脚本对 SessionHost 路径硬编码 Release。

同时说明两点实测结果：内置门不经 `Test.ps1` 运行，直接启动 GUI 后其 SessionHost 会按 Detach 语义留在后台并锁住输出目录 DLL，必须按 `ExecutablePath` 精确清理；`--tab-smoke`、`--cwd-smoke`、`--palette-smoke` 在当前代码下仍有失败（分别是 `OpenProcess` 访问被拒、cwd 未上报、调色板关闭未移除 Pane），属于既有缺陷而非本轮搬移引入，已记录待修。

当前仍待完成：O2 的更大范围有序输入基准、O6/O7/O8 的安装版前后性能对比、O9 第二步（会话协调/状态采集/布局持久化抽服务）、内置门的既有失败、正式发布签名与完整键盘到渲染延迟验收。安装版 0.1.6.1 未被本次源码改动替换；源码与开发包版本为 0.1.6.4；本机只读性能采样显示高输出会话是卡顿的主要风险来源，详见 `docs/M10-RELIABILITY.md`。
