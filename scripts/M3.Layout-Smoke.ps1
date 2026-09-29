param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$assembly = Join-Path $PSScriptRoot "../src/Cmux.Core/bin/$Configuration/net8.0/Cmux.Core.dll"
Add-Type -Path (Resolve-Path $assembly)
$path = Join-Path $env:TEMP ("cmux-layout-smoke-{0}.json" -f [guid]::NewGuid())
$path2 = Join-Path $env:TEMP ("cmux-layout-smoke-backup-{0}.json" -f [guid]::NewGuid())

function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $thrown = $false
    try { & $Action } catch { $thrown = $true }
    if (-not $thrown) { throw $Message }
}

try {
    $manager = [Cmux.Core.WorkspaceManager]::new()
    $root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $workspace = $manager.Create('Original', $root)
    $tab = $manager.CreateTab($workspace.Id, 'Shell')
    $right = $manager.SplitPane($workspace.Id, $tab.Id, $tab.RootPane.Id, [Cmux.Core.PaneOrientation]::Right)
    $manager.ResizePane($workspace.Id, $tab.Id, $tab.RootPane.Id, 0.6)
    $manager.SetTerminalLaunch($workspace.Id, $tab.Id, $right.SessionId, 'PowerShell 7', 'pwsh.exe -NoLogo', $root, 'Shell')
    $store = [Cmux.Core.LayoutStore]::new($path)
    $store.Save($manager.Export())
    $manager.Rename($workspace.Id, 'New name')
    $valid = $manager.Export()
    $store.Save($valid)

    $restored = [Cmux.Core.WorkspaceManager]::new()
    $restored.Restore($store.Load())
    if ($restored.Active.Name -ne 'New name') { throw 'Latest layout was not restored.' }
    if ($restored.Active.Tabs[0].RootPane.Ratio -ne 0.6) { throw 'Split ratio was not restored.' }
    $leaf = @($restored.Active.Tabs[0].RootPane.Terminals())[1]
    if ($leaf.ProfileName -ne 'PowerShell 7' -or $leaf.WorkingDirectory -ne $root) { throw 'Terminal launch was not restored.' }

    $validJson = $valid | ConvertTo-Json -Depth 30
    $semantic = $validJson | ConvertFrom-Json
    $semantic.workspaces[0].tabs = $null
    $semantic | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding utf8
    $recovered = [Cmux.Core.WorkspaceManager]::new()
    $recovered.Restore($store.Load())
    if ($recovered.Active.Name -ne 'Original') { throw 'Semantic-invalid main did not fall back to the valid backup.' }

    $badRatio = $validJson | ConvertFrom-Json
    $badRatio.workspaces[0].tabs[0].rootPane.ratio = 1.2
    $badRatio | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding utf8
    $ratioRecovered = [Cmux.Core.WorkspaceManager]::new()
    $ratioRecovered.Restore($store.Load())
    if ($ratioRecovered.Active.Name -ne 'Original') { throw 'Invalid ratio did not fall back to backup.' }

    $duplicate = $validJson | ConvertFrom-Json
    $duplicate.workspaces[0].tabs[0].rootPane.childA.id = $duplicate.workspaces[0].tabs[0].rootPane.id
    Assert-Throws { [Cmux.Core.LayoutValidator]::Validate($duplicate) } 'Duplicate pane id was accepted.'

    $missingChild = $validJson | ConvertFrom-Json
    $missingChild.workspaces[0].tabs[0].rootPane.childB = $null
    Assert-Throws { [Cmux.Core.LayoutValidator]::Validate($missingChild) } 'Split without both children was accepted.'

    $dangling = $validJson | ConvertFrom-Json
    $dangling.workspaces[0].tabs[0].activePaneId = [guid]::NewGuid()
    Assert-Throws { [Cmux.Core.LayoutValidator]::Validate($dangling) } 'Dangling active pane id was accepted.'
    $noActiveTab = $validJson | ConvertFrom-Json
    $noActiveTab.workspaces[0].activeTabId = $null
    Assert-Throws { [Cmux.Core.LayoutValidator]::Validate($noActiveTab) } 'Workspace with tabs but no active tab was accepted.'

    $mainText = '{broken main'
    $backupText = '{broken backup'
    [IO.File]::WriteAllText($path, $mainText)
    [IO.File]::WriteAllText("$path.bak", $backupText)
    Assert-Throws { $store.Load() } 'Both invalid layout files were accepted.'
    if ((Get-Content -LiteralPath $path -Raw) -ne $mainText -or (Get-Content -LiteralPath "$path.bak" -Raw) -ne $backupText) {
        throw 'Invalid layout evidence was overwritten.'
    }

    $store2 = [Cmux.Core.LayoutStore]::new($path2)
    $store2.Save($valid)
    $store2.Save($valid)
    $polluted = $validJson | ConvertFrom-Json
    $polluted.workspaces[0].tabs = $null
    $polluted | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path2 -Encoding utf8
    $store2.Save($valid)
    $backup = Get-Content -LiteralPath "$path2.bak" -Raw | ConvertFrom-Json
    if ($backup.workspaces[0].tabs.Count -ne 1) { throw 'Invalid old main polluted the valid backup.' }

    Write-Output 'Layout save/restore, semantic validation, backup fallback, and evidence preservation: PASS'
}
finally {
    foreach ($file in @($path, "$path.bak", "$path.tmp", "$path.bak.tmp", $path2, "$path2.bak", "$path2.tmp", "$path2.bak.tmp")) {
        Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue
    }
}
