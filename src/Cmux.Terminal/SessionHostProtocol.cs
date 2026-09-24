using System.Security.Principal;

namespace Cmux.Terminal;

public static class SessionHostProtocol
{
    public static string PipeName => "cmux-session-host-" +
        WindowsIdentity.GetCurrent().User!.Value.Replace('-', '_');
}

public sealed record HostRequest(
    string Command,
    string? SessionId = null,
    string? CommandLine = null,
    string? WorkingDirectory = null,
    Dictionary<string, string>? Environment = null,
    string? Data = null,
    uint Rows = 0,
    uint Columns = 0);

public sealed record HostSession(string SessionId, int ProcessId, string CommandLine);

public sealed record HostResponse(
    bool Ok,
    string? Error = null,
    string? SessionId = null,
    int ProcessId = 0,
    HostSession[]? Sessions = null,
    string? Replay = null,
    string? Event = null,
    string? Output = null);
