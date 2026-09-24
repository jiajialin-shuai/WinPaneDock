namespace Cmux.Core;

public sealed record FocusPath(Guid? WorkspaceId, Guid? TabId, Guid? PaneId, Guid? TerminalSessionId);

public sealed class FocusManager(WorkspaceManager workspaces)
{
    public FocusPath Current { get; private set; } = new(null, null, null, null);

    public void FocusWorkspace(Guid workspaceId)
    {
        workspaces.Select(workspaceId);
        Reconcile();
    }

    public void FocusTab(Guid workspaceId, Guid tabId)
    {
        workspaces.Select(workspaceId);
        workspaces.SelectTab(workspaceId, tabId);
        Reconcile();
    }

    public void FocusPane(Guid workspaceId, Guid tabId, Guid paneId)
    {
        FocusTab(workspaceId, tabId);
        workspaces.SelectPane(workspaceId, tabId, paneId);
        Reconcile();
    }

    public void FocusTerminal(Guid workspaceId, Guid tabId, Guid terminalSessionId)
    {
        var tab = workspaces.Workspaces.First(w => w.Id == workspaceId).Tabs.First(t => t.Id == tabId);
        var pane = tab.RootPane.Terminals().FirstOrDefault(p => p.SessionId == terminalSessionId)
            ?? throw new ArgumentException("Terminal session not found.");
        FocusPane(workspaceId, tabId, pane.Id);
    }

    public void Reconcile()
    {
        var workspace = workspaces.Active;
        var tab = workspace?.Tabs.FirstOrDefault(t => t.Id == workspace.ActiveTabId);
        var pane = tab?.RootPane.Terminals().FirstOrDefault(p => p.Id == tab.ActivePaneId);
        Current = new FocusPath(workspace?.Id, tab?.Id, pane?.Id, pane?.SessionId);
    }
}
