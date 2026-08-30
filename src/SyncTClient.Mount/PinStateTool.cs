using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SyncTClient.Mount;

/// <summary>
/// Setzt den Anheft-Zustand von Platzhaltern -- zum Vergleichen, nicht fuer
/// den Betrieb.
/// </summary>
/// <remarks>
/// Der Zustand steht in den Dateiattributen und ist einer der wenigen
/// Unterschiede, die die Shell zwischen zwei sonst gleichen Platzhaltern
/// sieht. "Nicht angeheftet" sagt ihr: diese Datei soll gar nicht erst lokal
/// liegen. Ob sie daraus auch schliesst, sich die Vorschau zu sparen, laesst
/// sich nur messen -- also machen wir es umschaltbar.
/// </remarks>
public static class PinStateTool
{
    public static int Run(string path, string state, bool recurse)
    {
        var pinState = state.ToLowerInvariant() switch
        {
            "unspecified" or "offen" => 0u,
            "pinned" or "angeheftet" => 1u,
            "unpinned" or "frei" => 2u,
            "excluded" => 3u,
            "inherit" => 4u,
            _ => uint.MaxValue
        };

        if (pinState == uint.MaxValue)
        {
            Console.Error.WriteLine("Zustand: unspecified | pinned | unpinned | excluded | inherit");
            return 1;
        }

        var full = Path.GetFullPath(path);
        var targets = Directory.Exists(full)
            ? (recurse
                ? Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                : Directory.EnumerateFiles(full))
            : [full];

        int done = 0, failed = 0;
        foreach (var file in targets)
        {
            using var handle = Open(file);
            if (handle is null) { failed++; continue; }

            var hr = CfSetPinState(handle, pinState, 0, 0);
            if (hr == 0) done++;
            else { failed++; Console.WriteLine($"  {Path.GetFileName(file)}: 0x{(uint)hr:X8}"); }
        }

        Console.WriteLine($"{done} gesetzt, {failed} fehlgeschlagen.");
        return failed == 0 ? 0 : 1;
    }

    private static SafeFileHandle? Open(string file)
    {
        try { return File.OpenHandle(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    [DllImport("cldapi.dll", PreserveSig = true)]
    private static extern int CfSetPinState(SafeFileHandle file, uint state, uint flags, nint overlapped);
}
