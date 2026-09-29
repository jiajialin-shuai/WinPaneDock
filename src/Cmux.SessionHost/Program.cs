using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cmux.Core;
using Cmux.Terminal;

var arguments = Environment.GetCommandLineArgs();
var instanceOption = ReadOption(arguments, "--instance-id");
if (instanceOption is not null) InstanceScope.Configure(instanceOption);

using var mutex = new Mutex(true, "Local\\" + SessionHostProtocol.PipeName, out var firstInstance);
if (!firstInstance) return;

var log = new DiagnosticLog("session-host");
var identity = SessionHostProtocol.CurrentIdentity();
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
log.Write(DiagnosticLevel.Info, "host.start",
    $"version={identity.Version} protocol={identity.ProtocolVersion} instance={identity.InstanceId.Length}");
AppDomain.CurrentDomain.ProcessExit += (_, _) => log.Write(DiagnosticLevel.Info, "host.stop");
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    log.Write(DiagnosticLevel.Critical, "host.unhandled", exception: e.ExceptionObject as Exception);
TaskScheduler.UnobservedTaskException += (_, e) =>
    log.Write(DiagnosticLevel.Error, "host.unobserved-task", exception: e.Exception);

var sessions = new Dictionary<string, HostedSession>();
var gate = new object();
const int MaxOutputBatchChars = 64 * 1024;

while (true)
{
    var pipe = new NamedPipeServerStream(SessionHostProtocol.PipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    try { await pipe.WaitForConnectionAsync(); }
    catch (Exception ex)
    {
        pipe.Dispose();
        log.Write(DiagnosticLevel.Critical, "host.listen.failed", exception: ex);
        throw;
    }

    _ = Task.Run(async () =>
    {
        try { await HandleClientAsync(pipe); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            log.Write(DiagnosticLevel.Debug, "host.client.closed", exception: ex);
        }
        catch (Exception ex)
        {
            log.Write(DiagnosticLevel.Error, "host.client.failed", exception: ex);
        }
    });
}

async Task HandleClientAsync(NamedPipeServerStream pipe)
{
    using (pipe)
    using (var reader = new StreamReader(pipe, leaveOpen: true))
    using (var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true })
    {
        HostedSession? attached = null;
        OutputSubscription? subscription = null;
        HostRequest? request = null;
        try
        {
            string? line;
            using (var requestDeadline = new CancellationTokenSource(NamedPipeProtocol.DefaultRequestTimeoutMs))
            {
                line = await NamedPipeProtocol.ReadLineAsync(reader, NamedPipeProtocol.MaxRequestChars,
                    requestDeadline.Token);
            }
            request = line is null ? null : JsonSerializer.Deserialize<HostRequest>(line, jsonOptions);
            if (request is null) return;
            if (request.ProtocolVersion is not 0 and not SessionHostProtocol.ProtocolVersion)
                throw new IOException($"Unsupported SessionHost protocol {request.ProtocolVersion}.");
            if (request.Command is not ("identity" or "list"))
            {
                // identity/list stay reachable for diagnostics, but anything that can reach
                // a session must present protocol v2 and the lease, otherwise a protocol 0
                // request from any same-user process would opt out of the fence entirely.
                if (request.ProtocolVersion != SessionHostProtocol.ProtocolVersion)
                    throw new IOException($"Session commands require protocol {SessionHostProtocol.ProtocolVersion}.");
                if (request.LeaseId is null && request.Command is ("input" or "resize" or "attach" or "close"))
                    throw new IOException("Session lease is required for protocol v2 session requests.");
            }

            switch (request.Command)
            {
                case "identity":
                    await SendResponseAsync(writer, new HostResponse(true, Identity: identity));
                    break;
                case "list":
                    HostSession[] list;
                    lock (gate) list = sessions.Values.Select(s => s.Info).ToArray();
                    await SendResponseAsync(writer, new HostResponse(true, Sessions: list, Identity: identity));
                    break;
                case "create":
                    if (string.IsNullOrWhiteSpace(request.CommandLine))
                        throw new ArgumentException("CommandLine is required.");
                    var id = request.SessionId ?? Guid.NewGuid().ToString("N");
                    if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("SessionId is required.");
                    var session = new HostedSession(id, request.CommandLine,
                        request.WorkingDirectory, request.Environment, request.LeaseId, OnSessionExited,
                        (sessionId, message) => log.Write(DiagnosticLevel.Warning, "session.slow-consumer",
                            $"sessionId={sessionId} {message}"),
                        (sessionId, exception) => log.Write(DiagnosticLevel.Error, "session.input.failed",
                            $"sessionId={sessionId}", exception));
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
                    log.Write(DiagnosticLevel.Info, "session.created", $"sessionId={id} processId={session.ProcessId}");
                    await SendResponseAsync(writer, new HostResponse(true, SessionId: id,
                        ProcessId: session.ProcessId, LeaseId: session.LeaseId));
                    break;
                case "input":
                    GetSession(request.SessionId, request.LeaseId).WriteInput(request.Data ?? "");
                    log.Write(DiagnosticLevel.Debug, "session.input", $"sessionId={request.SessionId} chars={request.Data?.Length ?? 0}");
                    await SendResponseAsync(writer, new HostResponse(true));
                    break;
                case "resize":
                    GetSession(request.SessionId, request.LeaseId).Resize(request.Rows, request.Columns);
                    await SendResponseAsync(writer, new HostResponse(true));
                    break;
                case "close":
                    // Close is idempotent so a retry after a lost response is safe.
                    if (request.SessionId is { } closingId)
                    {
                        HostedSession? closing = null;
                        lock (gate)
                        {
                            if (sessions.TryGetValue(closingId, out var current))
                            {
                                if (request.LeaseId is not null && request.LeaseId != current.LeaseId)
                                    throw new IOException("Stale SessionHost lease.");
                                closing = current;
                                sessions.Remove(closingId);
                            }
                        }
                        if (closing is not null)
                        {
                            closing.Dispose();
                            log.Write(DiagnosticLevel.Info, "session.closed", $"sessionId={closingId}");
                        }
                    }
                    await SendResponseAsync(writer, new HostResponse(true, SessionId: request.SessionId));
                    break;
                case "attach":
                    attached = GetSession(request.SessionId, request.LeaseId);
                    subscription = attached.Subscribe(request.IncludeReplay, request.ReplayLimit);
                    log.Write(DiagnosticLevel.Debug, "session.attached", $"sessionId={attached.Id}");
                    await SendResponseAsync(writer, new HostResponse(true, SessionId: attached.Id,
                        ProcessId: attached.ProcessId, Replay: subscription.Replay, Identity: identity,
                        LeaseId: attached.LeaseId));
                    _ = WatchDisconnectAsync(reader, subscription);
                    long outputBatches = 0;
                    long outputCharacters = 0;
                    string? pendingChunk = null;
                    while (await subscription.WaitToReadAsync(CancellationToken.None) || pendingChunk is not null)
                    {
                        var first = pendingChunk ?? await subscription.ReadAsync(CancellationToken.None);
                        pendingChunk = null;
                        subscription.MarkConsumed(first.Length);
                        var batch = new StringBuilder();
                        if (first.Length > MaxOutputBatchChars)
                        {
                            batch.Append(first[..MaxOutputBatchChars]);
                            pendingChunk = first[MaxOutputBatchChars..];
                        }
                        else batch.Append(first);
                        while (batch.Length < MaxOutputBatchChars)
                        {
                            if (subscription.TryRead(out var next))
                            {
                                subscription.MarkConsumed(next.Length);
                                if (batch.Length + next.Length > MaxOutputBatchChars && batch.Length > 0)
                                {
                                    var available = MaxOutputBatchChars - batch.Length;
                                    batch.Append(next[..available]);
                                    pendingChunk = next[available..];
                                    break;
                                }
                                batch.Append(next);
                                continue;
                            }
                            using var batchDelay = new CancellationTokenSource(8);
                            try
                            {
                                if (!await subscription.WaitToReadAsync(batchDelay.Token)) break;
                            }
                            catch (OperationCanceledException) { break; }
                        }
                        using var writeDeadline = new CancellationTokenSource(NamedPipeProtocol.DefaultRequestTimeoutMs);
                        await SendResponseAsync(writer,
                            new HostResponse(true, Event: "output", Output: batch.ToString()),
                            writeDeadline.Token);
                        outputBatches++;
                        outputCharacters += batch.Length;
                        if ((outputBatches & 127) == 0)
                            log.Write(DiagnosticLevel.Debug, "session.output.metrics",
                                $"sessionId={request.SessionId} batches={outputBatches} characters={outputCharacters}");
                    }
                    if (subscription.Error is { } error) throw new SlowConsumerException(error);
                    break;
                default:
                    throw new ArgumentException("Unknown command.");
            }
        }
        catch (OperationCanceledException)
        {
            log.Write(DiagnosticLevel.Warning, "host.request.timeout",
                $"sessionId={request?.SessionId} command={request?.Command}");
        }
        catch (SlowConsumerException ex)
        {
            log.Write(DiagnosticLevel.Warning, "host.slow-consumer",
                $"sessionId={request?.SessionId} error={ex.Message}");
            await TrySendErrorAsync(writer, ex.Message);
        }
        catch (Exception ex)
        {
            log.Write(DiagnosticLevel.Warning, "host.request.failed",
                $"sessionId={request?.SessionId} command={request?.Command} errorType={ex.GetType().Name}");
            await TrySendErrorAsync(writer, ex.Message);
        }
        finally
        {
            if (attached is not null && subscription is not null) attached.Unsubscribe(subscription);
        }
    }
}

HostedSession GetSession(string? id, string? leaseId = null)
{
    if (id is null) throw new ArgumentException("SessionId is required.");
    lock (gate)
    {
        if (!sessions.TryGetValue(id, out var session)) throw new ArgumentException("Session not found.");
        if (leaseId is not null && leaseId != session.LeaseId)
            throw new IOException("Stale SessionHost lease.");
        return session;
    }
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
    log.Write(DiagnosticLevel.Info, "session.exited", $"sessionId={session.Id}");
});

async Task SendResponseAsync(StreamWriter writer, HostResponse response, CancellationToken cancellationToken = default)
{
    await NamedPipeProtocol.WriteLineAsync(writer, JsonSerializer.Serialize(response, jsonOptions), cancellationToken);
}

async Task TrySendErrorAsync(StreamWriter writer, string message)
{
    try
    {
        using var deadline = new CancellationTokenSource(NamedPipeProtocol.DefaultRequestTimeoutMs);
        await SendResponseAsync(writer, new HostResponse(false, Error: message, Identity: identity), deadline.Token);
    }
    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
}

async Task WatchDisconnectAsync(StreamReader reader, OutputSubscription subscription)
{
    try
    {
        var buffer = new char[256];
        while (await reader.ReadAsync(buffer.AsMemory()) != 0)
        {
            // Attach is one-way after the initial request. Any client-to-host
            // data is a protocol violation; do not retain an unbounded stream.
            break;
        }
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    finally { subscription.Complete(); }
}

static string? ReadOption(string[] args, string name)
{
    for (var i = 1; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

sealed class SlowConsumerException(string message) : IOException(message);

sealed class OutputSubscription
{
    private static readonly int MaxQueuedChars = GetSubscriberBudget();
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();
    private readonly object _gate = new();
    private long _queuedChars;
    private long _peakQueuedChars;
    private bool _completed;

    public string Replay { get; init; } = "";
    public string? Error { get; private set; }

    public bool TryEnqueue(string data, out bool slowConsumer)
    {
        slowConsumer = false;
        lock (_gate)
        {
            if (_completed) return false;
            if (data.Length > MaxQueuedChars || _queuedChars + data.Length > MaxQueuedChars)
            {
                slowConsumer = true;
                _completed = true;
                Error = $"Output consumer exceeded the {MaxQueuedChars}-character backlog budget; reconnect required.";
                while (_channel.Reader.TryRead(out _)) { }
                _queuedChars = 0;
                _channel.Writer.TryComplete(new SlowConsumerException(Error));
                return false;
            }
            if (!_channel.Writer.TryWrite(data)) return false;
            _queuedChars += data.Length;
            _peakQueuedChars = Math.Max(_peakQueuedChars, _queuedChars);
            return true;
        }
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        _channel.Reader.WaitToReadAsync(cancellationToken);

    public ValueTask<string> ReadAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);

    public bool TryRead(out string data) => _channel.Reader.TryRead(out data!);

    public void MarkConsumed(int characters)
    {
        lock (_gate) _queuedChars = Math.Max(0, _queuedChars - characters);
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            _channel.Writer.TryComplete();
        }
    }

    public long PeakQueuedChars => Interlocked.Read(ref _peakQueuedChars);

    private static int GetSubscriberBudget()
    {
        var configured = int.TryParse(Environment.GetEnvironmentVariable("CMUX_OUTPUT_BACKLOG_CHARS"), out var value)
            ? value : 4 * 1024 * 1024;
        return Math.Clamp(configured, 64 * 1024, 64 * 1024 * 1024);
    }
}

sealed class HostedSession : IDisposable
{
    private const int MaxReplayChars = 1_000_000;
    private readonly object _gate = new();
    private readonly ConptyProcess _process;
    private readonly List<OutputSubscription> _subscribers = [];
    private readonly Channel<string> _inputQueue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly CancellationTokenSource _inputStop = new();
    private readonly Queue<string> _replay = [];
    private int _replayLength;
    private readonly Action<string, string> _onSlowConsumer;
    private readonly Action<string, Exception> _onInputError;

    public HostedSession(string id, string commandLine, string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment, string? leaseId,
        Action<HostedSession> onExit, Action<string, string> onSlowConsumer,
        Action<string, Exception> onInputError)
    {
        Id = id;
        LeaseId = leaseId ?? Guid.NewGuid().ToString("N");
        _onSlowConsumer = onSlowConsumer;
        _onInputError = onInputError;
        _process = new ConptyProcess(commandLine, workingDirectory, environment);
        _process.Output += OnOutput;
        _process.Exited += () => onExit(this);
    }

    public string Id { get; }
    public string LeaseId { get; }
    public int ProcessId => _process.ProcessId;
    public HostSession Info => new(Id, ProcessId, _process.CommandLine, LeaseId);
    public void Start()
    {
        _process.Start();
        _ = Task.Run(PumpInputAsync);
    }

    public void WriteInput(string data)
    {
        if (!string.IsNullOrEmpty(data) && !_inputQueue.Writer.TryWrite(data))
            throw new IOException("Session input queue is full.");
    }

    public void Resize(uint rows, uint columns) => _process.Resize(rows, columns);

    public OutputSubscription Subscribe(bool includeReplay = true, int replayLimit = 0)
    {
        lock (_gate)
        {
            string replay = "";
            if (includeReplay)
            {
                replay = string.Concat(_replay);
                // A bounded suffix keeps a reconnect's first line small enough to write inside
                // the deadline even when the whole 1,000,000-character buffer is available.
                if (replayLimit > 0 && replay.Length > replayLimit) replay = replay[^replayLimit..];
            }
            var subscription = new OutputSubscription { Replay = replay };
            _subscribers.Add(subscription);
            return subscription;
        }
    }

    public void Unsubscribe(OutputSubscription subscription)
    {
        lock (_gate) _subscribers.Remove(subscription);
        subscription.Complete();
    }

    private void OnOutput(string data)
    {
        List<(OutputSubscription Subscription, string Message)>? slow = null;
        lock (_gate)
        {
            AppendReplay(data);
            foreach (var subscriber in _subscribers.ToArray())
            {
                if (subscriber.TryEnqueue(data, out var isSlow)) continue;
                if (!isSlow) continue;
                _subscribers.Remove(subscriber);
                (slow ??= []).Add((subscriber,
                    $"backlog={subscriber.PeakQueuedChars} characters; output connection closed"));
            }
        }
        if (slow is null) return;
        foreach (var item in slow) _onSlowConsumer(Id, item.Message);
    }

    private void AppendReplay(string data)
    {
        if (data.Length > MaxReplayChars)
            data = data[^MaxReplayChars..];
        _replay.Enqueue(data);
        _replayLength += data.Length;
        while (_replayLength > MaxReplayChars && _replay.Count > 0)
        {
            var removed = _replay.Dequeue();
            _replayLength -= removed.Length;
        }
        while (_replayLength > MaxReplayChars && _replay.Count > 0)
        {
            var first = _replay.Peek();
            var remove = Math.Min(first.Length, _replayLength - MaxReplayChars);
            _replay.Dequeue();
            _replay.Enqueue(first[remove..]);
            _replayLength -= remove;
        }
    }

    private async Task PumpInputAsync()
    {
        try
        {
            await foreach (var data in _inputQueue.Reader.ReadAllAsync(_inputStop.Token))
                _process.WriteInput(data);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _onInputError(Id, ex); }
    }

    public void Dispose()
    {
        _inputStop.Cancel();
        _inputQueue.Writer.TryComplete();
        _process.Dispose();
        lock (_gate)
        {
            foreach (var subscriber in _subscribers) subscriber.Complete();
            _subscribers.Clear();
            _replay.Clear();
            _replayLength = 0;
        }
    }
}
