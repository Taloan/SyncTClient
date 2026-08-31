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
/// eigenen Datei koennte veralten: wer den Eintrag von Hand entfernt, bekaeme
/// weiterhin einen gesetzten Haken zu sehen.
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

    /// <summary>Gibt an, ob der Eintrag vorhanden ist.</summary>
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
                // Laesst sich der Wert nicht lesen, gilt der Eintrag als nicht gesetzt.
                return false;
            }
        }
    }

    /// <summary>Setzt den Eintrag oder loescht ihn.</summary>
    /// <remarks>
    /// Beim Setzen wird der Pfad neu geschrieben, auch wenn schon einer
    /// eingetragen ist. Nach dem Verschieben der Programmdatei stimmt der
    /// Eintrag damit wieder.
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

        // Ohne bekannten eigenen Pfad gibt es nichts einzutragen. Der Haken
        // steht beim naechsten Nachlesen wieder auf nicht gesetzt.
        if (Command is { } command) key.SetValue(EntryName, command);
    }
}

/// <summary>
/// Das Symbol im Infobereich. Solange das Fenster versteckt ist, fuehrt nur
/// ueber dieses Symbol ein Weg zurueck.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Window _window;
    private readonly Action _exit;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _open = new();
    private readonly Forms.ToolStripMenuItem _quit = new();

    /// <param name="window">Das Fenster, das gezeigt und geholt wird.</param>
    /// <param name="exit">Was "Beenden" ausloest. Diese Klasse beendet selbst nichts.</param>
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

        // Der Doppelklick ist die Geste, die Nutzer zuerst versuchen.
        _icon.DoubleClick += (_, _) => Restore();

        Translate();
    }

    /// <summary>Holt das Fenster zurueck, aus jedem Zustand.</summary>
    /// <remarks>
    /// Versteckt und minimiert sind zwei verschiedene Zustaende, und beide
    /// koennen zugleich vorliegen. Deshalb wird beides nacheinander aufgehoben.
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
    /// Ein Menue aus Windows Forms folgt keinem <c>DynamicResource</c>. Nach
    /// einem Sprachwechsel muessen die Beschriftungen von Hand gesetzt werden.
    /// </remarks>
    public void Translate()
    {
        _open.Text = App.S("S.Tray.Open");
        _quit.Text = App.S("S.Tray.Quit");
        _icon.Text = App.S("S.Tray.Tip");
    }

    /// <summary>
    /// Ein Symbol ohne eigene Symboldatei: Windows liefert das Symbol der
    /// eigenen Programmdatei. Fehlt es, wird das allgemeine
    /// Anwendungssymbol verwendet.
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
            // Ein Ersatzsymbol ist noetig. Ohne Symbol liesse sich ein
            // verstecktes Fenster nicht mehr zurueckholen.
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        // Ohne diesen Aufruf bleibt das Symbol im Infobereich stehen, bis der
        // Mauszeiger darueberfaehrt.
        _icon.Visible = false;
        _icon.Dispose();
    }
}
