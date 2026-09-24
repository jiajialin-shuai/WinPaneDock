namespace Cmux.Core;

public enum AgentStatus { Unknown, Working, Waiting, Idle, Completed, Error }

public sealed record AgentEvent(Guid WorkspaceId, Guid PaneId, Guid SessionId, AgentStatus Status,
    string? WorkingDirectory = null);
