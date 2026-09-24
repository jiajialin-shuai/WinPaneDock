# M0 输入注入: 把键盘/鼠标消息 PostMessage 到官方 HwndTerminal 窗口。
# 走的是与真实输入完全相同的 wndproc 链路 (HwndHost MessageHook -> HwndTerminal::HwndTerminalWndProc),
# 仅跳过 OS 前台/IME 层 —— 因为测试机多屏且用户正在实时使用, 抢前台会把按键打到用户窗口 (安全红线)。
# 坐标系: -X/-Y 使用 PrintWindow 截图坐标 (即主窗口矩形, 含标题栏/状态栏)。
param(
    [Parameter(Mandatory = $true)][string]$Match,
    [Parameter(Mandatory = $true)]
    [ValidateSet("Text", "Combo", "Enter", "Wheel", "Click", "Drag", "FocusOn", "FocusOff", "List", "Esc")][string]$Action,
    [string]$Text,
    [string]$Combo,          # e.g. ctrl+c / ctrl+v / shift+home / f5
    [int]$X, [int]$Y,        # 起点 (截图坐标)
    [int]$X2, [int]$Y2,      # 终点 (拖动)
    [int]$Delta = 600,
    [int]$DelayMs = 30       # 消息间延时, 模拟真实节奏
)

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public class PostIn {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    public const uint WM_KEYDOWN=0x100, WM_KEYUP=0x101, WM_CHAR=0x102,
                      WM_SETFOCUS=0x7, WM_KILLFOCUS=0x8,
                      WM_MOUSEMOVE=0x200, WM_LBUTTONDOWN=0x201, WM_LBUTTONUP=0x202,
                      WM_MOUSEWHEEL=0x20A;
    public const uint MAPVK_VK_TO_VSC = 0;

    public static List<IntPtr> Children(IntPtr parent) {
        var list = new List<IntPtr>();
        EnumChildWindows(parent, (h, l) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    public static string ClassName(IntPtr h) {
        var sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString();
    }
    public static IntPtr LParam(int x, int y) { return (IntPtr)((y << 16) | (x & 0xFFFF)); }
    public static IntPtr MakeKeyLParam(uint vk, bool down, bool enhanced, bool repeat) {
        uint scan = MapVirtualKey(vk, MAPVK_VK_TO_VSC) & 0xFF;
        uint flags = enhanced ? 0x01000000u : 0;
        uint param = (repeat ? 1u : 0u) | (scan << 16) | flags | (down ? 0u : 0xC0000000u);
        return (IntPtr)(long)param;
    }
}
"@

function Get-Target {
    $proc = Get-Process | Where-Object { $_.MainWindowTitle -like "*$Match*" } | Select-Object -First 1
    if (-not $proc) { Write-Error "no window matching '$Match'"; exit 1 }
    $main = $proc.MainWindowHandle
    $children = [PostIn]::Children($main)
    if ($Action -eq "List") {
        foreach ($c in $children) {
            $r = New-Object PostIn+RECT; [PostIn]::GetWindowRect($c, [ref]$r) | Out-Null
            Write-Output ("{0} class={1} rect={2},{3} {4}x{5}" -f $c, [PostIn]::ClassName($c), $r.Left, $r.Top, ($r.Right-$r.Left), ($r.Bottom-$r.Top))
        }
        exit 0
    }
    # HwndTerminal 类名含 "Terminal"; 找不到则退回面积最大的子窗口
    $term = $children | Where-Object { [PostIn]::ClassName($_) -like "*erminal*" } | Select-Object -First 1
    if (-not $term) { $term = $children | Select-Object -Last 1 }
    $pr = New-Object PostIn+RECT; [PostIn]::GetWindowRect($main, [ref]$pr) | Out-Null
    $cr = New-Object PostIn+RECT; [PostIn]::GetWindowRect($term, [ref]$cr) | Out-Null
    return @{ Main = $main; Term = $term; ParentRect = $pr; ChildRect = $cr; Pid = $proc.Id }
}

$t = Get-Target
Write-Output "main=$($t.Main) term=$($t.Term) class=$([PostIn]::ClassName($t.Term)) pid=$($t.Pid)"
Write-Output ("parent=$($t.ParentRect.Left),$($t.ParentRect.Top) child=$($t.ChildRect.Left),$($t.ChildRect.Top)")

# 截图坐标 -> 终端子窗口客户区坐标
function To-Client([int]$px, [int]$py) {
    $ox = $t.ChildRect.Left - $t.ParentRect.Left
    $oy = $t.ChildRect.Top - $t.ParentRect.Top
    return @(($px - $ox), ($py - $oy))   # 注意: 必须显式括号, 否则 PS 把逗号优先解析为数组减法
}

$termH = $t.Term
$script:HeldMods = @()
$vkMap = @{
    "enter" = 0x0D; "tab" = 0x09; "esc" = 0x1B; "escape" = 0x1B
    "backspace" = 0x08; "delete" = 0x2E; "up" = 0x26; "down" = 0x28
    "left" = 0x25; "right" = 0x27; "home" = 0x24; "end" = 0x23
    "pgup" = 0x21; "pgdn" = 0x22; "space" = 0x20
    "ctrl" = 0x11; "shift" = 0x10; "alt" = 0x12
    "f4" = 0x73; "f5" = 0x74; "c" = 0x43; "v" = 0x56; "a" = 0x41
}

function Send-Key([uint32]$vk, [bool]$enhanced = $false) {
    [PostIn]::PostMessage($termH, [PostIn]::WM_KEYDOWN, [IntPtr]$vk, [PostIn]::MakeKeyLParam($vk, $true, $enhanced, $false)) | Out-Null
    Start-Sleep -Milliseconds $DelayMs

    # 与真实 TranslateMessage 一致: 生成 WM_CHAR; Ctrl 按住时字母应转为控制码 (Ctrl+C -> 0x03)
    $charCode = $null
    switch ($vk) {
        0x0D { $charCode = 0x0D }
        0x09 { $charCode = 0x09 }
        0x1B { $charCode = 0x1B }
        0x20 { $charCode = 0x20 }
        default {
            if ($vk -ge 0x30 -and $vk -le 0x39) { $charCode = $vk }            # 0-9
            elseif ($vk -ge 0x41 -and $vk -le 0x5A) {                           # A-Z
                if ($script:HeldMods -contains "ctrl") { $charCode = $vk - 0x40 }
                else { $charCode = $vk }
            }
            elseif ($vk -ge 0x60 -and $vk -le 0x69) { $charCode = $vk - 0x30 }  # numpad
        }
    }
    if ($null -ne $charCode) {
        [PostIn]::PostMessage($termH, [PostIn]::WM_CHAR, [IntPtr][uint32]$charCode, [PostIn]::MakeKeyLParam([uint32]$charCode, $true, $enhanced, $true)) | Out-Null
        Start-Sleep -Milliseconds $DelayMs
    }
    [PostIn]::PostMessage($termH, [PostIn]::WM_KEYUP, [IntPtr]$vk, [PostIn]::MakeKeyLParam($vk, $false, $enhanced, $false)) | Out-Null
    Start-Sleep -Milliseconds $DelayMs
}

switch ($Action) {
    "List" { }  # 已在 Get-Target 处理
    "FocusOn" {
        # 官方 WPF 包装层的真实链路 (TerminalContainer_MessageHook): WM_MOUSEACTIVATE -> this.Focus() + SetFocus(hwnd)
        # -> WM_SETFOCUS -> TerminalSetFocused(true) -> _focused=true (SGR 鼠标上报的必要条件之一)。
        # 直接 post WM_SETFOCUS 不会触发 HwndHost.Focus() 的内部状态, 故按官方顺序发 WM_MOUSEACTIVATE。
        [PostIn]::PostMessage($termH, 0x21, [IntPtr]1, [PostIn]::LParam(1, 1)) | Out-Null   # WM_MOUSEACTIVATE, HTCLIENT
        Start-Sleep -Milliseconds $DelayMs
        [PostIn]::PostMessage($termH, [PostIn]::WM_SETFOCUS, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Write-Output "WM_MOUSEACTIVATE + WM_SETFOCUS posted"
    }
    "FocusOff" {
        [PostIn]::PostMessage($termH, [PostIn]::WM_KILLFOCUS, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Write-Output "WM_KILLFOCUS posted"
    }
    "Text" {
        if (-not $Text) { Write-Error "-Text required"; exit 1 }
        foreach ($ch in $Text.ToCharArray()) {
            [PostIn]::PostMessage($termH, [PostIn]::WM_CHAR, [IntPtr][char]$ch, [PostIn]::MakeKeyLParam(0, $true, $false, $true)) | Out-Null
            Start-Sleep -Milliseconds $DelayMs
        }
        Write-Output "posted $($Text.Length) WM_CHAR"
    }
    "Enter" { Send-Key 0x0D }
    "Esc"   { Send-Key 0x1B }
    "Combo" {
        if (-not $Combo) { Write-Error "-Combo required"; exit 1 }
        $parts = $Combo.ToLower().Split("+")
        $mods = @(); $main = $null
        foreach ($p in $parts) {
            if ($p -in @("ctrl", "shift", "alt")) { $mods += $p } else { $main = $p }
        }
        # 按下修饰键 (只发 WM_KEYDOWN, 让该线程的 key state 变为 down —— 原生代码用 GetKeyState 读修饰键)
        foreach ($m in $mods) { $script:HeldMods += $m; Send-Key([uint32]$vkMap[$m]) }
        $enhanced = $main -in @("up", "down", "left", "right", "home", "end", "pgup", "pgdn", "delete")
        Send-Key([uint32]$vkMap[$main]) $enhanced
        # 抬起修饰键 (逆序)
        [array]::Reverse($mods)
        foreach ($m in $mods) {
            $vk = [uint32]$vkMap[$m]
            [PostIn]::PostMessage($termH, [PostIn]::WM_KEYUP, [IntPtr]$vk, [PostIn]::MakeKeyLParam($vk, $false, $false, $false)) | Out-Null
            $script:HeldMods = @($script:HeldMods | Where-Object { $_ -ne $m })
            Start-Sleep -Milliseconds $DelayMs
        }
        Write-Output "combo $Combo sent"
    }
    "Wheel" {
        $c = To-Client $X $Y
        $sx = $t.ChildRect.Left + $c[0]; $sy = $t.ChildRect.Top + $c[1]   # WM_MOUSEWHEEL 用屏幕坐标
        # HIWORD 是有符号 16 位 delta: 先 -band 0xFFFF 取低 16 位 (得 0xFD98), 再左移 16 位
        $hi = $Delta -band 0xFFFF
        $wParam = [IntPtr](([int64]$hi) -shl 16)
        [PostIn]::PostMessage($termH, [PostIn]::WM_MOUSEWHEEL, $wParam, [PostIn]::LParam($sx, $sy)) | Out-Null
        Write-Output "wheel delta=$Delta screen=($sx,$sy) client=($($c[0]),$($c[1]))"
    }
    "Click" {
        $c = To-Client $X $Y
        $lp = [PostIn]::LParam($c[0], $c[1])
        [PostIn]::PostMessage($termH, [PostIn]::WM_MOUSEMOVE, [IntPtr]0, $lp) | Out-Null
        Start-Sleep -Milliseconds $DelayMs
        [PostIn]::PostMessage($termH, [PostIn]::WM_LBUTTONDOWN, [IntPtr]0x1, $lp) | Out-Null   # MK_LBUTTON
        Start-Sleep -Milliseconds $DelayMs
        [PostIn]::PostMessage($termH, [PostIn]::WM_LBUTTONUP, [IntPtr]0, $lp) | Out-Null
        Write-Output "click client=($($c[0]),$($c[1]))"
    }
    "Drag" {
        $c1 = To-Client $X $Y
        $c2 = To-Client $X2 $Y2
        [PostIn]::PostMessage($termH, [PostIn]::WM_MOUSEMOVE, [IntPtr]0, [PostIn]::LParam($c1[0], $c1[1])) | Out-Null
        Start-Sleep -Milliseconds $DelayMs
        [PostIn]::PostMessage($termH, [PostIn]::WM_LBUTTONDOWN, [IntPtr]0x1, [PostIn]::LParam($c1[0], $c1[1])) | Out-Null
        Start-Sleep -Milliseconds $DelayMs
        $steps = 10
        for ($i = 1; $i -le $steps; $i++) {
            $x = [int]($c1[0] + ($c2[0] - $c1[0]) * $i / $steps)
            $y = [int]($c1[1] + ($c2[1] - $c1[1]) * $i / $steps)
            [PostIn]::PostMessage($termH, [PostIn]::WM_MOUSEMOVE, [IntPtr]0x1, [PostIn]::LParam($x, $y)) | Out-Null
            Start-Sleep -Milliseconds 30
        }
        [PostIn]::PostMessage($termH, [PostIn]::WM_LBUTTONUP, [IntPtr]0, [PostIn]::LParam($c2[0], $c2[1])) | Out-Null
        Write-Output "drag ($($c1[0]),$($c1[1])) -> ($($c2[0]),$($c2[1]))"
    }
}
