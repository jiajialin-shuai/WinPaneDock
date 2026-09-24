if (-not $global:CmuxOriginalPrompt) {
    $global:CmuxOriginalPrompt = (Get-Command prompt -CommandType Function).ScriptBlock
}

function global:prompt {
    $directory = (Get-Location).ProviderPath
    if ($directory -ne $global:CmuxReportedDirectory) {
        $global:CmuxReportedDirectory = $directory
        & cmux cwd $directory 2>$null | Out-Null
    }
    & $global:CmuxOriginalPrompt
}
