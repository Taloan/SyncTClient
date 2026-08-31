using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SyncTClient.Mount;

/// <summary>
/// Beschreibt eine Vorschau-Bitmap so, dass sich ein Standardsymbol erkennen
/// laesst.
/// </summary>
/// <remarks>
/// Ein hochskaliertes Symbol hat dieselben Kopfdaten wie eine echte Vorschau:
/// 256x256, 32 bpp. Unterscheiden lassen sich beide nur an den Bildpunkten.
/// Verschiedene Fotos ergeben verschiedene Abdruecke, ein Symbol ergibt fuer
/// jede Datei denselben. Ohne diesen Vergleich wird ein Symbol leicht
/// faelschlich als Erfolg gewertet.
/// </remarks>
internal static class BitmapFingerprint
{
    public static string Describe(nint bitmap)
    {
        if (bitmap == 0) return "nichts";

        var info = new BitmapInfo();
        if (GetObject(bitmap, Marshal.SizeOf<BitmapInfo>(), ref info) == 0) return "unlesbar";

        return $"{info.Width}x{info.Height}, {info.BitsPerPixel} bpp, Pixel {Pixels(info)}";
    }

    private static string Pixels(BitmapInfo info)
    {
        if (info.Bits == 0 || info.WidthBytes <= 0 || info.Height == 0) return "?";

        var bytes = new byte[info.WidthBytes * Math.Abs(info.Height)];
        Marshal.Copy(info.Bits, bytes, 0, bytes.Length);
        return Convert.ToHexString(SHA256.HashData(bytes))[..12];
    }

    public static void Release(nint bitmap)
    {
        if (bitmap != 0) DeleteObject(bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPerPixel;
        public nint Bits;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(nint handle, int size, ref BitmapInfo info);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);
}
