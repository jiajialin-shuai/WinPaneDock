using System.IO.Pipes;
using System.Text.Json;

namespace Cmux.Terminal;

public static class SessionHostClient
{
    public static HostResponse Send(HostRequest request, int timeoutMs = 5000)
    {
        using var pipe = Connect(timeoutMs);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        writer.WriteLine(JsonSerializer.Serialize(request));
        var response = JsonSerializer.Deserialize<HostResponse>(reader.ReadLine() ?? "");
        if (response is null) throw new IOException("SessionHost returned no response.");
        if (!response.Ok) throw new IOException(response.Error);
        return response;
    }

    public static (NamedPipeClientStream Pipe, StreamReader Reader, HostResponse Response) Attach(string sessionId)
    {
        var pipe = Connect(5000);
        try
        {
            var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(new HostRequest("attach", sessionId)));
            writer.Dispose();
            var reader = new StreamReader(pipe, leaveOpen: true);
            var response = JsonSerializer.Deserialize<HostResponse>(reader.ReadLine() ?? "");
            if (response is null || !response.Ok) throw new IOException(response?.Error ?? "Attach failed.");
            return (pipe, reader, response);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static NamedPipeClientStream Connect(int timeoutMs)
    {
        var pipe = new NamedPipeClientStream(".", SessionHostProtocol.PipeName,
            PipeDirection.InOut, PipeOptions.None);
        try { pipe.Connect(timeoutMs); return pipe; }
        catch { pipe.Dispose(); throw; }
    }
}
