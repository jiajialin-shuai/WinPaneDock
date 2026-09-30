using System.IO.Pipes;
using System.Text.Json;

namespace Cmux.Terminal;

public sealed class SessionHostProtocolException(string message) : IOException(message);

public static class SessionHostClient
{
    private const int MaxResponseChars = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static HostResponse Send(HostRequest request, int timeoutMs = 5000) =>
        SendAsync(request, timeoutMs).GetAwaiter().GetResult();

    public static async Task<HostResponse> SendAsync(
        HostRequest request, int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMs, 1);
        using var deadline = CreateDeadline(timeoutMs, cancellationToken);
        NamedPipeClientStream? pipe = null;
        StreamWriter? writer = null;
        StreamReader? reader = null;
        try
        {
            pipe = await ConnectAsync(deadline.Token).ConfigureAwait(false);
            writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            reader = new StreamReader(pipe, leaveOpen: true);
            var frames = new Cmux.Core.NamedPipeLineReader(reader, MaxResponseChars);
            var payload = JsonSerializer.Serialize(request with { ProtocolVersion = SessionHostProtocol.ProtocolVersion }, JsonOptions);
            await Cmux.Core.NamedPipeProtocol.WriteLineAsync(writer, payload, deadline.Token)
                .ConfigureAwait(false);
            var line = await frames.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            return ParseResponse(line);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("SessionHost request deadline exceeded.", ex);
        }
        finally
        {
            TryDispose(reader);
            TryDispose(writer);
            TryDispose(pipe);
        }
    }

    public static (NamedPipeClientStream Pipe, Cmux.Core.NamedPipeLineReader Frames, HostResponse Response) Attach(
        string sessionId, int timeoutMs = 5000, string? leaseId = null,
        bool includeReplay = true, int replayLimit = 0) =>
        AttachAsync(sessionId, timeoutMs, leaseId, includeReplay, replayLimit).GetAwaiter().GetResult();

    // The attach response and the first output frames can arrive in a single read, so the
    // frame reader that parsed the response is handed on rather than the bare stream: the
    // caller's output pump continues from the carry instead of starting mid-frame.
    public static async Task<(NamedPipeClientStream Pipe, Cmux.Core.NamedPipeLineReader Frames, HostResponse Response)> AttachAsync(
        string sessionId, int timeoutMs = 5000, string? leaseId = null,
        bool includeReplay = true, int replayLimit = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMs, 1);
        using var deadline = CreateDeadline(timeoutMs, cancellationToken);
        var pipe = await ConnectAsync(deadline.Token).ConfigureAwait(false);
        StreamWriter? writer = null;
        Cmux.Core.NamedPipeLineReader? frames = null;
        try
        {
            writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            var payload = JsonSerializer.Serialize(
                new HostRequest("attach", sessionId, ProtocolVersion: SessionHostProtocol.ProtocolVersion,
                    LeaseId: leaseId, IncludeReplay: includeReplay, ReplayLimit: replayLimit), JsonOptions);
            await Cmux.Core.NamedPipeProtocol.WriteLineAsync(writer, payload, deadline.Token)
                .ConfigureAwait(false);
            TryDispose(writer);
            writer = null;
            frames = new Cmux.Core.NamedPipeLineReader(new StreamReader(pipe, leaveOpen: true), MaxResponseChars);
            var line = await frames.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            var response = ParseResponse(line);
            var result = (pipe, frames, response);
            frames = null;
            return result;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            TryDispose(frames);
            TryDispose(pipe);
            throw new TimeoutException("SessionHost attach deadline exceeded.", ex);
        }
        catch
        {
            TryDispose(frames);
            TryDispose(pipe);
            throw;
        }
        finally
        {
            TryDispose(writer);
        }
    }

    public static HostIdentity RequireCompatibleIdentity(
        HostResponse response, string? expectedExecutablePath = null)
    {
        if (response.Identity is null)
            throw new SessionHostProtocolException("SessionHost does not expose a protocol identity.");
        if (response.Identity.ProtocolVersion != SessionHostProtocol.ProtocolVersion)
            throw new SessionHostProtocolException($"SessionHost protocol {response.Identity.ProtocolVersion} is incompatible with {SessionHostProtocol.ProtocolVersion}.");
        if (!string.Equals(response.Identity.InstanceId, Cmux.Core.InstanceScope.Id, StringComparison.Ordinal))
            throw new SessionHostProtocolException("SessionHost instance identity does not match the requested instance.");
        if (expectedExecutablePath is not null &&
            !PathsEqual(response.Identity.ExecutablePath, expectedExecutablePath))
            throw new SessionHostProtocolException("SessionHost executable identity does not match the requested build.");
        return response.Identity;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", SessionHostProtocol.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        var connectTask = pipe.ConnectAsync();
        try
        {
            await connectTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            _ = connectTask.ContinueWith(task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            TryDispose(pipe);
            throw;
        }
    }

    private static CancellationTokenSource CreateDeadline(int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        return deadline;
    }

    private static HostResponse ParseResponse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) throw new IOException("SessionHost returned no response.");
        if (line.Length > MaxResponseChars) throw new IOException("SessionHost response exceeds the client limit.");
        var response = JsonSerializer.Deserialize<HostResponse>(line, JsonOptions);
        if (response is null) throw new IOException("SessionHost returned an invalid response.");
        if (!response.Ok) throw new IOException(response.Error ?? "SessionHost request failed.");
        return response;
    }

    private static void TryDispose(IDisposable? disposable)
    {
        try { disposable?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
    }
}
