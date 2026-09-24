$ErrorActionPreference = 'Stop'
$assembly = Join-Path $PSScriptRoot '../src/Cmux.Core/bin/Debug/net8.0/Cmux.Core.dll'
Add-Type -Path (Resolve-Path $assembly)
$path = Join-Path $env:TEMP ("cmux-layout-smoke-{0}.json" -f [guid]::NewGuid())
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
    $store.Save($manager.Export())

    $restored = [Cmux.Core.WorkspaceManager]::new()
    $restored.Restore($store.Load())
    if ($restored.Active.Name -ne 'New name') { throw 'Latest layout was not restored.' }
    if ($restored.Active.Tabs[0].RootPane.Ratio -ne 0.6) { throw 'Split ratio was not restored.' }
    $leaf = @($restored.Active.Tabs[0].RootPane.Terminals())[1]
    if ($leaf.ProfileName -ne 'PowerShell 7' -or $leaf.WorkingDirectory -ne $root) { throw 'Terminal launch was not restored.' }

    Set-Content -LiteralPath $path -Value '{broken json' -Encoding utf8
    $recovered = [Cmux.Core.WorkspaceManager]::new()
    $recovered.Restore($store.Load())
    if ($recovered.Active.Name -ne 'Original') { throw 'Backup recovery failed.' }
    Write-Output 'Layout save/restore, pane ratio, launch metadata, and backup recovery: PASS'
}
finally {
    foreach ($file in @($path, "$path.bak", "$path.tmp", "$path.bak.tmp")) {
        Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue
    }
}
