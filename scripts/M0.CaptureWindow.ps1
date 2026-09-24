param(
    [Parameter(Mandatory = $true)][string]$Match,
    [Parameter(Mandatory = $true)][string]$Out,
    [ValidateSet("PrintWindow", "Screen")][string]$Mode = "PrintWindow"
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Win32Cap2 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int maxCount);
    [DllImport("user32.dll")] public static extern bool GetWindowRect2(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr hWnd);

    public const uint GW_OWNER = 4, GW_HWNDPREV = 3;
    public const int GWL_EXSTYLE = -20;
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    public static string Title(IntPtr h) {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, 512);
        return sb.ToString();
    }

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
$h = $proc.MainWindowHandle

$rect = New-Object Win32Cap2+RECT
[Win32Cap2]::GetWindowRect($h, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$hh = $rect.Bottom - $rect.Top
$fg = [Win32Cap2]::GetForegroundWindow()
$fgPid = 0
[Win32Cap2]::GetWindowThreadProcessId($fg, [ref]$fgPid) | Out-Null
Write-Output "target pid=$($proc.Id) rect=$($rect.Left),$($rect.Top) ${w}x${hh}"
Write-Output "foreground: pid=$fgPid title='$([Win32Cap2]::Title($fg))'"

# 找出与目标矩形相交且可见的其它顶层窗口 (只报标题, 不抓内容)
$exStyle = [Win32Cap2]::GetWindowLong($h, -20)
Write-Output ("target exstyle=0x{0:X8} (WS_EX_TOPMOST={1})" -f $exStyle, (($exStyle -band 0x8) -ne 0))

$outDir = Split-Path $Out -Parent
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

if ($Mode -eq "PrintWindow") {
    # PW_RENDERFULLCONTENT = 2 (Win 8.1+), 抓 DWM 合成后的实际像素
    $mem = New-Object System.Drawing.Bitmap($w, $hh)
    $g = [System.Drawing.Graphics]::FromImage($mem)
    $mhdc = $g.GetHdc()
    $ok = [Win32Cap2]::PrintWindow($h, $mhdc, 2)
    $g.ReleaseHdc($mhdc)
    $g.Dispose()
    if ($ok) { $mem.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); Write-Output "saved(PrintWindow): $Out" }
    else { Write-Output "PrintWindow returned false" }
    $mem.Dispose()
}
