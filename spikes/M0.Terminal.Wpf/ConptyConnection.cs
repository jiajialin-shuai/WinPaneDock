using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cmux.Terminal;
using Microsoft.Terminal.Wpf;

namespace Cmux.Spike.Terminal;

public sealed class ConptyConnection : ITerminalConnection, IDisposable
{
    // Replaying device queries would make the control answer them again, sending
    // capability reports into the still-running TUI as if the user had typed them.
    private static readonly Regex ReplayDeviceQueries = new(@"\x1b\[(?:\?u|[?>=]?[0-9;]*[cn])", RegexOptions.Compiled);
    private readonly string _commandLine;
    private readonly string? _workingDirectory;
    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly string _sessionId;
    private NamedPipeClientStream? _pipe;
    private bool _detached;
    private bool _closed;
    private long _lastOutputTick;

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
    public int ProcessId { get; private set; }
    public string CommandLine => _commandLine;
    public long LastOutputTick => Volatile.Read(ref _lastOutputTick);

    public void Start()
    {
        EnsureHost();
        var existing = SessionHostClient.Send(new HostRequest("list")).Sessions?
            .FirstOrDefault(s => s.SessionId == _sessionId);
        var result = existing is null
            ? SessionHostClient.Send(new HostRequest("create", _sessionId, _commandLine,
                _workingDirectory, _environment is null ? null : new Dictionary<string, string>(_environment)))
            : new HostResponse(true, ProcessId: existing.ProcessId);
        ProcessId = result.ProcessId;
        var attached = SessionHostClient.Attach(_sessionId);
        _pipe = attached.Pipe;
        if (!string.IsNullOrEmpty(attached.Response.Replay))
            TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(existing is null
                ? attached.Response.Replay : FilterReplay(attached.Response.Replay)));
        _ = Task.Run(() => ReadOutputAsync(attached.Reader));
    }

    public void WriteInput(string data)
    {
        if (!_closed && !string.IsNullOrEmpty(data))
            SessionHostClient.Send(new HostRequest("input", _sessionId, Data: data));
    }

    public void Resize(uint rows, uint columns)
    {
        if (!_closed && rows > 0 && columns > 0)
            SessionHostClient.Send(new HostRequest("resize", _sessionId, Rows: rows, Columns: columns));
    }

    public void Detach()
    {
        _detached = true;
        _pipe?.Dispose();
        _pipe = null;
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _pipe?.Dispose();
        _pipe = null;
        if (!_detached)
            SessionHostClient.Send(new HostRequest("close", _sessionId));
    }

    public void Kill() => Close();
    public void Dispose() => Close();

    internal static string FilterReplay(string replay) => ReplayDeviceQueries.Replace(replay, "");

    private async Task ReadOutputAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var response = JsonSerializer.Deserialize<HostResponse>(line);
                if (response?.Event == "output" && response.Output is { } data)
                {
                    Volatile.Write(ref _lastOutputTick, Environment.TickCount64);
                    TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(data));
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { reader.Dispose(); }
    }

    private static void EnsureHost()
    {
        try { SessionHostClient.Send(new HostRequest("list"), 200); return; }
        catch (TimeoutException) { }
        catch (IOException) { }

        var path = Path.Combine(AppContext.BaseDirectory, "Cmux.SessionHost.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("SessionHost was not built.", path);
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
        });
        for (var i = 0; i < 30; i++)
        {
            Thread.Sleep(100);
            try { SessionHostClient.Send(new HostRequest("list"), 200); return; }
            catch (TimeoutException) { }
            catch (IOException) { }
        }
        throw new IOException("SessionHost did not start.");
    }
}
