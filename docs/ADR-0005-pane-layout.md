# ADR-0005: Preserve native terminal windows during split layout

状态：已采纳（2026-09-23）。

Core 中的 PaneNode 是二叉树，Split 节点保存方向和比例，Terminal 叶子保存 SessionId。WPF Tab 以一个 Canvas 承载该 Tab 的全部 TerminalControl；改变 Pane Tree 时只重算各控件位置和大小，不移除仍在使用的控件。拖动 Thumb 更新比例。

原因：官方 WPF TerminalContainer 在离开可视树时会销毁原生 HwndTerminal；若重建 WPF Grid 并重挂旧控件，屏幕缓冲区会丢失。Canvas 保留每个控件实例及其 Hwnd，并让 Pane Tree 仍独立于 UI 实现。
