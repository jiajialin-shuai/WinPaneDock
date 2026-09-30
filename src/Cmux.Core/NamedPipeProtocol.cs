using System.Text;

namespace Cmux.Core;

public static class NamedPipeProtocol
{
    public const int DefaultRequestTimeoutMs = 5000;
    public const int MaxRequestChars = 256 * 1024;

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

/// <summary>
/// Reads newline-delimited frames from a pipe, keeping the bytes that follow a frame
/// boundary for the next call.
/// </summary>
/// <remarks>
/// One read fills the whole buffer at a time, so a single read routinely spans a frame
/// boundary or several. Whatever follows the newline has to be carried over: a reader
/// that drops it desynchronises the stream, and every frame after the dropped remainder
/// starts mid-payload and fails to parse. On the terminal output channel that is fatal —
/// each unparsable frame forces a reconnect, and once the reconnect budget is spent the
/// pane is frozen until the terminal is restarted (see docs/M5-SESSION-HOST.md).
/// The remainder belongs to the pipe, not to the call, so it is held per reader:
/// create exactly one reader per <see cref="StreamReader"/>. Handing the reader itself to
/// the next stage matters as much as reusing it: a stage that builds a fresh reader over
/// the same stream starts again from the middle of the carry.
/// </remarks>
public sealed class NamedPipeLineReader : IDisposable
{
    private const int ReadChunkChars = 4096;

    private readonly StreamReader _reader;
    private readonly int _maxChars;
    private char[]? _buffer;
    private int _start;
    private int _end;

    public NamedPipeLineReader(StreamReader reader, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maxChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxChars));
        _reader = reader;
        _maxChars = maxChars;
    }

    /// <summary>
    /// Returns the next frame without its terminator, or null at end of stream.
    /// </summary>
    /// <remarks>
    /// A frame longer than <c>maxChars</c> throws, after its terminator has already been
    /// consumed. Staying on the offending frame would wedge the reader: every later call
    /// would fail the same way, so a caller that chose to carry on could never recover.
    /// </remarks>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            // A carried-over read can hold the whole of the next frame, or several of them,
            // so the scan has to run over the carry both before and after any new read.
            if (TryTakeLine(out var line)) return line;
            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                return _end > _start ? TakeRemainder() : null;
        }
    }

    /// <summary>Extracts one complete frame from the carry if it already holds one.</summary>
    private bool TryTakeLine(out string? line)
    {
        var buffer = _buffer;
        if (buffer is null) { line = null; return false; }
        var terminator = Array.IndexOf(buffer, '\n', _start, _end - _start);
        if (terminator < 0) { line = null; return false; }
        var start = _start;
        _start = terminator + 1;
        line = BuildLine(start, terminator);
        return true;
    }

    /// <summary>Reads one more chunk from the pipe, growing the carry when it is full.</summary>
    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        EnsureCapacity(ReadChunkChars);
        var read = await _reader.ReadAsync(_buffer.AsMemory(_end, ReadChunkChars), cancellationToken)
            .ConfigureAwait(false);
        if (read <= 0) return false;
        _end += read;
        return true;
    }

    /// <summary>Builds a frame, dropping the carriage returns a CRLF terminator leaves behind.</summary>
    private string BuildLine(int start, int end)
    {
        var builder = new StringBuilder(end - start);
        for (var i = start; i < end; i++)
        {
            var character = _buffer![i];
            if (character == '\r') continue;
            if (builder.Length >= _maxChars)
                throw new InvalidDataException($"IPC frame exceeds the {_maxChars}-character limit.");
            builder.Append(character);
        }
        return builder.ToString();
    }

    /// <summary>Returns an unterminated trailing frame at end of stream, if any.</summary>
    private string TakeRemainder()
    {
        var line = BuildLine(_start, _end);
        _start = _end = 0;
        return line;
    }

    /// <summary>
    /// Makes room for another chunk. The carry is compacted first, so a reader that just
    /// handed out a frame reuses the space instead of growing on every frame.
    /// </summary>
    private void EnsureCapacity(int extra)
    {
        _buffer ??= new char[ReadChunkChars];
        if (_end + extra > _buffer.Length)
        {
            if (_start > 0)
            {
                Array.Copy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
        }
        if (_end + extra > _buffer.Length)
        {
            var capacity = _buffer.Length;
            while (capacity < _end + extra) capacity *= 2;
            Array.Resize(ref _buffer, capacity);
        }
    }

    /// <summary>Releases the underlying stream. The carry is discarded with it.</summary>
    public void Dispose() => _reader.Dispose();
}
