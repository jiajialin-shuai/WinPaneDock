namespace Cmux.Core;

public enum PaneOrientation { Right, Down }

public sealed class PaneNode
{
    internal PaneNode(Guid sessionId)
        : this(Guid.NewGuid(), sessionId) { }

    internal PaneNode(Guid id, Guid? sessionId)
    {
        Id = id;
        SessionId = sessionId;
    }

    public Guid Id { get; }
    public Guid? SessionId { get; internal set; }
    public PaneOrientation? Orientation { get; internal set; }
    public double Ratio { get; internal set; } = 0.5;
    public PaneNode? ChildA { get; internal set; }
    public PaneNode? ChildB { get; internal set; }
    public bool IsTerminal => SessionId is not null;
    public string ProfileName { get; internal set; } = "";
    public string CommandLine { get; internal set; } = "";
    public string WorkingDirectory { get; internal set; } = "";
    public string Title { get; internal set; } = "";

    public IEnumerable<PaneNode> Terminals()
    {
        if (IsTerminal) { yield return this; yield break; }
        if (ChildA is not null) foreach (var pane in ChildA.Terminals()) yield return pane;
        if (ChildB is not null) foreach (var pane in ChildB.Terminals()) yield return pane;
    }
}
