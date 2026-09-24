# DECSET 解析探针: 用模式 7 (autowrap) 验证私有 DECSET 是否真的被终端解析。
# ESC[?7l 关闭自动换行后打印 300 字符长行: 若截断(单行不续行) -> DECSET 生效; 若换行续行 -> DECSET 未生效。
$e = [char]27
$line = 'A' * 300
[Console]::Out.Write("=== M0 DECSET(mode7) PROBE ===`r`n")
[Console]::Out.Write("NOWRAP>>>" + $e + '[?7l' + $line + $e + '[?7h' + "<<<END`r`n")
[Console]::Out.Write("WRAP-RESTORED>>>" + $line + "<<<END2`r`n")
