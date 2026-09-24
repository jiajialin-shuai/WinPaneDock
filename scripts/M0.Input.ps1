param(
    [Parameter(Mandatory = $true)][string]$Match,
    [Parameter(Mandatory = $true)][string[]]$Keys,   # 逐段发送 (规避 SendKeys 256 字符限制)
    [int]$DelayMs = 80
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Key {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public static void Activate(IntPtr hWnd) {
        ShowWindow(hWnd, 9);
        uint dummy;
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out dummy);
        uint targetThread = GetWindowThreadProcessId(hWnd, out dummy);
        AttachThreadInput(fgThread, targetThread, true);
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        SetForegroundWindow(hWnd);
        BringWindowToTop(hWnd);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        AttachThreadInput(fgThread, targetThread, false);
    }
}
"@

$proc = Get-Process | Where-Object { $_.MainWindowTitle -like "*$Match*" } | Select-Object -First 1
if (-not $proc) { Write-Error "no window matching '$Match'"; exit 1 }

[Win32Key]::Activate($proc.MainWindowHandle)
Start-Sleep -Milliseconds 500

$fg = [Win32Key]::GetForegroundWindow()
if ($fg -ne $proc.MainWindowHandle) {
    # 安全红线: 前台不是目标窗口时绝不能注入按键, 否则会打到用户正在用的窗口。
    Write-Error "ABORT: foreground is NOT the target window (foreground lock). Input NOT sent."
    exit 2
}
$wsh = New-Object -ComObject WScript.Shell
foreach ($k in $Keys) {
    $wsh.SendKeys($k)
    Start-Sleep -Milliseconds $DelayMs
}
Write-Output "sent $($Keys.Count) chunk(s) to pid $($proc.Id)"
