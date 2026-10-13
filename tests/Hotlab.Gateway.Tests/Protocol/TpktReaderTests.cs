using Hotlab.Gateway.Protocol;
using static Hotlab.Gateway.Tests.Protocol.X224TestPdus;

namespace Hotlab.Gateway.Tests.Protocol;

public class TpktReaderTests
{
    private const int Max = 1024;

    [Fact]
    public async Task ReadsExactlyOnePdu_LeavingTheRestUnread()
    {
        byte[] trailing = [0xAA, 0xBB, 0xCC];
        using var ms = new MemoryStream([.. Mstsc, .. trailing]);

        var pdu = await TpktReader.ReadPduAsync(ms, Max, CancellationToken.None);

        Assert.Equal(Mstsc, pdu);
        Assert.Equal(Mstsc.Length, ms.Position);
    }

    [Fact]
    public async Task HandlesOneBytePerRead()
    {
        using var stream = new OneByteStream([.. Mstsc, 0x01, 0x02]);

        var pdu = await TpktReader.ReadPduAsync(stream, Max, CancellationToken.None);

        Assert.Equal(Mstsc, pdu);
        Assert.True(X224ConnectionRequest.TryParse(pdu, out var req));
        Assert.Equal("eltons", req!.Cookie);
    }

    [Fact]
    public async Task LengthEqualToMax_IsAccepted()
    {
        using var ms = new MemoryStream(Mstsc);
        var pdu = await TpktReader.ReadPduAsync(ms, Mstsc.Length, CancellationToken.None);
        Assert.Equal(Mstsc, pdu);
    }

    [Fact]
    public async Task Oversize_Throws()
    {
        using var ms = new MemoryStream(Mstsc);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => TpktReader.ReadPduAsync(ms, Mstsc.Length - 1, CancellationToken.None));
    }

    [Fact]
    public async Task MaxLengthTpkt_HeaderOnly_ThrowsBeforeReadingBody()
    {
        using var ms = new MemoryStream([0x03, 0x00, 0xFF, 0xFF]);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => TpktReader.ReadPduAsync(ms, Max, CancellationToken.None));
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0x00, 0x00, 0x0B })] // bad version
    [InlineData(new byte[] { 0x16, 0x03, 0x01, 0x00 })] // TLS ClientHello
    [InlineData(new byte[] { 0x47, 0x45, 0x54, 0x20 })] // "GET "
    [InlineData(new byte[] { 0x03, 0x01, 0x00, 0x0B })] // reserved byte set
    [InlineData(new byte[] { 0x03, 0x00, 0x00, 0x04 })] // header only
    [InlineData(new byte[] { 0x03, 0x00, 0x00, 0x00 })] // zero length
    [InlineData(new byte[] { 0x03, 0x00, 0x00, 0x06 })] // below minimum
    public async Task MalformedHeader_Throws(byte[] header)
    {
        using var ms = new MemoryStream([.. header, .. new byte[32]]);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => TpktReader.ReadPduAsync(ms, Max, CancellationToken.None));
    }

    [Fact]
    public async Task EmptyStream_ThrowsEndOfStream()
    {
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => TpktReader.ReadPduAsync(ms, Max, CancellationToken.None));
    }

    [Fact]
    public async Task EofInHeader_ThrowsEndOfStream()
    {
        using var ms = new MemoryStream([0x03, 0x00]);
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => TpktReader.ReadPduAsync(ms, Max, CancellationToken.None));
    }

    [Fact]
    public async Task EofInBody_ThrowsEndOfStream()
    {
        using var stream = new OneByteStream(Mstsc.AsSpan(0, Mstsc.Length - 1).ToArray());
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => TpktReader.ReadPduAsync(stream, Max, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_IsHonoured()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var ms = new MemoryStream(Mstsc);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TpktReader.ReadPduAsync(ms, Max, cts.Token));
    }

    [Fact]
    public async Task MaxLengthBelowMinimum_ThrowsArgument()
    {
        using var ms = new MemoryStream(Mstsc);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => TpktReader.ReadPduAsync(ms, 3, CancellationToken.None));
    }

    /// <summary>A read-only stream that yields at most one byte per read call.</summary>
    private sealed class OneByteStream(byte[] data) : Stream
    {
        private int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _pos >= data.Length)
                return 0;
            buffer[0] = data[_pos++];
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
