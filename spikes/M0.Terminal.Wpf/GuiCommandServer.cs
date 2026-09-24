using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Cmux.Core;

namespace Cmux.Spike.Terminal;

public sealed class GuiCommandServer(Func<GuiCommandRequest, GuiCommandResponse> execute) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    public void Start() => _ = Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(GuiCommandProtocol.PipeName,
                    PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(_stop.Token);
                GuiCommandResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<GuiCommandRequest>(line ?? "");
                    response = request is null ? new(false, "Invalid command.") : execute(request);
                }
                catch (Exception ex) { response = new(false, ex.Message); }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                App.Log($"GUI command pipe failed: {ex}");
                try { await Task.Delay(1000, _stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose() => _stop.Cancel();
}
