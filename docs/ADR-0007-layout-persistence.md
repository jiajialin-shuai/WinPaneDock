# ADR-0007: JSON layout snapshot

状态：已采纳（2026-09-23）。

Workspace Core 定义可序列化的 Workspace / Tab / Pane Tree 快照，并在 Terminal 叶子保存重建 Shell 所需的 Profile、命令行与工作目录。进程 ID 和屏幕缓冲区不保存。WPF 层负责把快照写到用户本地配置目录，并在启动时据此重建 UI 与 Shell。

M3.3 的落盘使用短时 debounce、同目录临时文件原子替换，以及上一个有效文件备份。此格式只包含数据，不包含 WPF 或 ConPTY 类型。
