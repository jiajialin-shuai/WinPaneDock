$ErrorActionPreference = 'Stop'

$hostExe = Join-Path $PSScriptRoot '..\src\Cmux.SessionHost\bin\Release\net8.0-windows\Cmux.SessionHost.exe'
$pipeName = 'cmux-session-host-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_')
$hostProcess = Start-Process -FilePath $hostExe -WindowStyle Hidden -PassThru
$sessionId = [guid]::NewGuid().ToString('N')
$exitId = [guid]::NewGuid().ToString('N')

function Send-HostRequest($request) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(5000)
        $writer = [IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe)
        $writer.WriteLine(($request | ConvertTo-Json -Compress -Depth 5))
        $line = $reader.ReadLine()
        if (-not $line) { throw 'SessionHost returned no response.' }
        $result = $line | ConvertFrom-Json
        if (-not $result.Ok) { throw $result.Error }
        return $result
    }
    finally { $pipe.Dispose() }
}

try {
    $created = Send-HostRequest @{ Command = 'create'; SessionId = $sessionId; CommandLine = 'cmd.exe /k'; WorkingDirectory = $PSScriptRoot }
    if ($created.ProcessId -le 0) { throw 'No shell PID returned.' }

    Send-HostRequest @{ Command = 'input'; SessionId = $sessionId; Data = "echo CMUX_M5_BEFORE`r" } | Out-Null
    Start-Sleep -Milliseconds 700
    $listed = Send-HostRequest @{ Command = 'list' }
    $same = @($listed.Sessions | Where-Object SessionId -eq $sessionId)
    if ($same.Count -ne 1 -or $same[0].ProcessId -ne $created.ProcessId) { throw 'Session was lost after client disconnect.' }

    Start-Sleep -Seconds 2
    Send-HostRequest @{ Command = 'input'; SessionId = $sessionId; Data = "echo CMUX_M5_AFTER`r" } | Out-Null
    Start-Sleep -Milliseconds 700
    $attached = Send-HostRequest @{ Command = 'attach'; SessionId = $sessionId }
    if ($attached.ProcessId -ne $created.ProcessId -or
        $attached.Replay -notmatch 'CMUX_M5_BEFORE' -or
        $attached.Replay -notmatch 'CMUX_M5_AFTER') {
        throw 'Reattached output missed a marker or shell PID changed.'
    }
    $exiting = Send-HostRequest @{ Command = 'create'; SessionId = $exitId; CommandLine = 'cmd.exe /c echo CMUX_NATURAL_EXIT'; WorkingDirectory = $PSScriptRoot }
    Start-Sleep -Seconds 8
    $afterExit = Send-HostRequest @{ Command = 'list' }
    Write-Host "Natural exit probe: sessions=$(@($afterExit.Sessions | Where-Object SessionId -eq $exitId).Count), shellAlive=$([bool](Get-Process -Id $exiting.ProcessId -ErrorAction SilentlyContinue))"
    if (@($afterExit.Sessions | Where-Object SessionId -eq $exitId).Count -ne 0 -or
        (Get-Process -Id $exiting.ProcessId -ErrorAction SilentlyContinue)) {
        throw 'Naturally exited shell was not cleaned up.'
    }
    Write-Host "M5/M10 SessionHost smoke PASS: detached replay, stable PID, natural exit cleaned up."
}
finally {
    try { Send-HostRequest @{ Command = 'close'; SessionId = $sessionId } | Out-Null } catch { }
    try { Send-HostRequest @{ Command = 'close'; SessionId = $exitId } | Out-Null } catch { }
    if (-not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force }
}
