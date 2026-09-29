using System.Diagnostics;
using System.IO;
using System.Windows;
using Brushes = System.Windows.Media.Brushes;
using Size = System.Windows.Size;
using Cmux.Core;

namespace Cmux.Spike.Terminal;

/// <summary>
/// Built-in gate runs that drive the real window. They are isolated from the
/// production paths: <see cref="AllowsLayoutPersistence"/> keeps a gate run from
/// loading or writing the user layout, and every gate starts its own isolated
/// SessionHost when launched with --instance-id.
/// </summary>
public partial class MainWindow
{
    private sealed record SmokeOptions
    {
        public int LifecycleCycles { get; init; }
        public bool TabGate { get; init; }
        public bool PaneGate { get; init; }
        public bool M2Gate { get; init; }
        public bool EventGate { get; init; }
        public bool NotificationGate { get; init; }
        public bool CwdGate { get; init; }
        public bool PaletteGate { get; init; }
        public bool ReattachGate { get; init; }
        public bool ReplayGate { get; init; }

        public bool Active => LifecycleCycles > 0 || TabGate || PaneGate || M2Gate || EventGate
            || NotificationGate || CwdGate || PaletteGate || ReattachGate || ReplayGate;

        /// <summary>A gate run owns its own shells, so the user layout is neither loaded nor saved.</summary>
        public bool AllowsLayoutPersistence => !Active;

        public static SmokeOptions Parse(string[] args) => new()
        {
            LifecycleCycles = ParseCycles(args),
            TabGate = args.Contains("--tab-smoke"),
            PaneGate = args.Contains("--pane-smoke"),
            M2Gate = args.Contains("--m2-gate-smoke"),
            EventGate = args.Contains("--event-smoke"),
            NotificationGate = args.Contains("--notification-smoke"),
            CwdGate = args.Contains("--cwd-smoke"),
            PaletteGate = args.Contains("--palette-smoke"),
            ReattachGate = args.Contains("--reattach-smoke"),
            ReplayGate = args.Contains("--replay-smoke"),
        };

        private static int ParseCycles(string[] args)
        {
            for (var i = 1; i < args.Length - 1; i++)
                if (args[i] == "--lifecycle-smoke" && int.TryParse(args[i + 1], out var cycles) && cycles is > 0 and <= 500)
                    return cycles;
            return 0;
        }
    }

    private async Task RunLifecycleSmokeAsync()
    {
        var initialHandles = Process.GetCurrentProcess().HandleCount;
        var failures = new List<string>();
        for (var i = 0; i < _smoke.LifecycleCycles; i++)
        {
            try
            {
                StartTerminal("cmd.exe", Environment.CurrentDirectory, "CMD");
                Terminal.TriggerResize(new Size(Math.Max(200, Terminal.ActualWidth - i % 2 * 30),
                    Math.Max(120, Terminal.ActualHeight - i % 2 * 20)));
                Terminal.Focus();
                await Task.Delay(50);
                if (i % 10 == 4)
                {
                    var oldPid = _connection!.ProcessId;
                    OnRestartClicked(this, new RoutedEventArgs());
                    if (_connection?.ProcessId == oldPid) failures.Add($"cycle {i + 1}: restart retained shell {oldPid}");
                }
                var pid = _connection!.ProcessId;
                CloseTerminal(i % 10 == 9);
                try { using var shell = Process.GetProcessById(pid); if (!shell.HasExited) failures.Add($"cycle {i + 1}: shell {pid} alive"); }
                catch (ArgumentException) { }
            }
            catch (Exception ex)
            {
                failures.Add($"cycle {i + 1}: {ex.Message}");
                try { CloseTerminal(true); } catch { }
                break;
            }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var finalHandles = Process.GetCurrentProcess().HandleCount;
        var result = $"cycles={_smoke.LifecycleCycles}; initialHandles={initialHandles}; finalHandles={finalHandles}; failures={failures.Count}{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "m1-lifecycle-smoke.log"), result);
        System.Windows.Application.Current.Shutdown(failures.Count == 0 ? 0 : 1);
    }

    private async Task RunTabSmokeAsync()
    {
        var failures = new List<string>();
        try
        {
            var firstWorkspace = _workspaces.Active!;
            StartTerminal("cmd.exe /k echo TAB-ONE", firstWorkspace.RootDirectory, "CMD");
            await Task.Delay(150);
            var firstTab = _activeTab!;
            var firstPid = _connection!.ProcessId;

            CreateTab(firstWorkspace);
            RefreshTabs();
            await Task.Delay(100);
            StartTerminal("cmd.exe /k echo TAB-TWO", firstWorkspace.RootDirectory, "CMD");
            var secondPid = _connection!.ProcessId;

            _focus.FocusTab(firstWorkspace.Id, firstTab.Tab.Id);
            RefreshTabs();
            await Task.Delay(100);
            var secondWorkspace = _workspaces.Create("Other", Environment.CurrentDirectory);
            CreateTab(secondWorkspace);
            RefreshWorkspaces();
            await Task.Delay(100);
            StartTerminal("cmd.exe /k echo WORKSPACE-TWO", secondWorkspace.RootDirectory, "CMD");
            var thirdPid = _connection!.ProcessId;

            _focus.FocusWorkspace(firstWorkspace.Id);
            RefreshWorkspaces();
            await Task.Delay(100);
            if (_activeTab != firstTab || firstTab.View.Visibility != Visibility.Visible)
                failures.Add("First tab was not restored.");
            foreach (var pid in new[] { firstPid, secondPid, thirdPid })
            {
                try { using var process = Process.GetProcessById(pid); if (process.HasExited) failures.Add($"Shell {pid} exited."); }
                catch (ArgumentException) { failures.Add($"Shell {pid} disappeared."); }
            }
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "m2-tab-smoke.log"),
            $"failures={failures.Count}{Environment.NewLine}" + string.Join(Environment.NewLine, failures));
        await Task.Delay(5000); // allow visual capture after switching back to the first tab
        foreach (var state in _tabs.Values)
            foreach (var pane in state.Panes.Values) ClosePaneConnection(pane, false);
        System.Windows.Application.Current.Shutdown(failures.Count == 0 ? 0 : 1);
    }

    private async Task RunPaneSmokeAsync()
    {
        var failures = new List<string>();
        try
        {
            StartTerminal("cmd.exe /k echo PANE-ONE", Environment.CurrentDirectory, "CMD");
            await Task.Delay(100);
            SplitActivePane(PaneOrientation.Right);
            await Task.Delay(100);
            SplitActivePane(PaneOrientation.Down);
            await Task.Delay(100);
            var tab = _activeTab!;
            var leaves = tab.Tab.RootPane.Terminals().ToArray();
            if (leaves.Length != 3 || tab.Panes.Count != 3) failures.Add("Three panes were not created.");
            _workspaces.ResizePane(tab.WorkspaceId, tab.Tab.Id, tab.Tab.RootPane.Id, 0.6);
            RenderPaneLayout(tab);
            FocusPane(tab, leaves[0].SessionId!.Value, true);
            FocusPaneInDirection(1, 0);
            if (_activePane == tab.Panes[leaves[0].SessionId!.Value]) failures.Add("Directional pane focus failed.");
            FocusPane(tab, leaves[0].SessionId!.Value, true);
            foreach (var pane in tab.Panes.Values)
                if (pane.Connection is null || pane.Connection.ProcessId == 0) failures.Add("A pane has no shell.");
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }

        await Task.Delay(5000);
        if (_activeTab is { } current && _activePane is { Connection: { } closing })
        {
            var closingPid = closing.ProcessId;
            OnClosePaneClicked(this, new RoutedEventArgs());
            if (current.Tab.RootPane.Terminals().Count() != 2) failures.Add("Close Pane did not leave two panes.");
            try { using var shell = Process.GetProcessById(closingPid); if (!shell.HasExited) failures.Add("Closed pane shell is alive."); }
            catch (ArgumentException) { }
        }
        if (_activeTab is { } active)
            foreach (var pane in active.Panes.Values) ClosePaneConnection(pane, false);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "m2-pane-smoke.log"),
            $"failures={failures.Count}{Environment.NewLine}" + string.Join(Environment.NewLine, failures));
        System.Windows.Application.Current.Shutdown(failures.Count == 0 ? 0 : 1);
    }

    private async Task RunM2GateSmokeAsync()
    {
        var failures = new List<string>();
        var pids = new List<int>();
        var detections = new List<string>();
        try
        {
            var workspace = _workspaces.Active!;
            var tab = _activeTab!;
            var right = _workspaces.SplitPane(workspace.Id, tab.Tab.Id, tab.Tab.RootPane.Id, PaneOrientation.Right);
            AddPane(tab, right.SessionId!.Value);
            var lowerLeft = _workspaces.SplitPane(workspace.Id, tab.Tab.Id, tab.Tab.RootPane.ChildA!.Id, PaneOrientation.Down);
            AddPane(tab, lowerLeft.SessionId!.Value);
            var lowerRight = _workspaces.SplitPane(workspace.Id, tab.Tab.Id, right.Id, PaneOrientation.Down);
            AddPane(tab, lowerRight.SessionId!.Value);
            ShowTab(tab.Tab.Id);
            RenderPaneLayout(tab);
            tab.View.UpdateLayout();
            await Task.Delay(100);

            var pwsh = ((IReadOnlyList<TerminalProfile>)ProfilePicker.ItemsSource)
                .First(p => p.Name == "PowerShell 7").CommandLine;
            var commands = new[]
            {
                (Command: $"{pwsh} -NoProfile -NoExit -Command codex", Name: "Codex"),
                (Command: $"{pwsh} -NoProfile -NoExit", Name: "PowerShell"),
                (Command: $"{pwsh} -NoProfile -NoExit -Command claude", Name: "Claude"),
                (Command: "python.exe -m http.server 18765 --bind 127.0.0.1", Name: "Dev Server"),
            };
            var leaves = tab.Tab.RootPane.Terminals().ToArray();
            for (var i = 0; i < leaves.Length; i++)
            {
                FocusPane(tab, leaves[i].SessionId!.Value, true);
                StartTerminal(commands[i].Command, workspace.RootDirectory, commands[i].Name);
                pids.Add(_connection!.ProcessId);
                await Task.Delay(100);
            }
            FocusPane(tab, leaves[0].SessionId!.Value, true);
            FocusPaneInDirection(1, 0);
            FocusPane(tab, leaves[0].SessionId!.Value, true);
            await Task.Delay(2500);
            foreach (var pid in pids)
            {
                try { using var process = Process.GetProcessById(pid); if (process.HasExited) failures.Add($"Process {pid} exited."); }
                catch (ArgumentException) { failures.Add($"Process {pid} disappeared."); }
            }
            var snapshot = AgentDetector.Scan();
            for (var i = 0; i < leaves.Length; i++)
            {
                var pane = tab.Panes[leaves[i].SessionId!.Value];
                var detection = AgentDetector.Detect(pane.Connection!.ProcessId, snapshot,
                    pane.Connection.CommandLine, pane.LastProfileName ?? "");
                detections.Add($"{commands[i].Name}:{detection.Type}/{detection.Source}");
                if (i == 0 && detection.Type != AgentType.Codex) failures.Add("Codex was not detected.");
                if (i == 2 && detection.Type != AgentType.Claude) failures.Add("Claude was not detected.");
            }
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "m2-gate-smoke.log"),
            $"pids={string.Join(',', pids)}; detections={string.Join(',', detections)}; failures={failures.Count}{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures));
        await Task.Delay(5000);
        foreach (var tab in _tabs.Values)
            foreach (var pane in tab.Panes.Values) ClosePaneConnection(pane, false);
        System.Windows.Application.Current.Shutdown(failures.Count == 0 ? 0 : 1);
    }

    private async Task RunEventSmokeAsync()
    {
        var failure = "";
        try
        {
            var reportedDirectory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            StartTerminal($"cmd.exe /k cmux notify working && cmux notify waiting && cmux cwd \"{reportedDirectory}\"",
                Environment.CurrentDirectory, "CMD");
            await Task.Delay(1500);
            if (_activePane?.Status != AgentStatus.Waiting)
                failure = $"Expected Waiting, got {_activePane?.Status}.";
            if (_activePane?.CurrentDirectory != reportedDirectory)
                failure = $"Expected cwd {reportedDirectory}, got {_activePane?.CurrentDirectory}.";
            if (HandleAgentEvent(new AgentEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AgentStatus.Error)))
                failure = "Unknown session event was accepted.";
            if (_activePane?.Status != AgentStatus.Waiting)
                failure = "Unknown session changed the active pane.";
        }
        catch (Exception ex) { failure = ex.ToString(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "m4-event-smoke.log"),
            failure.Length == 0 ? "working -> waiting: PASS" : failure);
        await Task.Delay(3000);
        CloseTerminal(false);
        System.Windows.Application.Current.Shutdown(failure.Length == 0 ? 0 : 1);
    }

    private async Task RunNotificationSmokeAsync()
    {
        var failure = "";
        try
        {
            StartTerminal("cmd.exe", Environment.CurrentDirectory, "CMD");
            var tab = _activeTab!;
            var first = tab.Tab.RootPane.Terminals().First();
            SplitActivePane(PaneOrientation.Right);
            await Task.Delay(100);
            var working = new AgentEvent(tab.WorkspaceId, first.Id, first.SessionId!.Value, AgentStatus.Working);
            HandleAgentEvent(working);
            var firstPane = tab.Panes[first.SessionId.Value];
            if (StatusBrush(firstPane) != Brushes.DodgerBlue)
                failure = "Working did not show the blue status light.";
            HandleAgentEvent(working with { Status = AgentStatus.Waiting });
            if (firstPane.CompletionRead || StatusBrush(firstPane) != Brushes.Orange)
                failure = "Background completion did not show the unread orange status light.";
            if (_waitingNotificationCount != 1 || _pendingNotification is null)
                failure = "Background Working -> Waiting did not notify.";
            NavigateToPendingNotification();
            if (!firstPane.CompletionRead || StatusBrush(firstPane) != Brushes.MediumSeaGreen)
                failure = "Reading the completed Pane did not show the green status light.";
            if (_focus.Current.PaneId != first.Id || _pendingNotification is not null)
                failure = "Notification click did not focus the original pane.";
        }
        catch (Exception ex) { failure = ex.ToString(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "m4-notification-smoke.log"),
            failure.Length == 0 ? "background waiting notification and click navigation: PASS" : failure);
        foreach (var tab in _tabs.Values)
            foreach (var pane in tab.Panes.Values) ClosePaneConnection(pane, false);
        System.Windows.Application.Current.Shutdown(failure.Length == 0 ? 0 : 1);
    }

    private async Task RunCwdSmokeAsync()
    {
        var failure = "";
        try
        {
            var profile = ((IReadOnlyList<TerminalProfile>)ProfilePicker.ItemsSource)
                .First(p => p.Name == "PowerShell 7");
            StartProfile(profile);
            if (_connection is null) throw new InvalidOperationException("PowerShell did not start.");
            var directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            _connection.WriteInput($"Set-Location -LiteralPath '{directory.Replace("'", "''")}'\r");
            await Task.Delay(1800);
            if (_activePane?.CurrentDirectory != directory)
                failure = $"Expected cwd {directory}, got {_activePane?.CurrentDirectory}.";
        }
        catch (Exception ex) { failure = ex.ToString(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "m7-cwd-smoke.log"),
            failure.Length == 0 ? "PowerShell prompt cwd update: PASS" : failure);
        CloseTerminal(false);
        System.Windows.Application.Current.Shutdown(failure.Length == 0 ? 0 : 1);
    }

    /// <summary>
    /// Drives the output-stream recovery path. A dropped attach pipe used to kill the read
    /// loop for good, which froze the pane for the rest of the GUI's lifetime. The gate
    /// kills the pipe on purpose and requires output to resume on the same shell.
    /// </summary>
    private async Task RunReattachSmokeAsync()
    {
        var failure = "";
        try
        {
            var profile = ((IReadOnlyList<TerminalProfile>)ProfilePicker.ItemsSource)
                .First(p => p.Name == "PowerShell 7");
            StartProfile(profile);
            if (_connection is null) throw new InvalidOperationException("PowerShell did not start.");
            var connection = _connection;
            // The shell PID is only published once the host acknowledges the create.
            for (var i = 0; i < 100 && connection.ProcessId == 0; i++) await Task.Delay(100);
            if (connection.ProcessId == 0) throw new InvalidOperationException("The host never reported a shell PID.");
            var processId = connection.ProcessId;

            // Wait for the first frames so the attach is provably live before it is killed.
            var before = connection.LastOutputTick;
            for (var i = 0; i < 100 && connection.LastOutputTick == before; i++) await Task.Delay(100);
            if (connection.LastOutputTick == before) throw new InvalidOperationException("No output arrived before the attach was dropped.");

            // Saturate the host's replay buffer first. This is the case that used to be missed: a
            // reconnect that asks for all 1,000,000 characters sends one multi-megabyte JSON line,
            // which cannot be written inside the deadline, so every retry times out and the pane
            // dies after exhausting the attempt budget.
            connection.WriteInput("$cmuxBlob = 'Y' * 100000; 1..14 | ForEach-Object { $cmuxBlob }\r");
            // Prove the data really traversed, so the host's replay buffer is genuinely
            // saturated. A quiesce-only check would pass instantly if the command never ran.
            var charsBefore = connection.OutputCharactersSent;
            var fillDeadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < fillDeadline &&
                   connection.OutputCharactersSent - charsBefore < 1_000_000) await Task.Delay(250);
            var delivered = connection.OutputCharactersSent - charsBefore;
            if (delivered < 1_000_000)
                throw new InvalidOperationException(
                    $"Only {delivered} characters were delivered; the replay buffer was never saturated, so this run proves nothing.");

            // Then let the stream go quiet so the attach is dropped at a realistic point.
            var last = connection.LastOutputTick;
            var quiet = 0;
            var quietDeadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < quietDeadline)
            {
                await Task.Delay(500);
                if (connection.LastOutputTick == last) { if (++quiet >= 4) break; }
                else { quiet = 0; last = connection.LastOutputTick; }
            }
            if (quiet < 4) throw new InvalidOperationException("Output never quiesced; the gate setup is unreliable.");

            connection.DropAttachForTest();

            // Wait for the reconnect to be under way, then let it settle. The probe has to be
            // typed after the attach is back: anything sent while the pipe is dead produces
            // output that is correctly discarded, which would look like a recovery failure.
            var startDeadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < startDeadline && connection.ReattachCount == 0) await Task.Delay(100);
            if (connection.ReattachCount == 0) throw new InvalidOperationException("The output stream never reattached.");
            await Task.Delay(3000);

            // The reconnect must not have pulled the whole saturated buffer, and it must not
            // have burned the attempt budget getting there.
            if (connection.LastReattachReplayChars > 256 * 1024)
                throw new InvalidOperationException(
                    $"Reconnect replayed {connection.LastReattachReplayChars} characters; it must stay bounded so the first line fits the write deadline.");
            if (connection.ReattachCount > 2)
                throw new InvalidOperationException(
                    $"Reconnect needed {connection.ReattachCount} attempts; a bounded replay should succeed first try.");

            var beforeProbe = connection.LastOutputTick;
            connection.WriteInput("Write-Output CMUX_REATTACH_PROBE\r");
            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < deadline && connection.LastOutputTick == beforeProbe) await Task.Delay(100);
            if (connection.LastOutputTick == beforeProbe)
                throw new InvalidOperationException("Output did not resume after the attach was re-established.");
            if (connection.ProcessId != processId)
                throw new InvalidOperationException($"Reattach changed the shell PID from {processId} to {connection.ProcessId}.");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "m10-reattach-smoke.log"),
            failure.Length == 0 ? "output stream recovery after attach loss: PASS" : failure);
        CloseTerminal(false);
        System.Windows.Application.Current.Shutdown(failure.Length == 0 ? 0 : 1);
    }

    /// <summary>
    /// A TUI that paints its screen once and then goes quiet stays invisible if its first
    /// output was dropped, because TerminalContainer discards output that arrives before the
    /// native window exists and the program never repaints. The gate forces that exact
    /// ordering: bind the connection while the control is still unrealised, then require the
    /// buffered tail to be replayed once the control loads.
    /// </summary>
    private async Task RunReplaySmokeAsync()
    {
        var failure = "";
        try
        {
            var workspace = _workspaces.Active!;
            CreateTab(workspace);
            RefreshTabs();

            // No dispatcher turn here on purpose: a freshly added control has not been through
            // a layout pass, so it has no native terminal yet. This is exactly the ordering in
            // StartTerminalAsync, where Boot() binds the connection from a Dispatcher callback
            // before the pane has ever been measured.
            var tabState = _activeTab
                ?? throw new InvalidOperationException("The new group did not become active.");
            var pane = tabState.Panes[tabState.Tab.RootPane.Terminals().First().SessionId!.Value];
            if (pane.Control.IsLoaded)
                throw new InvalidOperationException("The new control was already loaded, so the gate proves nothing.");

            var connection = new ConptyConnection(
                "cmd.exe /k echo CMUX_REPLAY_PROBE", workspace.RootDirectory,
                new Dictionary<string, string> { ["CMUX_SESSION_ID"] = Guid.NewGuid().ToString() });
            connection.Faulted += OnConnectionFaulted;
            pane.Connection = connection;
            // Same helper Boot() and FocusPane() use, so the gate exercises the real path.
            AttachControlConnection(pane, connection);
            if (!pane.NeedsOutputReplay)
                throw new InvalidOperationException("Binding an unrealised control did not arm the replay.");

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && connection.LastOutputTick == 0) await Task.Delay(100);
            if (connection.LastOutputTick == 0)
                throw new InvalidOperationException("The probe produced no output to buffer.");

            // Let layout build the native terminal and the posted replay run.
            await Task.Delay(3000);
            if (pane.NeedsOutputReplay)
                throw new InvalidOperationException("The control never loaded, so the replay path was not exercised.");
            connection.Dispose();
        }
        catch (Exception ex) { failure = ex.ToString(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "m10-replay-smoke.log"),
            failure.Length == 0 ? "startup output replay for an unrealised control: PASS" : failure);
        foreach (var state in _tabs.Values)
            foreach (var p in state.Panes.Values) ClosePaneConnection(p, false);
        System.Windows.Application.Current.Shutdown(failure.Length == 0 ? 0 : 1);
    }

    private async Task RunPaletteSmokeAsync()
    {
        var failure = "";
        try
        {
            OpenPalette();
            PaletteQuery.Text = "Split Right";
            ExecuteSelectedPaletteCommand();
            if (_activeTab?.Tab.RootPane.Terminals().Count() != 2)
                failure = "Split Right did not create a second Pane.";
            OpenPalette();
            PaletteQuery.Text = "Close Terminal";
            ExecuteSelectedPaletteCommand();
            if (_activeTab?.Tab.RootPane.Terminals().Count() != 1)
                failure = "Close Pane did not remove the second Pane.";
            if (PalettePopup.IsOpen)
                failure = "Palette remained visible after command.";
        }
        catch (Exception ex) { failure = ex.ToString(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "m8-palette-smoke.log"),
            failure.Length == 0 ? "palette split/close pane: PASS" : failure);
        await Task.Delay(100);
        System.Windows.Application.Current.Shutdown(failure.Length == 0 ? 0 : 1);
    }
}
