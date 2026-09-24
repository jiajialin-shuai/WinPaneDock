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
using Cmux.Core;
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
    private sealed record SidebarItem(Workspace Workspace, string Name, string? Branch, SidebarTerminalItem[] Terminals);
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
    private readonly Dictionary<Guid, GitProjectContext?> _gitContexts = [];
    private readonly Dictionary<string, GitProjectContext?> _directoryGitContexts = new(StringComparer.OrdinalIgnoreCase);
    private bool _gitRefreshing;
    private readonly string? _requestedProfile;
    private readonly string? _legacyCommandLine;
    private readonly int _lifecycleSmokeCycles;
    private readonly bool _tabSmoke;
    private readonly bool _paneSmoke;
    private readonly bool _m2GateSmoke;
    private readonly bool _eventSmoke;
    private readonly bool _notificationSmoke;
    private readonly bool _cwdSmoke;
    private readonly bool _paletteSmoke;
    private readonly bool _saveEnabled;
    private readonly LayoutStore _layoutStore;
    private readonly AgentEventServer _eventServer;
    private readonly GuiCommandServer _guiCommandServer;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private AgentEvent? _pendingNotification;
    private int _waitingNotificationCount;
    private readonly DispatcherTimer _saveTimer;
    private bool _restoring;
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
        _focus = new Cmux.Core.FocusManager(_workspaces);
        InitializeComponent();
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;

        var (profile, commandLine, font) = ParseArgs(Environment.GetCommandLineArgs());
        _fontOverride = font;
        _requestedProfile = profile;
        _legacyCommandLine = commandLine;
        _lifecycleSmokeCycles = ParseSmokeCycles(Environment.GetCommandLineArgs());
        _tabSmoke = Environment.GetCommandLineArgs().Contains("--tab-smoke");
        _paneSmoke = Environment.GetCommandLineArgs().Contains("--pane-smoke");
        _m2GateSmoke = Environment.GetCommandLineArgs().Contains("--m2-gate-smoke");
        _eventSmoke = Environment.GetCommandLineArgs().Contains("--event-smoke");
        _notificationSmoke = Environment.GetCommandLineArgs().Contains("--notification-smoke");
        _cwdSmoke = Environment.GetCommandLineArgs().Contains("--cwd-smoke");
        _paletteSmoke = Environment.GetCommandLineArgs().Contains("--palette-smoke");
        _saveEnabled = _lifecycleSmokeCycles == 0 && !_tabSmoke && !_paneSmoke && !_m2GateSmoke && !_eventSmoke && !_notificationSmoke && !_cwdSmoke && !_paletteSmoke;
        var layoutPath = ReadOption(Environment.GetCommandLineArgs(), "--layout-path") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux", "workspace-state.json");
        _layoutStore = new LayoutStore(layoutPath);
        _eventServer = new AgentEventServer(agentEvent => Dispatcher.Invoke(() => HandleAgentEvent(agentEvent)));
        _guiCommandServer = new GuiCommandServer(command => Dispatcher.Invoke(() => ExecuteGuiCommand(command)));
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "cmux",
            Visible = true,
        };
        _notifyIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(NavigateToPendingNotification);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += (_, _) => SaveLayout();
        Title = "cmux — Select profile";

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
        _statusTimer.Tick += (_, _) => UpdateStatus();
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
            var snapshot = _saveEnabled ? _layoutStore.Load() : null;
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
            if (_saveEnabled)
            {
                _gitTimer.Start();
                _ = RefreshGitContextsAsync();
            }
            ProfilePicker.SelectedItem = ((IReadOnlyList<TerminalProfile>)ProfilePicker.ItemsSource)
                .FirstOrDefault(p => p.Name == "PowerShell 7") ?? ProfilePicker.Items[0];
            StatusText.Text = $"Ready. Config: {ProfileStore.FilePath}";
            if (_paletteSmoke)
                Dispatcher.BeginInvoke(new Action(async () => await RunPaletteSmokeAsync()));
            else if (_cwdSmoke)
                Dispatcher.BeginInvoke(new Action(async () => await RunCwdSmokeAsync()));
            else if (_notificationSmoke)
                Dispatcher.BeginInvoke(new Action(async () => await RunNotificationSmokeAsync()));
            else if (_eventSmoke)
                Dispatcher.BeginInvoke(new Action(async () => await RunEventSmokeAsync()));
            else if (_restoring)
                Dispatcher.BeginInvoke(new Action(async () => await RestoreShellsAsync()));
            else if (_m2GateSmoke)
                Dispatcher.BeginInvoke(new Action(async () => await RunM2GateSmokeAsync()));
            else if (_paneSmoke)
                Dispatcher.BeginInvoke(new Action(async () => await RunPaneSmokeAsync()));
            else if (_tabSmoke)
                Dispatcher.BeginInvoke(new Action(async () => await RunTabSmokeAsync()));
            else if (_lifecycleSmokeCycles > 0)
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
            else if (snapshot is not { Workspaces.Length: > 0 } && _saveEnabled &&
                     ProfilePicker.SelectedItem is TerminalProfile defaultProfile)
                StartProfile(defaultProfile);
            if (!_restoring) MarkLayoutDirty();
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
        if (_saveEnabled)
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
        NewWorkspacePopup.IsOpen = true;
        NewWorkspaceNameInput.Focus();
    }

    private void OnCreateWorkspaceClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var workspace = _workspaces.Create(NewWorkspaceNameInput.Text, NewWorkspaceRootInput.Text);
            CreateTab(workspace);
            NewWorkspacePopup.IsOpen = false;
            RefreshWorkspaces();
            MarkLayoutDirty();
        }
        catch (Exception ex) { NewWorkspaceError.Text = ex.Message; }
    }

    private void OnCancelNewWorkspaceClicked(object sender, RoutedEventArgs e) => NewWorkspacePopup.IsOpen = false;

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
        foreach (var tab in workspace.Tabs)
        {
            var state = _tabs[tab.Id];
            foreach (var pane in state.Panes.Values) ClosePaneConnection(pane, false);
            TerminalHost.Children.Remove(state.View);
            _tabs.Remove(tab.Id);
        }
        _workspaces.Delete(workspace.Id);
    });
    private void OnPinWorkspaceClicked(object sender, RoutedEventArgs e) => ChangeWorkspace(() => _workspaces.TogglePin(SelectedWorkspace.Id));
    private void OnMoveWorkspaceUpClicked(object sender, RoutedEventArgs e) => ChangeWorkspace(() => _workspaces.Move(SelectedWorkspace.Id, -1));
    private void OnMoveWorkspaceDownClicked(object sender, RoutedEventArgs e) => ChangeWorkspace(() => _workspaces.Move(SelectedWorkspace.Id, 1));

    private void ChangeWorkspace(Action action)
    {
        try { action(); RefreshWorkspaces(); MarkLayoutDirty(); }
        catch (Exception ex) { WorkspaceMessage.Foreground = System.Windows.Media.Brushes.LightCoral; WorkspaceMessage.Text = ex.Message; }
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
        var items = _workspaces.Workspaces.Select(workspace =>
        {
            var running = new List<PaneState>();
            foreach (var tab in workspace.Tabs)
            {
                if (!_tabs.TryGetValue(tab.Id, out var tabState)) continue;
                foreach (var leaf in tab.RootPane.Terminals())
                    if (leaf.SessionId is { } id && tabState.Panes.TryGetValue(id, out var pane))
                        running.Add(pane);
            }
            var details = running.Select(p => new SidebarTerminalItem(
                $"Terminal {p.Number}  { (p.Detection.Type == AgentType.Unknown ? p.LastProfileName ?? "" : p.Detection.Type.ToString())}",
                StatusBrush(p), StatusDescription(p))).ToArray();
            var branch = _gitContexts.TryGetValue(workspace.Id, out var git) && git is not null
                ? $"⑂  {git.Branch}{(git.IsDirty ? " *" : "")}" : null;
            return new SidebarItem(workspace, $"{(workspace.IsPinned ? "📌 " : "")}{workspace.Name}", branch, details);
        }).ToArray();
        var signature = string.Join("|", items.Select(i =>
            $"{i.Workspace.Id}:{i.Name}:{i.Branch}:{string.Join(',', i.Terminals.Select(t => t.Name + t.StatusText))}")) + _workspaces.ActiveId;
        if (signature == _sidebarSignature) return;
        _sidebarSignature = signature;
        _refreshingWorkspaces = true;
        WorkspaceList.ItemsSource = items;
        WorkspaceList.SelectedItem = items.FirstOrDefault(i => i.Workspace.Id == _workspaces.ActiveId);
        _refreshingWorkspaces = false;
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
        _ => "No task status",
    };

    private void CreateTab(Workspace workspace)
    {
        var number = workspace.Tabs.Count + 1;
        while (workspace.Tabs.Any(tab => tab.Title.Equals($"Group {number}", StringComparison.OrdinalIgnoreCase))) number++;
        var tab = _workspaces.CreateTab(workspace.Id, $"Group {number}");
        AttachTab(workspace, tab);
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
        foreach (var pane in state.Panes.Values) ClosePaneConnection(pane, false);
        TerminalHost.Children.Remove(state.View);
        _tabs.Remove(state.Tab.Id);
        _workspaces.CloseTab(SelectedWorkspace.Id, state.Tab.Id);
        RefreshTabs();
    });

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
        Title = _activeTab is null ? "cmux — No tab" : $"cmux — {_lastProfileName ?? _activeTab.Tab.Title}";
        if (running) UpdateStatus();
        else StatusText.Text = _activeTab is null ? "Create a group to start a terminal." : "Terminal closed. Use Start to reopen this pane.";
    }

    private void MarkLayoutDirty()
    {
        if (!_saveEnabled || _restoring) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveLayout()
    {
        _saveTimer.Stop();
        if (!_saveEnabled || _restoring) return;
        try { _layoutStore.Save(_workspaces.Export()); }
        catch (Exception ex) { App.Log($"Layout save failed: {ex}"); }
    }

    private async Task RestoreShellsAsync()
    {
        var target = _focus.Current;
        try
        {
            foreach (var workspace in _workspaces.Workspaces)
            {
                _focus.FocusWorkspace(workspace.Id);
                RefreshWorkspaces();
                foreach (var tab in workspace.Tabs)
                {
                    _focus.FocusTab(workspace.Id, tab.Id);
                    RefreshTabs();
                    await Dispatcher.Yield(DispatcherPriority.Loaded);
                    foreach (var leaf in tab.RootPane.Terminals())
                    {
                        if (string.IsNullOrWhiteSpace(leaf.CommandLine)) continue;
                        var state = _tabs[tab.Id];
                        FocusPane(state, leaf.SessionId!.Value, true);
                        await Dispatcher.Yield(DispatcherPriority.Loaded);
                        try
                        {
                            var directory = Directory.Exists(leaf.WorkingDirectory) ? leaf.WorkingDirectory
                                : Directory.Exists(workspace.RootDirectory) ? workspace.RootDirectory : Environment.CurrentDirectory;
                            StartTerminal(leaf.CommandLine, directory, leaf.ProfileName);
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
            if (target.WorkspaceId is { } workspaceId && _workspaces.Workspaces.Any(w => w.Id == workspaceId))
            {
                _focus.FocusWorkspace(workspaceId);
                if (target.TabId is { } tabId) _focus.FocusTab(workspaceId, tabId);
                if (target.PaneId is { } paneId && target.TabId is { } focusedTabId)
                    _focus.FocusPane(workspaceId, focusedTabId, paneId);
            }
            RefreshWorkspaces();
            _restoring = false;
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
        if (_activePane.Status is AgentStatus.Waiting or AgentStatus.Completed &&
            (focusControl || IsActive && _activePane.Control.IsKeyboardFocusWithin))
            _activePane.CompletionRead = true;
        UpdatePaneLabel(_activePane);
        UpdatePaneBorders();
        if (focusControl) _activePane.Control.Focus();
        UpdateStatus();
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
        ClosePaneConnection(pane, false);
        if (!_workspaces.ClosePane(tab.WorkspaceId, tab.Tab.Id, leaf.Id)) return;
        tab.View.Children.Remove(pane.Frame);
        tab.Panes.Remove(leaf.SessionId!.Value);
        MarkLayoutDirty();
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

    private async Task RunLifecycleSmokeAsync()
    {
        var initialHandles = Process.GetCurrentProcess().HandleCount;
        var failures = new List<string>();
        for (var i = 0; i < _lifecycleSmokeCycles; i++)
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
        var result = $"cycles={_lifecycleSmokeCycles}; initialHandles={initialHandles}; finalHandles={finalHandles}; failures={failures.Count}{Environment.NewLine}" +
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
            HandleAgentEvent(working with { Status = AgentStatus.Waiting });
            if (_waitingNotificationCount != 1 || _pendingNotification is null)
                failure = "Background Working -> Waiting did not notify.";
            NavigateToPendingNotification();
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

    private void StartProfile(TerminalProfile profile, string? inheritedDirectory = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profile.Command))
                throw new InvalidDataException($"{profile.Name}: set command in {ProfileStore.FilePath}, then reopen cmux.");
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
        if (_connection is not null) return;
        var pane = _activeTab!.Tab.RootPane.Terminals().First(p => p.Id == _activeTab.Tab.ActivePaneId);
        var environment = new Dictionary<string, string>
        {
            ["CMUX_WORKSPACE_ID"] = _activeTab.WorkspaceId.ToString(),
            ["CMUX_PANE_ID"] = pane.Id.ToString(),
            ["CMUX_SESSION_ID"] = pane.SessionId!.Value.ToString(),
            ["CMUX_PIPE_NAME"] = _eventServer.PipeName,
        };
        _connection = new ConptyConnection(commandLine, workingDirectory, environment);
        _activePane!.Status = AgentStatus.Unknown;
        _activePane.CompletionRead = false;
        Title = $"cmux — {profileName}";
        Boot();
        _lastCommandLine = commandLine;
        _lastWorkingDirectory = workingDirectory;
        _activePane!.CurrentDirectory = workingDirectory;
        _lastProfileName = profileName;
        UpdatePaneLabel(_activePane!);
        var sessionId = pane.SessionId!.Value;
        _workspaces.SetTerminalLaunch(_activeTab.WorkspaceId, _activeTab.Tab.Id, sessionId,
            profileName, commandLine, workingDirectory, profileName);
        MarkLayoutDirty();
        ProfilePicker.IsEnabled = true;
        StartButton.IsEnabled = false;
        RestartButton.IsEnabled = true;
        CloseButton.IsEnabled = true;
        KillButton.IsEnabled = true;
        FocusButton.IsEnabled = true;
    }

    private void CloseTerminal(bool kill)
    {
        if (_activePane is null || _activeTab is null) return;
        var leaf = _activeTab.Tab.RootPane.Terminals().First(p => p.Id == _activeTab.Tab.ActivePaneId);
        try { ClosePaneConnection(_activePane, kill); }
        finally
        {
            _workspaces.ClearTerminalLaunch(_activeTab.WorkspaceId, _activeTab.Tab.Id, leaf.SessionId!.Value);
            MarkLayoutDirty();
        }
    }

    private int CloseAllTerminals()
    {
        var closed = 0;
        foreach (var tab in _tabs.Values)
            foreach (var leaf in tab.Tab.RootPane.Terminals())
            {
                if (leaf.SessionId is not { } sessionId) continue;
                var pane = tab.Panes[sessionId];
                if (pane.Connection is not null)
                {
                    ClosePaneConnection(pane, false);
                    closed++;
                }
                _workspaces.ClearTerminalLaunch(tab.WorkspaceId, tab.Tab.Id, sessionId);
            }
        RefreshWorkspaces();
        MarkLayoutDirty();
        SaveLayout();
        return closed;
    }

    private void ClosePaneConnection(PaneState state, bool kill)
    {
        if (state.Connection is null) return;
        var connection = state.Connection;
        state.Connection = null;
        UpdatePaneLabel(state);
        try { state.Control.Connection = null!; }
        finally { if (kill) connection.Kill(); else connection.Close(); }
        if (state != _activePane) return;
        ProfilePicker.IsEnabled = true;
        StartButton.IsEnabled = true;
        RestartButton.IsEnabled = _lastCommandLine is not null;
        CloseButton.IsEnabled = false;
        KillButton.IsEnabled = false;
        FocusButton.IsEnabled = false;
        Title = "cmux — Select profile";
        StatusText.Text = kill ? "Terminal killed." : "Terminal closed.";
    }

    private void Boot()
    {
        // Connection 的 setter 会调用 connection.Start() (官方行为)。
        Terminal.Connection = _connection;
        Terminal.Margin = new Thickness(_settings!.Padding);
        Terminal.SetTheme(_settings.CreateTheme(_uiTheme), _fontOverride ?? _settings.FontFamily, _settings.FontSize);
        Terminal.Focus();
        UpdateStatus();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
        _statusTimer.Stop();
        _gitTimer.Stop();
        SaveLayout();
        _eventServer.Dispose();
        _guiCommandServer.Dispose();
        _notifyIcon.Dispose();
        UnregisterWindowHotkeys();
        _windowSource?.RemoveHook(OnWindowMessage);
        try
        {
            foreach (var state in _tabs.Values)
                foreach (var pane in state.Panes.Values)
                {
                    pane.Connection?.Detach();
                    ClosePaneConnection(pane, false);
                }
        }
        catch (Exception ex)
        {
            // 关闭路径不允许抛出导致 Crash (M0 Gate)。
            System.Diagnostics.Trace.WriteLine($"close failed: {ex.Message}");
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
            var results = await Task.WhenAll(directories.Select(async directory =>
                (Directory: directory, Context: await Task.Run(() => GitProjectContext.ReadAsync(directory)))));
            foreach (var result in results) _directoryGitContexts[result.Directory] = result.Context;
            foreach (var workspace in workspaces)
                _gitContexts[workspace.Id] = _directoryGitContexts[workspace.RootDirectory];
            RefreshWorkspaceSidebar();
            UpdateStatus();
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

    private void UpdateStatus()
    {
        var processes = AgentDetector.Scan();
        foreach (var tab in _tabs.Values)
            foreach (var pane in tab.Panes.Values)
            {
                pane.Detection = pane.Connection is { } connection
                    ? AgentDetector.Detect(connection.ProcessId, processes, connection.CommandLine, pane.LastProfileName ?? "")
                    : new AgentDetection(AgentType.Unknown, AgentDetectionSource.None, 0);
                UpdatePaneLabel(pane);
            }
        RefreshWorkspaceSidebar();
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
            $"{_workspaces.Active?.Name}  /  {_activeTab?.Tab.Title}    •    {_lastProfileName}    •    {_activePane?.Detection.Type}: {_activePane?.Status}    •    {directory}{branch}";
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
            UpdateStatus();
            return true;
        }
        var previous = pane.Status;
        pane.Status = agentEvent.Status;
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
        UpdateStatus();
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

    private static int ParseSmokeCycles(string[] args)
    {
        for (var i = 1; i < args.Length - 1; i++)
            if (args[i] == "--lifecycle-smoke" && int.TryParse(args[i + 1], out var cycles) && cycles is > 0 and <= 500)
                return cycles;
        return 0;
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 1; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
