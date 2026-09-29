using System.Reflection;
using System.Security.Principal;
using Cmux.Core;

namespace Cmux.Terminal;

public static class SessionHostProtocol
{
    public const int ProtocolVersion = 2;

    public static string PipeName => InstanceScope.Qualify("cmux-session-host-" +
        WindowsIdentity.GetCurrent().User!.Value.Replace('-', '_'));

    public static string EventPipeName => PipeName + "-events";

    public static HostIdentity CurrentIdentity() => new(
        Environment.ProcessId,
        Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location ?? "",
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
        ProtocolVersion,
        InstanceScope.Id);
}

public sealed record HostRequest(
    string Command,
    string? SessionId = null,
    string? CommandLine = null,
    string? WorkingDirectory = null,
    Dictionary<string, string>? Environment = null,
    string? Data = null,
    uint Rows = 0,
    uint Columns = 0,
    int ProtocolVersion = 0,
    string? LeaseId = null,
    // Attach replay control. A saturated session can hold 1,000,000 characters, and asking for
    // all of it produces a single multi-megabyte JSON line that cannot be written inside the
    // write deadline, which turns every reconnect into a guaranteed timeout. Reconnects that
    // already hold a local tail ask for a bounded suffix instead.
    bool IncludeReplay = true,
    int ReplayLimit = 0);

public sealed record HostSession(string SessionId, int ProcessId, string CommandLine, string LeaseId);

public sealed record HostIdentity(
    int ProcessId,
    string ExecutablePath,
    string Version,
    int ProtocolVersion,
    string InstanceId);

public sealed record HostResponse(
    bool Ok,
    string? Error = null,
    string? SessionId = null,
    int ProcessId = 0,
    HostSession[]? Sessions = null,
    string? Replay = null,
    string? Event = null,
    string? Output = null,
    HostIdentity? Identity = null,
    string? LeaseId = null);
