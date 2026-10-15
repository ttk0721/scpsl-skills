using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SlKit.Core;

/// <summary>
/// Reads a stream to the end, but never more than a byte limit, and decodes text the way an HTTP
/// client would (byte order mark first, then the declared charset, then UTF-8).
/// </summary>
/// <remarks>
/// Why: <c>HttpContent.ReadAsStringAsync</c> buffers the whole body, so a misbehaving or hostile
/// endpoint can make a plugin allocate hundreds of megabytes, and on the server's non-generational
/// Boehm GC every later collection then marks that heap as well. Reading stops one byte after the
/// limit, so an oversized body costs at most <c>limit + 1</c> bytes plus one copy buffer.
/// Thread affinity: static and stateless, any thread.
/// </remarks>
public static class BoundedRead
{
    /// <summary>Largest limit accepted: a byte array cannot grow beyond this on .NET Framework/Mono.</summary>
    public const long MaxLimit = int.MaxValue - 64;

    private const int ChunkSize = 16 * 1024;

    /// <summary>
    /// Reads <paramref name="stream"/> to the end, or stops as soon as more than
    /// <paramref name="maxBytes"/> bytes have arrived (then <see cref="BoundedReadResult.Exceeded"/> is true).
    /// </summary>
    /// <param name="stream">Source stream; not disposed.</param>
    /// <param name="maxBytes">Limit in bytes; 0 allows only an empty stream. Clamped to <see cref="MaxLimit"/>.</param>
    /// <param name="cancellationToken">Passed to every read.</param>
    /// <returns>The bytes read and whether the limit was exceeded.</returns>
    public static async Task<BoundedReadResult> ReadAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        if (stream == null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Limit must not be negative.");
        }

        int limit = (int)Math.Min(maxBytes, MaxLimit);
        byte[] chunk = new byte[ChunkSize];
        using (MemoryStream collected = new MemoryStream())
        {
            while (true)
            {
                // One byte past the limit is enough to know it was exceeded; never read further.
                long room = (long)limit + 1 - collected.Length;
                int want = (int)Math.Min(chunk.Length, room);
                int read = await stream.ReadAsync(chunk, 0, want, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                collected.Write(chunk, 0, read);
                if (collected.Length > limit)
                {
                    return new BoundedReadResult(Array.Empty<byte>(), 0, true);
                }
            }

            return new BoundedReadResult(collected.ToArray(), (int)collected.Length, false);
        }
    }

    /// <summary>
    /// Decodes bytes as text: a UTF-8/UTF-16 byte order mark wins, then <paramref name="charset"/>
    /// (as in a <c>Content-Type</c> header; quotes allowed), then UTF-8. An unknown charset falls back
    /// to UTF-8 instead of throwing.
    /// </summary>
    /// <param name="buffer">Bytes to decode.</param>
    /// <param name="count">Number of valid bytes at the start of <paramref name="buffer"/>.</param>
    /// <param name="charset">Declared charset, or null.</param>
    /// <returns>The decoded text; empty for no bytes.</returns>
    public static string Decode(byte[] buffer, int count, string charset)
    {
        if (buffer == null || count <= 0)
        {
            return string.Empty;
        }

        if (count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(buffer, 3, count - 3);
        }

        if (count >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(buffer, 2, count - 2);
        }

        if (count >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(buffer, 2, count - 2);
        }

        return ResolveCharset(charset).GetString(buffer, 0, count);
    }

    private static Encoding ResolveCharset(string charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        string name = charset.Trim().Trim('"', '\'');
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (Exception e) when (e is ArgumentException || e is NotSupportedException)
        {
            // A runtime without the code page (Mono without its I18N assemblies, .NET without
            // legacy code pages) says either; both mean "use UTF-8", never a thrown result.
            return Encoding.UTF8;
        }
    }
}
