namespace Cmux.Core;

public sealed class TerminalTab
{
    internal TerminalTab(string title)
        : this(Guid.NewGuid(), title, new PaneNode(Guid.NewGuid()), null) { }

    internal TerminalTab(Guid id, string title, PaneNode rootPane, Guid? activePaneId)
    {
        Id = id;
        Title = title;
        RootPane = rootPane;
        ActivePaneId = activePaneId ?? rootPane.Terminals().First().Id;
    }

    public Guid Id { get; }
    public string Title { get; internal set; }
    public PaneNode RootPane { get; internal set; }
    public Guid ActivePaneId { get; internal set; }
}
