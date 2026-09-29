param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('working', 'waiting', 'completed')]
    [string]$Status
)

if ($env:CMUX_WORKSPACE_ID -and $env:CMUX_PANE_ID -and
    $env:CMUX_SESSION_ID -and $env:CMUX_PIPE_NAME) {
    $cmux = Get-Command cmux.exe -ErrorAction SilentlyContinue
    if ($cmux) {
        & $cmux.Source notify $Status 2>$null | Out-Null
    }
}

# Codex Stop hooks require JSON on stdout when they exit successfully.
if ($Status -eq 'waiting') { [Console]::Out.WriteLine('{}') }
