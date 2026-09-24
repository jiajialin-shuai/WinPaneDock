using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cmux.Core;

if (args is ["doctor"]) return Doctor.Run();

GuiCommandRequest? gui = args.Length > 0 ? args[0] switch
{
    "new" when args.Length == 1 => new("new"),
    "workspace" when args.Length == 2 => new("workspace", args[1], Environment.CurrentDirectory),
    "split" when args.Length == 2 => new("split", args[1].ToLowerInvariant()),
    "run" when args.Length >= 2 => new("run", string.Join(' ', args.Skip(1)), Environment.CurrentDirectory),
    "focus" when args.Length == 2 => new("focus", args[1]),
    "list" when args.Length == 1 => new("list"),
    _ => null,
} : null;

if (gui is not null)
{
    try
    {
        using var guiPipe = new NamedPipeClientStream(".", GuiCommandProtocol.PipeName, PipeDirection.InOut);
        await guiPipe.ConnectAsync(2000);
        using var guiWriter = new StreamWriter(guiPipe, leaveOpen: true) { AutoFlush = true };
        using var guiReader = new StreamReader(guiPipe, leaveOpen: true);
        await guiWriter.WriteLineAsync(JsonSerializer.Serialize(gui));
        var reply = JsonSerializer.Deserialize<GuiCommandResponse>(await guiReader.ReadLineAsync() ?? "");
        if (reply is null || !reply.Ok)
        {
            Console.Error.WriteLine(reply?.Message ?? "cmux GUI did not respond.");
            return 1;
        }
        if (reply.Items is { } items) foreach (var item in items) Console.WriteLine(item);
        else if (reply.Message is { } message) Console.WriteLine(message);
        return 0;
    }
    catch (Exception ex) when (ex is IOException or TimeoutException)
    {
        Console.Error.WriteLine($"cmux GUI command failed: {ex.Message}");
        return 1;
    }
}

AgentStatus parsedStatus = AgentStatus.Unknown;
var notify = args.Length == 2 && args[0] == "notify" &&
    Enum.TryParse<AgentStatus>(args[1], true, out parsedStatus) &&
    parsedStatus is AgentStatus.Working or AgentStatus.Waiting or AgentStatus.Completed or AgentStatus.Error;
var cwd = args.Length == 2 && args[0] == "cwd" && Directory.Exists(args[1]);
if (!notify && !cwd)
{
    Console.Error.WriteLine("Usage: cmux new | workspace <name> | split <right|down> | run <command> | focus <pane> | list | notify <status> | cwd <directory> | doctor");
    return 2;
}

if (!Guid.TryParse(Environment.GetEnvironmentVariable("CMUX_WORKSPACE_ID"), out var workspaceId) ||
    !Guid.TryParse(Environment.GetEnvironmentVariable("CMUX_PANE_ID"), out var paneId) ||
    !Guid.TryParse(Environment.GetEnvironmentVariable("CMUX_SESSION_ID"), out var sessionId) ||
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CMUX_PIPE_NAME")))
{
    Console.Error.WriteLine("cmux session environment is missing; run this inside a cmux terminal.");
    return 2;
}

try
{
    using var pipe = new NamedPipeClientStream(".", Environment.GetEnvironmentVariable("CMUX_PIPE_NAME")!, PipeDirection.InOut);
    await pipe.ConnectAsync(2000);
    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
    using var reader = new StreamReader(pipe, leaveOpen: true);
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    options.Converters.Add(new JsonStringEnumConverter());
    await writer.WriteLineAsync(JsonSerializer.Serialize(new AgentEvent(workspaceId, paneId, sessionId,
        notify ? parsedStatus : AgentStatus.Unknown, cwd ? Path.GetFullPath(args[1]) : null), options));
    var reply = await reader.ReadLineAsync();
    if (reply == "OK") return 0;
    Console.Error.WriteLine(reply ?? "cmux did not acknowledge the event.");
    return 1;
}
catch (Exception ex) when (ex is IOException or TimeoutException)
{
    Console.Error.WriteLine($"cmux notification failed: {ex.Message}");
    return 1;
}
