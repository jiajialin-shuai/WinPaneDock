# DECRQM 查询 v2: 定时读 + 十六进制转储, 避免控制字符污染渲染/无限阻塞。
# CSI ? Pi $ p  ->  回复 CSI ? Pi ; Ps $ y   (Ps: 1=set 2=reset 0=not recognized)
$e = [char]27
$ys = [char]36

Write-Host "=== M0 DECRQM QUERY v2 ==="
# 开启 1000/1002/1003/1006/1004 (与 mouse-probe 相同)
[Console]::Out.Write($e + '[?1000h' + $e + '[?1002h' + $e + '[?1003h' + $e + '[?1006h' + $e + '[?1004h')
Start-Sleep -Milliseconds 500
[Console]::Out.Write($e + '[?1000' + $ys + 'p' + $e + '[?1002' + $ys + 'p' + $e + '[?1004' + $ys + 'p')
Start-Sleep -Milliseconds 500

$stdin = [Console]::OpenStandardInput()   # 只开一次: 多次 Open 会在同一句柄上挂起并发读, 丢失超时后到达的字节
function Read-Timed([int]$ms) {
    $buf = New-Object byte[] 512
    $t = $stdin.ReadAsync($buf, 0, 512)
    if (-not $t.Wait($ms)) { return $null }   # 超时: 读仍挂起, 但脚本随后即退出
    if ($t.Result -le 0) { return $null }
    return [System.Text.Encoding]::UTF8.GetString($buf, 0, $t.Result)
}
function To-Hex([string]$s) {
    if ($null -eq $s) { return "<timeout>" }
    $codes = $s.ToCharArray() | ForEach-Object { '{0:X2}' -f [int]$_ }
    # 顺带按 ASCII 可读还原
    $ascii = ($s.ToCharArray() | ForEach-Object { if ([int]$_ -ge 32 -and [int]$_ -lt 127) { $_ } else { '.' } }) -join ''
    return "hex=" + ($codes -join ' ') + "  ascii='$ascii'"
}

$acc = ''
for ($i = 0; $i -lt 6; $i++) {
    $chunk = Read-Timed 1500
    if ($null -eq $chunk) { Write-Host "chunk${i}: <timeout, stop>"; break }
    $acc += $chunk
    Write-Host ("chunk{0} ({1} chars): {2}" -f $i, $chunk.Length, (To-Hex $chunk))
}
Write-Host ("ACC ({0}): {1}" -f $acc.Length, (To-Hex $acc))
if ($acc -match '\[\?(\d+);(\d+)\$y') {
    Write-Host ("PARSED: mode={0} state={1} (1=set 2=reset 0=unsupported)" -f $Matches[1], $Matches[2])
}
