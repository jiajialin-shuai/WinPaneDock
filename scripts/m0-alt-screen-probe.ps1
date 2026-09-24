$e = [char]27
[Console]::Out.Write("PRIMARY-BEFORE`r`n")
[Console]::Out.Write("$e[?1049h")
[Console]::Out.Write("ALTERNATE-SCREEN-ACTIVE`r`n")
Start-Sleep -Seconds 5
[Console]::Out.Write("$e[?1049l")
[Console]::Out.Write("PRIMARY-RESTORED`r`n")
