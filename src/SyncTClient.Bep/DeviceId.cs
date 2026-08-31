using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SyncTClient.Bep;

/// <summary>
/// Eine Syncthing-Geraete-ID: der SHA-256-Hash des DER-kodierten Zertifikats.
/// Die Textform ist Base32 mit eingestreuten Pruefziffern, gruppiert zu je
/// sieben Zeichen. Sie ist kompatibel zu <c>lib/protocol/deviceid.go</c>.
/// </summary>
public readonly struct DeviceId : IEquatable<DeviceId>
{
    public const int Length = 32;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private readonly byte[]? _bytes;

    private DeviceId(byte[] bytes) => _bytes = bytes;

    public static readonly DeviceId Empty = new(new byte[Length]);

    public ReadOnlySpan<byte> Span => _bytes ?? Empty._bytes!;

    public byte[] ToByteArray() => Span.ToArray();

    /// <summary>Leitet die ID aus dem DER-kodierten Zertifikat ab.</summary>
    public static DeviceId FromCertificate(ReadOnlySpan<byte> rawCertificate)
        => new(SHA256.HashData(rawCertificate));

    public static DeviceId FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
            throw new ArgumentException($"Geraete-ID muss {Length} Bytes lang sein, war {bytes.Length}.", nameof(bytes));
        return new DeviceId(bytes.ToArray());
    }

    /// <summary>Die ersten 64 Bit, wie Syncthing sie in Logzeilen zeigt.</summary>
    public string Short()
    {
        var span = Span;
        return Base32Encode(span[..8])[..7];
    }

    /// <summary>Dieselben ersten 64 Bit als Zahl.</summary>
    /// <remarks>
    /// In <c>modified_by</c> und in den Zaehlern des Versionsvektors steht die
    /// Geraete-ID nicht als Text, sondern als diese Zahl. Gelesen wird sie
    /// big-endian, wie in <c>lib/protocol/deviceid.go</c>.
    /// </remarks>
    public ulong ShortId() => BinaryPrimitives.ReadUInt64BigEndian(Span[..8]);

    public override string ToString()
    {
        var span = Span;
        if (span.SequenceEqual(Empty.Span)) return string.Empty;
        return Chunkify(Luhnify(Base32Encode(span)));
    }

    public static DeviceId Parse(string text)
    {
        if (!TryParse(text, out var id, out var error))
            throw new FormatException(error);
        return id;
    }

    public static bool TryParse(string text, out DeviceId id, out string? error)
    {
        id = Empty;
        error = null;

        var s = (text ?? string.Empty).Trim().Trim('=').ToUpperInvariant();
        // Haeufige Vertipper, die Syncthing selbst ebenfalls toleriert.
        s = s.Replace('0', 'O').Replace('1', 'I').Replace('8', 'B');
        s = s.Replace("-", string.Empty).Replace(" ", string.Empty);

        switch (s.Length)
        {
            case 0:
                return true;

            case 56:
                if (!TryUnluhnify(s, out s!, out error)) return false;
                goto case 52;

            case 52:
                try
                {
                    var raw = Base32Decode(s);
                    if (raw.Length != Length)
                    {
                        error = $"Dekodiert zu {raw.Length} statt {Length} Bytes.";
                        return false;
                    }
                    id = new DeviceId(raw);
                    return true;
                }
                catch (FormatException ex)
                {
                    error = ex.Message;
                    return false;
                }

            default:
                error = $"\"{text}\": ungueltige Laenge {s.Length} (erwartet 52 oder 56 Zeichen ohne Trenner).";
                return false;
        }
    }

    // ------------------------------------------------------------ Base32

    private static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] Base32Decode(string s)
    {
        var result = new List<byte>(s.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            var v = Base32Alphabet.IndexOf(c);
            if (v < 0)
                throw new FormatException($"Zeichen '{c}' gehoert nicht zum Base32-Alphabet.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                result.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return result.ToArray();
    }

    // ------------------------------------------------------------ Pruefziffern
    //
    // Kein echter Luhn. Syncthing verwendet eine eigene Variante ueber dem
    // Base32-Alphabet. Siehe lib/protocol/luhn.go.

    private static char Luhn32(ReadOnlySpan<char> s)
    {
        const int n = 32;
        var factor = 1;
        var sum = 0;
        foreach (var c in s)
        {
            var codepoint = Base32Alphabet.IndexOf(c);
            if (codepoint < 0)
                throw new FormatException($"Zeichen '{c}' gehoert nicht zum Base32-Alphabet.");
            var addend = factor * codepoint;
            factor = factor == 2 ? 1 : 2;
            addend = (addend / n) + (addend % n);
            sum += addend;
        }
        return Base32Alphabet[(n - sum % n) % n];
    }

    /// <summary>
    /// Macht aus 52 Zeichen 56: je 13 Zeichen gefolgt von einer Pruefziffer.
    /// </summary>
    private static string Luhnify(string s)
    {
        if (s.Length != 52)
            throw new ArgumentException($"Erwartet 52 Zeichen, bekam {s.Length}.", nameof(s));

        var res = new StringBuilder(56);
        for (var i = 0; i < 4; i++)
        {
            var part = s.AsSpan(i * 13, 13);
            res.Append(part);
            res.Append(Luhn32(part));
        }
        return res.ToString();
    }

    private static bool TryUnluhnify(string s, out string? result, out string? error)
    {
        result = null;
        error = null;
        if (s.Length != 56)
        {
            error = $"Erwartet 56 Zeichen, bekam {s.Length}.";
            return false;
        }

        var res = new StringBuilder(52);
        for (var i = 0; i < 4; i++)
        {
            var part = s.AsSpan(i * 14, 13);
            char expected;
            try
            {
                expected = Luhn32(part);
            }
            catch (FormatException ex)
            {
                error = ex.Message;
                return false;
            }
            if (s[(i + 1) * 14 - 1] != expected)
            {
                error = $"\"{s}\": Pruefziffer in Gruppe {i + 1} stimmt nicht (erwartet '{expected}').";
                return false;
            }
            res.Append(part);
        }
        result = res.ToString();
        return true;
    }

    /// <summary>
    /// Teilt 56 Zeichen in acht Siebenergruppen, durch Bindestriche getrennt.
    /// </summary>
    private static string Chunkify(string s)
    {
        var chunks = s.Length / 7;
        var sb = new StringBuilder(chunks * 8 - 1);
        for (var i = 0; i < chunks; i++)
        {
            if (i > 0) sb.Append('-');
            sb.Append(s.AsSpan(i * 7, 7));
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ Gleichheit

    public bool Equals(DeviceId other) => Span.SequenceEqual(other.Span);

    public override bool Equals(object? obj) => obj is DeviceId other && Equals(other);

    public override int GetHashCode() => (int)BinaryPrimitives.ReadUInt32BigEndian(Span);

    public static bool operator ==(DeviceId a, DeviceId b) => a.Equals(b);

    public static bool operator !=(DeviceId a, DeviceId b) => !a.Equals(b);
}
