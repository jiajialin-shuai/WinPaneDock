# 把 spike 窗口切到前台并校验 (安全红线: 只允许把前台切到目标窗口, 校验失败即 exit 2, 后续注入必须中止)。
# SetForegroundWindow 可能被前台锁拒绝: 用一次 ALT 点按解锁 (ALT 是系统认可的"用户输入"解锁键), 再重试。
param([string]$Match = "cmux M0 spike")

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Foreground {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public const uint KEYEVENTF_KEYUP = 0x0002;
}
"@

$proc = Get-Process | Where-Object { $_.MainWindowTitle -like "*$Match*" } | Select-Object -First 1
if (-not $proc) { Write-Error "no window matching '$Match'"; exit 1 }
$hwnd = $proc.MainWindowHandle

if ([Foreground]::IsIconic($hwnd)) { [void][Foreground]::ShowWindowAsync($hwnd, 9) }   # SW_RESTORE

$ok = [Foreground]::SetForegroundWindow($hwnd)
if (-not $ok -or [Foreground]::GetForegroundWindow() -ne $hwnd) {
    # ALT 点按解锁前台锁 (ALT 单独按下/抬起, 不带其他键, 不会向目标输入字符)
    [Foreground]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)        # ALT down
    Start-Sleep -Milliseconds 30
    [Foreground]::keybd_event(0x12, 0, [Foreground]::KEYEVENTF_KEYUP, [UIntPtr]::Zero)  # ALT up
    Start-Sleep -Milliseconds 100
    [void][Foreground]::SetForegroundWindow($hwnd)
}

Start-Sleep -Milliseconds 300
$fg = [Foreground]::GetForegroundWindow()
if ($fg -ne $hwnd) {
    $fgPid = 0; [void][Foreground]::GetWindowThreadProcessId($fg, [ref]$fgPid)
    Write-Error "SAFETY: foreground=$(( $fg )) pid=$fgPid != target pid=$($proc.Id) -> abort (exit 2)"
    exit 2
}
Write-Output "foreground verified: hwnd=$hwnd pid=$($proc.Id)"
exit 0
