using System.Buffers;
using System.Text;

namespace Cmux.Core;

public static class NamedPipeProtocol
{
    public const int DefaultRequestTimeoutMs = 5000;
    public const int MaxRequestChars = 256 * 1024;

    public static async Task<string?> ReadLineAsync(
        StreamReader reader,
        int maxChars,
        CancellationToken cancellationToken)
    {
        if (maxChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxChars));
        var builder = new StringBuilder();
        var buffer = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) return builder.Length == 0 ? null : builder.ToString();
                for (var i = 0; i < read; i++)
                {
                    var character = buffer[i];
                    if (character == '\n') return builder.ToString();
                    if (character == '\r') continue;
                    if (builder.Length >= maxChars)
                        throw new InvalidDataException($"IPC request exceeds the {maxChars}-character limit.");
                    builder.Append(character);
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    public static async Task WriteLineAsync(
        StreamWriter writer,
        string value,
        CancellationToken cancellationToken)
    {
        // The token has to reach the StreamWriter. Wrapping a token-less write in
        // WaitAsync(token) only cancels the await: the pipe write stays in flight, and the
        // next write on that PipeStream (the writer's dispose-time flush, for example)
        // fails with "The stream is currently in use by a previous operation on the stream."
        // A timed-out write must actually be abandoned, not merely unobserved.
        await writer.WriteLineAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public static int RemainingMilliseconds(long startedAt, int timeoutMs)
    {
        var elapsed = Environment.TickCount64 - startedAt;
        return Math.Max(1, timeoutMs - (int)Math.Min(elapsed, timeoutMs));
    }
}
