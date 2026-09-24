using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using Cmux.Terminal;

using var mutex = new Mutex(true, "Local\\" + SessionHostProtocol.PipeName, out var firstInstance);
if (!firstInstance) return;

var sessions = new Dictionary<string, HostedSession>();
var gate = new object();

while (true)
{
    var pipe = new NamedPipeServerStream(SessionHostProtocol.PipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    try { await pipe.WaitForConnectionAsync(); }
    catch { pipe.Dispose(); throw; }
    _ = Task.Run(() => HandleClientAsync(pipe));
}

async Task HandleClientAsync(NamedPipeServerStream pipe)
{
    using (pipe)
    using (var reader = new StreamReader(pipe, leaveOpen: true))
    using (var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true })
    {
        HostedSession? attached = null;
        Channel<string>? channel = null;
        try
        {
            var line = await reader.ReadLineAsync();
            var request = line is null ? null : JsonSerializer.Deserialize<HostRequest>(line);
            if (request is null) return;

            switch (request.Command)
            {
                case "list":
                    HostSession[] list;
                    lock (gate) list = sessions.Values.Select(s => s.Info).ToArray();
                    await SendAsync(writer, new HostResponse(true, Sessions: list));
                    break;
                case "create":
                    if (string.IsNullOrWhiteSpace(request.CommandLine))
                        throw new ArgumentException("CommandLine is required.");
                    var id = request.SessionId ?? Guid.NewGuid().ToString("N");
                    var session = new HostedSession(id, request.CommandLine,
                        request.WorkingDirectory, request.Environment, OnSessionExited);
                    lock (gate)
                    {
                        if (sessions.ContainsKey(id)) throw new ArgumentException("SessionId already exists.");
                        sessions.Add(id, session);
                    }
                    try { session.Start(); }
                    catch
                    {
                        lock (gate) sessions.Remove(id);
                        session.Dispose();
                        throw;
                    }
                    await SendAsync(writer, new HostResponse(true, SessionId: id,
                        ProcessId: session.ProcessId));
                    break;
                case "input":
                    GetSession(request.SessionId).WriteInput(request.Data ?? "");
                    await SendAsync(writer, new HostResponse(true));
                    break;
                case "resize":
                    GetSession(request.SessionId).Resize(request.Rows, request.Columns);
                    await SendAsync(writer, new HostResponse(true));
                    break;
                case "close":
                    var closing = GetSession(request.SessionId);
                    lock (gate) sessions.Remove(closing.Id);
                    closing.Dispose();
                    await SendAsync(writer, new HostResponse(true));
                    break;
                case "attach":
                    attached = GetSession(request.SessionId);
                    (channel, var replay) = attached.Subscribe();
                    await SendAsync(writer, new HostResponse(true, SessionId: attached.Id,
                        ProcessId: attached.ProcessId, Replay: replay));
                    _ = WatchDisconnectAsync(reader, channel);
                    await foreach (var output in channel.Reader.ReadAllAsync())
                        await SendAsync(writer, new HostResponse(true, Event: "output", Output: output));
                    break;
                default:
                    throw new ArgumentException("Unknown command.");
            }
        }
        catch (Exception ex)
        {
            try { await SendAsync(writer, new HostResponse(false, Error: ex.Message)); }
            catch (IOException) { }
        }
        finally
        {
            if (attached is not null && channel is not null) attached.Unsubscribe(channel);
        }
    }
}

HostedSession GetSession(string? id)
{
    if (id is null) throw new ArgumentException("SessionId is required.");
    lock (gate) return sessions.TryGetValue(id, out var session)
        ? session : throw new ArgumentException("Session not found.");
}

void OnSessionExited(HostedSession session) => _ = Task.Run(async () =>
{
    await Task.Delay(2000);
    lock (gate)
    {
        if (!sessions.TryGetValue(session.Id, out var current) || !ReferenceEquals(current, session)) return;
        sessions.Remove(session.Id);
    }
    session.Dispose();
});

static Task SendAsync(StreamWriter writer, HostResponse response) =>
    writer.WriteLineAsync(JsonSerializer.Serialize(response));

static async Task WatchDisconnectAsync(StreamReader reader, Channel<string> channel)
{
    try { await reader.ReadToEndAsync(); }
    catch (IOException) { }
    catch (ObjectDisposedException) { }
    finally { channel.Writer.TryComplete(); }
}

sealed class HostedSession : IDisposable
{
    private const int MaxReplayChars = 1_000_000;
    private readonly object _gate = new();
    private readonly ConptyProcess _process;
    private readonly List<Channel<string>> _subscribers = new();
    private readonly System.Text.StringBuilder _replay = new();

    public HostedSession(string id, string commandLine, string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment, Action<HostedSession> onExit)
    {
        Id = id;
        _process = new ConptyProcess(commandLine, workingDirectory, environment);
        _process.Output += OnOutput;
        _process.Exited += () => onExit(this);
    }

    public string Id { get; }
    public int ProcessId => _process.ProcessId;
    public HostSession Info => new(Id, ProcessId, _process.CommandLine);
    public void Start() => _process.Start();
    public void WriteInput(string data) => _process.WriteInput(data);
    public void Resize(uint rows, uint columns) => _process.Resize(rows, columns);

    public (Channel<string>, string) Subscribe()
    {
        lock (_gate)
        {
            var channel = Channel.CreateUnbounded<string>();
            _subscribers.Add(channel);
            return (channel, _replay.ToString());
        }
    }

    public void Unsubscribe(Channel<string> channel)
    {
        lock (_gate) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    private void OnOutput(string data)
    {
        lock (_gate)
        {
            _replay.Append(data);
            if (_replay.Length > MaxReplayChars)
                _replay.Remove(0, _replay.Length - MaxReplayChars);
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(data);
        }
    }

    public void Dispose()
    {
        _process.Dispose();
        lock (_gate)
        {
            foreach (var subscriber in _subscribers) subscriber.Writer.TryComplete();
            _subscribers.Clear();
        }
    }
}
