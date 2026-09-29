param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$assembly = Join-Path $PSScriptRoot "../src/Cmux.Core/bin/$Configuration/net8.0/Cmux.Core.dll"
Add-Type -Path (Resolve-Path $assembly)
$manager = [Cmux.Core.WorkspaceManager]::new()
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$alpha = $manager.Create('Alpha', $root)
$firstTab = $manager.CreateTab($alpha.Id, 'First')
$right = $manager.SplitPane($alpha.Id, $firstTab.Id, $firstTab.RootPane.Id, [Cmux.Core.PaneOrientation]::Right)
$down = $manager.SplitPane($alpha.Id, $firstTab.Id, $right.Id, [Cmux.Core.PaneOrientation]::Down)
$focus = [Cmux.Core.FocusManager]::new($manager)
$focus.FocusTerminal($alpha.Id, $firstTab.Id, $down.SessionId)
if ($focus.Current.PaneId -ne $down.Id -or $focus.Current.TerminalSessionId -ne $down.SessionId) { throw 'Focus path failed.' }
if (@($firstTab.RootPane.Terminals()).Count -ne 3) { throw 'Pane split failed.' }
$manager.ResizePane($alpha.Id, $firstTab.Id, $firstTab.RootPane.Id, 0.6)
if ($firstTab.RootPane.Ratio -ne 0.6) { throw 'Pane resize failed.' }
$manager.SelectPane($alpha.Id, $firstTab.Id, $down.Id)
if ($firstTab.ActivePaneId -ne $down.Id) { throw 'Pane focus failed.' }
if (-not $manager.ClosePane($alpha.Id, $firstTab.Id, $down.Id)) { throw 'Pane close failed.' }
if (@($firstTab.RootPane.Terminals()).Count -ne 2) { throw 'Pane close retained leaf.' }
$focus.Reconcile()
if ($focus.Current.PaneId -eq $down.Id) { throw 'Focus was not reconciled after close.' }
$secondTab = $manager.CreateTab($alpha.Id, 'Second')
$manager.SelectTab($alpha.Id, $firstTab.Id)
if ($alpha.ActiveTabId -ne $firstTab.Id -or $alpha.Tabs.Count -ne 2) { throw 'Tab create/select failed.' }
$manager.CloseTab($alpha.Id, $firstTab.Id)
if ($alpha.ActiveTabId -ne $secondTab.Id) { throw 'Tab close fallback failed.' }
$beta = $manager.Create('Beta', $root)
$gamma = $manager.Create('Gamma', $root)
$manager.Rename($beta.Id, 'Server')
$manager.TogglePin($gamma.Id)
$manager.TogglePin($alpha.Id)
$manager.Move($alpha.Id, -1)
if (($manager.Workspaces.Name -join ',') -ne 'Alpha,Gamma,Server') { throw 'Pin/reorder failed.' }
if (-not $manager.Workspaces[0].IsPinned -or -not $manager.Workspaces[1].IsPinned) { throw 'Pinned state failed.' }

$manager.Select($beta.Id)
if ($manager.Active.Name -ne 'Server') { throw 'Switch failed.' }
$manager.Delete($beta.Id)
if ($manager.Active.Name -ne 'Alpha') { throw 'Active fallback failed.' }
try { $manager.Rename($gamma.Id, 'Alpha'); throw 'Duplicate name accepted.' }
catch [System.ArgumentException] { }

Write-Output 'Workspace, tab, pane, and focus path: PASS'
