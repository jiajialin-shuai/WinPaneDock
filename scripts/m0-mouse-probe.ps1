# M0 鼠标协议探针: 让子进程开启 SGR Mouse Reporting, 并把收到的原始序列打出来。
# 若点击/拖动/滚轮后出现 ESC[<...M/m 序列, 即证明完整鼠标上报链路可用。
param([string]$LogPath)
$e = [char]27
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MouseProbeConsole {
    [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] public static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] public static extern bool SetConsoleMode(IntPtr h, uint mode);
    [DllImport("kernel32.dll")] public static extern bool ReadFile(IntPtr h, byte[] bytes, int length, out int read, IntPtr overlapped);
}
'@
$inputHandle = [MouseProbeConsole]::GetStdHandle(-10)
$oldMode = [uint32]0
if (-not [MouseProbeConsole]::GetConsoleMode($inputHandle, [ref]$oldMode)) { throw 'GetConsoleMode failed' }
# VT input + mouse/window events; disable line input and echo so ESC sequences arrive immediately.
$rawMode = ($oldMode -bor 0x0200 -bor 0x0010 -bor 0x0008) -band (-bnot 0x0002) -band (-bnot 0x0004)
if (-not [MouseProbeConsole]::SetConsoleMode($inputHandle, [uint32]$rawMode)) { throw 'SetConsoleMode failed' }

Write-Host "=== M0 MOUSE PROBE ===" -ForegroundColor Yellow
Write-Host "enabling SGR mouse (1000 click / 1002 drag / 1003 any / 1006 SGR) + focus events (1004)"

# 1000 = click, 1002 = button-motion(拖动), 1003 = any-motion, 1006 = SGR 编码, 1004 = focus
[Console]::Out.Write("$e[?1000h$e[?1002h$e[?1003h$e[?1006h$e[?1004h")
Write-Host "mouse modes ON. now click / drag / wheel / switch window..."
Write-Host "(raw sequences received below; press q + Enter to quit)"

try {
    while ($true) {
        $buf = New-Object byte[] 512
        $n = 0
        if (-not [MouseProbeConsole]::ReadFile($inputHandle, $buf, $buf.Length, [ref]$n, [IntPtr]::Zero) -or $n -le 0) { break }
        $s = [Text.Encoding]::UTF8.GetString($buf, 0, $n)
        if ($s -match 'q') { break }
        $show = $s.Replace("$e", '<ESC>').Replace("`r", '<CR>').Replace("`n", '<LF>')
        Write-Host "RECV($n): $show"
        if ($LogPath) { Add-Content -LiteralPath $LogPath -Value "RECV($n): $show" }
    }
} finally {
    [Console]::Out.Write("$e[?1004l$e[?1003l$e[?1002l$e[?1006l$e[?1000l")
    [void][MouseProbeConsole]::SetConsoleMode($inputHandle, $oldMode)
    Write-Host "mouse modes OFF, probe exit."
}
