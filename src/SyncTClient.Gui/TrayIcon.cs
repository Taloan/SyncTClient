using System.Drawing;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace SyncTClient.Gui;

/// <summary>
/// Der Eintrag, mit dem Windows dieses Programm beim Anmelden startet.
/// </summary>
/// <remarks>
/// Er steht in der Registry und nicht in der Konfiguration. Eine Kopie in der
/// eigenen Datei waere eine Behauptung, die niemand nachhaelt: wer den Eintrag
/// von Hand entfernt, bekaeme weiterhin einen gesetzten Haken zu sehen.
/// </remarks>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string EntryName = "SyncTClient";

    /// <summary>
    /// Was im Eintrag steht: der volle Pfad dieser Programmdatei.
    /// </summary>
    /// <remarks>
    /// In Anfuehrungszeichen, sonst liest Windows einen Pfad mit Leerzeichen
    /// als Befehl und Argumente.
    /// </remarks>
    private static string? Command
        => Environment.ProcessPath is { Length: > 0 } path ? $"\"{path}\"" : null;

    /// <summary>Steht der Eintrag?</summary>
    public static bool Enabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(EntryName) is string value && value.Length > 0;
            }
            catch (Exception)
            {
                // Keine Auskunft heisst: es ist nichts eingerichtet.
                return false;
            }
        }
    }

    /// <summary>Setzt den Eintrag oder loescht ihn.</summary>
    /// <remarks>
    /// Beim Setzen wird der Pfad neu geschrieben, auch wenn schon einer da
    /// steht -- eine verschobene Programmdatei bringt sich damit selbst in
    /// Ordnung.
    /// </remarks>
    public static void Set(bool wanted)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;

        if (!wanted)
        {
            key.DeleteValue(EntryName, throwOnMissingValue: false);
            return;
        }

        // Ohne bekannten eigenen Pfad gibt es nichts einzutragen; der Haken
        // faellt beim naechsten Nachlesen von selbst zurueck.
        if (Command is { } command) key.SetValue(EntryName, command);
    }
}

/// <summary>
/// Das Symbol im Infobereich -- der einzige Weg zurueck, solange das Fenster
/// versteckt ist.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Window _window;
    private readonly Action _exit;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _open = new();
    private readonly Forms.ToolStripMenuItem _quit = new();

    /// <param name="window">Das Fenster, das gezeigt und geholt wird.</param>
    /// <param name="exit">Was "Beenden" tun soll -- hier wird nichts beendet.</param>
    public TrayIcon(Window window, Action exit)
    {
        _window = window;
        _exit = exit;

        _open.Click += (_, _) => Restore();
        _quit.Click += (_, _) => _exit();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_open);
        menu.Items.Add(_quit);

        _icon = new Forms.NotifyIcon
        {
            Icon = Symbol(),
            ContextMenuStrip = menu,
            Visible = true
        };

        // Der Doppelklick ist das, was jeder zuerst versucht.
        _icon.DoubleClick += (_, _) => Restore();

        Translate();
    }

    /// <summary>Holt das Fenster zurueck, aus jedem Zustand.</summary>
    /// <remarks>
    /// Versteckt und minimiert sind zweierlei, und beides kann zugleich
    /// zutreffen -- deshalb beides nacheinander.
    /// </remarks>
    public void Restore()
    {
        _window.Show();

        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        _window.Activate();
    }

    /// <summary>
    /// Nimmt die Beschriftungen neu aus dem Woerterbuch.
    /// </summary>
    /// <remarks>
    /// Ein Menue aus Windows Forms folgt keinem <c>DynamicResource</c>; nach
    /// einem Sprachwechsel muss es jemand nachziehen.
    /// </remarks>
    public void Translate()
    {
        _open.Text = App.S("S.Tray.Open");
        _quit.Text = App.S("S.Tray.Quit");
        _icon.Text = App.S("S.Tray.Tip");
    }

    /// <summary>
    /// Ein Symbol, ohne eines mitzubringen: Windows kennt das der eigenen
    /// Programmdatei. Gibt es keines, tut es das der Anwendungen allgemein.
    /// </summary>
    private static Icon Symbol()
    {
        try
        {
            if (Environment.ProcessPath is { Length: > 0 } path)
                return Icon.ExtractAssociatedIcon(path) ?? SystemIcons.Application;
        }
        catch (Exception)
        {
            // Ein Ersatzsymbol ist besser als keines: ohne Symbol waere ein
            // verstecktes Fenster nicht mehr zu holen.
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        // Ohne das bleibt ein totes Symbol stehen, bis jemand mit der Maus
        // darueberfaehrt.
        _icon.Visible = false;
        _icon.Dispose();
    }
}
