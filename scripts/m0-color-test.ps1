# M0 短渲染测试 (不刷屏): ANSI16 / 256 / TrueColor / Unicode / Nerd Font / 光标回填
$e = [char]27
Write-Host "=== M0 COLOR TEST ===" -ForegroundColor Yellow
$names = @('blk','red','grn','yel','blu','mag','cyn','wht')
for ($i = 0; $i -lt 8; $i++) { Write-Host "$e[38;5;$($i)m■$e[48;5;$($i)m $names[$i] $e[0m " -NoNewline }
Write-Host ""
Write-Host -NoNewline "256: "
for ($i = 16; $i -lt 52; $i++) { Write-Host "$e[48;5;${i}m $e[0m" -NoNewline }
Write-Host ""
Write-Host -NoNewline "RGB: "
for ($i = 0; $i -lt 48; $i++) {
    $r = [int](255 * $i / 47); $b = [int](255 * (47 - $i) / 47)
    Write-Host "$e[48;2;$r;120;$b;m $e[0m" -NoNewline
}
Write-Host ""
Write-Host "UNI: 中文 日本語 한국어 ΔθπΩ αβγ →←↑↓ ±×÷ ½¼¾ ¿¡ ©®™ 😀🎉"
Write-Host "NF :  branch  commit  folder  linux  pwsh  rust  docker  ok  sync"
Write-Host -NoNewline "CURSOR BACK: [  ]"
Write-Host ""
Write-Host "$e[1A$e[8Dok$e[0m <- ok should be inside brackets"
