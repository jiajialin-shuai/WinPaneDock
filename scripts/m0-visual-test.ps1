# M0 渲染验收脚本: ANSI / 256 色 / TrueColor / Unicode / Nerd Font / Scrollback
$e = [char]27

Write-Host ""
Write-Host "=== M0 RENDER TEST ===" -ForegroundColor Yellow

# 1. ANSI 16 色 (前景 + 背景)
$names = @('black','red','green','yellow','blue','magenta','cyan','white')
for ($i = 0; $i -lt 8; $i++) {
    Write-Host "$($e)[38;5;$($i)m■ $e[48;5;$($i)m  $names[$i] $e[0m " -NoNewline
}
Write-Host ""

# 2. 256 色渐变背景
Write-Host -NoNewline "256: "
for ($i = 16; $i -lt 52; $i++) { Write-Host "$e[48;5;${i}m $e[0m" -NoNewline }
Write-Host ""

# 3. 24-bit TrueColor 渐变
Write-Host -NoNewline "RGB: "
for ($i = 0; $i -lt 48; $i++) {
    $r = [int](255 * $i / 47); $b = [int](255 * (47 - $i) / 47)
    Write-Host "$e[48;2;$r;120;$b;m $e[0m" -NoNewline
}
Write-Host ""

# 4. Unicode
Write-Host "UNI: 中文 日本語 한국어 ΔθπΩ αβγ →←↑↓ ±×÷ ½¼¾ ¿¡ ©®™ 😀🎉🙏"

# 5. Nerd Font 图标 (需要 Cascadia Code NF)
Write-Host "NF :  branch  commit  folder  linux  pwsh  rust  go  node  docker  ok  err  sync "

# 6. 光标移动 + 反向文本
Write-Host -NoNewline "CURSOR: [    ] back="
Write-Host ""
Write-Host "$e[1A$e[6Dok$e[0m" -NoNewline
Write-Host " <- 光标应回填在方括号内"

# 7. Scrollback: 3000 行, 用于滚轮测试
Write-Host ""
Write-Host "--- SCROLLBACK START ---"
1..3000 | ForEach-Object { Write-Host "scroll line $_" }
Write-Host "--- SCROLLBACK END (you are here) ---"
