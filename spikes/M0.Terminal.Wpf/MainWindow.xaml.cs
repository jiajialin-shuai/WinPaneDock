using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Size = System.Windows.Size;
using Clipboard = System.Windows.Clipboard;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using Cmux.Core;
using Cmux.Terminal;
using Microsoft.Terminal.Wpf;

namespace Cmux.Spike.Terminal;

public partial class MainWindow : Window
{
    private sealed class PaneState
    {
        public TerminalControl Control { get; } = new();
        public Border Frame { get; }
        public DockPanel Header { get; }
        public TextBlock Label { get; } = new() { Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center };
        public Border StatusDot { get; } = new() { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        public System.Windows.Controls.Button CloseButton { get; } = new() { Content = "×", ToolTip = "Close this terminal", Padding = new Thickness(8, 0, 8, 0) };
        public int Number { get; set; }
        public ConptyConnection? Connection { get; set; }
        public bool ControlAttached { get; set; }
        /// <summary>The connection was bound before the control built its native terminal, so the first paint was dropped.</summary>
        public bool NeedsOutputReplay { get; set; }
        public string? LastCommandLine { get; set; }
        public string? LastWorkingDirectory { get; set; }
        public string? CurrentDirectory { get; set; }
        public string? LastProfileName { get; set; }
        public AgentDetection Detection { get; set; } = new(AgentType.Unknown, AgentDetectionSource.None, 0);
        public AgentStatus Status { get; set; } = AgentStatus.Unknown;
        public bool CompletionRead { get; set; }

        public PaneState()
        {
            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            content.RowDefinitions.Add(new RowDefinition());
            Header = new DockPanel { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 32, 38)) };
            CloseButton.Height = 22;
            DockPanel.SetDock(CloseButton, Dock.Right);
            Header.Children.Add(CloseButton);
            Header.Children.Add(StatusDot);
            Header.Children.Add(Label);
            Grid.SetRow(Control, 1);
            content.Children.Add(Header);
            content.Children.Add(Control);
            Frame = new Border { Child = content, BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent };
        }
    }

    private sealed class TabState(TerminalTab tab, Guid workspaceId)
    {
        public TerminalTab Tab { get; } = tab;
        public Guid WorkspaceId { get; } = workspaceId;
        public Canvas View { get; } = new() { Background = Brushes.Black, ClipToBounds = true };
        public Dictionary<Guid, PaneState> Panes { get; } = [];
        public Dictionary<Guid, Thumb> Splitters { get; } = [];
    }

    private sealed record SidebarTerminalItem(string Name, System.Windows.Media.Brush DotBrush, string StatusText);
    private sealed record StatusProbe(PaneState Pane, ConptyConnection? Connection, int ProcessId,
        string CommandLine, string ProfileName);
    private sealed record LaunchState(string ProfileName, string CommandLine, string WorkingDirectory, string Title);
    private sealed record PendingLaunchClear(Guid WorkspaceId, Guid TabId, Guid SessionId, LaunchState State);
    private sealed record SidebarGroupItem(string Name, SidebarTerminalItem[] Terminals);
    private sealed record SidebarItem(Workspace Workspace, string Name, string? Branch, SidebarGroupItem[] Groups);
    private sealed record PaletteCommand(string Label, Action Run);

    private readonly Dictionary<Guid, TabState> _tabs = [];
    private TabState? _activeTab;
    private PaneState? _activePane;
    private TerminalControl Terminal => _activePane?.Control ?? throw new InvalidOperationException("No pane selected.");
    private ConptyConnection? _connection
    {
        get => _activePane?.Connection;
        set { if (_activePane is not null) _activePane.Connection = value; }
    }
    private readonly string? _fontOverride;
    private TerminalSettings? _settings;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _gitTimer;
    private bool _statusRefreshing;
    private int _statusGeneration;
    private readonly Dictionary<Guid, GitProjectContext?> _gitContexts = [];
    private readonly Dictionary<string, GitProjectContext?> _directoryGitContexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _gitWorktreeRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _gitRootResolvedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _gitResolutionFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gitQueryGate = new(4, 4);
    private bool _gitRefreshing;
    private readonly string? _requestedProfile;
    private readonly string? _legacyCommandLine;
    private readonly SmokeOptions _smoke;
    private readonly LayoutStore _layoutStore;
    private readonly AgentEventServer _eventServer;
    private readonly GuiCommandServer _guiCommandServer;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Drawing.Icon _appIcon;
    private AgentEvent? _pendingNotification;
    private int _waitingNotificationCount;
    private readonly DispatcherTimer _saveTimer;
    private bool _restoring;
    private bool _layoutSaveBlocked;
    private readonly HashSet<Guid> _closingSessions = [];
    private string? _lastSaveError;
    private string? _layoutRecoveryNotice;
    private string? _lastCommandLine { get => _activePane?.LastCommandLine; set { if (_activePane is not null) _activePane.LastCommandLine = value; } }
    private string? _lastWorkingDirectory { get => _activePane?.LastWorkingDirectory; set { if (_activePane is not null) _activePane.LastWorkingDirectory = value; } }
    private string? _lastProfileName { get => _activePane?.LastProfileName; set { if (_activePane is not null) _activePane.LastProfileName = value; } }
    private HwndSource? _windowSource;
    private readonly HashSet<int> _registeredHotkeys = [];
    private readonly WorkspaceManager _workspaces = new();
    private readonly Cmux.Core.FocusManager _focus;
    private bool _refreshingWorkspaces;
    private string _sidebarSignature = "";
    private bool _refreshingTabs;
    private TerminalTab? _renamingTab;
    private bool _sidebarCollapsed;
    private PaletteCommand[] _paletteCommands = [];
    private string _uiTheme = "Night";
    private static string ThemePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux", "ui-theme.txt");

    public MainWindow()
    {
        var commandLineArgs = Environment.GetCommandLineArgs();
        var instanceOption = ReadOption(commandLineArgs, "--instance-id");
        if (instanceOption is not null) InstanceScope.Configure(instanceOption);
        _focus = new Cmux.Core.FocusManager(_workspaces);
        InitializeComponent();
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;

        var (profile, commandLine, font) = ParseArgs(Environment.GetCommandLineArgs());
        _fontOverride = font;
        _requestedProfile = profile;
        _legacyCommandLine = commandLine;
        _smoke = SmokeOptions.Parse(commandLineArgs);
        var layoutPath = ReadOption(Environment.GetCommandLineArgs(), "--layout-path") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux", "workspace-state.json");
        _layoutStore = new LayoutStore(layoutPath);
        _eventServer = new AgentEventServer(agentEvent => Dispatcher.Invoke(() => HandleAgentEvent(agentEvent)));
        _guiCommandServer = new GuiCommandServer(command => Dispatcher.Invoke(() => ExecuteGuiCommand(command)));
        using var iconStream = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/Cmux.ico")).Stream;
        _appIcon = new System.Drawing.Icon(iconStream);
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = "WinPaneDock",
            Visible = true,
        };
        _notifyIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(NavigateToPendingNotification);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += (_, _) => SaveLayout();
        Title = "WinPaneDock — Select profile";

        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += (_, _) =>
        {
            _windowSource = (HwndSource)PresentationSource.FromVisual(this);
            _windowSource.AddHook(OnWindowMessage);
            ApplyWindowChrome(_windowSource.Handle);
            if (IsActive) RegisterWindowHotkeys();
        };
        Activated += (_, _) => RegisterWindowHotkeys();
        Deactivated += (_, _) => UnregisterWindowHotkeys();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => QueueStatusUpdate();
        _statusTimer.Start();
        _gitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _gitTimer.Tick += async (_, _) => await RefreshGitContextsAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _eventServer.Start();
            _guiCommandServer.Start();
            ProfilePicker.ItemsSource = ProfileStore.Load();
            _settings = TerminalSettings.Load();
            var savedTheme = File.Exists(ThemePath) ? File.ReadAllText(ThemePath).Trim() : "Night";
            ThemePicker.SelectedIndex = savedTheme switch { "Day" => 1, "Gray" => 2, _ => 0 };
            WorkspaceRootInput.Text = Environment.CurrentDirectory;
            var snapshot = _smoke.AllowsLayoutPersistence ? _layoutStore.Load() : null;
            if (_layoutStore.LastRecoveryMessage is { } recoveryMessage)
            {
                _layoutRecoveryNotice = $"LAYOUT RECOVERED: {recoveryMessage}";
                App.Log($"Layout recovered from backup: {recoveryMessage}");
                StatusText.Text = _layoutRecoveryNotice;
            }
            if (snapshot is { Workspaces.Length: > 0 })
            {
                _restoring = true;
                _workspaces.Restore(snapshot);
                foreach (var workspace in _workspaces.Workspaces)
                    foreach (var tab in workspace.Tabs) AttachTab(workspace, tab);
            }
            else
            {
                var initial = _workspaces.Create("Default", Environment.CurrentDirectory);
                CreateTab(initial);
            }
            RefreshWorkspaces();
            App.Diagnostics.Write(DiagnosticLevel.Info, "gui.ready",
                $"workspaces={_workspaces.Workspaces.Count} restoring={_restoring}");
            if (_smoke.AllowsLayoutPersistence)
            {
                _gitTimer.Start();
                _ = RefreshGitContextsAsync();
            }
            ProfilePicker.SelectedItem = ((IReadOnlyList<TerminalProfile>)ProfilePicker.ItemsSource)
                .FirstOrDefault(p => p.Name == "PowerShell 7") ?? ProfilePicker.Items[0];
            StatusText.Text = _layoutRecoveryNotice ?? $"Ready. Config: {ProfileStore.FilePath}";
            if (_smoke.PaletteGate)
                Dispatcher.BeginInvoke(new Action(async () => await RunPaletteSmokeAsync()));
            else if (_smoke.ReattachGate)
                Dispatcher.BeginInvoke(new Action(async () => await RunReattachSmokeAsync()));
            else if (_smoke.ReplayGate)
                Dispatcher.BeginInvoke(new Action(async () => await RunReplaySmokeAsync()));
            else if (_smoke.CwdGate)
                Dispatcher.BeginInvoke(new Action(async () => await RunCwdSmokeAsync()));
            else if (_smoke.NotificationGate)
                Dispatcher.BeginInvoke(new Action(async () => await RunNotificationSmokeAsync()));
            else if (_smoke.EventGate)
                Dispatcher.BeginInvoke(new Action(async () => await RunEventSmokeAsync()));
            else if (_restoring)
                Dispatcher.BeginInvoke(new Action(async () => await RestoreShellsAsync()));
            else if (_smoke.M2Gate)
                Dispatcher.BeginInvoke(new Action(async () => await RunM2GateSmokeAsync()));
            else if (_smoke.PaneGate)
                Dispatcher.BeginInvoke(new Action(async () => await RunPaneSmokeAsync()));
            else if (_smoke.TabGate)
                Dispatcher.BeginInvoke(new Action(async () => await RunTabSmokeAsync()));
            else if (_smoke.LifecycleCycles > 0)
                Dispatcher.BeginInvoke(new Action(async () => await RunLifecycleSmokeAsync()));
            else if (_legacyCommandLine is not null)
                StartTerminal(_legacyCommandLine, Environment.CurrentDirectory, "Custom");
            else if (_requestedProfile is not null)
            {
                var selected = ((IReadOnlyList<TerminalProfile>)ProfilePicker.ItemsSource)
                    .FirstOrDefault(p => p.Name.Equals(_requestedProfile, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Unknown profile: {_requestedProfile}");
                ProfilePicker.SelectedItem = selected;
                StartProfile(selected);
            }
            else if (snapshot is not { Workspaces.Length: > 0 } && _smoke.AllowsLayoutPersistence &&
                     ProfilePicker.SelectedItem is TerminalProfile defaultProfile)
                StartProfile(defaultProfile);
            if (!_restoring) MarkLayoutDirty();
        }
        catch (LayoutRecoveryException ex)
        {
            _layoutSaveBlocked = true;
            _lastSaveError = ex.Message;
            App.Log($"Layout recovery blocked persistence: {ex}");
            StatusText.Text = "LAYOUT RECOVERY FAILED: primary and backup are invalid; files were preserved and saving is disabled.";
            WorkspaceMessage.Foreground = System.Windows.Media.Brushes.LightCoral;
            WorkspaceMessage.Text = "The existing layout could not be recovered. Rename or remove the invalid files before restarting; this session will not overwrite them.";
            try
            {
                var initial = _workspaces.Create("Default", Environment.CurrentDirectory);
                CreateTab(initial);
                RefreshWorkspaces();
            }
            catch (Exception initializationError)
            {
                App.Log($"Could not create recovery workspace: {initializationError}");
            }
        }
        catch (Exception ex)
        {
            CloseTerminal(false);
            App.Log($"OnLoaded failed: {ex}");
            StatusText.Text = $"BOOT FAILED: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        if (ProfilePicker.SelectedItem is TerminalProfile profile) StartProfile(profile);
    }

    private void OnEditProfilesClicked(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true, ArgumentList = { ProfileStore.FilePath } });
    }

    private void OnEditSettingsClicked(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true, ArgumentList = { TerminalSettings.FilePath } });
    }

    private void OnRestartClicked(object sender, RoutedEventArgs e)
    {
        if (_lastCommandLine is null || _lastWorkingDirectory is null || _lastProfileName is null) return;
        CloseTerminal(false);
        try { StartTerminal(_lastCommandLine, _lastWorkingDirectory, _lastProfileName); }
        catch (Exception ex) { CloseTerminal(false); StatusText.Text = $"RESTART FAILED: {ex.Message}"; App.Log($"Restart failed: {ex}"); }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => CloseTerminal(false);

    private void OnToggleSidebarClicked(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        SidebarColumn.Width = new GridLength(_sidebarCollapsed ? 0 : 240);
        SidebarPanel.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCommandsClicked(object sender, RoutedEventArgs e) => OpenPalette();

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = !SettingsPopup.IsOpen;

    private void OnThemeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ThemePicker.SelectedItem is not ComboBoxItem item) return;
        var theme = item.Content.ToString() ?? "Night";
        _uiTheme = theme;
        string[] colors = theme switch
        {
            "Day" => ["#FF24262A", "#FF73767C", "#FFF5F4F0", "#FFEAE9E5", "#FFFFFFFF", "#FFFFFFFF", "#FFCAC9C5", "#FFDEDDDA", "#FFD4D3CF", "#FFD5D9DE"],
            "Gray" => ["#FFE5E5E5", "#FFAAAAAA", "#FF343638", "#FF414345", "#FF505255", "#FF393B3D", "#FF65676A", "#FF5B5D60", "#FF707275", "#FF59616A"],
            _ => ["#FFE6E8EC", "#FFADB0B6", "#FF17191D", "#FF20242B", "#FF30343B", "#FF242830", "#FF454A53", "#FF41464F", "#FF505A68", "#FF334B68"],
        };
        var keys = new[] { "UiText", "UiMuted", "UiBackground", "UiPanel", "UiSurface", "UiInput", "UiBorder", "UiHover", "UiPressed", "UiSelected" };
        for (var i = 0; i < keys.Length; i++) Resources[keys[i]] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[i])!);
        Resources["UiTerminal"] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
            theme == "Day" ? "#FFF7F6F2" : theme == "Gray" ? "#FF2B2D30" : "#FF0C0C0C")!);
        foreach (var tab in _tabs.Values)
        {
            tab.View.Background = (System.Windows.Media.Brush)Resources["UiTerminal"];
            foreach (var pane in tab.Panes.Values)
            {
                pane.Header.Background = (System.Windows.Media.Brush)Resources["UiPanel"];
                pane.Label.Foreground = (System.Windows.Media.Brush)Resources["UiMuted"];
                if (_settings is not null) pane.Control.SetTheme(_settings.CreateTheme(theme), _fontOverride ?? _settings.FontFamily, _settings.FontSize);
            }
        }
        if (_windowSource is not null) ApplyWindowChrome(_windowSource.Handle, theme != "Day");
        if (_smoke.AllowsLayoutPersistence)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThemePath)!);
            File.WriteAllText(ThemePath, theme);
        }
    }

    private void OpenPalette()
    {
        _paletteCommands =
        [
            new("New Workspace", () => OnNewWorkspaceClicked(this, new RoutedEventArgs())),
            new("New Group with Terminal", () => RunGuiCommand("new")),
            new("Split Right", () => RunGuiCommand("split", "right")),
            new("Split Down", () => RunGuiCommand("split", "down")),
            new("Close Terminal", () => RunGuiCommand("close-pane")),
            new("Close All Terminals", () => RunGuiCommand("close-all")),
            new("Rename Workspace", () =>
            {
                var name = Microsoft.VisualBasic.Interaction.InputBox("Workspace name", "Rename workspace",
                    _workspaces.Active?.Name ?? "");
                if (string.IsNullOrWhiteSpace(name)) return;
                RunGuiCommand("rename-workspace", name);
            }),
            new("Open Project", OpenProject),
            new("Restart Terminal", () => RunGuiCommand("restart")),
            new("Focus Next Pane", () => RunGuiCommand("focus-next")),
        ];
        _paletteCommands = _paletteCommands.Concat(_workspaces.Workspaces.Select(workspace =>
            new PaletteCommand($"Switch Workspace: {workspace.Name}", () =>
                RunGuiCommand("switch-workspace", workspace.Id.ToString())))).ToArray();
        PalettePopup.IsOpen = true;
        PaletteQuery.Clear();
        FilterPalette();
        PaletteQuery.Focus();
    }

    private void OpenProject()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Open project folder" };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        RunGuiCommand("workspace", Path.GetFileName(dialog.SelectedPath.TrimEnd(Path.DirectorySeparatorChar)),
            dialog.SelectedPath);
    }

    private void OnPaletteQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => FilterPalette();

    private void FilterPalette()
    {
        if (PaletteList is null || PaletteQuery is null) return;
        var query = PaletteQuery.Text.Trim();
        PaletteList.ItemsSource = _paletteCommands
            .Where(command => command.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        PaletteList.SelectedIndex = PaletteList.Items.Count > 0 ? 0 : -1;
    }

    private void OnPaletteQueryKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ClosePalette(); e.Handled = true; }
        else if (e.Key == Key.Enter) { ExecuteSelectedPaletteCommand(); e.Handled = true; }
        else if (e.Key == Key.Down && PaletteList.Items.Count > 0)
        {
            PaletteList.SelectedIndex = Math.Min(PaletteList.SelectedIndex + 1, PaletteList.Items.Count - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && PaletteList.Items.Count > 0)
        {
            PaletteList.SelectedIndex = Math.Max(PaletteList.SelectedIndex - 1, 0);
            e.Handled = true;
        }
    }

    private void OnPaletteCommandDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        ExecuteSelectedPaletteCommand();

    private void ExecuteSelectedPaletteCommand()
    {
        if (PaletteList.SelectedItem is not PaletteCommand command) return;
        ClosePalette();
        try { command.Run(); }
        catch (Exception ex) { StatusText.Text = $"COMMAND FAILED: {ex.Message}"; App.Log($"Command failed: {ex}"); }
    }

    private void ClosePalette()
    {
        PalettePopup.IsOpen = false;
        _activePane?.Control.Focus();
    }

    private void RunGuiCommand(string command, string? argument = null, string? directory = null)
    {
        var response = ExecuteGuiCommand(new GuiCommandRequest(command, argument, directory));
        if (!response.Ok) throw new InvalidOperationException(response.Message);
    }

    private GuiCommandResponse ExecuteGuiCommand(GuiCommandRequest request)
    {
        try
        {
            switch (request.Command)
            {
                case "list":
                    return new(true, Items: _workspaces.Workspaces.Select(workspace =>
                        $"{(workspace.Id == _workspaces.ActiveId ? "*" : " ")} {workspace.Name} ({workspace.Tabs.Count} tabs)").ToArray());
                case "workspace":
                    if (string.IsNullOrWhiteSpace(request.Argument)) return new(false, "Workspace name is required.");
                    var existing = _workspaces.Workspaces.FirstOrDefault(w =>
                        w.Name.Equals(request.Argument, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null) _focus.FocusWorkspace(existing.Id);
                    else
                    {
                        var root = Directory.Exists(request.Directory) ? request.Directory : Environment.CurrentDirectory;
                        CreateTab(_workspaces.Create(request.Argument, root));
                        RefreshWorkspaces();
                        // Same contract as the dialog: a brand new workspace must come up
                        // with a live terminal in its first pane.
                        if (ProfilePicker.SelectedItem is TerminalProfile profile) StartProfile(profile);
                    }
                    RefreshWorkspaces();
                    MarkLayoutDirty();
                    return new(true, $"Workspace: {request.Argument}");
                case "switch-workspace":
                    var target = _workspaces.Workspaces.FirstOrDefault(w =>
                        w.Id.ToString() == request.Argument || w.Name.Equals(request.Argument, StringComparison.OrdinalIgnoreCase));
                    if (target is null) return new(false, "Workspace not found.");
                    _focus.FocusWorkspace(target.Id);
                    RefreshWorkspaces();
                    MarkLayoutDirty();
                    return new(true, $"Workspace: {target.Name}");
                case "rename-workspace":
                    if (_workspaces.Active is null || string.IsNullOrWhiteSpace(request.Argument))
                        return new(false, "Select a Workspace and provide a name.");
                    _workspaces.Rename(_workspaces.Active.Id, request.Argument);
                    RefreshWorkspaces();
                    MarkLayoutDirty();
                    return new(true, $"Renamed to {request.Argument}");
                case "new":
                    if (_workspaces.Active is null) return new(false, "No Workspace selected.");
                    OnNewTabClicked(this, new RoutedEventArgs());
                    return new(true, "New Terminal");
                case "run":
                    if (string.IsNullOrWhiteSpace(request.Argument) || _workspaces.Active is null)
                        return new(false, "Command and Workspace are required.");
                    if (_connection is not null) { CreateTab(_workspaces.Active); RefreshTabs(); }
                    StartTerminal(request.Argument, Directory.Exists(request.Directory)
                        ? request.Directory : _workspaces.Active.RootDirectory, request.Argument);
                    return new(true, $"Started {request.Argument}");
                case "split":
                    if (_activePane is null) return new(false, "No Pane selected.");
                    if (request.Argument == "right") SplitActivePane(PaneOrientation.Right);
                    else if (request.Argument == "down") SplitActivePane(PaneOrientation.Down);
                    else return new(false, "Split direction must be right or down.");
                    return new(true, $"Split {request.Argument}");
                case "close-pane":
                    if (_activeTab is null) return new(false, "No terminal group selected.");
                    OnClosePaneClicked(this, new RoutedEventArgs());
                    return new(true, "Terminal closed");
                case "close-all":
                    return new(true, $"Closed {CloseAllTerminals()} terminals");
                case "focus-next":
                    OnFocusPaneClicked(this, new RoutedEventArgs());
                    return new(true, "Focused next Pane");
                case "focus":
                    if (!int.TryParse(request.Argument, out var index) || _activeTab is null)
                        return new(false, "Focus expects a 1-based Pane number.");
                    var leaves = _activeTab.Tab.RootPane.Terminals().ToArray();
                    if (index < 1 || index > leaves.Length) return new(false, "Pane number is out of range.");
                    FocusPane(_activeTab, leaves[index - 1].SessionId!.Value, true);
                    return new(true, $"Focused Pane {index}");
                case "restart":
                    if (_lastCommandLine is null) return new(false, "No Terminal to restart.");
                    OnRestartClicked(this, new RoutedEventArgs());
                    return new(true, "Terminal restarted");
                default:
                    return new(false, "Unknown command.");
            }
        }
        catch (Exception ex) { App.Log($"Command failed: {ex}"); return new(false, ex.Message); }
    }

    private void OnKillClicked(object sender, RoutedEventArgs e) => CloseTerminal(true);

    private void OnFocusClicked(object sender, RoutedEventArgs e) => Terminal.Focus();

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (_activePane is not { Status: AgentStatus.Waiting or AgentStatus.Completed } pane ||
            !pane.Control.IsKeyboardFocusWithin) return;
        pane.CompletionRead = true;
        UpdatePaneLabel(pane);
        RefreshWorkspaceSidebar();
    }

    private Workspace SelectedWorkspace => (WorkspaceList.SelectedItem as SidebarItem)?.Workspace
        ?? throw new InvalidOperationException("Select a workspace first.");

    private void OnWorkspaceSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_refreshingWorkspaces || WorkspaceList.SelectedItem is not SidebarItem item) return;
        var workspace = item.Workspace;
        _focus.FocusWorkspace(workspace.Id);
        WorkspaceNameInput.Text = workspace.Name;
        WorkspaceRootInput.Text = workspace.RootDirectory;
        WorkspaceMessage.Text = $"{workspace.Name}\n{workspace.RootDirectory}";
        RefreshTabs();
        MarkLayoutDirty();
    }

    private void OnNewWorkspaceClicked(object sender, RoutedEventArgs e)
    {
        NewWorkspaceNameInput.Clear();
        NewWorkspaceRootInput.Text = _workspaces.Active?.RootDirectory ?? Environment.CurrentDirectory;
        NewWorkspaceError.Text = "";
        NewWorkspaceScrim.Visibility = Visibility.Visible;
        NewWorkspacePopup.IsOpen = true;
        NewWorkspaceNameInput.Focus();
    }

    private void CloseNewWorkspacePopup()
    {
        NewWorkspacePopup.IsOpen = false;
        NewWorkspaceScrim.Visibility = Visibility.Collapsed;
    }

    private void OnNewWorkspaceScrimClick(object sender, MouseButtonEventArgs e) => CloseNewWorkspacePopup();

    private void OnNewWorkspaceKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseNewWorkspacePopup();
                break;
            case Key.Enter:
                e.Handled = true;
                OnCreateWorkspaceClicked(this, new RoutedEventArgs());
                break;
        }
    }

    private void OnBrowseWorkspaceRootClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select workspace root directory",
                Multiselect = false,
                InitialDirectory = Directory.Exists(NewWorkspaceRootInput.Text)
                    ? NewWorkspaceRootInput.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog(this) == true) NewWorkspaceRootInput.Text = dialog.FolderName;
        }
        catch (Exception ex) { NewWorkspaceError.Text = $"Folder picker failed: {ex.Message}"; }
    }

    private void OnCreateWorkspaceClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var workspace = _workspaces.Create(NewWorkspaceNameInput.Text, NewWorkspaceRootInput.Text);
            CreateTab(workspace);
            App.Diagnostics.Write(DiagnosticLevel.Info, "workspace.created", $"workspaceId={workspace.Id}");
            CloseNewWorkspacePopup();
            RefreshWorkspaces();
            MarkLayoutDirty();
            // CreateTab only builds the pane tree; the shell is a separate ConPTY launch.
            // Without this the new workspace opens on a blank pane until the user hits Start.
            if (ProfilePicker.SelectedItem is TerminalProfile profile) StartProfile(profile);
        }
        catch (Exception ex) { NewWorkspaceError.Text = ex.Message; }
    }

    private void OnCancelNewWorkspaceClicked(object sender, RoutedEventArgs e) => CloseNewWorkspacePopup();

    private void OnApplyRootClicked(object sender, RoutedEventArgs e) => ChangeWorkspace(() =>
    {
        _workspaces.SetRootDirectory(SelectedWorkspace.Id, WorkspaceRootInput.Text.Trim());
    });

    private void OnRenameWorkspaceClicked(object sender, RoutedEventArgs e) => ChangeWorkspace(() =>
    {
        _workspaces.Rename(SelectedWorkspace.Id, WorkspaceNameInput.Text);
    });

    private void OnDeleteWorkspaceClicked(object sender, RoutedEventArgs e) => ChangeWorkspace(() =>
    {
        var workspace = SelectedWorkspace;
        var states = workspace.Tabs.Select(tab => _tabs[tab.Id]).ToArray();
        var pending = CaptureAndClearLaunches(workspace.Tabs.SelectMany(tab => tab.RootPane.Terminals()
            .Select(leaf => (workspace.Id, tab, leaf))));
        foreach (var item in pending) _closingSessions.Add(item.SessionId);
        if (!CommitLayout("workspace-close-intent", force: true))
        {
            foreach (var item in pending) _closingSessions.Remove(item.SessionId);
            RestoreLaunches(pending);
            MarkLayoutDirty();
            throw new InvalidOperationException("Layout could not record the close intent; workspace was kept.");
        }
        if (states.SelectMany(state => state.Panes.Values).Any(pane => !ClosePaneConnection(pane, false)))
            throw new InvalidOperationException("One or more terminals could not be closed; the workspace was kept for retry.");
        foreach (var state in states)
        {
            TerminalHost.Children.Remove(state.View);
            _tabs.Remove(state.Tab.Id);
        }
        _workspaces.Delete(workspace.Id);
        App.Diagnostics.Write(DiagnosticLevel.Info, "workspace.deleted", $"workspaceId={workspace.Id}");
    }, immediate: true);
    private void OnPinWorkspaceClicked(object sender, RoutedEventArgs e) => ChangeWorkspace(() => _workspaces.TogglePin(SelectedWorkspace.Id));
    private void OnMoveWorkspaceUpClicked(object sender, RoutedEventArgs e) => ChangeWorkspace(() => _workspaces.Move(SelectedWorkspace.Id, -1));
    private void OnMoveWorkspaceDownClicked(object sender, RoutedEventArgs e) => ChangeWorkspace(() => _workspaces.Move(SelectedWorkspace.Id, 1));

    private void ChangeWorkspace(Action action, bool immediate = false)
    {
        try
        {
            action();
            RefreshWorkspaces();
            if (immediate) CommitLayout("workspace");
            else MarkLayoutDirty();
        }
        catch (Exception ex)
        {
            WorkspaceMessage.Foreground = System.Windows.Media.Brushes.LightCoral;
            WorkspaceMessage.Text = ex.Message;
        }
    }

    private void RefreshWorkspaces()
    {
        _focus.Reconcile();
        RefreshWorkspaceSidebar();
        WorkspaceMessage.SetResourceReference(TextBlock.ForegroundProperty, "UiMuted");
        WorkspaceMessage.Text = _workspaces.Active is { } workspace
            ? $"{workspace.Name}\n{workspace.RootDirectory}"
            : "No workspace selected.";
        if (_workspaces.Active is { } active)
        {
            WorkspaceNameInput.Text = active.Name;
            WorkspaceRootInput.Text = active.RootDirectory;
        }
        else { WorkspaceNameInput.Clear(); WorkspaceRootInput.Clear(); }
        RefreshTabs();
    }

    private void RefreshWorkspaceSidebar()
    {
        var signature = BuildSidebarSignature();
        if (signature == _sidebarSignature) return;
        _sidebarSignature = signature;
        var items = _workspaces.Workspaces.Select(workspace =>
        {
            var groups = workspace.Tabs.Select(tab =>
            {
                var terminals = _tabs.TryGetValue(tab.Id, out var tabState)
                    ? tab.RootPane.Terminals()
                        .Select(leaf => leaf.SessionId is { } id && tabState.Panes.TryGetValue(id, out var pane) ? pane : null)
                        .OfType<PaneState>()
                        .Select(p => new SidebarTerminalItem(
                            $"Terminal {p.Number}  {(p.Detection.Type == AgentType.Unknown ? p.LastProfileName ?? "" : p.Detection.Type.ToString())}",
                            StatusBrush(p), StatusDescription(p))).ToArray()
                    : [];
                return new SidebarGroupItem(tab.Title, terminals);
            }).ToArray();
            var branch = _gitContexts.TryGetValue(workspace.Id, out var git) && git is not null
                ? $"⑂  {git.Branch}{(git.IsDirty ? " *" : "")}" : null;
            return new SidebarItem(workspace, $"{(workspace.IsPinned ? "📌 " : "")}{workspace.Name}", branch, groups);
        }).ToArray();
        _refreshingWorkspaces = true;
        WorkspaceList.ItemsSource = items;
        WorkspaceList.SelectedItem = items.FirstOrDefault(i => i.Workspace.Id == _workspaces.ActiveId);
        _refreshingWorkspaces = false;
    }

    private string BuildSidebarSignature()
    {
        var builder = new StringBuilder();
        builder.Append(_workspaces.ActiveId);
        foreach (var workspace in _workspaces.Workspaces)
        {
            builder.Append('|').Append(workspace.Id).Append(':').Append(workspace.IsPinned)
                .Append(':').Append(workspace.Name);
            var branch = _gitContexts.TryGetValue(workspace.Id, out var git) && git is not null
                ? $"{git.Branch}*{git.IsDirty}" : "-";
            builder.Append(':').Append(branch);
            foreach (var tab in workspace.Tabs)
            {
                builder.Append('|').Append(tab.Id).Append(':').Append(tab.Title);
                if (!_tabs.TryGetValue(tab.Id, out var tabState)) continue;
                foreach (var leaf in tab.RootPane.Terminals())
                {
                    if (leaf.SessionId is not { } id || !tabState.Panes.TryGetValue(id, out var pane)) continue;
                    builder.Append(';').Append(pane.Number).Append(':').Append(pane.Detection.Type)
                        .Append(':').Append(pane.Status).Append(':').Append(pane.CompletionRead)
                        .Append(':').Append(pane.Connection is not null).Append(':').Append(pane.LastProfileName)
                        .Append(':').Append(HasRecentOutput(pane));
                }
            }
        }
        return builder.ToString();
    }

    private static bool HasRecentOutput(PaneState pane) => pane.Connection is { LastOutputTick: > 0 } connection &&
        Environment.TickCount64 - connection.LastOutputTick < 4000;

    private static System.Windows.Media.Brush StatusBrush(PaneState pane) => pane.Connection is null ? Brushes.Gray : pane.Status switch
    {
        AgentStatus.Working => Brushes.DodgerBlue,
        AgentStatus.Unknown or AgentStatus.Idle when HasRecentOutput(pane) => Brushes.DodgerBlue,
        AgentStatus.Waiting or AgentStatus.Completed when !pane.CompletionRead => Brushes.Orange,
        AgentStatus.Waiting or AgentStatus.Completed => Brushes.MediumSeaGreen,
        AgentStatus.Error => Brushes.IndianRed,
        _ => Brushes.Gray,
    };

    private static string StatusDescription(PaneState pane) => pane.Connection is null ? "Terminal closed" : pane.Status switch
    {
        AgentStatus.Working => "Running",
        AgentStatus.Unknown or AgentStatus.Idle when HasRecentOutput(pane) => "Recent terminal output",
        AgentStatus.Waiting or AgentStatus.Completed when !pane.CompletionRead => "Finished, unread",
        AgentStatus.Waiting or AgentStatus.Completed => "Finished, read",
        AgentStatus.Error => "Error",
        _ => pane.Detection.Type == AgentType.Unknown ? "No agent detected" : "Agent has not reported task status",
    };

    private static string ActivityLabel(PaneState pane) => pane.Detection.Type switch
    {
        AgentType.Unknown when pane.Status == AgentStatus.Unknown => "No agent detected",
        AgentType.Unknown => $"Agent: {pane.Status}",
        _ when pane.Status == AgentStatus.Unknown => $"{pane.Detection.Type}: status not reported",
        _ => $"{pane.Detection.Type}: {pane.Status}",
    };

    private void CreateTab(Workspace workspace)
    {
        var number = workspace.Tabs.Count + 1;
        while (workspace.Tabs.Any(tab => tab.Title.Equals($"Group {number}", StringComparison.OrdinalIgnoreCase))) number++;
        var tab = _workspaces.CreateTab(workspace.Id, $"Group {number}");
        AttachTab(workspace, tab);
        App.Diagnostics.Write(DiagnosticLevel.Info, "group.created", $"workspaceId={workspace.Id} groupId={tab.Id}");
        MarkLayoutDirty();
    }

    private void AttachTab(Workspace workspace, TerminalTab tab)
    {
        var state = new TabState(tab, workspace.Id) { View = { Visibility = Visibility.Hidden } };
        state.View.SetResourceReference(Canvas.BackgroundProperty, "UiTerminal");
        foreach (var leaf in tab.RootPane.Terminals()) AddPane(state, leaf.SessionId!.Value);
        state.View.SizeChanged += (_, _) => RenderPaneLayout(state);
        _tabs.Add(tab.Id, state);
        TerminalHost.Children.Add(state.View);
    }

    private void OnNewTabClicked(object sender, RoutedEventArgs e)
    {
        ChangeWorkspace(() => CreateTab(SelectedWorkspace));
        if (ProfilePicker.SelectedItem is TerminalProfile profile) StartProfile(profile);
    }

    private void OnCloseTabClicked(object sender, RoutedEventArgs e) => ChangeWorkspace(() =>
    {
        if (_activeTab is null) return;
        var state = _activeTab;
        var pending = CaptureAndClearLaunches(state.Tab.RootPane.Terminals()
            .Select(leaf => (state.WorkspaceId, state.Tab, leaf)));
        foreach (var item in pending) _closingSessions.Add(item.SessionId);
        if (!CommitLayout("group-close-intent", force: true))
        {
            foreach (var item in pending) _closingSessions.Remove(item.SessionId);
            RestoreLaunches(pending);
            MarkLayoutDirty();
            throw new InvalidOperationException("Layout could not record the close intent; group was kept.");
        }
        if (state.Panes.Values.Any(pane => !ClosePaneConnection(pane, false)))
            throw new InvalidOperationException("One or more terminals could not be closed; the group was kept for retry.");
        TerminalHost.Children.Remove(state.View);
        _tabs.Remove(state.Tab.Id);
        _workspaces.CloseTab(SelectedWorkspace.Id, state.Tab.Id);
        App.Diagnostics.Write(DiagnosticLevel.Info, "group.closed", $"workspaceId={state.WorkspaceId} groupId={state.Tab.Id}");
        RefreshTabs();
    }, immediate: true);

    private void OnCloseTabItemClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TerminalTab tab) return;
        TerminalTabs.SelectedItem = tab;
        OnCloseTabClicked(sender, e);
        e.Handled = true;
    }

    private void OnGroupTitleClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || (sender as FrameworkElement)?.DataContext is not TerminalTab tab) return;
        TerminalTabs.SelectedItem = tab;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _renamingTab = tab;
            GroupNameInput.Text = tab.Title;
            GroupRenameError.Text = "";
            GroupRenamePopup.IsOpen = true;
            GroupNameInput.Focus();
            GroupNameInput.SelectAll();
        }), DispatcherPriority.Background);
        e.Handled = true;
    }

    private void OnSaveGroupNameClicked(object sender, RoutedEventArgs e)
    {
        if (_renamingTab is null || _workspaces.Active is not { } workspace) return;
        try
        {
            _workspaces.RenameTab(workspace.Id, _renamingTab.Id, GroupNameInput.Text);
            GroupRenamePopup.IsOpen = false;
            _renamingTab = null;
            RefreshTabs();
            RefreshWorkspaceSidebar();
            MarkLayoutDirty();
        }
        catch (Exception ex) { GroupRenameError.Text = ex.Message; }
    }

    private void OnCancelGroupRenameClicked(object sender, RoutedEventArgs e)
    {
        GroupRenamePopup.IsOpen = false;
        _renamingTab = null;
    }

    private void OnGroupNameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnSaveGroupNameClicked(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Escape) { OnCancelGroupRenameClicked(sender, new RoutedEventArgs()); e.Handled = true; }
    }

    private void OnTabSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_refreshingTabs || TerminalTabs.SelectedItem is not TerminalTab tab || _workspaces.Active is not { } workspace) return;
        _focus.FocusTab(workspace.Id, tab.Id);
        ShowTab(tab.Id);
        MarkLayoutDirty();
    }

    private void RefreshTabs()
    {
        _refreshingTabs = true;
        var workspace = _workspaces.Active;
        TerminalTabs.ItemsSource = workspace?.Tabs.ToArray();
        TerminalTabs.SelectedItem = workspace?.Tabs.FirstOrDefault(t => t.Id == workspace.ActiveTabId);
        _refreshingTabs = false;
        ShowTab(workspace?.ActiveTabId);
    }

    private void ShowTab(Guid? tabId)
    {
        _focus.Reconcile();
        _activeTab = tabId is { } id ? _tabs[id] : null;
        foreach (var state in _tabs.Values)
            state.View.Visibility = state == _activeTab ? Visibility.Visible : Visibility.Hidden;
        _activePane = _focus.Current.TerminalSessionId is { } sessionId && _activeTab is not null
            ? _activeTab.Panes[sessionId] : null;
        if (_activeTab is { } current) Dispatcher.BeginInvoke(new Action(() => RenderPaneLayout(current)));
        UpdatePaneBorders();
        var running = _connection is not null;
        ProfilePicker.IsEnabled = true;
        StartButton.IsEnabled = !running && _activeTab is not null;
        RestartButton.IsEnabled = _lastCommandLine is not null;
        CloseButton.IsEnabled = running;
        KillButton.IsEnabled = running;
        FocusButton.IsEnabled = running;
        Title = _activeTab is null ? "WinPaneDock — No tab" : $"WinPaneDock — {_lastProfileName ?? _activeTab.Tab.Title}";
        if (running) QueueStatusUpdate();
        else StatusText.Text = _activeTab is null ? "Create a group to start a terminal." : "Terminal closed. Use Start to reopen this pane.";
    }

    private void MarkLayoutDirty()
    {
        if (!_smoke.AllowsLayoutPersistence || _restoring || _layoutSaveBlocked) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private bool SaveLayout(bool force = false)
    {
        _saveTimer.Stop();
        if (!_smoke.AllowsLayoutPersistence || (_restoring && !force)) return true;
        if (_layoutSaveBlocked)
        {
            ShowLayoutSaveError(_lastSaveError ?? "Layout persistence is disabled until the invalid files are repaired.");
            return false;
        }
        try
        {
            _layoutStore.Save(_workspaces.Export());
            _lastSaveError = null;
            App.Diagnostics.Write(DiagnosticLevel.Debug, "layout.saved", $"workspaces={_workspaces.Workspaces.Count}");
            return true;
        }
        catch (Exception ex)
        {
            _lastSaveError = ex.Message;
            ShowLayoutSaveError($"LAYOUT SAVE FAILED: {ex.Message}");
            App.Log($"Layout save failed: {ex}");
            return false;
        }
    }

    private bool CommitLayout(string operation, bool force = false)
    {
        var saved = SaveLayout(force);
        App.Diagnostics.Write(DiagnosticLevel.Info, "layout.commit",
            $"operation={operation} saved={saved} workspaces={_workspaces.Workspaces.Count}");
        return saved;
    }

    private void ShowLayoutSaveError(string message)
    {
        if (StatusText is not null) StatusText.Text = message;
        if (WorkspaceMessage is not null)
        {
            WorkspaceMessage.Foreground = System.Windows.Media.Brushes.LightCoral;
            WorkspaceMessage.Text = message;
        }
    }

    private static LaunchState CaptureLaunch(PaneNode leaf) =>
        new(leaf.ProfileName, leaf.CommandLine, leaf.WorkingDirectory, leaf.Title);

    private List<PendingLaunchClear> CaptureAndClearLaunches(IEnumerable<(Guid WorkspaceId, TerminalTab Tab, PaneNode Leaf)> leaves)
    {
        var pending = new List<PendingLaunchClear>();
        foreach (var item in leaves)
        {
            pending.Add(new PendingLaunchClear(item.WorkspaceId, item.Tab.Id, item.Leaf.SessionId!.Value,
                CaptureLaunch(item.Leaf)));
            _workspaces.ClearTerminalLaunch(item.WorkspaceId, item.Tab.Id, item.Leaf.SessionId!.Value);
        }
        return pending;
    }

    private void RestoreLaunches(IEnumerable<PendingLaunchClear> pending)
    {
        foreach (var item in pending)
            _workspaces.SetTerminalLaunch(item.WorkspaceId, item.TabId, item.SessionId,
                item.State.ProfileName, item.State.CommandLine, item.State.WorkingDirectory, item.State.Title);
    }

    private async Task CleanupExplicitCloseSessionsAsync()
    {
        var emptyLaunches = _workspaces.Workspaces.SelectMany(workspace => workspace.Tabs)
            .SelectMany(tab => tab.RootPane.Terminals())
            .Where(leaf => string.IsNullOrWhiteSpace(leaf.CommandLine) && leaf.SessionId is not null)
            .Select(leaf => leaf.SessionId!.Value)
            .ToHashSet();
        if (emptyLaunches.Count == 0) return;
        try
        {
            var response = await SessionHostClient.SendAsync(new HostRequest("list"), 500).ConfigureAwait(false);
            SessionHostClient.RequireCompatibleIdentity(response,
                Path.Combine(AppContext.BaseDirectory, "Cmux.SessionHost.exe"));
            foreach (var session in response.Sessions ?? [])
            {
                if (!Guid.TryParse(session.SessionId, out var sessionId) || !emptyLaunches.Contains(sessionId)) continue;
                await SessionHostClient.SendAsync(new HostRequest("close", session.SessionId,
                    LeaseId: session.LeaseId)).ConfigureAwait(false);
                App.Diagnostics.Write(DiagnosticLevel.Info, "orphan.closed",
                    $"sessionId={session.SessionId} processId={session.ProcessId}");
            }
        }
        catch (Exception ex)
        {
            App.Log($"Explicit-close reconciliation failed: {ex}");
        }
    }

    private async Task RestoreShellsAsync()
    {
        var target = _focus.Current;
        App.Diagnostics.Write(DiagnosticLevel.Info, "restore.start", $"workspaces={_workspaces.Workspaces.Count}");
        await CleanupExplicitCloseSessionsAsync().ConfigureAwait(true);
        try
        {
            foreach (var workspace in _workspaces.Workspaces)
            {
                App.Diagnostics.Write(DiagnosticLevel.Debug, "restore.workspace.begin", $"workspaceId={workspace.Id}");
                _focus.FocusWorkspace(workspace.Id);
                App.Diagnostics.Write(DiagnosticLevel.Debug, "restore.workspace.focused", $"workspaceId={workspace.Id}");
                RefreshWorkspaces();
                App.Diagnostics.Write(DiagnosticLevel.Debug, "restore.workspace.refreshed", $"workspaceId={workspace.Id}");
                foreach (var tab in workspace.Tabs)
                {
                    _focus.FocusTab(workspace.Id, tab.Id);
                    RefreshTabs();
                    App.Diagnostics.Write(DiagnosticLevel.Debug, "restore.tab.ready", $"tabId={tab.Id}");
                    await Task.Delay(1);
                    App.Diagnostics.Write(DiagnosticLevel.Debug, "restore.tab.resumed", $"tabId={tab.Id}");
                    foreach (var leaf in tab.RootPane.Terminals())
                    {
                        if (string.IsNullOrWhiteSpace(leaf.CommandLine)) continue;
                        var state = _tabs[tab.Id];
                        FocusPane(state, leaf.SessionId!.Value, true);
                        App.Diagnostics.Write(DiagnosticLevel.Debug, "restore.pane.focused", $"sessionId={leaf.SessionId}");
                        await Task.Delay(1);
                        App.Diagnostics.Write(DiagnosticLevel.Debug, "restore.pane.resumed", $"sessionId={leaf.SessionId}");
                        try
                        {
                            var directory = Directory.Exists(leaf.WorkingDirectory) ? leaf.WorkingDirectory
                                : Directory.Exists(workspace.RootDirectory) ? workspace.RootDirectory : Environment.CurrentDirectory;
                            App.Diagnostics.Write(DiagnosticLevel.Debug, "restore.terminal.start",
                                $"sessionId={leaf.SessionId} command={leaf.ProfileName}");
                            await StartTerminalAsync(leaf.CommandLine, directory, leaf.ProfileName);
                        }
                        catch (Exception ex)
                        {
                            CloseTerminal(false);
                            App.Log($"Restore terminal failed: {ex}");
                        }
                    }
                }
            }
        }
        finally
        {
            // Focus restoration is best effort: the focused group/pane may have been closed
            // while restore was still starting shells. It must never keep _restoring set,
            // because MarkLayoutDirty is a no-op for the rest of the session while it is.
            try
            {
                if (target.WorkspaceId is { } workspaceId && _workspaces.Workspaces.Any(w => w.Id == workspaceId))
                {
                    _focus.FocusWorkspace(workspaceId);
                    if (target.TabId is { } tabId) _focus.FocusTab(workspaceId, tabId);
                    if (target.PaneId is { } paneId && target.TabId is { } focusedTabId)
                        _focus.FocusPane(workspaceId, focusedTabId, paneId);
                }
                RefreshWorkspaces();
            }
            catch (Exception ex)
            {
                App.Log($"Restore focus fallback failed: {ex}");
            }
            finally
            {
                _restoring = false;
            }
            App.Diagnostics.Write(DiagnosticLevel.Info, "restore.complete", $"workspaces={_workspaces.Workspaces.Count}");
            MarkLayoutDirty();
        }
    }

    private static System.Windows.Controls.Primitives.ScrollBar? FindTerminalScrollBar(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.Primitives.ScrollBar bar) return bar;
            if (FindTerminalScrollBar(child) is { } nested) return nested;
        }
        return null;
    }

    private void AddPane(TabState tab, Guid sessionId)
    {
        var pane = new PaneState();
        pane.Control.Loaded += (_, _) =>
        {
            if (FindTerminalScrollBar(pane.Control) is { } scrollBar &&
                TryFindResource(typeof(System.Windows.Controls.Primitives.ScrollBar)) is Style scrollStyle)
            {
                scrollBar.Style = scrollStyle;
                scrollBar.Width = 10;
            }
            // TerminalContainer throws away output that arrives before its native window
            // exists, and a TUI will not repaint itself afterwards. Replay the buffered tail
            // once the control is realised, posted so the layout pass that builds the native
            // terminal has completed.
            if (pane.NeedsOutputReplay)
            {
                pane.NeedsOutputReplay = false;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(() => pane.Connection?.ReplayBufferedOutput()));
            }
        };
        pane.Header.SetResourceReference(DockPanel.BackgroundProperty, "UiPanel");
        pane.Label.SetResourceReference(TextBlock.ForegroundProperty, "UiMuted");
        pane.Header.MouseLeftButtonDown += (_, _) => FocusPane(tab, sessionId, true);
        pane.Control.GotFocus += (_, _) => FocusPane(tab, sessionId, false);
        pane.CloseButton.Click += (_, _) =>
        {
            FocusPane(tab, sessionId, false);
            OnClosePaneClicked(this, new RoutedEventArgs());
        };
        tab.Panes.Add(sessionId, pane);
        tab.View.Children.Add(pane.Frame);
    }

    private void FocusPane(TabState tab, Guid sessionId, bool focusControl)
    {
        if (tab != _activeTab) return;
        var leaf = tab.Tab.RootPane.Terminals().FirstOrDefault(p => p.SessionId == sessionId);
        if (leaf is null) return;
        _focus.FocusTerminal(tab.WorkspaceId, tab.Tab.Id, sessionId);
        _activePane = tab.Panes[sessionId];
        if (_activePane.Connection is { IsPrepared: true } prepared && !_activePane.ControlAttached)
        {
            AttachControlConnection(_activePane, prepared);
            // A pane attached lazily through focus never went through Boot(), so it still
            // needs the configured padding, theme and font.
            ApplyTerminalAppearance(_activePane);
        }
        if (_activePane.Status is AgentStatus.Waiting or AgentStatus.Completed &&
            (focusControl || IsActive && _activePane.Control.IsKeyboardFocusWithin))
            _activePane.CompletionRead = true;
        UpdatePaneLabel(_activePane);
        UpdatePaneBorders();
        if (focusControl) _activePane.Control.Focus();
        QueueStatusUpdate();
        MarkLayoutDirty();
    }

    private void UpdatePaneBorders()
    {
        if (_activeTab is null) return;
        foreach (var pane in _activeTab.Panes.Values)
            pane.Frame.BorderBrush = pane == _activePane
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(110, 168, 255)) : Brushes.Transparent;
    }

    private static void UpdatePaneLabel(PaneState pane)
    {
        var status = pane.Connection is null
            ? pane.LastProfileName is null ? "Not started" : "Closed"
            : pane.LastProfileName ?? "Running";
        pane.Label.Text = $"  Terminal {pane.Number}  •  {status}";
        pane.StatusDot.Background = pane.Connection is null ? Brushes.Transparent : StatusBrush(pane);
        pane.StatusDot.ToolTip = pane.Connection is null ? "Terminal closed" : StatusDescription(pane);
    }

    private void OnSplitRightClicked(object sender, RoutedEventArgs e) => SplitActivePane(PaneOrientation.Right);
    private void OnSplitDownClicked(object sender, RoutedEventArgs e) => SplitActivePane(PaneOrientation.Down);

    private void SplitActivePane(PaneOrientation orientation)
    {
        if (_activeTab is null || _activePane is null) return;
        var tab = _activeTab;
        var oldPane = _activePane;
        var directory = Directory.Exists(oldPane.CurrentDirectory) ? oldPane.CurrentDirectory : oldPane.LastWorkingDirectory;
        var newLeaf = _workspaces.SplitPane(tab.WorkspaceId, tab.Tab.Id, tab.Tab.ActivePaneId, orientation);
        AddPane(tab, newLeaf.SessionId!.Value);
        MarkLayoutDirty();
        ShowTab(tab.Tab.Id);
        RenderPaneLayout(tab);
        tab.View.UpdateLayout();
        if (ProfilePicker.SelectedItem is TerminalProfile profile) StartProfile(profile, directory);
        Terminal.Focus();
    }

    private void OnClosePaneClicked(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null || _activePane is null) return;
        if (_activeTab.Tab.RootPane.Terminals().Count() <= 1)
        {
            CloseTerminal(false);
            return;
        }
        var tab = _activeTab;
        var pane = _activePane;
        var leaf = tab.Tab.RootPane.Terminals().First(p => p.Id == tab.Tab.ActivePaneId);
        var sessionId = leaf.SessionId!.Value;
        _closingSessions.Add(sessionId);
        var pending = CaptureAndClearLaunches([(tab.WorkspaceId, tab.Tab, leaf)]);
        if (!CommitLayout("pane-close-intent", force: true))
        {
            _closingSessions.Remove(sessionId);
            RestoreLaunches(pending);
            MarkLayoutDirty();
            return;
        }
        if (!ClosePaneConnection(pane, false))
        {
            StatusText.Text = "Terminal close failed; the pane was kept so you can retry.";
            return;
        }
        if (!_workspaces.ClosePane(tab.WorkspaceId, tab.Tab.Id, leaf.Id)) return;
        tab.View.Children.Remove(pane.Frame);
        tab.Panes.Remove(leaf.SessionId!.Value);
        ShowTab(tab.Tab.Id);
        RenderPaneLayout(tab);
    }

    private void OnFocusPaneClicked(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null) return;
        var leaves = _activeTab.Tab.RootPane.Terminals().ToArray();
        if (leaves.Length == 0) return;
        var index = Array.FindIndex(leaves, p => p.Id == _activeTab.Tab.ActivePaneId);
        FocusPane(_activeTab, leaves[(index + 1) % leaves.Length].SessionId!.Value, true);
    }

    private void FocusPaneInDirection(int dx, int dy)
    {
        if (_activeTab is null || _activePane is null) return;
        var current = _activePane.Frame;
        var x = Canvas.GetLeft(current) + current.Width / 2;
        var y = Canvas.GetTop(current) + current.Height / 2;
        var candidate = _activeTab.Panes
            .Where(pair => pair.Value != _activePane)
            .Select(pair => new
            {
                pair.Key,
                X = Canvas.GetLeft(pair.Value.Frame) + pair.Value.Frame.Width / 2 - x,
                Y = Canvas.GetTop(pair.Value.Frame) + pair.Value.Frame.Height / 2 - y,
            })
            .Where(p => p.X * dx + p.Y * dy > 0)
            .OrderBy(p => Math.Abs(dx != 0 ? p.Y : p.X) * 2 + Math.Abs(p.X * dx + p.Y * dy))
            .FirstOrDefault();
        if (candidate is not null) FocusPane(_activeTab, candidate.Key, true);
    }

    private void RenderPaneLayout(TabState tab)
    {
        var width = tab.View.ActualWidth;
        var height = tab.View.ActualHeight;
        if (width < 20 || height < 20) return;
        var used = new HashSet<Guid>();
        var terminalNumber = 0;
        Layout(tab.Tab.RootPane, 0, 0, width, height);
        foreach (var id in tab.Splitters.Keys.Where(id => !used.Contains(id)).ToArray())
        {
            tab.View.Children.Remove(tab.Splitters[id]);
            tab.Splitters.Remove(id);
        }

        void Layout(PaneNode node, double x, double y, double w, double h)
        {
            if (node.IsTerminal)
            {
                var pane = tab.Panes[node.SessionId!.Value];
                pane.Number = ++terminalNumber;
                UpdatePaneLabel(pane);
                var frame = pane.Frame;
                Canvas.SetLeft(frame, x);
                Canvas.SetTop(frame, y);
                frame.Width = Math.Max(2, w);
                frame.Height = Math.Max(2, h);
                return;
            }

            used.Add(node.Id);
            if (!tab.Splitters.TryGetValue(node.Id, out var splitter))
            {
                splitter = new Thumb { Background = Brushes.DimGray, Cursor = node.Orientation == PaneOrientation.Right ? Cursors.SizeWE : Cursors.SizeNS };
                var splitNode = node;
                splitter.DragDelta += (_, e) =>
                {
                    var length = (double)tab.Splitters[splitNode.Id].Tag;
                    var delta = splitNode.Orientation == PaneOrientation.Right ? e.HorizontalChange : e.VerticalChange;
                    _workspaces.ResizePane(tab.WorkspaceId, tab.Tab.Id, splitNode.Id, Math.Clamp(splitNode.Ratio + delta / length, 0.15, 0.85));
                    RenderPaneLayout(tab);
                    MarkLayoutDirty();
                };
                tab.Splitters.Add(node.Id, splitter);
                tab.View.Children.Add(splitter);
            }
            if (node.Orientation == PaneOrientation.Right)
            {
                var available = Math.Max(4, w - 6);
                var left = available * node.Ratio;
                splitter.Tag = available;
                Canvas.SetLeft(splitter, x + left); Canvas.SetTop(splitter, y);
                splitter.Width = 6; splitter.Height = h;
                Layout(node.ChildA!, x, y, left, h);
                Layout(node.ChildB!, x + left + 6, y, available - left, h);
            }
            else
            {
                var available = Math.Max(4, h - 6);
                var top = available * node.Ratio;
                splitter.Tag = available;
                Canvas.SetLeft(splitter, x); Canvas.SetTop(splitter, y + top);
                splitter.Width = w; splitter.Height = 6;
                Layout(node.ChildA!, x, y, w, top);
                Layout(node.ChildB!, x, y + top + 6, w, available - top);
            }
        }
    }

    private void StartProfile(TerminalProfile profile, string? inheritedDirectory = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profile.Command))
                throw new InvalidDataException($"{profile.Name}: set command in {ProfileStore.FilePath}, then reopen WinPaneDock.");
            var directory = string.IsNullOrWhiteSpace(profile.StartingDirectory)
                ? inheritedDirectory ?? _workspaces.Active?.RootDirectory ?? Environment.CurrentDirectory
                : profile.WorkingDirectory;
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException(directory);
            var commandLine = profile.CommandLine;
            if (profile.Name is "PowerShell" or "PowerShell 7" &&
                !profile.Args.Any(arg => arg.Equals("-Command", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-File", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-EncodedCommand", StringComparison.OrdinalIgnoreCase)))
            {
                var script = Path.Combine(AppContext.BaseDirectory, "cmux-prompt.ps1").Replace("'", "''");
                commandLine += $" -NoExit -Command \". '{script}'\"";
            }
            StartTerminal(commandLine, directory, profile.Name);
        }
        catch (Exception ex)
        {
            CloseTerminal(false);
            App.Log($"Profile start failed: {ex}");
            StatusText.Text = $"START FAILED: {ex.Message}";
        }
    }

    private void StartTerminal(string commandLine, string workingDirectory, string profileName)
    {
        if (_activeTab is not null && _activePane is not null)
        {
            var sessionId = _activeTab.Tab.RootPane.Terminals()
                .First(p => p.Id == _activeTab.Tab.ActivePaneId).SessionId;
            if (sessionId is { } id) _closingSessions.Remove(id);
        }
        _ = StartTerminalAsync(commandLine, workingDirectory, profileName);
    }

    private async Task StartTerminalAsync(string commandLine, string workingDirectory, string profileName)
    {
        if (_connection is not null || _activeTab is null || _activePane is null) return;
        var tabState = _activeTab;
        var paneState = _activePane;
        var pane = tabState.Tab.RootPane.Terminals().First(p => p.Id == tabState.Tab.ActivePaneId);
        var sessionId = pane.SessionId!.Value;
        var environment = new Dictionary<string, string>
        {
            ["CMUX_WORKSPACE_ID"] = tabState.WorkspaceId.ToString(),
            ["CMUX_PANE_ID"] = pane.Id.ToString(),
            ["CMUX_SESSION_ID"] = sessionId.ToString(),
            ["CMUX_PIPE_NAME"] = _eventServer.PipeName,
        };
        var connection = new ConptyConnection(commandLine, workingDirectory, environment);
        connection.Faulted += OnConnectionFaulted;
        _connection = connection;
        paneState.Status = AgentStatus.Unknown;
        paneState.CompletionRead = false;
        paneState.CurrentDirectory = workingDirectory;
        _lastCommandLine = commandLine;
        _lastWorkingDirectory = workingDirectory;
        _lastProfileName = profileName;
        _workspaces.SetTerminalLaunch(tabState.WorkspaceId, tabState.Tab.Id, sessionId,
            profileName, commandLine, workingDirectory, profileName);
        MarkLayoutDirty();
        Title = $"WinPaneDock — {profileName}";
        UpdatePaneLabel(paneState);
        ProfilePicker.IsEnabled = true;
        StartButton.IsEnabled = false;
        RestartButton.IsEnabled = true;
        CloseButton.IsEnabled = true;
        KillButton.IsEnabled = true;
        FocusButton.IsEnabled = true;

        try
        {
            await connection.PrepareHostAsync().ConfigureAwait(true);
            await Dispatcher.InvokeAsync(() =>
            {
                if (paneState.Connection != connection) return;
                if (_closingSessions.Contains(sessionId))
                {
                    // The explicit close already ended the shell, so this connection is
                    // disposable. Clear the pane reference too, otherwise the pane keeps a
                    // disposed connection and Start stays disabled until the user retries.
                    paneState.Connection = null;
                    paneState.ControlAttached = false;
                    connection.Dispose();
                    if (ReferenceEquals(_activePane, paneState)) UpdatePaneLabel(paneState);
                    return;
                }
                _workspaces.SetTerminalLaunch(tabState.WorkspaceId, tabState.Tab.Id, sessionId,
                    profileName, commandLine, workingDirectory, profileName);
                App.Diagnostics.Write(DiagnosticLevel.Info, "terminal.started",
                    $"workspaceId={tabState.WorkspaceId} groupId={tabState.Tab.Id} sessionId={sessionId} processId={connection.ProcessId} profile={profileName}");
                if (ReferenceEquals(_activePane, paneState)) Boot();
                MarkLayoutDirty();
            });
        }
        catch (Exception ex)
        {
            App.Log($"Terminal start failed: {ex}");
            await Dispatcher.InvokeAsync(() =>
            {
                if (paneState.Connection != connection) return;
                paneState.Connection = null;
                connection.Dispose();
                if (ReferenceEquals(_activePane, paneState))
                {
                    CloseTerminal(false);
                    StatusText.Text = $"START FAILED: {ex.Message}";
                }
            });
        }
    }

    private void CloseTerminal(bool kill)
    {
        if (_activePane is null || _activeTab is null) return;
        var leaf = _activeTab.Tab.RootPane.Terminals().First(p => p.Id == _activeTab.Tab.ActivePaneId);
        var sessionId = leaf.SessionId!.Value;
        _closingSessions.Add(sessionId);
        var pending = CaptureAndClearLaunches([(_activeTab.WorkspaceId, _activeTab.Tab, leaf)]);
        if (!CommitLayout("terminal-close-intent", force: true))
        {
            _closingSessions.Remove(sessionId);
            RestoreLaunches(pending);
            MarkLayoutDirty();
            return;
        }
        if (!ClosePaneConnection(_activePane, kill))
            StatusText.Text = "Terminal close failed; the pane was kept so you can retry.";
    }

    private int CloseAllTerminals()
    {
        var leaves = _tabs.Values.SelectMany(tab => tab.Tab.RootPane.Terminals()
            .Select(leaf => (tab.WorkspaceId, tab.Tab, leaf))).ToArray();
        var pending = CaptureAndClearLaunches(leaves);
        foreach (var item in pending) _closingSessions.Add(item.SessionId);
        if (!CommitLayout("close-all-intent", force: true))
        {
            foreach (var item in pending) _closingSessions.Remove(item.SessionId);
            RestoreLaunches(pending);
            MarkLayoutDirty();
            return 0;
        }
        var closed = 0;
        var failed = 0;
        foreach (var tab in _tabs.Values)
            foreach (var leaf in tab.Tab.RootPane.Terminals())
            {
                if (leaf.SessionId is not { } sessionId) continue;
                var pane = tab.Panes[sessionId];
                if (pane.Connection is not null)
                {
                    if (ClosePaneConnection(pane, false)) closed++;
                    else failed++;
                }
            }
        RefreshWorkspaces();
        if (failed > 0) StatusText.Text = $"{failed} terminal(s) could not be closed; retry them individually.";
        return closed;
    }

    private bool ClosePaneConnection(PaneState state, bool kill, bool detached = false)
    {
        if (state.Connection is null) return true;
        var connection = state.Connection;
        try
        {
            if (detached) connection.Detach();
            else if (kill) connection.Kill();
            else connection.Close();
        }
        catch (Exception ex)
        {
            App.Log($"Terminal close failed for session {connection.CommandLine}: {ex}");
            if (state == _activePane) StatusText.Text = $"Terminal close failed: {ex.Message}";
            return false;
        }

        state.Connection = null;
        state.ControlAttached = false;
        try { state.Control.Connection = null!; }
        catch (Exception ex) { App.Log($"Terminal control detach failed: {ex}"); }
        UpdatePaneLabel(state);
        App.Diagnostics.Write(DiagnosticLevel.Info, detached ? "terminal.detached" : "terminal.closed",
            $"processId={connection.ProcessId} killed={kill}");
        if (state != _activePane) return true;
        ProfilePicker.IsEnabled = true;
        StartButton.IsEnabled = true;
        RestartButton.IsEnabled = _lastCommandLine is not null;
        CloseButton.IsEnabled = false;
        KillButton.IsEnabled = false;
        FocusButton.IsEnabled = false;
        Title = "WinPaneDock — Select profile";
        StatusText.Text = kill ? "Terminal killed." : "Terminal closed.";
        return true;
    }

    private void OnConnectionFaulted(object? sender, string message)
    {
        App.Log($"Terminal connection fault: {message}");
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (sender is not ConptyConnection connection || _tabs.Values
                .SelectMany(tab => tab.Panes.Values).All(pane => pane.Connection != connection)) return;
            StatusText.Text = $"TERMINAL CONNECTION ERROR: {message}";
        }));
    }

    private void ApplyTerminalAppearance(PaneState pane)
    {
        if (_settings is null) return;
        pane.Control.Margin = new Thickness(_settings.Padding);
        pane.Control.SetTheme(_settings.CreateTheme(_uiTheme),
            _fontOverride ?? _settings.FontFamily, _settings.FontSize);
    }

    /// <summary>
    /// Binds a connection to a pane's control. TerminalContainer discards output that arrives
    /// before its native window exists, so a control that is not loaded yet must be flagged for
    /// a buffered replay once it realises.
    /// </summary>
    private void AttachControlConnection(PaneState pane, ConptyConnection connection)
    {
        if (!pane.Control.IsLoaded) pane.NeedsOutputReplay = true;
        // The Connection setter calls connection.Start() (official behaviour).
        pane.Control.Connection = connection;
        pane.ControlAttached = true;
    }

    private void Boot()
    {
        var pane = _activePane ?? throw new InvalidOperationException("No pane selected.");
        AttachControlConnection(pane, pane.Connection!);
        ApplyTerminalAppearance(pane);
        Terminal.Focus();
        QueueStatusUpdate();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        App.Diagnostics.Write(DiagnosticLevel.Info, "gui.closing", $"attachedTerminals={_tabs.Values.Sum(t => t.Panes.Values.Count(p => p.Connection is not null))}");
        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
        _statusTimer.Stop();
        _gitTimer.Stop();
        SaveLayout(force: _tabs.Values.SelectMany(tab => tab.Panes.Values)
            .Any(pane => pane.Connection is not null));
        _eventServer.Dispose();
        _guiCommandServer.Dispose();
        _notifyIcon.Dispose();
        _appIcon.Dispose();
        UnregisterWindowHotkeys();
        _windowSource?.RemoveHook(OnWindowMessage);
        try
        {
            foreach (var state in _tabs.Values)
                foreach (var pane in state.Panes.Values)
                {
                    ClosePaneConnection(pane, false, detached: true);
                }
        }
        catch (Exception ex)
        {
            // 关闭路径不允许抛出导致 Crash (M0 Gate)。
            App.Diagnostics.Write(DiagnosticLevel.Error, "gui.close.failed", exception: ex);
        }
    }

    private async Task RefreshGitContextsAsync()
    {
        if (_gitRefreshing) return;
        _gitRefreshing = true;
        try
        {
            var workspaces = _workspaces.Workspaces.ToArray();
            var directories = workspaces.Select(w => w.RootDirectory)
                .Concat(_tabs.Values.SelectMany(t => t.Panes.Values).Select(p => p.CurrentDirectory).OfType<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            var referenced = directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _gitWorktreeRoots.Keys.Where(directory => !referenced.Contains(directory)).ToArray())
            {
                _gitWorktreeRoots.Remove(stale);
                _gitRootResolvedAt.Remove(stale);
                _gitResolutionFailures.Remove(stale);
            }
            foreach (var stale in _gitRootResolvedAt.Keys.Where(directory => !referenced.Contains(directory)).ToArray())
                _gitRootResolvedAt.Remove(stale);
            foreach (var stale in _gitResolutionFailures.Keys.Where(directory => !referenced.Contains(directory)).ToArray())
                _gitResolutionFailures.Remove(stale);

            var now = DateTimeOffset.UtcNow;
            var unresolved = directories.Where(directory =>
                (!_gitWorktreeRoots.ContainsKey(directory) &&
                    (!_gitResolutionFailures.TryGetValue(directory, out var failedAt) || now - failedAt > TimeSpan.FromSeconds(30))) ||
                (_gitRootResolvedAt.TryGetValue(directory, out var resolvedAt) && now - resolvedAt > TimeSpan.FromMinutes(5))).ToArray();
            if (unresolved.Length > 0)
            {
                var resolved = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                await Task.WhenAll(unresolved.Select(async directory =>
                {
                    await _gitQueryGate.WaitAsync();
                    try { resolved[directory] = await GitProjectContext.ReadWorktreeRootAsync(directory); }
                    finally { _gitQueryGate.Release(); }
                }));
                foreach (var pair in resolved)
                {
                    if (pair.Value is null)
                    {
                        _gitWorktreeRoots.Remove(pair.Key);
                        _gitRootResolvedAt.Remove(pair.Key);
                        _gitResolutionFailures[pair.Key] = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        _gitWorktreeRoots[pair.Key] = pair.Value;
                        _gitRootResolvedAt[pair.Key] = DateTimeOffset.UtcNow;
                        _gitResolutionFailures.Remove(pair.Key);
                    }
                }
            }

            var roots = directories.Where(directory =>
                !_gitResolutionFailures.TryGetValue(directory, out var failedAt) ||
                now - failedAt > TimeSpan.FromSeconds(30))
                .Select(directory => _gitWorktreeRoots.TryGetValue(directory, out var root) && root is not null ? root : directory)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var contexts = new ConcurrentDictionary<string, GitProjectContext?>(StringComparer.OrdinalIgnoreCase);
            await Task.WhenAll(roots.Select(async root =>
            {
                await _gitQueryGate.WaitAsync();
                try { contexts[root] = await GitProjectContext.ReadAsync(root); }
                finally { _gitQueryGate.Release(); }
            }));

            var current = new Dictionary<string, GitProjectContext?>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in directories)
            {
                var root = _gitWorktreeRoots.TryGetValue(directory, out var resolvedRoot) && resolvedRoot is not null
                    ? resolvedRoot : directory;
                current[directory] = contexts.TryGetValue(root, out var context) ? context : null;
            }
            foreach (var stale in _directoryGitContexts.Keys
                .Where(directory => !current.ContainsKey(directory)).ToArray())
                _directoryGitContexts.Remove(stale);
            foreach (var pair in current) _directoryGitContexts[pair.Key] = pair.Value;
            foreach (var workspace in workspaces)
                _gitContexts[workspace.Id] = _directoryGitContexts.GetValueOrDefault(workspace.RootDirectory);
            RefreshWorkspaceSidebar();
            UpdateStatusText();
        }
        catch (Exception ex) { App.Log($"Git context refresh failed: {ex}"); }
        finally { _gitRefreshing = false; }
    }

    private void RegisterWindowHotkeys()
    {
        if (_windowSource is null) return;
        foreach (var (id, modifiers, key) in new (int, uint, uint)[]
        {
            (1, 0x0002, 0x56), // Ctrl+V
            (2, 0x0001, 0x44), // Alt+D
            (3, 0x0005, 0x44), // Alt+Shift+D
            (4, 0x0001, 0x25), (5, 0x0001, 0x26), (6, 0x0001, 0x27), (7, 0x0001, 0x28),
            (8, 0x0006, 0x50), // Ctrl+Shift+P
        })
        {
            if (_registeredHotkeys.Contains(id)) continue;
            if (RegisterHotKey(_windowSource.Handle, id, modifiers, key)) _registeredHotkeys.Add(id);
            else App.Log($"Hotkey {id} registration failed: {Marshal.GetLastWin32Error()}");
        }
    }

    private void UnregisterWindowHotkeys()
    {
        if (_windowSource is null) return;
        foreach (var id in _registeredHotkeys) UnregisterHotKey(_windowSource.Handle, id);
        _registeredHotkeys.Clear();
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312)
        {
            switch (wParam.ToInt32())
            {
                case 1:
                    if (Keyboard.FocusedElement is System.Windows.Controls.TextBox textBox) textBox.Paste();
                    else if (Clipboard.ContainsText()) _connection?.WriteInput(Clipboard.GetText());
                    break;
                case 2: SplitActivePane(PaneOrientation.Right); break;
                case 3: SplitActivePane(PaneOrientation.Down); break;
                case 4: FocusPaneInDirection(-1, 0); break;
                case 5: FocusPaneInDirection(0, -1); break;
                case 6: FocusPaneInDirection(1, 0); break;
                case 7: FocusPaneInDirection(0, 1); break;
                case 8: OpenPalette(); break;
                default: return IntPtr.Zero;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnThreadFilterMessage(ref MSG message, ref bool handled)
    {
        if (handled || message.message != 0x0100 || _windowSource is null) return;
        var key = message.wParam.ToInt32();
        if (key is not (0x09 or 0x25 or 0x26 or 0x27 or 0x28)) return;
        if (!IsChild(_windowSource.Handle, message.hwnd)) return;
        var className = new StringBuilder(64);
        if (GetClassName(message.hwnd, className, className.Capacity) == 0 ||
            !string.Equals(className.ToString(), "HwndTerminalClass", StringComparison.Ordinal)) return;
        TranslateMessage(ref message);
        DispatchMessage(ref message);
        handled = true;
    }

    private static void ApplyWindowChrome(IntPtr hwnd, bool dark = true)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var enabled = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var rounded = 2;
        var mica = 2;
        DwmSetWindowAttribute(hwnd, 33, ref rounded, sizeof(int));
        DwmSetWindowAttribute(hwnd, 38, ref mica, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG message);

    private void QueueStatusUpdate()
    {
        if (_statusRefreshing) return;
        var probes = CaptureStatusProbes();
        if (probes.All(probe => probe.Connection is null))
        {
            ApplyStatusProbes(probes, new AgentProcessSnapshot([]));
            return;
        }
        _statusRefreshing = true;
        var generation = Interlocked.Increment(ref _statusGeneration);
        _ = Task.Run(() =>
        {
            try
            {
                var snapshot = new AgentProcessSnapshot(AgentDetector.Scan());
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation != _statusGeneration)
                    {
                        _statusRefreshing = false;
                        return;
                    }
                    _statusRefreshing = false;
                    ApplyStatusProbes(probes, snapshot);
                }));
            }
            catch (Exception ex)
            {
                App.Log($"Background status scan failed: {ex}");
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(new Action(() => _statusRefreshing = false));
            }
        });
    }

    private void UpdateStatus()
    {
        Interlocked.Increment(ref _statusGeneration);
        var probes = CaptureStatusProbes();
        var snapshot = probes.Any(probe => probe.Connection is not null)
            ? new AgentProcessSnapshot(AgentDetector.Scan()) : new AgentProcessSnapshot([]);
        ApplyStatusProbes(probes, snapshot);
    }

    private StatusProbe[] CaptureStatusProbes() => _tabs.Values
        .SelectMany(tab => tab.Panes.Values)
        .Select(pane => new StatusProbe(pane, pane.Connection, pane.Connection?.ProcessId ?? 0,
            pane.Connection?.CommandLine ?? "", pane.LastProfileName ?? ""))
        .ToArray();

    private void ApplyStatusProbes(StatusProbe[] probes, AgentProcessSnapshot processes)
    {
        foreach (var probe in probes)
        {
            if (probe.Pane.Connection != probe.Connection) continue;
            probe.Pane.Detection = probe.Connection is not null
                ? AgentDetector.Detect(probe.ProcessId, processes, probe.CommandLine, probe.ProfileName)
                : new AgentDetection(AgentType.Unknown, AgentDetectionSource.None, 0);
            UpdatePaneLabel(probe.Pane);
        }
        RefreshWorkspaceSidebar();
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        if (_connection is null) return;
        var pid = _connection.ProcessId;
        var alive = false;
        if (pid != 0)
        {
            try { using var process = Process.GetProcessById(pid); alive = !process.HasExited; }
            catch (ArgumentException) { }
        }
        var directory = _activePane?.CurrentDirectory;
        var git = directory is not null && _directoryGitContexts.TryGetValue(directory, out var context)
            ? context : null;
        var branch = git is null ? "" : $"    •    {git.Branch}{(git.IsDirty ? " *" : "")}";
        StatusText.Text =
            $"{_workspaces.Active?.Name}  /  {_activeTab?.Tab.Title}    •    {_lastProfileName}    •    {(_activePane is { } activePane ? ActivityLabel(activePane) : "No terminal")}    •    {directory}{branch}";
        StatusText.ToolTip =
            $"{_connection.CommandLine}\nAgent source: {_activePane?.Detection.Source}\nFont: {_fontOverride ?? _settings?.FontFamily}\n{Terminal.Rows}x{Terminal.Columns}\nShell PID: {pid} {(alive ? "alive" : "n/a")}";
    }

    private bool HandleAgentEvent(AgentEvent agentEvent)
    {
        var tab = _tabs.Values.FirstOrDefault(t => t.WorkspaceId == agentEvent.WorkspaceId &&
            t.Tab.RootPane.Terminals().Any(p => p.Id == agentEvent.PaneId && p.SessionId == agentEvent.SessionId));
        if (tab is null || !tab.Panes.TryGetValue(agentEvent.SessionId, out var pane) || pane.Connection is null)
            return false;
        if (agentEvent.WorkingDirectory is { } directory)
        {
            if (!Directory.Exists(directory)) return false;
            pane.CurrentDirectory = Path.GetFullPath(directory);
            QueueStatusUpdate();
            UpdateStatusText();
            return true;
        }
        var previous = pane.Status;
        pane.Status = agentEvent.Status;
        if (previous != agentEvent.Status)
            App.Diagnostics.Write(DiagnosticLevel.Info, "agent.status.changed",
                $"sessionId={agentEvent.SessionId} from={previous} to={agentEvent.Status}");
        if (agentEvent.Status == AgentStatus.Working) pane.CompletionRead = false;
        if (agentEvent.Status is AgentStatus.Waiting or AgentStatus.Completed)
            pane.CompletionRead = IsActive && pane.Control.IsKeyboardFocusWithin;
        if (previous == AgentStatus.Working && agentEvent.Status == AgentStatus.Waiting &&
            (!IsActive || _focus.Current.TerminalSessionId != agentEvent.SessionId))
        {
            _pendingNotification = agentEvent;
            _waitingNotificationCount++;
            var workspace = _workspaces.Workspaces.First(w => w.Id == agentEvent.WorkspaceId);
            var agent = pane.Detection.Type == AgentType.Unknown ? "Agent" : pane.Detection.Type.ToString();
            _notifyIcon.ShowBalloonTip(5000, $"{agent} is waiting",
                $"{workspace.Name} / {tab.Tab.Title}", System.Windows.Forms.ToolTipIcon.Info);
        }
        UpdatePaneLabel(pane);
        QueueStatusUpdate();
        UpdateStatusText();
        return true;
    }

    private void NavigateToPendingNotification()
    {
        if (_pendingNotification is not { } target) return;
        _pendingNotification = null;
        var tab = _tabs.Values.FirstOrDefault(t => t.WorkspaceId == target.WorkspaceId &&
            t.Tab.RootPane.Terminals().Any(p => p.Id == target.PaneId && p.SessionId == target.SessionId));
        if (tab is null) return;
        _focus.FocusPane(target.WorkspaceId, tab.Tab.Id, target.PaneId);
        RefreshWorkspaces();
        FocusPane(tab, target.SessionId, true);
        WindowState = WindowState.Normal;
        Activate();
    }

    private static (string? profile, string? commandLine, string? font) ParseArgs(string[] args)
    {
        string? profile = null;
        string? commandLine = null;
        string? font = null;

        for (var i = 1; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--cmd":
                    commandLine = args[++i];
                    break;
                case "--profile":
                    profile = args[++i];
                    break;
                case "--font":
                    font = args[++i];
                    break;
            }
        }

        return (profile, commandLine, font);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 1; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
