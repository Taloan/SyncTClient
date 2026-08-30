using System.Buffers.Binary;
using Google.Protobuf;
using K4os.Compression.LZ4;
using SyncTClient.Bep.Proto;

namespace SyncTClient.Bep;

/// <summary>
/// Die Rahmung des Block Exchange Protocol auf dem Draht:
/// <code>
/// uint16 BE   Laenge des Headers
/// bytes       Header (protobuf: Typ + Kompression)
/// uint32 BE   Laenge der Nachricht
/// bytes       Nachricht (protobuf, optional LZ4-Block-komprimiert)
/// </code>
/// Nachgebaut nach <c>lib/protocol/protocol.go</c>, writeMessage/readMessage.
/// </summary>
public static class BepFraming
{
    /// <summary>500 MB, wie MaxMessageLen in Syncthing.</summary>
    public const int MaxMessageLength = 500 * 1000 * 1000;

    /// <summary>
    /// Schreibt eine Nachricht unkomprimiert. Das ist immer zulaessig -- die
    /// Gegenseite entscheidet unabhaengig davon, ob sie selbst komprimiert.
    /// </summary>
    public static async Task WriteMessageAsync(
        Stream stream, MessageType type, IMessage message, CancellationToken ct)
    {
        var header = new Header { Type = type, Compression = MessageCompression.None };
        var headerBytes = header.ToByteArray();
        var payload = message.ToByteArray();

        var frame = new byte[2 + headerBytes.Length + 4 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), (ushort)headerBytes.Length);
        headerBytes.CopyTo(frame.AsSpan(2));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(2 + headerBytes.Length), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(2 + headerBytes.Length + 4));

        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Liest die naechste Nachricht und packt sie noetigenfalls aus.</summary>
    public static async Task<(MessageType Type, byte[] Payload)> ReadMessageAsync(
        Stream stream, CancellationToken ct)
    {
        var scratch = new byte[4];

        await stream.ReadExactlyAsync(scratch.AsMemory(0, 2), ct).ConfigureAwait(false);
        int headerLength = BinaryPrimitives.ReadUInt16BigEndian(scratch);

        var headerBytes = new byte[headerLength];
        await stream.ReadExactlyAsync(headerBytes, ct).ConfigureAwait(false);
        var header = Header.Parser.ParseFrom(headerBytes);

        await stream.ReadExactlyAsync(scratch.AsMemory(0, 4), ct).ConfigureAwait(false);
        var messageLength = (int)BinaryPrimitives.ReadUInt32BigEndian(scratch);
        if (messageLength < 0 || messageLength > MaxMessageLength)
            throw new InvalidDataException($"Nachrichtenlaenge {messageLength} ausserhalb des Erlaubten.");

        var payload = new byte[messageLength];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

        payload = header.Compression switch
        {
            MessageCompression.None => payload,
            MessageCompression.Lz4 => DecompressLz4(payload),
            _ => throw new InvalidDataException($"Unbekannte Kompression {header.Compression}.")
        };

        return (header.Type, payload);
    }

    /// <summary>
    /// Syncthing nutzt das LZ4-<em>Block</em>format mit vorangestellter
    /// Originalgroesse als uint32 BE -- nicht das Frame-Format.
    /// </summary>
    private static byte[] DecompressLz4(byte[] compressed)
    {
        if (compressed.Length < 4)
            throw new InvalidDataException($"Komprimierte Nachricht ist mit {compressed.Length} Bytes zu kurz.");

        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(compressed);
        if (size < 0 || size > MaxMessageLength)
            throw new InvalidDataException($"Entpackte Groesse {size} ausserhalb des Erlaubten.");

        var target = new byte[size];
        var written = LZ4Codec.Decode(compressed.AsSpan(4), target.AsSpan());
        if (written != size)
            throw new InvalidDataException($"LZ4 lieferte {written} statt {size} Bytes.");

        return target;
    }
}
