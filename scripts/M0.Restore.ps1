# 恢复被最小化的 spike 主窗口 (ShowWindow SW_RESTORE=9)
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class WinRestore { [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c); }
"@
$p = Get-Process Cmux.Spike.Terminal -ErrorAction Stop
[void][WinRestore]::ShowWindow($p.MainWindowHandle, 9)
Start-Sleep -Milliseconds 500
[void][WinRestore]::ShowWindow($p.MainWindowHandle, 9)
Write-Output "restored pid=$($p.Id) hwnd=$($p.MainWindowHandle)"
