using System.Windows;
using Microsoft.Win32;

namespace SyncTClient.Gui;

public partial class App : Application
{
    /// <summary>
    /// Farbschema und Sprache liegen als austauschbare Wörterbücher vor.
    /// </summary>
    /// <remarks>
    /// Beides über denselben Weg: Fenster greifen mit <c>DynamicResource</c>
    /// darauf zu, und ein Austausch schlägt sofort durch -- ohne Neustart und
    /// ohne dass ein Fenster davon wissen muss.
    /// </remarks>
    private static ResourceDictionary? _theme;
    private static ResourceDictionary? _strings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Vor dem ersten Fenster: sonst blitzt es hell auf, bevor die
        // Einstellung gelesen ist.
        ApplyTheme(null);
        ApplyLanguage(null);
    }

    /// <summary>Setzt das Farbschema. Leer heisst: wie das System es haelt.</summary>
    public static void ApplyTheme(string? theme)
    {
        var dark = theme switch
        {
            "Dunkel" => true,
            "Hell" => false,
            _ => SystemPrefersDark()
        };

        Swap(ref _theme, dark ? "Themen/Dunkel.xaml" : "Themen/Hell.xaml");

        // Die eigenen Farben allein reichen nicht: DataGrid-Kopfzeilen,
        // Bildlaufleisten und Aufklappmenues bringen ihre eigenen mit. Nur
        // ueber ThemeMode werden auch die dunkel.
#pragma warning disable WPF0001
        Current.ThemeMode = theme switch
        {
            "Dunkel" => ThemeMode.Dark,
            "Hell" => ThemeMode.Light,
            _ => ThemeMode.System
        };
#pragma warning restore WPF0001
    }

    /// <summary>Setzt die Sprache. Leer heisst: wie das System eingestellt ist.</summary>
    public static void ApplyLanguage(string? language)
    {
        var english = language switch
        {
            "en" => true,
            "de" => false,
            _ => !System.Globalization.CultureInfo.CurrentUICulture
                .TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase)
        };

        Swap(ref _strings, english ? "Sprachen/en.xaml" : "Sprachen/de.xaml");
    }

    /// <summary>
    /// Ein Text aus dem Wörterbuch, für alles, was nicht im XAML steht.
    /// </summary>
    /// <remarks>
    /// Fehlt der Schlüssel, steht er selbst da. Das ist hässlich und genau
    /// deshalb richtig: eine vergessene Übersetzung soll auffallen, nicht
    /// als leere Fläche verschwinden.
    /// </remarks>
    public static string S(string key)
        => Current?.TryFindResource(key) as string ?? key;

    public static string S(string key, params object?[] args)
    {
        try { return string.Format(S(key), args); }
        catch (FormatException) { return S(key); }
    }

    private static void Swap(ref ResourceDictionary? current, string path)
    {
        var next = new ResourceDictionary { Source = new Uri(path, UriKind.Relative) };

        // Erst das neue dazu, dann das alte weg: dazwischen soll kein
        // Schlüssel unauffindbar sein.
        Current.Resources.MergedDictionaries.Add(next);
        if (current is not null) Current.Resources.MergedDictionaries.Remove(current);

        current = next;
    }

    /// <summary>Was Windows für Anwendungen eingestellt hat.</summary>
    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            // Ohne Auskunft bleibt es hell -- das ist die Vorgabe von Windows.
            return false;
        }
    }
}
