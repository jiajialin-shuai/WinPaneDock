param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$assembly = Join-Path $PSScriptRoot "../src/Cmux.Core/bin/$Configuration/net8.0/Cmux.Core.dll"
Add-Type -Path (Resolve-Path $assembly)
$agents = @('Codex','Claude','Grok','Gemini','OpenCode')
foreach ($name in $agents) {
    $processes = [Cmux.Core.AgentProcess[]]@(
        [Cmux.Core.AgentProcess]::new(100, 0, 'pwsh.exe'),
        [Cmux.Core.AgentProcess]::new(101, 100, 'node.exe'),
        [Cmux.Core.AgentProcess]::new(102, 101, "$name.exe"),
        [Cmux.Core.AgentProcess]::new(200, 0, 'pwsh.exe')
    )
    $result = [Cmux.Core.AgentDetector]::Detect(100, $processes, '', '')
    if ($result.Type.ToString() -ne $name -or $result.Source.ToString() -ne 'ProcessTree' -or $result.ProcessId -ne 102) {
        throw "$name process-tree detection failed."
    }
    $other = [Cmux.Core.AgentDetector]::Detect(200, $processes, '', '')
    if ($other.Type.ToString() -ne 'Unknown') { throw "$name leaked across process trees." }
}
$rootOnly = [Cmux.Core.AgentProcess[]]@([Cmux.Core.AgentProcess]::new(100, 0, 'pwsh.exe'))
$hint = [Cmux.Core.AgentDetector]::Detect(100, $rootOnly, 'pwsh.exe -Command grok', '')
if ($hint.Type.ToString() -ne 'Grok' -or $hint.Source.ToString() -ne 'LaunchCommand') { throw 'Launch command fallback failed.' }
$scan = [Cmux.Core.AgentDetector]::Scan()
if (-not @($scan | Where-Object ProcessId -eq $PID)) { throw 'Windows process scan missed current shell.' }
$statuses = [Cmux.Core.AgentStatus[]]@(
    [Cmux.Core.AgentStatus]::Completed,
    [Cmux.Core.AgentStatus]::Working,
    [Cmux.Core.AgentStatus]::Waiting,
    [Cmux.Core.AgentStatus]::Error
)
if ([Cmux.Core.AgentStatusAggregator]::Aggregate($statuses).ToString() -ne 'Error') { throw 'Workspace status priority failed.' }
Write-Output 'Agent detection and workspace status priority: PASS'
