namespace Cmux.Core;

public sealed record LayoutSnapshot(int Version, Guid? ActiveWorkspaceId, WorkspaceSnapshot[] Workspaces);

public sealed record WorkspaceSnapshot(Guid Id, string Name, string RootDirectory, bool IsPinned,
    Guid? ActiveTabId, TabSnapshot[] Tabs);

public sealed record TabSnapshot(Guid Id, string Title, Guid ActivePaneId, PaneSnapshot RootPane);

public sealed record PaneSnapshot(Guid Id, Guid? SessionId, PaneOrientation? Orientation, double Ratio,
    PaneSnapshot? ChildA, PaneSnapshot? ChildB, string ProfileName, string CommandLine,
    string WorkingDirectory, string Title);
