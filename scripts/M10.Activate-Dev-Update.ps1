$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Run this script with Windows PowerShell: powershell.exe -NoProfile -File scripts\M10.Activate-Dev-Update.ps1'
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$package = Join-Path $root 'artifacts\cmux-0.1.6.0-x64-dev.msix'
if (-not (Test-Path -LiteralPath $package)) { throw "Package not found: $package" }

$installed = Get-AppxPackage -Name Cmux.Windows | Select-Object -First 1
if ($installed) {
    $hostPath = Join-Path $installed.InstallLocation 'Cmux.SessionHost.exe'
    if (Get-Process Cmux.Spike.Terminal -ErrorAction SilentlyContinue) {
        throw 'Close all cmux windows before activating the update.'
    }

    $hostProcess = Get-Process Cmux.SessionHost -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $hostPath }
    if ($hostProcess) {
        $pipeName = 'cmux-session-host-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_')
        $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
        try {
            $pipe.Connect(3000)
            $writer = New-Object IO.StreamWriter($pipe)
            $writer.AutoFlush = $true
            $reader = New-Object IO.StreamReader($pipe)
            $writer.WriteLine('{"Command":"list"}')
            $response = $reader.ReadLine() | ConvertFrom-Json
            if (-not $response.Ok) { throw $response.Error }
            $activeSessions = @($response.Sessions | Where-Object { $_ })
            if ($activeSessions.Count -gt 0) {
                throw "Close the $($activeSessions.Count) active cmux terminals before updating."
            }
        }
        finally { $pipe.Dispose() }
        $hostProcess | Stop-Process
    }
}

Add-AppxPackage -Path $package -ErrorAction Stop
Get-AppxPackage -Name Cmux.Windows | Select-Object Name, Version, PackageFullName
