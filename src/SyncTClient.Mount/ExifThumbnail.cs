using System.Buffers.Binary;

namespace SyncTClient.Mount;

/// <summary>
/// Holt das eingebettete Vorschaubild aus dem Kopf einer JPEG-Datei.
/// </summary>
/// <remarks>
/// Kameras legen im EXIF-Block eine kleine JPEG-Kopie ab, typischerweise
/// 160x120 bis 320x240. Sie steht weit vorn in der Datei, deshalb genuegen
/// wenige Kilobyte. Bei den Testbildern reichten 64 KB fuer eine Vorschau von
/// 256x171, waehrend die ganze Datei 32,8 MB hat.
///
/// Der Block wird von Hand geparst statt ueber eine Bilddekodierung. Eine
/// Bilddekodierung arbeitet auf einem abgeschnittenen Datenstrom nicht
/// zuverlaessig, und die eingebetteten Bytes werden ohnehin unveraendert
/// weitergereicht.
/// </remarks>
public static class ExifThumbnail
{
    /// <summary>
    /// Wieviel vom Dateianfang geholt werden muss. Entspricht genau einem
    /// Syncthing-Block, kostet also eine einzige Anfrage.
    /// </summary>
    public const int RequiredPrefixBytes = 128 * 1024;

    private const ushort TagJpegOffset = 0x0201;
    private const ushort TagJpegLength = 0x0202;

    /// <summary>
    /// Liefert die Bytes des eingebetteten Vorschau-JPEGs, oder null, wenn
    /// keines vorhanden ist.
    /// </summary>
    public static byte[]? TryExtract(ReadOnlySpan<byte> jpegPrefix)
    {
        // Ein Byte-Parser trifft auf abgeschnittenen Daten frueher oder
        // spaeter auf eine Laengenangabe, die nicht mehr passt. Das ist kein
        // Fehler, sondern der Normalfall bei unvollstaendigen Dateien. In
        // diesem Fall gibt es keine Vorschau.
        try
        {
            var exif = FindExifSegment(jpegPrefix);
            if (exif.IsEmpty) return null;

            return TryReadFromTiff(exif, jpegPrefix);
        }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
    }

    /// <summary>Sucht den APP1-Abschnitt mit der Kennung "Exif\0\0".</summary>
    private static ReadOnlySpan<byte> FindExifSegment(ReadOnlySpan<byte> data)
    {
        // Start of Image
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return default;

        var position = 2;
        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF) return default;

            var marker = data[position + 1];
            // Fuellbytes zwischen Abschnitten sind erlaubt.
            if (marker == 0xFF) { position++; continue; }
            // Ab dem Bilddatenstrom gibt es keine Metadaten mehr.
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { position += 2; continue; }
            if (marker == 0xDA) return default;

            if (position + 4 > data.Length) return default;
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
            if (segmentLength < 2) return default;

            var payloadStart = position + 4;
            var payloadLength = segmentLength - 2;
            if (payloadStart + payloadLength > data.Length)
            {
                // Abgeschnitten: nur so weit lesen, wie Daten vorhanden sind.
                payloadLength = data.Length - payloadStart;
                if (payloadLength <= 0) return default;
            }

            if (marker == 0xE1 && payloadLength > 6)
            {
                var payload = data.Slice(payloadStart, payloadLength);
                if (payload[0] == (byte)'E' && payload[1] == (byte)'x' && payload[2] == (byte)'i' &&
                    payload[3] == (byte)'f' && payload[4] == 0 && payload[5] == 0)
                {
                    return payload[6..];
                }
            }

            position = payloadStart + segmentLength - 2;
        }

        return default;
    }

    /// <summary>
    /// Liest die zweite Bildverzeichnisstruktur (IFD1). Dort beschreibt die
    /// Kamera das Vorschaubild.
    /// </summary>
    private static byte[]? TryReadFromTiff(ReadOnlySpan<byte> tiff, ReadOnlySpan<byte> wholeFile)
    {
        if (tiff.Length < 8) return null;

        bool littleEndian;
        if (tiff[0] == 'I' && tiff[1] == 'I') littleEndian = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') littleEndian = false;
        else return null;

        if (ReadUInt16(tiff[2..], littleEndian) != 42) return null;

        var ifd0Offset = ReadUInt32(tiff[4..], littleEndian);
        if (ifd0Offset + 2 > (uint)tiff.Length) return null;

        // Am Ende von IFD0 steht der Verweis auf IFD1.
        var entryCount = ReadUInt16(tiff[(int)ifd0Offset..], littleEndian);
        var afterEntries = (int)ifd0Offset + 2 + entryCount * 12;
        if (afterEntries + 4 > tiff.Length) return null;

        var ifd1Offset = ReadUInt32(tiff[afterEntries..], littleEndian);
        if (ifd1Offset == 0 || ifd1Offset + 2 > (uint)tiff.Length) return null;

        var thumbCount = ReadUInt16(tiff[(int)ifd1Offset..], littleEndian);
        uint thumbOffset = 0, thumbLength = 0;

        for (var i = 0; i < thumbCount; i++)
        {
            var entry = (int)ifd1Offset + 2 + i * 12;
            if (entry + 12 > tiff.Length) break;

            var tag = ReadUInt16(tiff[entry..], littleEndian);
            if (tag == TagJpegOffset) thumbOffset = ReadUInt32(tiff[(entry + 8)..], littleEndian);
            else if (tag == TagJpegLength) thumbLength = ReadUInt32(tiff[(entry + 8)..], littleEndian);
        }

        if (thumbOffset == 0 || thumbLength == 0) return null;
        if (thumbOffset + thumbLength > (uint)tiff.Length) return null;
        // Unplausibel grossen Angaben wird nicht vertraut.
        if (thumbLength > 4 * 1024 * 1024) return null;

        var thumbnail = tiff.Slice((int)thumbOffset, (int)thumbLength);

        // Das Vorschaubild muss selbst ein JPEG sein.
        if (thumbnail.Length < 4 || thumbnail[0] != 0xFF || thumbnail[1] != 0xD8) return null;

        return thumbnail.ToArray();
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data)
            : BinaryPrimitives.ReadUInt16BigEndian(data);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data)
            : BinaryPrimitives.ReadUInt32BigEndian(data);

    /// <summary>Dateiendungen, bei denen sich der Versuch lohnt.</summary>
    public static bool LooksLikeJpeg(string path)
        => path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
}
