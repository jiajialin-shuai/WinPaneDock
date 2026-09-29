namespace Cmux.Core;

public sealed class WorkspaceManager
{
    private readonly List<Workspace> _workspaces = [];

    public IReadOnlyList<Workspace> Workspaces => _workspaces;
    public Guid? ActiveId { get; private set; }
    public Workspace? Active => _workspaces.FirstOrDefault(w => w.Id == ActiveId);

    public Workspace Create(string name, string rootDirectory)
    {
        var cleanName = name.Trim();
        if (cleanName.Length == 0) throw new ArgumentException("Workspace name is required.");
        if (_workspaces.Any(w => w.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Workspace '{cleanName}' already exists.");
        if (!Directory.Exists(rootDirectory)) throw new DirectoryNotFoundException(rootDirectory);

        var workspace = new Workspace(cleanName, rootDirectory);
        _workspaces.Add(workspace);
        ActiveId = workspace.Id;
        return workspace;
    }

    public void Rename(Guid id, string name)
    {
        var workspace = Find(id);
        var cleanName = name.Trim();
        if (cleanName.Length == 0) throw new ArgumentException("Workspace name is required.");
        if (_workspaces.Any(w => w.Id != id && w.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Workspace '{cleanName}' already exists.");
        workspace.Name = cleanName;
    }

    public void SetRootDirectory(Guid id, string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory)) throw new DirectoryNotFoundException(rootDirectory);
        Find(id).RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public void Delete(Guid id)
    {
        _workspaces.Remove(Find(id));
        if (ActiveId == id) ActiveId = _workspaces.FirstOrDefault()?.Id;
    }

    public void Select(Guid id) => ActiveId = Find(id).Id;

    public void TogglePin(Guid id)
    {
        var workspace = Find(id);
        _workspaces.Remove(workspace);
        workspace.IsPinned = !workspace.IsPinned;
        _workspaces.Insert(_workspaces.Count(w => w.IsPinned), workspace);
    }

    public void Move(Guid id, int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var workspace = Find(id);
        var index = _workspaces.IndexOf(workspace);
        var target = index + direction;
        if (target < 0 || target >= _workspaces.Count || _workspaces[target].IsPinned != workspace.IsPinned) return;
        (_workspaces[index], _workspaces[target]) = (_workspaces[target], _workspaces[index]);
    }

    public TerminalTab CreateTab(Guid workspaceId, string title)
    {
        var workspace = Find(workspaceId);
        var tab = new TerminalTab(title);
        workspace.MutableTabs.Add(tab);
        workspace.ActiveTabId = tab.Id;
        return tab;
    }

    public void RenameTab(Guid workspaceId, Guid tabId, string title)
    {
        var workspace = Find(workspaceId);
        var cleanTitle = title.Trim();
        if (cleanTitle.Length == 0) throw new ArgumentException("Group name is required.");
        if (workspace.Tabs.Any(t => t.Id != tabId && t.Title.Equals(cleanTitle, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Group '{cleanTitle}' already exists.");
        FindTab(workspaceId, tabId).Title = cleanTitle;
    }

    public void SelectTab(Guid workspaceId, Guid tabId)
    {
        var workspace = Find(workspaceId);
        if (!workspace.Tabs.Any(t => t.Id == tabId)) throw new ArgumentException("Tab not found.");
        workspace.ActiveTabId = tabId;
    }

    public void CloseTab(Guid workspaceId, Guid tabId)
    {
        var workspace = Find(workspaceId);
        var tab = workspace.MutableTabs.FirstOrDefault(t => t.Id == tabId)
            ?? throw new ArgumentException("Tab not found.");
        workspace.MutableTabs.Remove(tab);
        if (workspace.ActiveTabId == tabId) workspace.ActiveTabId = workspace.Tabs.FirstOrDefault()?.Id;
    }

    public PaneNode SplitPane(Guid workspaceId, Guid tabId, Guid paneId, PaneOrientation orientation)
    {
        var tab = FindTab(workspaceId, tabId);
        var leaf = FindPane(tab.RootPane, paneId) ?? throw new ArgumentException("Pane not found.");
        if (!leaf.IsTerminal) throw new ArgumentException("Only terminal panes can split.");
        var split = new PaneNode(Guid.NewGuid(), null)
        {
            ChildA = leaf,
            ChildB = new PaneNode(Guid.NewGuid()),
            Orientation = orientation,
            Ratio = 0.5,
        };
        var parent = FindParent(tab.RootPane, paneId);
        if (parent is null) tab.RootPane = split;
        else if (parent.ChildA?.Id == paneId) parent.ChildA = split;
        else parent.ChildB = split;
        tab.ActivePaneId = split.ChildB!.Id;
        return split.ChildB;
    }

    public void SelectPane(Guid workspaceId, Guid tabId, Guid paneId)
    {
        var tab = FindTab(workspaceId, tabId);
        if (FindPane(tab.RootPane, paneId)?.IsTerminal != true) throw new ArgumentException("Terminal pane not found.");
        tab.ActivePaneId = paneId;
    }

    public void ResizePane(Guid workspaceId, Guid tabId, Guid splitId, double ratio)
    {
        var split = FindPane(FindTab(workspaceId, tabId).RootPane, splitId);
        if (split?.IsTerminal != false || ratio is < 0.15 or > 0.85) throw new ArgumentOutOfRangeException(nameof(ratio));
        split.Ratio = ratio;
    }

    public void SetTerminalLaunch(Guid workspaceId, Guid tabId, Guid sessionId,
        string profileName, string commandLine, string workingDirectory, string title)
    {
        var pane = FindTab(workspaceId, tabId).RootPane.Terminals().FirstOrDefault(p => p.SessionId == sessionId)
            ?? throw new ArgumentException("Terminal session not found.");
        pane.ProfileName = profileName;
        pane.CommandLine = commandLine;
        pane.WorkingDirectory = workingDirectory;
        pane.Title = title;
    }

    public void ClearTerminalLaunch(Guid workspaceId, Guid tabId, Guid sessionId)
    {
        var pane = FindTab(workspaceId, tabId).RootPane.Terminals().FirstOrDefault(p => p.SessionId == sessionId)
            ?? throw new ArgumentException("Terminal session not found.");
        pane.ProfileName = "";
        pane.CommandLine = "";
        pane.WorkingDirectory = "";
        pane.Title = "";
    }

    public bool ClosePane(Guid workspaceId, Guid tabId, Guid paneId)
    {
        var tab = FindTab(workspaceId, tabId);
        if (tab.RootPane.Id == paneId) return false;
        var parent = FindParent(tab.RootPane, paneId) ?? throw new ArgumentException("Pane not found.");
        var sibling = parent.ChildA?.Id == paneId ? parent.ChildB! : parent.ChildA!;
        if (tab.RootPane.Id == parent.Id) tab.RootPane = sibling;
        else
        {
            var grandparent = FindParent(tab.RootPane, parent.Id)!;
            if (grandparent.ChildA?.Id == parent.Id) grandparent.ChildA = sibling;
            else grandparent.ChildB = sibling;
        }
        tab.ActivePaneId = sibling.Terminals().First().Id;
        return true;
    }

    private TerminalTab FindTab(Guid workspaceId, Guid tabId) => Find(workspaceId).Tabs.FirstOrDefault(t => t.Id == tabId)
        ?? throw new ArgumentException("Tab not found.");

    private static PaneNode? FindPane(PaneNode node, Guid id) => node.Id == id ? node
        : (node.ChildA is not null ? FindPane(node.ChildA, id) : null)
          ?? (node.ChildB is not null ? FindPane(node.ChildB, id) : null);

    private static PaneNode? FindParent(PaneNode node, Guid childId)
    {
        if (node.ChildA?.Id == childId || node.ChildB?.Id == childId) return node;
        return (node.ChildA is not null ? FindParent(node.ChildA, childId) : null)
            ?? (node.ChildB is not null ? FindParent(node.ChildB, childId) : null);
    }

    private Workspace Find(Guid id) => _workspaces.FirstOrDefault(w => w.Id == id)
        ?? throw new ArgumentException("Workspace not found.");

    public LayoutSnapshot Export() => new(1, ActiveId, _workspaces.Select(w =>
        new WorkspaceSnapshot(w.Id, w.Name, w.RootDirectory, w.IsPinned, w.ActiveTabId,
            w.Tabs.Select(t => new TabSnapshot(t.Id, t.Title, t.ActivePaneId, Snapshot(t.RootPane))).ToArray()))
        .ToArray());

    public void Restore(LayoutSnapshot snapshot)
    {
        LayoutValidator.Validate(snapshot);
        var restored = new List<Workspace>();
        foreach (var data in snapshot.Workspaces)
        {
            var workspace = new Workspace(data.Id, data.Name, data.RootDirectory) { IsPinned = data.IsPinned };
            foreach (var tab in data.Tabs)
                workspace.MutableTabs.Add(new TerminalTab(tab.Id, LegacyGroupTitle(tab.Title), RestorePane(tab.RootPane), tab.ActivePaneId));
            workspace.ActiveTabId = data.ActiveTabId;
            restored.Add(workspace);
        }
        _workspaces.Clear();
        _workspaces.AddRange(restored);
        ActiveId = restored.Any(w => w.Id == snapshot.ActiveWorkspaceId)
            ? snapshot.ActiveWorkspaceId : restored.FirstOrDefault()?.Id;
    }

    private static string LegacyGroupTitle(string title) =>
        title.StartsWith("Terminal ", StringComparison.Ordinal) &&
        int.TryParse(title[9..], out var number) && number > 0
            ? $"Group {number}" : title;

    private static PaneSnapshot Snapshot(PaneNode pane) => new(pane.Id, pane.SessionId, pane.Orientation,
        pane.Ratio, pane.ChildA is null ? null : Snapshot(pane.ChildA),
        pane.ChildB is null ? null : Snapshot(pane.ChildB), pane.ProfileName, pane.CommandLine,
        pane.WorkingDirectory, pane.Title);

    private static PaneNode RestorePane(PaneSnapshot data) => new(data.Id, data.SessionId)
    {
        Orientation = data.Orientation,
        Ratio = data.Ratio,
        ChildA = data.ChildA is null ? null : RestorePane(data.ChildA),
        ChildB = data.ChildB is null ? null : RestorePane(data.ChildB),
        ProfileName = data.ProfileName,
        CommandLine = data.CommandLine,
        WorkingDirectory = data.WorkingDirectory,
        Title = data.Title,
    };
}
