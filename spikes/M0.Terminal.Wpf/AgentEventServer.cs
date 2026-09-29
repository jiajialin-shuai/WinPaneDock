using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cmux.Core;
using Cmux.Terminal;

namespace Cmux.Spike.Terminal;

public sealed class AgentEventServer(Func<AgentEvent, bool> handleEvent) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public string PipeName { get; } = SessionHostProtocol.EventPipeName;

    public void Start() => _ = Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(NamedPipeProtocol.DefaultRequestTimeoutMs);
                var line = await NamedPipeProtocol.ReadLineAsync(reader, NamedPipeProtocol.MaxRequestChars,
                    deadline.Token);
                var agentEvent = line is null ? null : JsonSerializer.Deserialize<AgentEvent>(line, JsonOptions);
                var accepted = agentEvent is not null && handleEvent(agentEvent);
                await NamedPipeProtocol.WriteLineAsync(writer,
                    accepted ? "OK" : "Unknown WinPaneDock session.", deadline.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (OperationCanceledException)
            {
                App.Diagnostics.Write(DiagnosticLevel.Warning, "agent-event.timeout");
            }
            catch (Exception ex)
            {
                App.Log($"Agent event pipe failed: {ex}");
                try { await Task.Delay(1000, _stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose() => _stop.Cancel();
}
