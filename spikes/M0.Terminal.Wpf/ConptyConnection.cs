using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Cmux.Core;
using Cmux.Terminal;
using Microsoft.Terminal.Wpf;

namespace Cmux.Spike.Terminal;

public sealed class ConptyConnection : ITerminalConnection, IDisposable
{
    // Replaying device queries would make the control answer them again, sending
    // capability reports into the still-running TUI as if the user had typed them.
    private static readonly Regex ReplayDeviceQueries = new(@"\x1b\[(?:\?u|[?>=]?[0-9;]*[cn])", RegexOptions.Compiled);
    private static readonly Regex ReplayColorQueries = new(@"\x1b\](?:(?:10|11|12|17);\?(?:;\?)*|4;\d{1,3};\?(?:;\d{1,3};\?)*)(?:\x07|\x1b\\)", RegexOptions.Compiled);
    private static readonly Regex ReplayEchoedColorReplies = new(@"(?<!\x1b)\](?:10|11|12|17);rgb:[0-9a-f]{1,4}/[0-9a-f]{1,4}/[0-9a-f]{1,4}(?:\\|\x07)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const int MaxQueuedRequests = 4096;
    private const int MaxQueuedOutputBatches = 32;
    private const int MaxOutputBatchChars = 64 * 1024;
    private const int OutputBatchDelayMs = 8;
    // An attach pipe is not durable: a write deadline, a malformed frame or a host restart
    // ends it. The session outlives the pipe, so a bounded reconnect with backoff keeps a
    // pane alive instead of freezing it for the rest of the GUI's lifetime.
    private const int MaxReattachAttempts = 6;
    private const int ReattachBaseDelayMs = 250;
    private const int ReattachMaxDelayMs = 4000;
    // The first attach of a restored session may legitimately carry a 1,000,000-character
    // replay, so it gets a deadline of its own instead of the generic 5s request timeout.
    private const int FirstAttachTimeoutMs = 30000;
    private const int ReattachTimeoutMs = 10000;
    // A reconnect only needs enough replay to repaint the screen; it already has a local tail
    // buffer, and asking for the whole thing is what turns a reconnect into a guaranteed
    // multi-megabyte write deadline.
    private const int ReattachReplayLimit = 256 * 1024;
    // The native terminal handle only exists once the HwndHost builds its window, and
    // TerminalContainer drops output that arrives before then. A TUI that has already
    // painted its screen will not repaint on its own, so a bounded tail is kept here and
    // replayed once the control is realised.
    private const int MaxTailChars = 512 * 1024;

    private enum OutputPumpStop
    {
        /// <summary>The connection is finished on purpose: closed, detached, or torn down.</summary>
        Completed,
        /// <summary>The pipe died but the session should still exist.</summary>
        Reattachable,
        /// <summary>The session itself is gone, so reconnecting cannot help.</summary>
        Permanent,
    }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _outputGate = new();
    private readonly StringBuilder _pendingOutput = new();
    private readonly object _tailGate = new();
    private readonly StringBuilder _outputTail = new();
    private readonly Channel<string> _outputBatches = Channel.CreateBounded<string>(
        new BoundedChannelOptions(MaxQueuedOutputBatches)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private System.Threading.Timer? _outputFlushTimer;
    private Task? _outputEmitter;
    private readonly string _commandLine;
    private readonly string? _workingDirectory;
    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly string _sessionId;
    private string? _leaseId = Guid.NewGuid().ToString("N");
    private readonly Channel<QueuedRequest> _requests = Channel.CreateBounded<QueuedRequest>(
        new BoundedChannelOptions(MaxQueuedRequests) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly CancellationTokenSource _requestStop = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private Cmux.Core.NamedPipeLineReader? _pendingOutputReader;
    private string? _pendingReplay;
    private Task? _requestPump;
    private Task? _outputPump;
    private int _started;
    private int _prepared;
    private int _attached;
    private int _reattachAttempts;
    private int _pumpsStarted;
    private int _transportStopped;
    private Task? _startTask;
    private int _closed;
    private volatile bool _detached;
    private long _lastOutputTick;
    private long _outputBatchesSent;
    private long _outputCharactersSent;

    public ConptyConnection(string commandLine, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        _commandLine = commandLine;
        _workingDirectory = workingDirectory;
        _environment = environmentOverrides;
        _sessionId = environmentOverrides?.GetValueOrDefault("CMUX_SESSION_ID")
            ?? Guid.NewGuid().ToString();
    }

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;
    public event EventHandler<string>? Faulted;
    public int ProcessId { get; private set; }
    public string CommandLine => _commandLine;
    public long LastOutputTick => Volatile.Read(ref _lastOutputTick);
    public bool IsPrepared => Volatile.Read(ref _prepared) != 0;
    public string? LastError { get; private set; }

    /// <summary>How many times the output stream has been re-established after a pipe loss.</summary>
    internal int ReattachCount => Volatile.Read(ref _reattachAttempts);

    /// <summary>Replay size accepted by the most recent reconnect.</summary>
    internal int LastReattachReplayChars { get; private set; }

    /// <summary>Output characters actually delivered to the control.</summary>
    internal long OutputCharactersSent => Interlocked.Read(ref _outputCharactersSent);

    /// <summary>
    /// Drops the attach pipe as if the host had torn it down, so the recovery path can be
    /// driven deterministically by a gate run instead of waiting for a real 5s write
    /// deadline. The session is untouched; only the subscription dies.
    /// </summary>
    internal void DropAttachForTest() => DisposePipe();

    public void Start()
    {
        if (Volatile.Read(ref _prepared) == 0)
            StartAsync().GetAwaiter().GetResult();
        AttachAndStart();
    }

    /// <summary>Prepares the host/session/attach pipe away from the WPF Dispatcher.</summary>
    public Task PrepareHostAsync() => StartAsync();

    /// <summary>
    /// Shares one preparation task, so a control attach that races an in-flight
    /// <see cref="PrepareHostAsync"/> waits for the lease and shell PID instead of
    /// attaching with them still unset.
    /// </summary>
    private Task StartAsync()
    {
        var pending = Volatile.Read(ref _startTask);
        if (pending is not null) return pending;
        var created = RunStartAsync();
        return Interlocked.CompareExchange(ref _startTask, created, null) ?? created;
    }

    private async Task RunStartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        await _lifecycleGate.WaitAsync(_requestStop.Token).ConfigureAwait(false);
        try
        {
            App.Diagnostics.Write(DiagnosticLevel.Debug, "terminal.host.ensure", $"instance={InstanceScope.Id}");
            await EnsureHostAsync(_requestStop.Token).ConfigureAwait(false);
            _requestStop.Token.ThrowIfCancellationRequested();
            var listResponse = await SessionHostClient.SendAsync(new HostRequest("list"), cancellationToken: _requestStop.Token)
                .ConfigureAwait(false);
            SessionHostClient.RequireCompatibleIdentity(listResponse,
                Path.Combine(AppContext.BaseDirectory, "Cmux.SessionHost.exe"));
            var existing = listResponse.Sessions?.FirstOrDefault(s => s.SessionId == _sessionId);
            var result = existing is null
                ? await SessionHostClient.SendAsync(new HostRequest("create", _sessionId, _commandLine,
                    _workingDirectory, _environment is null ? null : new Dictionary<string, string>(_environment),
                    LeaseId: _leaseId), cancellationToken: _requestStop.Token).ConfigureAwait(false)
                : new HostResponse(true, ProcessId: existing.ProcessId);
            if (existing is null && result.LeaseId is not null && result.LeaseId != _leaseId)
                throw new IOException("SessionHost returned a different session lease.");
            _leaseId = existing?.LeaseId ?? result.LeaseId ?? _leaseId
                ?? throw new IOException("SessionHost did not return a session lease.");
            _requestStop.Token.ThrowIfCancellationRequested();
            ProcessId = result.ProcessId;
            Interlocked.Exchange(ref _prepared, 1);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void AttachAndStart()
    {
        if (Interlocked.Exchange(ref _attached, 1) != 0)
        {
            StartPumps();
            return;
        }
        try
        {
            var attached = SessionHostClient.Attach(_sessionId, FirstAttachTimeoutMs, leaseId: _leaseId);
            SessionHostClient.RequireCompatibleIdentity(attached.Response,
                Path.Combine(AppContext.BaseDirectory, "Cmux.SessionHost.exe"));
            _pipe = attached.Pipe;
            _pendingOutputReader = attached.Frames;
            _pendingReplay = string.IsNullOrEmpty(attached.Response.Replay) ? null
                : FilterReplay(attached.Response.Replay);
            StartPumps();
        }
        catch
        {
            Interlocked.Exchange(ref _attached, 0);
            Interlocked.Exchange(ref _pendingOutputReader, null)?.Dispose();
            DisposePipe();
            throw;
        }
    }

    private void StartPumps()
    {
        if (Interlocked.Exchange(ref _pumpsStarted, 1) != 0) return;
        if (_pendingReplay is { } replay)
        {
            _pendingReplay = null;
            TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(replay));
        }
        _requestPump = Task.Run(RunRequestsAsync);
        _outputEmitter = Task.Run(EmitOutputAsync);
        if (_pendingOutputReader is not null) _outputPump = Task.Run(ReadOutputAsync);
    }

    /// <summary>Drops the current attach pipe without touching the session.</summary>
    private void DisposePipe()
    {
        var pipe = Interlocked.Exchange(ref _pipe, null);
        if (pipe is null) return;
        try { pipe.Dispose(); } catch { }
    }

    private static void TryDispose(IDisposable? disposable)
    {
        if (disposable is null) return;
        try { disposable.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
    }

    public void WriteInput(string data)
    {
        if (Volatile.Read(ref _closed) != 0 || _detached || string.IsNullOrEmpty(data)) return;
        for (var offset = 0; offset < data.Length;)
        {
            var count = InputChunker.GetChunkLength(data, offset);
            if (!_requests.Writer.TryWrite(QueuedRequest.Input(data.Substring(offset, count))))
            {
                ReportError("Input queue is full; the terminal connection was paused to protect memory.");
                return;
            }
            offset += count;
        }
    }

    public void Resize(uint rows, uint columns)
    {
        if (Volatile.Read(ref _closed) != 0 || _detached || rows == 0 || columns == 0) return;
        if (!_requests.Writer.TryWrite(QueuedRequest.Resize(rows, columns)))
            ReportError("Resize queue is full; the terminal connection was paused to protect the GUI.");
    }

    public void Detach()
    {
        _detached = true;
        _requestStop.Cancel();
        _lifecycleGate.Wait();
        try
        {
            StopTransport();
        }
        finally { _lifecycleGate.Release(); }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _requestStop.Cancel();
        _lifecycleGate.Wait();
        try
        {
            StopTransport();
            if (!_detached)
                SessionHostClient.Send(new HostRequest("close", _sessionId, LeaseId: _leaseId));
        }
        catch
        {
            // Keep the connection retryable when the close acknowledgement is lost.
            Interlocked.Exchange(ref _closed, 0);
            throw;
        }
        finally { _lifecycleGate.Release(); }
    }

    public void Kill() => Close();
    public void Dispose()
    {
        try { Close(); }
        catch (Exception ex) { ReportError($"Terminal close failed: {ex.Message}"); }
    }

    internal static string FilterReplay(string replay) => ReplayEchoedColorReplies.Replace(
        ReplayColorQueries.Replace(ReplayDeviceQueries.Replace(replay, ""), ""), "");

    private async Task RunRequestsAsync()
    {
        try
        {
            while (await _requests.Reader.WaitToReadAsync(_requestStop.Token))
            {
                QueuedRequest? pendingResize = null;
                while (!_requestStop.IsCancellationRequested && _requests.Reader.TryRead(out var request))
                {
                    if (request.IsResize) pendingResize = request;
                    else await SendRequestAsync(new HostRequest("input", _sessionId, Data: request.Data,
                        LeaseId: _leaseId), _requestStop.Token);
                }
                if (!_requestStop.IsCancellationRequested && pendingResize is { } resize)
                    await SendRequestAsync(new HostRequest("resize", _sessionId,
                        Rows: resize.Rows, Columns: resize.Columns, LeaseId: _leaseId), _requestStop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError($"SessionHost request failed: {ex.Message}"); }
    }

    private async Task SendRequestAsync(HostRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await SessionHostClient.SendAsync(request, 5000, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { ReportError($"SessionHost request failed: {ex.Message}"); }
    }

    private async Task ReadOutputAsync()
    {
        var stop = OutputPumpStop.Completed;
        var frameCount = 0L;
        try
        {
            while (true)
            {
                var reader = Interlocked.Exchange(ref _pendingOutputReader, null);
                if (reader is null) { stop = OutputPumpStop.Completed; break; }
                var outcome = await PumpFramesAsync(reader, frameCount).ConfigureAwait(false);
                stop = outcome.Stop;
                frameCount = outcome.Frames;
                TryDispose(reader);
                if (stop != OutputPumpStop.Reattachable) break;
                if (!await TryReattachOutputAsync().ConfigureAwait(false))
                {
                    stop = OutputPumpStop.Completed;
                    break;
                }
            }
        }
        finally
        {
            FlushOutput();
            _outputBatches.Writer.TryComplete();
        }
        if (stop == OutputPumpStop.Permanent && !_detached)
            ReportError(LastError ?? "The session ended and the output stream could not be restored.");
    }

    /// <summary>
    /// Reads frames from one attach pipe until it stops. Returns why it stopped so the
    /// caller can decide between giving up and reattaching.
    /// </summary>
    private async Task<(OutputPumpStop Stop, long Frames)> PumpFramesAsync(
        Cmux.Core.NamedPipeLineReader frames, long frameCount)
    {
        string? offending = null;
        // The reader is the one the attach produced, so the first output frames that shared
        // a read with the attach response are still pending inside it. A reader built here
        // would start from the middle of that carry and desynchronise the stream.
        try
        {
            while (await frames.ReadLineAsync(CancellationToken.None) is { } line)
            {
                HostResponse? response;
                try { response = JsonSerializer.Deserialize<HostResponse>(line, JsonOptions); }
                catch (JsonException ex)
                {
                    // A frame that does not parse means the reader is no longer on a frame
                    // boundary. Log the raw head and the frame counter: that is the only
                    // evidence that distinguishes a lost tail from a desynchronised stream.
                    offending = line;
                    App.Diagnostics.Write(DiagnosticLevel.Error, "terminal.output.malformed",
                        $"sessionId={_sessionId} frames={frameCount} chars={line.Length} " +
                        $"head={SummarizeForLog(line)} reason={ex.Message}");
                    return (OutputPumpStop.Reattachable, frameCount);
                }
                if (response?.Event == "output" && response.Output is { } data)
                {
                    frameCount++;
                    Volatile.Write(ref _lastOutputTick, Environment.TickCount64);
                    QueueOutput(data);
                }
                else if (response is { Ok: false })
                {
                    var error = response.Error ?? "SessionHost disconnected the output stream.";
                    App.Diagnostics.Write(DiagnosticLevel.Warning, "terminal.output.refused",
                        $"sessionId={_sessionId} frames={frameCount} error={error}");
                    return (IsPermanentAttachFailure(error) ? OutputPumpStop.Permanent : OutputPumpStop.Reattachable, frameCount);
                }
            }
            // A clean end of stream: the host tore the attach down (write deadline, or the
            // client side went away). Reattaching is correct because the session is durable.
            return (OutputPumpStop.Reattachable, frameCount);
        }
        catch (IOException ex)
        {
            if (_detached || Volatile.Read(ref _closed) != 0) return (OutputPumpStop.Completed, frameCount);
            App.Diagnostics.Write(DiagnosticLevel.Warning, "terminal.output.io",
                $"sessionId={_sessionId} frames={frameCount} error={ex.GetType().Name}: {ex.Message}");
            return (OutputPumpStop.Reattachable, frameCount);
        }
        catch (ObjectDisposedException) { return (OutputPumpStop.Completed, frameCount); }
        catch (InvalidDataException ex)
        {
            // A single frame exceeded the reader limit. The reader has already stepped past
            // it, so the frames behind it are still intact, but the safest response to a
            // frame this far out of shape is to rebuild the pipe.
            if (_detached || Volatile.Read(ref _closed) != 0) return (OutputPumpStop.Completed, frameCount);
            App.Diagnostics.Write(DiagnosticLevel.Error, "terminal.output.oversize",
                $"sessionId={_sessionId} frames={frameCount} error={ex.Message}");
            return (OutputPumpStop.Reattachable, frameCount);
        }
    }

    /// <summary>
    /// Re-establishes the output pipe for a session that outlives its attach. The host
    /// keeps the session and replays everything buffered since the last subscriber, so the
    /// pane recovers instead of freezing for the rest of the GUI's lifetime.
    /// </summary>
    private async Task<bool> TryReattachOutputAsync()
    {
        if (Volatile.Read(ref _transportStopped) != 0 || Volatile.Read(ref _closed) != 0 || _detached)
            return false;
        if (Interlocked.Increment(ref _reattachAttempts) > MaxReattachAttempts)
        {
            ReportError($"Terminal output stopped after {MaxReattachAttempts} reconnect attempts. " +
                        "Restart this terminal to recover it.");
            return false;
        }
        var delay = Math.Min(ReattachBaseDelayMs * (1 << Math.Min(_reattachAttempts - 1, 5)), ReattachMaxDelayMs);
        await Task.Delay(delay).ConfigureAwait(false);
        if (Volatile.Read(ref _transportStopped) != 0 || Volatile.Read(ref _closed) != 0 || _detached)
            return false;
        try
        {
            var attached = await SessionHostClient.AttachAsync(_sessionId, ReattachTimeoutMs, _leaseId,
                    includeReplay: true, replayLimit: ReattachReplayLimit, cancellationToken: _requestStop.Token)
                .ConfigureAwait(false);
            SessionHostClient.RequireCompatibleIdentity(attached.Response,
                Path.Combine(AppContext.BaseDirectory, "Cmux.SessionHost.exe"));
            // The old pipe is dropped first: leaving it open would keep a dead subscription
            // alive on the host and let it fail its write deadline again.
            DisposePipe();
            _pipe = attached.Pipe;
            _pendingOutputReader = attached.Frames;
            Interlocked.Exchange(ref _attached, 1);
            var replayRaw = attached.Response.Replay;
            var replay = string.IsNullOrEmpty(replayRaw) ? null : FilterReplay(replayRaw);
            LastReattachReplayChars = replay?.Length ?? 0;
            App.Diagnostics.Write(DiagnosticLevel.Info, "terminal.reattached",
                $"sessionId={_sessionId} processId={attached.Response.ProcessId} " +
                $"replayRawChars={replayRaw?.Length ?? 0} replayChars={LastReattachReplayChars} " +
                $"attempt={Volatile.Read(ref _reattachAttempts)}");
            if (replay is not null) QueueOutput(replay);
            return true;
        }
        catch (SessionHostProtocolException ex)
        {
            // Identity mismatch: a different host build owns this session. Retrying is futile.
            App.Diagnostics.Write(DiagnosticLevel.Error, "terminal.reattach.identity", exception: ex);
            ReportError($"Terminal output could not be restored: {ex.Message}");
            return false;
        }
        catch (OperationCanceledException)
        {
            // A reconnect that times out is worth another try, but not indefinitely: if the host
            // cannot answer within the deadline there is nothing a shorter backoff will change.
            var attempt = Volatile.Read(ref _reattachAttempts);
            App.Diagnostics.Write(DiagnosticLevel.Warning, "terminal.reattach.timeout",
                $"sessionId={_sessionId} attempt={attempt} budget={MaxReattachAttempts}");
            return attempt < MaxReattachAttempts && Volatile.Read(ref _transportStopped) == 0;
        }
        catch (Exception ex)
        {
            var attempt = Volatile.Read(ref _reattachAttempts);
            App.Diagnostics.Write(DiagnosticLevel.Warning, "terminal.reattach.failed",
                $"sessionId={_sessionId} attempt={attempt} error={ex.GetType().Name}: {ex.Message}");
            return attempt < MaxReattachAttempts && !IsPermanentAttachFailure(ex.Message);
        }
    }

    private static bool IsPermanentAttachFailure(string message) =>
        message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("lease", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("identity", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("protocol", StringComparison.OrdinalIgnoreCase);

    /// <summary>Renders a bounded, control-character-safe excerpt of a raw frame.</summary>
    private static string SummarizeForLog(string value)
    {
        const int limit = 96;
        var truncated = value.Length > limit;
        var slice = truncated ? value[..limit] : value;
        var builder = new StringBuilder(slice.Length + 8);
        foreach (var character in slice)
        {
            if (character == '"' || character == '\\') { builder.Append('\\').Append(character); }
            else if (character < ' ' || character == '\u007f') { builder.Append("\\u").Append(((int)character).ToString("x4")); }
            else { builder.Append(character); }
        }
        return truncated ? builder.Append("...(+").Append(value.Length - limit).Append(')').ToString() : builder.ToString();
    }

    private void QueueOutput(string data)
    {
        string? immediate = null;
        lock (_outputGate)
        {
            _pendingOutput.Append(data);
            if (_pendingOutput.Length >= MaxOutputBatchChars) immediate = TakePendingOutput();
            else if (_outputFlushTimer is null)
                _outputFlushTimer = new System.Threading.Timer(_ => FlushOutput(), null, OutputBatchDelayMs, Timeout.Infinite);
        }
        if (immediate is not null) EnqueueOutputBatch(immediate);
    }

    private string TakePendingOutput()
    {
        if (_pendingOutput.Length == 0) return "";
        var result = _pendingOutput.ToString();
        _pendingOutput.Clear();
        return result;
    }

    private void FlushOutput()
    {
        string? batch = null;
        lock (_outputGate)
        {
            if (_pendingOutput.Length == 0)
            {
                _outputFlushTimer?.Dispose();
                _outputFlushTimer = null;
                return;
            }
            batch = TakePendingOutput();
            _outputFlushTimer?.Dispose();
            _outputFlushTimer = null;
        }
        EnqueueOutputBatch(batch);
    }

    private void EnqueueOutputBatch(string batch)
    {
        if (batch.Length == 0) return;
        // After a normal close the batch channel is completed, so a timer callback that is
        // still in flight would otherwise report a bogus "rendering is too slow" fault.
        if (Volatile.Read(ref _transportStopped) != 0) return;
        if (_outputBatches.Writer.TryWrite(batch)) return;
        // The renderer cannot keep up. Dropping the pipe ends the read loop, which now
        // reattaches and replays what was missed, so this is a recovery step rather than a
        // terminal failure; the loop reports to the user only if the retries run out.
        App.Diagnostics.Write(DiagnosticLevel.Warning, "terminal.output.backlog",
            $"sessionId={_sessionId} batchesSent={Volatile.Read(ref _outputBatchesSent)}");
        DisposePipe();
    }

    private async Task EmitOutputAsync()
    {
        try
        {
            await foreach (var batch in _outputBatches.Reader.ReadAllAsync())
            {
                AppendTail(batch);
                TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(batch));
                var batches = Interlocked.Increment(ref _outputBatchesSent);
                var characters = Interlocked.Add(ref _outputCharactersSent, batch.Length);
                if ((batches & 63) == 0)
                    App.Diagnostics.Write(DiagnosticLevel.Debug, "terminal.output.metrics",
                        $"batches={batches} characters={characters}");
            }
        }
        catch (ChannelClosedException) { }
        catch (Exception ex) { ReportError($"Terminal output rendering failed: {ex.Message}"); }
    }

    private void AppendTail(string batch)
    {
        lock (_tailGate)
        {
            _outputTail.Append(batch);
            if (_outputTail.Length <= MaxTailChars) return;
            _outputTail.Remove(0, _outputTail.Length - MaxTailChars);
        }
    }

    /// <summary>
    /// Re-sends the buffered tail to a control that was attached before its native terminal
    /// existed. Safe to call once per control realisation; it re-renders the screen instead of
    /// leaving a started TUI invisible until the user happens to resize something.
    /// </summary>
    internal void ReplayBufferedOutput()
    {
        string snapshot;
        lock (_tailGate)
        {
            if (_outputTail.Length == 0) return;
            snapshot = _outputTail.ToString();
        }
        App.Diagnostics.Write(DiagnosticLevel.Info, "terminal.output.replayed",
            $"sessionId={_sessionId} chars={snapshot.Length}");
        try { TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(snapshot)); }
        catch (Exception ex) { ReportError($"Terminal output replay failed: {ex.Message}"); }
    }

    private void StopTransport()
    {
        Interlocked.Exchange(ref _transportStopped, 1);
        _requestStop.Cancel();
        _requests.Writer.TryComplete();
        try { _requestPump?.Wait(TimeSpan.FromSeconds(1)); }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException) { }
        FlushOutput();
        _outputBatches.Writer.TryComplete();
        lock (_outputGate)
        {
            _outputFlushTimer?.Dispose();
            _outputFlushTimer = null;
        }
        Interlocked.Exchange(ref _pendingOutputReader, null)?.Dispose();
        DisposePipe();
    }

    private void ReportError(string message)
    {
        LastError = message;
        try { Faulted?.Invoke(this, message); } catch { }
    }

    private static async Task EnsureHostAsync(CancellationToken cancellationToken = default)
    {
        var deadline = Stopwatch.StartNew();
        var path = Path.Combine(AppContext.BaseDirectory, "Cmux.SessionHost.exe");
        try
        {
            App.Diagnostics.Write(DiagnosticLevel.Debug, "terminal.host.probe", "list");
            var response = await SessionHostClient.SendAsync(new HostRequest("list"), 500, cancellationToken)
                .ConfigureAwait(false);
            SessionHostClient.RequireCompatibleIdentity(response, path);
            App.Diagnostics.Write(DiagnosticLevel.Debug, "terminal.host.probe.ok", "list");
            return;
        }
        catch (SessionHostProtocolException) { throw; }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            App.Diagnostics.Write(DiagnosticLevel.Debug, "terminal.host.probe.failed", ex.GetType().Name);
        }

        if (!File.Exists(path)) throw new FileNotFoundException("SessionHost was not built.", path);
        App.Diagnostics.Write(DiagnosticLevel.Debug, "terminal.host.path", path);
        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.Environment[InstanceScope.EnvironmentVariable] = InstanceScope.Id;
        Process.Start(startInfo);
        App.Diagnostics.Write(DiagnosticLevel.Debug, "terminal.host.launched", $"path={path}");
        while (deadline.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            var remaining = Math.Max(1, 5000 - (int)deadline.ElapsedMilliseconds);
            try
            {
                var response = await SessionHostClient.SendAsync(new HostRequest("list"), Math.Min(500, remaining), cancellationToken)
                    .ConfigureAwait(false);
                SessionHostClient.RequireCompatibleIdentity(response, path);
                return;
            }
            catch (SessionHostProtocolException) { throw; }
            catch (Exception ex) when (ex is IOException or TimeoutException) { }
        }
        throw new IOException("SessionHost did not start before the deadline.");
    }

    private sealed record QueuedRequest
    {
        public bool IsResize { get; init; }
        public string? Data { get; init; }
        public uint Rows { get; init; }
        public uint Columns { get; init; }

        public static QueuedRequest Input(string data) => new() { Data = data };
        public static QueuedRequest Resize(uint rows, uint columns) =>
            new() { IsResize = true, Rows = rows, Columns = columns };
    }
}
