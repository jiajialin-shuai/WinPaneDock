$ErrorActionPreference = 'Stop'
$assembly = Join-Path $PSScriptRoot '../src/Cmux.Core/bin/Debug/net8.0/Cmux.Core.dll'
Add-Type -Path (Resolve-Path $assembly)
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$path = Join-Path $env:TEMP ("cmux-app-restore-{0}.json" -f [guid]::NewGuid())
$app = $null
$pipeName = 'cmux-session-host-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_')
$sessionIds = @()
function Get-HostSessions {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(5000)
        $writer = [IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe)
        $writer.WriteLine('{"Command":"list"}')
        return ($reader.ReadLine() | ConvertFrom-Json).Sessions
    }
    finally { $pipe.Dispose() }
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
    $store = [Cmux.Core.LayoutStore]::new($path)
    $store.Save($manager.Export())

    $exe = (Resolve-Path (Join-Path $root 'spikes/M0.Terminal.Wpf/bin/Debug/net8.0-windows/Cmux.Spike.Terminal.exe')).Path
    $app = Start-Process -FilePath $exe -ArgumentList "--layout-path `"$path`"" -PassThru
    Start-Sleep -Seconds 4
    $sessionIds = @($left.SessionId.ToString(), $right.SessionId.ToString(), $projectTab.RootPane.SessionId.ToString())
    $sessions = @(Get-HostSessions | Where-Object SessionId -in $sessionIds)
    $shells = @($sessions | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
    if ($shells.Count -ne 3) {
        throw "Restore launched $($shells.Count) live sessions; expected 3."
    }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'M0.CaptureWindow.ps1') -Match 'cmux —' `
        -Out (Join-Path $root 'artifacts/m3-restored-layout.png') -Mode PrintWindow | Out-Null
    Write-Output 'App restore: 2 workspaces, 3 live SessionHost shells, split layout: PASS'
}
finally {
    if ($app -and -not $app.HasExited) {
        $null = $app.CloseMainWindow()
        if (-not $app.WaitForExit(3000)) { $app.Kill(); $app.WaitForExit() }
    }
    foreach ($id in $sessionIds) {
        try {
            $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
            $pipe.Connect(1000)
            $writer = [IO.StreamWriter]::new($pipe)
            $writer.AutoFlush = $true
            $writer.WriteLine((@{ Command = 'close'; SessionId = $id } | ConvertTo-Json -Compress))
            $pipe.Dispose()
        } catch { }
    }
    foreach ($file in @($path, "$path.bak", "$path.tmp", "$path.bak.tmp")) {
        Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue
    }
}
