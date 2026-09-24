param(
    [Parameter(Mandatory = $true)][string]$Match,
    [Parameter(Mandatory = $true)][ValidateSet("click", "rightclick", "dblclick", "drag", "wheel")][string]$Action,
    [Parameter(Mandatory = $true)][int]$X,          # 相对窗口矩形 (与截图坐标系一致)
    [Parameter(Mandatory = $true)][int]$Y,
    [int]$X2,
    [int]$Y2,
    [int]$Delta = 600                               # wheel: 正=向上滚
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Mouse {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr extraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mouse; }
    [DllImport("user32.dll", SetLastError=true)] public static extern uint SendInput(uint count, INPUT[] input, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010, WHEEL = 0x0800, ABSOLUTE = 0x8000;

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

[Win32Mouse]::Activate($proc.MainWindowHandle)
Start-Sleep -Milliseconds 400

# 安全红线: 鼠标事件会作用到光标下的任何窗口, 前台不是目标时不允许发。
$fgNow = [Win32Mouse]::GetForegroundWindow()
if ($fgNow -ne $proc.MainWindowHandle) {
    Write-Error "ABORT: foreground is NOT the target window. Mouse events NOT sent."
    exit 2
}

$rect = New-Object Win32Mouse+RECT
[Win32Mouse]::GetWindowRect($proc.MainWindowHandle, [ref]$rect) | Out-Null
$ax = $rect.Left + $X
$ay = $rect.Top + $Y
[Win32Mouse]::SetCursorPos($ax, $ay) | Out-Null
Start-Sleep -Milliseconds 200

switch ($Action) {
    "click" {
        [Win32Mouse]::mouse_event([Win32Mouse]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [Win32Mouse]::mouse_event([Win32Mouse]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
    }
    "rightclick" {
        [Win32Mouse]::mouse_event([Win32Mouse]::RIGHTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [Win32Mouse]::mouse_event([Win32Mouse]::RIGHTUP, 0, 0, 0, [UIntPtr]::Zero)
    }
    "dblclick" {
        foreach ($i in 1..2) {
            [Win32Mouse]::mouse_event([Win32Mouse]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 60
            [Win32Mouse]::mouse_event([Win32Mouse]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 80
        }
    }
    "drag" {
        $bx = $rect.Left + $X2
        $by = $rect.Top + $Y2
        [Win32Mouse]::mouse_event([Win32Mouse]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 80
        # 分步移动, 让渲染器跟上
        $steps = 12
        for ($i = 1; $i -le $steps; $i++) {
            $cx = [int]($ax + ($bx - $ax) * $i / $steps)
            $cy = [int]($ay + ($by - $ay) * $i / $steps)
            [Win32Mouse]::SetCursorPos($cx, $cy) | Out-Null
            Start-Sleep -Milliseconds 25
        }
        Start-Sleep -Milliseconds 80
        [Win32Mouse]::mouse_event([Win32Mouse]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
    }
    "wheel" {
        $inputEvent = New-Object Win32Mouse+INPUT
        $inputEvent.type = 0
        $inputEvent.mouse.dwFlags = [Win32Mouse]::WHEEL
        $inputEvent.mouse.mouseData = [BitConverter]::ToUInt32([BitConverter]::GetBytes($Delta), 0)
        $sent = [Win32Mouse]::SendInput(1, @($inputEvent), [Runtime.InteropServices.Marshal]::SizeOf[Win32Mouse+INPUT]())
        if ($sent -ne 1) { throw "SendInput(wheel) failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
    }
}
Write-Output "$Action at ($X,$Y) window=$($rect.Left),$($rect.Top) size=$($rect.Right - $rect.Left)x$($rect.Bottom - $rect.Top)"
