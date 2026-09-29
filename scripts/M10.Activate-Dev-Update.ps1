$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Run this script with Windows PowerShell: powershell.exe -NoProfile -File scripts\M10.Activate-Dev-Update.ps1'
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$props = [xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)
$version = [string]$props.Project.PropertyGroup.CmuxAppVersion
$package = Join-Path $root "artifacts\WinPaneDock-$version-x64-dev.msix"
if (-not (Test-Path -LiteralPath $package)) { throw "Package not found: $package" }

$installed = Get-AppxPackage -Name Cmux.Windows | Select-Object -First 1
if ($installed) {
    $hostPath = Join-Path $installed.InstallLocation 'Cmux.SessionHost.exe'
    if (Get-Process Cmux.Spike.Terminal -ErrorAction SilentlyContinue) {
        throw 'Close all WinPaneDock windows before activating the update.'
    }

    $hostProcess = Get-Process Cmux.SessionHost -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $hostPath }
    if ($hostProcess) {
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_')
        $pipeName = 'cmux-session-host-' + $sid
        $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
        $reader = $null
        $writer = $null
        try {
            $pipe.Connect(3000)
            $writer = New-Object IO.StreamWriter($pipe)
            $writer.AutoFlush = $true
            $reader = New-Object IO.StreamReader($pipe)
            $writer.WriteLine('{"Command":"list","ProtocolVersion":2}')
            $task = $reader.ReadLineAsync()
            if (-not $task.Wait(3000)) { throw 'Installed SessionHost did not respond.' }
            $response = $task.Result | ConvertFrom-Json
            if (-not $response.Ok) { throw $response.Error }
            if ($response.Identity -and $response.Identity.ProtocolVersion -ne 2) {
                throw 'Installed SessionHost protocol is incompatible with this package.'
            }
            $activeSessions = @($response.Sessions | Where-Object { $_ })
            if ($activeSessions.Count -gt 0) {
                throw "Close the $($activeSessions.Count) active WinPaneDock terminals before updating."
            }
        }
        finally {
            try { if ($reader) { $reader.Dispose() } } catch { }
            try { if ($writer) { $writer.Dispose() } } catch { }
            try { $pipe.Dispose() } catch { }
        }
        $hostProcess | Stop-Process
    }
}

Add-AppxPackage -Path $package -ErrorAction Stop
Get-AppxPackage -Name Cmux.Windows | Select-Object Name, Version, PackageFullName
