namespace Cmux.Core;

public sealed class Workspace
{
    internal Workspace(string name, string rootDirectory)
        : this(Guid.NewGuid(), name, rootDirectory) { }

    internal Workspace(Guid id, string name, string rootDirectory)
    {
        Id = id;
        Name = name;
        RootDirectory = rootDirectory;
    }

    public Guid Id { get; }
    public string Name { get; internal set; }
    public string RootDirectory { get; internal set; }
    public bool IsPinned { get; internal set; }
    public string DisplayName => IsPinned ? $"📌 {Name}" : Name;
    internal List<TerminalTab> MutableTabs { get; } = [];
    public IReadOnlyList<TerminalTab> Tabs => MutableTabs;
    public Guid? ActiveTabId { get; internal set; }
}
