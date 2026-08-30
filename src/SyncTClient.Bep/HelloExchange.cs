using System.Buffers.Binary;
using Google.Protobuf;
using SyncTClient.Bep.Proto;

namespace SyncTClient.Bep;

/// <summary>
/// Der Hello-Austausch laeuft noch <em>vor</em> der BEP-Rahmung direkt auf dem
/// TLS-Stream und hat ein eigenes Format:
/// <code>
/// uint32 BE   Magic (0x2EA7D90B)
/// uint16 BE   Laenge
/// bytes       Hello (protobuf)
/// </code>
/// </summary>
public static class HelloExchange
{
    private const uint HelloMessageMagic = 0x2EA7D90B;
    private const uint Version13HelloMagic = 0x9F79BC40; // veraltet, nur zur Diagnose

    public static async Task<Hello> ExchangeAsync(Stream stream, Hello outgoing, CancellationToken ct)
    {
        await WriteAsync(stream, outgoing, ct).ConfigureAwait(false);
        return await ReadAsync(stream, ct).ConfigureAwait(false);
    }

    private static async Task WriteAsync(Stream stream, Hello hello, CancellationToken ct)
    {
        var payload = hello.ToByteArray();
        if (payload.Length > 32767)
            throw new InvalidOperationException("Hello-Nachricht ist zu gross.");

        var frame = new byte[4 + 2 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0), HelloMessageMagic);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(6));

        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<Hello> ReadAsync(Stream stream, CancellationToken ct)
    {
        var scratch = new byte[4];
        await stream.ReadExactlyAsync(scratch, ct).ConfigureAwait(false);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(scratch);

        if (magic == Version13HelloMagic)
            throw new InvalidDataException("Die Gegenstelle spricht eine zu alte Protokollversion (v0.13).");
        if (magic != HelloMessageMagic)
            throw new InvalidDataException(
                $"Unbekanntes Hello-Magic 0x{magic:X8} -- vermutlich kein Syncthing auf der Gegenseite.");

        await stream.ReadExactlyAsync(scratch.AsMemory(0, 2), ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16BigEndian(scratch);
        if (length > 32767)
            throw new InvalidDataException("Hello-Nachricht der Gegenstelle ist zu gross.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return Hello.Parser.ParseFrom(payload);
    }
}
