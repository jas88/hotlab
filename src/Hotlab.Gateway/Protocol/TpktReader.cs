using System.Buffers.Binary;

namespace Hotlab.Gateway.Protocol;

/// <summary>Reads whole TPKT-framed PDUs (RFC 1006) from a stream.</summary>
public static class TpktReader
{
    /// <summary>TPKT version number (RFC 1006 section 6).</summary>
    public const byte Version = 3;

    /// <summary>Size of the TPKT header: version, reserved, 16-bit big-endian length.</summary>
    public const int HeaderLength = 4;

    /// <summary>
    /// Smallest TPKT length accepted: the 4-byte header plus the smallest X.224 TPDU
    /// (3 bytes: LI, code, one more octet). Anything shorter cannot carry a TPDU.
    /// </summary>
    public const int MinimumLength = HeaderLength + 3;

    /// <summary>
    /// Reads exactly one TPKT PDU, header included. Throws <see cref="InvalidDataException"/>
    /// if the header is malformed or the declared length exceeds <paramref name="maxLength"/>,
    /// and <see cref="EndOfStreamException"/> if the stream ends mid-PDU.
    /// </summary>
    public static async Task<byte[]> ReadPduAsync(Stream stream, int maxLength, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, MinimumLength);

        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        if (header[0] != Version)
            throw new InvalidDataException($"Not a TPKT header: version byte 0x{header[0]:X2}, expected 0x03.");
        if (header[1] != 0)
            throw new InvalidDataException($"Not a TPKT header: reserved byte 0x{header[1]:X2}, expected 0x00.");

        int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        if (length < MinimumLength)
            throw new InvalidDataException($"TPKT length {length} is below the minimum of {MinimumLength}.");
        if (length > maxLength)
            throw new InvalidDataException($"TPKT length {length} exceeds the limit of {maxLength}.");

        var pdu = new byte[length];
        header.CopyTo(pdu, 0);
        await stream.ReadExactlyAsync(pdu.AsMemory(HeaderLength), ct).ConfigureAwait(false);
        return pdu;
    }
}
