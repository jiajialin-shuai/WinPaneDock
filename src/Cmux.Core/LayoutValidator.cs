using System.Text.Json;

namespace Cmux.Core;

public static class LayoutValidator
{
    private const int MaxTreeDepth = 256;
    private const int MaxNodes = 100_000;

    public static void Validate(LayoutSnapshot? snapshot)
    {
        if (snapshot is null) throw new LayoutValidationException("Layout snapshot is empty.");
        if (snapshot.Version != 1) throw new LayoutValidationException($"Unsupported layout version {snapshot.Version}.");
        if (snapshot.Workspaces is null) throw new LayoutValidationException("Layout workspaces are missing.");

        var workspaceIds = new HashSet<Guid>();
        var tabIds = new HashSet<Guid>();
        var paneIds = new HashSet<Guid>();
        var sessionIds = new HashSet<Guid>();
        var nodeCount = 0;
        foreach (var workspace in snapshot.Workspaces)
        {
            if (workspace is null) throw new LayoutValidationException("Layout contains a null workspace.");
            RequireId(workspace.Id, "workspace");
            if (!workspaceIds.Add(workspace.Id)) throw new LayoutValidationException($"Duplicate workspace id {workspace.Id}.");
            RequireText(workspace.Name, "workspace name");
            RequireText(workspace.RootDirectory, "workspace root directory");
            if (workspace.Tabs is null) throw new LayoutValidationException($"Workspace '{workspace.Name}' has no tabs array.");

            foreach (var tab in workspace.Tabs)
            {
                if (tab is null) throw new LayoutValidationException($"Workspace '{workspace.Name}' contains a null tab.");
                RequireId(tab.Id, "tab");
                if (!tabIds.Add(tab.Id)) throw new LayoutValidationException($"Duplicate tab id {tab.Id}.");
                RequireText(tab.Title, "tab title");
                if (tab.RootPane is null) throw new LayoutValidationException($"Tab '{tab.Title}' has no root pane.");

                var terminalPaneIds = ValidatePane(tab.RootPane, paneIds, sessionIds, ref nodeCount, 0);
                if (tab.ActivePaneId == Guid.Empty || !terminalPaneIds.Contains(tab.ActivePaneId))
                    throw new LayoutValidationException($"Tab '{tab.Title}' points to an invalid active pane.");
            }

            if (workspace.ActiveTabId is { } activeTabId && !workspace.Tabs.Any(tab => tab.Id == activeTabId))
                throw new LayoutValidationException($"Workspace '{workspace.Name}' points to an invalid active tab.");
            if (workspace.Tabs.Length == 0 && workspace.ActiveTabId is not null)
                throw new LayoutValidationException($"Workspace '{workspace.Name}' has an active tab but no tabs.");
            if (workspace.Tabs.Length > 0 && workspace.ActiveTabId is null)
                throw new LayoutValidationException($"Workspace '{workspace.Name}' has tabs but no active tab.");
        }

        if (snapshot.ActiveWorkspaceId is { } activeWorkspaceId &&
            !workspaceIds.Contains(activeWorkspaceId))
            throw new LayoutValidationException($"Layout points to an invalid active workspace {activeWorkspaceId}.");
        if (snapshot.Workspaces.Length == 0 && snapshot.ActiveWorkspaceId is not null)
            throw new LayoutValidationException("An empty layout cannot have an active workspace.");
    }

    private static HashSet<Guid> ValidatePane(
        PaneSnapshot pane,
        HashSet<Guid> paneIds,
        HashSet<Guid> sessionIds,
        ref int nodeCount,
        int depth)
    {
        if (pane is null) throw new LayoutValidationException("Layout contains a null pane.");
        if (depth > MaxTreeDepth) throw new LayoutValidationException("Pane tree exceeds the maximum depth.");
        if (++nodeCount > MaxNodes) throw new LayoutValidationException("Layout contains too many panes.");
        RequireId(pane.Id, "pane");
        if (!paneIds.Add(pane.Id)) throw new LayoutValidationException($"Duplicate pane id {pane.Id}.");
        RequireRatio(pane.Ratio);

        if (pane.SessionId is { } sessionId)
        {
            if (sessionId == Guid.Empty) throw new LayoutValidationException("Terminal session id cannot be empty.");
            if (!sessionIds.Add(sessionId)) throw new LayoutValidationException($"Duplicate terminal session id {sessionId}.");
            if (pane.Orientation is not null || pane.ChildA is not null || pane.ChildB is not null)
                throw new LayoutValidationException($"Terminal pane {pane.Id} cannot contain split children.");
            EnsureText(pane.ProfileName, $"profile for pane {pane.Id}");
            EnsureText(pane.CommandLine, $"command for pane {pane.Id}");
            EnsureText(pane.WorkingDirectory, $"working directory for pane {pane.Id}");
            EnsureText(pane.Title, $"title for pane {pane.Id}");
            return [pane.Id];
        }

        if (pane.Orientation is not (PaneOrientation.Right or PaneOrientation.Down))
            throw new LayoutValidationException($"Split pane {pane.Id} has an invalid orientation.");
        if (pane.ChildA is null || pane.ChildB is null)
            throw new LayoutValidationException($"Split pane {pane.Id} must contain both children.");
        var terminalIds = ValidatePane(pane.ChildA, paneIds, sessionIds, ref nodeCount, depth + 1);
        terminalIds.UnionWith(ValidatePane(pane.ChildB, paneIds, sessionIds, ref nodeCount, depth + 1));
        return terminalIds;
    }

    private static void RequireId(Guid id, string kind)
    {
        if (id == Guid.Empty) throw new LayoutValidationException($"Layout contains an empty {kind} id.");
    }

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new LayoutValidationException($"Layout {name} is required.");
    }

    private static void EnsureText(string? value, string name)
    {
        if (value is null) throw new LayoutValidationException($"Layout {name} is missing.");
    }

    private static void RequireRatio(double ratio)
    {
        if (!double.IsFinite(ratio) || ratio is < 0.15 or > 0.85)
            throw new LayoutValidationException($"Pane ratio {ratio} is outside the supported range.");
    }
}

public sealed class LayoutValidationException(string message) : JsonException(message);

public sealed class LayoutRecoveryException : Exception
{
    public LayoutRecoveryException(string path, string? backupPath, Exception primary, Exception? backup)
        : base($"Layout recovery failed. Primary: {primary.Message}" +
               (backup is null ? "." : $" Backup: {backup.Message}."), primary)
    {
        Path = path;
        BackupPath = backupPath;
        PrimaryError = primary.Message;
        BackupError = backup?.Message;
    }

    public string Path { get; }
    public string? BackupPath { get; }
    public string PrimaryError { get; }
    public string? BackupError { get; }
}
