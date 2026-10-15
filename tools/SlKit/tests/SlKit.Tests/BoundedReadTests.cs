using System;
using System.Threading;
using SlKit.Core;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>Tests of BoundedRead: the response size cap and the decoding of the body.</summary>
internal static class BoundedReadTests
{
    internal static void Register()
    {
        // Bounded body read and decoding (SlHttp size cap)
        Run(nameof(BoundedRead_ReadsBodiesUpToTheLimit), BoundedRead_ReadsBodiesUpToTheLimit);
        Run(nameof(BoundedRead_StopsOneBytePastTheLimit), BoundedRead_StopsOneBytePastTheLimit);
        Run(nameof(BoundedRead_ZeroLimitAndArguments), BoundedRead_ZeroLimitAndArguments);
        Run(nameof(BoundedRead_DecodesBomCharsetAndFallsBack), BoundedRead_DecodesBomCharsetAndFallsBack);
    }

    // ---------------------------------------------------------------------------------------
    // Bounded read. Regression for "SlHttp reads the whole body, however large".
    // ---------------------------------------------------------------------------------------

    /// <summary>A stream of a given length that counts how many bytes were actually read from it.</summary>
    private sealed class CountingStream : System.IO.Stream
    {
        private readonly long _length;
        private readonly int _maxChunk;
        private long _position;

        public CountingStream(long length, int maxChunk = int.MaxValue)
        {
            _length = length;
            _maxChunk = maxChunk;
        }

        public long BytesRead => _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(Math.Min(count, _maxChunk), _length - _position);
            for (int i = 0; i < n; i++)
            {
                buffer[offset + i] = (byte)'a';
            }

            _position += n;
            return n;
        }
    }

    private static void BoundedRead_ReadsBodiesUpToTheLimit()
    {
        BoundedReadResult small = BoundedRead.ReadAsync(new CountingStream(10), 1024, CancellationToken.None).GetAwaiter().GetResult();
        False(small.Exceeded);
        Equal(10, small.Count);

        // Exactly at the limit is fine, also when the stream hands out small chunks.
        BoundedReadResult exact = BoundedRead.ReadAsync(new CountingStream(100_000, 777), 100_000, CancellationToken.None).GetAwaiter().GetResult();
        False(exact.Exceeded, "a body of exactly the limit is accepted");
        Equal(100_000, exact.Count);
        Equal("aaa", BoundedRead.Decode(exact.Buffer, 3, null));

        BoundedReadResult empty = BoundedRead.ReadAsync(new CountingStream(0), 1024, CancellationToken.None).GetAwaiter().GetResult();
        False(empty.Exceeded);
        Equal(0, empty.Count);
        Equal(string.Empty, BoundedRead.Decode(empty.Buffer, empty.Count, "utf-8"));
    }

    private static void BoundedRead_StopsOneBytePastTheLimit()
    {
        const int Limit = 1024 * 1024;
        CountingStream huge = new CountingStream(500L * 1024 * 1024);
        BoundedReadResult r = BoundedRead.ReadAsync(huge, Limit, CancellationToken.None).GetAwaiter().GetResult();
        True(r.Exceeded, "500 MiB body against a 1 MiB limit");
        Equal(0, r.Count, "no partial body is handed out");
        Equal(Limit + 1L, huge.BytesRead, "reading stops one byte past the limit, the rest is never pulled");

        CountingStream onePast = new CountingStream(Limit + 1L, 4096);
        True(BoundedRead.ReadAsync(onePast, Limit, CancellationToken.None).GetAwaiter().GetResult().Exceeded, "limit + 1 is too large");
    }

    private static void BoundedRead_ZeroLimitAndArguments()
    {
        True(BoundedRead.ReadAsync(new CountingStream(1), 0, CancellationToken.None).GetAwaiter().GetResult().Exceeded, "limit 0 refuses one byte");
        False(BoundedRead.ReadAsync(new CountingStream(0), 0, CancellationToken.None).GetAwaiter().GetResult().Exceeded, "limit 0 accepts nothing");
        Throws<ArgumentOutOfRangeException>(() => BoundedRead.ReadAsync(new CountingStream(1), -1, CancellationToken.None).GetAwaiter().GetResult(), "negative limit");
        Throws<ArgumentNullException>(() => BoundedRead.ReadAsync(null, 1, CancellationToken.None).GetAwaiter().GetResult(), "null stream");
        Throws<ArgumentOutOfRangeException>(() => BoundedRead.Decode(new byte[2], 3, null), "count past buffer");
    }

    private static void BoundedRead_DecodesBomCharsetAndFallsBack()
    {
        byte[] utf8Bom = { 0xEF, 0xBB, 0xBF, 0xC5, 0xBA };          // BOM + "ź"
        Equal("ź", BoundedRead.Decode(utf8Bom, utf8Bom.Length, "iso-8859-1"), "UTF-8 BOM wins over the declared charset");

        byte[] utf16 = { 0xFF, 0xFE, 0x41, 0x00 };                  // LE BOM + "A"
        Equal("A", BoundedRead.Decode(utf16, utf16.Length, null), "UTF-16 LE BOM");

        byte[] latin1 = { 0xE9 };                                   // "é" in ISO-8859-1
        Equal("é", BoundedRead.Decode(latin1, 1, "\"ISO-8859-1\""), "quoted charset is honoured");

        byte[] utf8 = { 0xC5, 0xBA };
        Equal("ź", BoundedRead.Decode(utf8, 2, null), "no charset means UTF-8");
        Equal("ź", BoundedRead.Decode(utf8, 2, "no-such-charset"), "an unknown charset falls back to UTF-8, no throw");
        Equal(string.Empty, BoundedRead.Decode(null, 5, null), "null buffer");

        // A code page the runtime refuses may surface as NotSupportedException instead of ArgumentException.
        Equal("ź", BoundedRead.Decode(utf8, 2, "utf-7"), "a refused or obsolete charset falls back to UTF-8, no throw");
        Equal("ź", BoundedRead.Decode(utf8, 2, "   "), "blank charset");
    }
}
