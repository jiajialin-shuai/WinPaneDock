param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assembly = Join-Path $root "src/Cmux.Core/bin/$Configuration/net8.0/Cmux.Core.dll"
$exe = Join-Path $root "spikes/M0.Terminal.Wpf/bin/$Configuration/net8.0-windows/Cmux.Spike.Terminal.exe"
Add-Type -Path (Resolve-Path $assembly)
$instanceId = "restore-smoke-$([guid]::NewGuid().ToString('N'))"
$previousInstance = $env:CMUX_INSTANCE_ID
$env:CMUX_INSTANCE_ID = $instanceId
[Cmux.Core.InstanceScope]::Configure($instanceId)
$pipeName = [Cmux.Core.InstanceScope]::Qualify(
    'cmux-session-host-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_'))
$path = Join-Path $env:TEMP ("cmux-app-restore-{0}.json" -f [guid]::NewGuid())
$app = $null
$hostProcessId = $null
$sessionIds = @()
$leases = @{}

function Send-HostRequest($request) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    $reader = $null; $writer = $null
    try {
        $pipe.Connect(5000); $writer = [IO.StreamWriter]::new($pipe); $writer.AutoFlush = $true; $reader = [IO.StreamReader]::new($pipe)
        $writer.WriteLine(($request | ConvertTo-Json -Compress -Depth 8))
        $task = $reader.ReadLineAsync(); if (-not $task.Wait(5000)) { throw 'SessionHost response timed out.' }
        $response = $task.Result | ConvertFrom-Json; if (-not $response.Ok) { throw $response.Error }; return $response
    }
    finally {
        try { if ($reader) { $reader.Dispose() } } catch { }
        try { if ($writer) { $writer.Dispose() } } catch { }
        try { $pipe.Dispose() } catch { }
    }
}

try {
    $manager = [Cmux.Core.WorkspaceManager]::new()
    $default = $manager.Create('Default', $root)
    $tab = $manager.CreateTab($default.Id, 'Shells')
    $right = $manager.SplitPane($default.Id, $tab.Id, $tab.RootPane.Id, [Cmux.Core.PaneOrientation]::Right)
    $left = $tab.RootPane.ChildA
    $manager.SetTerminalLaunch($default.Id, $tab.Id, $left.SessionId, 'CMD', 'cmd.exe', $root, 'CMD')
    $manager.SetTerminalLaunch($default.Id, $tab.Id, $right.SessionId, 'CMD', 'cmd.exe', $root, 'CMD')
    $manager.SelectPane($default.Id, $tab.Id, $left.Id)
    $project = $manager.Create('Project', (Resolve-Path (Join-Path $root 'docs')).Path)
    $projectTab = $manager.CreateTab($project.Id, 'PowerShell')
    $manager.SetTerminalLaunch($project.Id, $projectTab.Id, $projectTab.RootPane.SessionId,
        'PowerShell', 'powershell.exe -NoLogo', $project.RootDirectory, 'PowerShell')
    $manager.TogglePin($project.Id)
    $manager.Select($default.Id)
    [Cmux.Core.LayoutStore]::new($path).Save($manager.Export())

    $arguments = "--layout-path `"$path`" --instance-id `"$instanceId`""
    $app = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
    Start-Sleep -Seconds 6
    if ($app.HasExited) { throw "App exited early: $($app.ExitCode)" }
    $identity = Send-HostRequest @{ Command = 'identity'; ProtocolVersion = 2 }
    if ($identity.Identity.InstanceId -ne $instanceId -or
        [IO.Path]::GetFullPath($identity.Identity.ExecutablePath) -ne [IO.Path]::GetFullPath((Join-Path $root "spikes/M0.Terminal.Wpf/bin/$Configuration/net8.0-windows/Cmux.SessionHost.exe"))) {
        throw 'App did not use the expected isolated SessionHost.'
    }
    $hostProcessId = $identity.Identity.ProcessId
    $sessionIds = @($left.SessionId.ToString(), $right.SessionId.ToString(), $projectTab.RootPane.SessionId.ToString())
    $sessions = @( (Send-HostRequest @{ Command = 'list'; ProtocolVersion = 2 }).Sessions | Where-Object SessionId -in $sessionIds)
    foreach ($session in $sessions) { $leases[$session.SessionId] = $session.LeaseId }
    $shells = @($sessions | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
    if ($shells.Count -ne 3) { throw "Restore launched $($shells.Count) live sessions; expected 3." }
    Write-Output 'App restore: 2 workspaces, 3 live isolated SessionHost shells, split layout: PASS'
}
finally {
    if ($app -and -not $app.HasExited) {
        $null = $app.CloseMainWindow()
        if (-not $app.WaitForExit(5000)) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue; $app.WaitForExit() }
    }
    foreach ($id in $sessionIds) {
        try { Send-HostRequest @{ Command = 'close'; ProtocolVersion = 2; SessionId = $id; LeaseId = $leases[$id] } | Out-Null } catch { }
    }
    if ($hostProcessId) { Stop-Process -Id $hostProcessId -Force -ErrorAction SilentlyContinue }
    foreach ($file in @($path, "$path.bak", "$path.tmp", "$path.bak.tmp")) {
        Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue
    }
    if ($null -eq $previousInstance) { Remove-Item Env:CMUX_INSTANCE_ID -ErrorAction SilentlyContinue } else { $env:CMUX_INSTANCE_ID = $previousInstance }
}
