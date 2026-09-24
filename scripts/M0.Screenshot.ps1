param(
    [Parameter(Mandatory = $true)][string]$Match,   # 窗口标题匹配
    [Parameter(Mandatory = $true)][string]$Out,     # 输出 PNG 路径
    [int]$WaitSec = 1
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Cap {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static void Activate(IntPtr hWnd) {
        ShowWindow(hWnd, 9);
        uint dummy;
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out dummy);
        uint targetThread = GetWindowThreadProcessId(hWnd, out dummy);
        AttachThreadInput(fgThread, targetThread, true);
        keybd_event(0x12, 0, 0, UIntPtr.Zero);   // Alt 按下, 解除前台锁
        SetForegroundWindow(hWnd);
        BringWindowToTop(hWnd);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);   // Alt 抬起
        AttachThreadInput(fgThread, targetThread, false);
    }
}
"@

$proc = Get-Process | Where-Object { $_.MainWindowTitle -like "*$Match*" } | Select-Object -First 1
if (-not $proc) { Write-Error "no window matching '$Match'"; exit 1 }

$HWND_TOPMOST = [IntPtr]1
$HWND_NOTOPMOST = [IntPtr](-2)
$swpFlags = 0x0001 -bor 0x0002   # NOMOVE | NOSIZE

# 临时置顶, 保证抓到的是目标窗口而不是前景窗口
[Win32Cap]::SetWindowPos($proc.MainWindowHandle, $HWND_TOPMOST, 0, 0, 0, 0, $swpFlags) | Out-Null
[Win32Cap]::Activate($proc.MainWindowHandle)
Start-Sleep -Milliseconds 800

$rect = New-Object Win32Cap+RECT
[Win32Cap]::GetWindowRect($proc.MainWindowHandle, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
if ($w -le 0 -or $h -le 0) { Write-Error "bad rect"; exit 1 }

Start-Sleep -Seconds $WaitSec
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$outDir = Split-Path $Out -Parent
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# 恢复 z-order
[Win32Cap]::SetWindowPos($proc.MainWindowHandle, $HWND_NOTOPMOST, 0, 0, 0, 0, $swpFlags) | Out-Null
Write-Output "saved: $Out ($w x $h)"
