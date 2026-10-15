namespace SlKit.Core;

/// <summary>Bytes read by <see cref="BoundedRead.ReadAsync"/>.</summary>
public struct BoundedReadResult
{
    /// <summary>Creates a result.</summary>
    /// <param name="buffer">Backing buffer; only the first <paramref name="count"/> bytes are valid.</param>
    /// <param name="count">Number of valid bytes.</param>
    /// <param name="exceeded">True when the stream held more than the limit.</param>
    public BoundedReadResult(byte[] buffer, int count, bool exceeded)
    {
        Buffer = buffer;
        Count = count;
        Exceeded = exceeded;
    }

    /// <summary>Backing buffer; only the first <see cref="Count"/> bytes are valid. Empty, never null.</summary>
    public byte[] Buffer { get; }

    /// <summary>Number of valid bytes in <see cref="Buffer"/>.</summary>
    public int Count { get; }

    /// <summary>True when the stream held more than the limit; the content is then incomplete and must not be used.</summary>
    public bool Exceeded { get; }
}
