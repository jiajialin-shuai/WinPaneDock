namespace Cmux.Terminal;

public static class InputChunker
{
    public const int MaxChunkChars = 16 * 1024;

    public static int GetChunkLength(string data, int offset)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, data.Length);
        if (offset == data.Length) return 0;
        var count = Math.Min(MaxChunkChars, data.Length - offset);
        if (count > 1 && offset + count < data.Length && char.IsHighSurrogate(data[offset + count - 1]))
            count--;
        return count;
    }
}
