using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace SyncTClient.Gui;

public partial class App : Application
{
    /// <summary>
    /// Farbschema und Sprache liegen als austauschbare Wörterbücher vor.
    /// </summary>
    /// <remarks>
    /// Beides läuft über denselben Weg: Fenster greifen mit
    /// <c>DynamicResource</c> darauf zu. Ein Austausch wirkt sofort, ohne
    /// Neustart und ohne dass ein Fenster davon wissen muss.
    /// </remarks>
    private static ResourceDictionary? _theme;
    private static ResourceDictionary? _strings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Vor dem ersten Fenster anwenden. Sonst erscheint es kurz hell,
        // bevor die Einstellung gelesen ist.
        ApplyTheme(null);
        ApplyLanguage(null);
    }

    /// <summary>Setzt das Farbschema. Leer bedeutet: der Einstellung des Systems folgen.</summary>
    public static void ApplyTheme(string? theme)
    {
        var dark = theme switch
        {
            "Dunkel" => true,
            "Hell" => false,
            _ => SystemPrefersDark()
        };

        Swap(ref _theme, dark ? "Themen/Dunkel.xaml" : "Themen/Hell.xaml");

        // Die eigenen Farben allein reichen nicht. DataGrid-Kopfzeilen,
        // Bildlaufleisten und Aufklappmenues haben eigene Farben. Nur ueber
        // ThemeMode werden auch diese dunkel.
#pragma warning disable WPF0001
        Current.ThemeMode = theme switch
        {
            "Dunkel" => ThemeMode.Dark,
            "Hell" => ThemeMode.Light,
            _ => ThemeMode.System
        };
#pragma warning restore WPF0001

        // Muss nach dem Thema laufen. Die Stile setzen auf den Stilen des
        // Themas auf, und die stehen erst nach dem Wechsel bereit.
        ApplyControlStyles();
    }

    /// <summary>
    /// Abstände für Knöpfe, Felder und Beschriftungen.
    /// </summary>
    /// <remarks>
    /// Im XAML standen sie als Stil ohne <c>BasedOn</c>. Ein solcher Stil
    /// ersetzt den Stil des Themas, statt ihn zu ergänzen. Die Knöpfe verloren
    /// dadurch Farbe und Vorlage und passten im dunklen Schema nicht mehr zum
    /// übrigen Programm. Hier laufen die Stile nach dem Themawechsel und
    /// nehmen den dann gültigen Stil als Grundlage.
    /// </remarks>
    private static void ApplyControlStyles()
    {
        Derive(typeof(Button),
            (Control.PaddingProperty, new Thickness(12, 5, 12, 5)),
            (FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0)));

        Derive(typeof(TextBox),
            (Control.PaddingProperty, new Thickness(4, 3, 4, 3)),
            (FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 6)));

        Derive(typeof(ComboBox),
            (Control.PaddingProperty, new Thickness(4, 3, 4, 3)),
            (FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 6)));

        Derive(typeof(Label),
            (Control.PaddingProperty, new Thickness(0, 4, 0, 2)),
            (Control.FontWeightProperty, FontWeights.SemiBold));

        DeriveTree();
        DeriveSelection();
    }

    /// <summary>
    /// Schrift- und Grundfarbe im Auswahlbaum.
    /// </summary>
    /// <remarks>
    /// Fluent faerbt den Baum nicht mit dem uebrigen Fenster. Die Eintraege
    /// blieben dunkel, und im dunklen Schema stand damit schwarze Schrift auf
    /// dunklem Grund. Beide Farben kommen aus demselben Woerterbuch wie alles
    /// andere und wechseln mit dem Schema.
    /// </remarks>
    private static void DeriveTree()
    {
        if (Current.TryFindResource("Text") is not Brush schrift) return;

        // Die Schrift der Eintraege erben die TextBlocks darin.
        Derive(typeof(TreeViewItem), (Control.ForegroundProperty, schrift));

        if (Current.TryFindResource("Feld") is Brush grund)
        {
            Derive(typeof(TreeView),
                (Control.ForegroundProperty, schrift),
                (Control.BackgroundProperty, grund));
        }
        else
        {
            Derive(typeof(TreeView), (Control.ForegroundProperty, schrift));
        }
    }

    /// <summary>
    /// Der Auswahlbalken in der Tabelle.
    /// </summary>
    /// <remarks>
    /// Fluent färbt ihn mit seiner Akzentfarbe, im dunklen Schema ist das ein
    /// helles Blau. Die Zellen haben eigene Textfarben: Grau für
    /// Nebensächliches, Blau für Verweise. Auf dem hellen Blau sind diese
    /// Farben nicht mehr lesbar. Der Balken wird deshalb selbst gefärbt, aus
    /// demselben Wörterbuch wie alles andere.
    /// </remarks>
    private static void DeriveSelection()
    {
        var flaeche = Current.TryFindResource("AuswahlFlaeche") as Brush;
        var schrift = Current.TryFindResource("AuswahlText") as Brush;
        if (flaeche is null || schrift is null) return;

        // Die Zeile bekommt die Farbe.
        var zeile = Basis(typeof(DataGridRow));
        var gewaehlt = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        gewaehlt.Setters.Add(new Setter(Control.BackgroundProperty, flaeche));
        gewaehlt.Setters.Add(new Setter(Control.ForegroundProperty, schrift));
        zeile.Triggers.Add(gewaehlt);
        Current.Resources[typeof(DataGridRow)] = zeile;

        // Die Zelle bekommt dieselbe Farbe wie die Zeile.
        //
        // Vorher stand hier Transparent, in der Annahme, die Zeile male den
        // Balken. Fluent zeichnet die Zeile aber nach eigener Vorlage und
        // beachtet ihren Hintergrund nicht -- durchsichtige Zellen liessen
        // also gar nichts uebrig. Der Balken war unsichtbar.
        var zelle = Basis(typeof(DataGridCell));
        var zelleGewaehlt = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        zelleGewaehlt.Setters.Add(new Setter(Control.BackgroundProperty, flaeche));
        zelleGewaehlt.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        zelleGewaehlt.Setters.Add(new Setter(Control.ForegroundProperty, schrift));
        zelle.Triggers.Add(zelleGewaehlt);
        Current.Resources[typeof(DataGridCell)] = zelle;
    }

    /// <summary>Ein Stil, der auf dem des Themas aufsetzt.</summary>
    private static Style Basis(Type type)
    {
        // Erst den eigenen Eintrag entfernen, sonst findet die Suche ihn selbst.
        Current.Resources.Remove(type);

        var basis = Current.TryFindResource(type) as Style;
        return basis is null ? new Style(type) : new Style(type, basis);
    }

    private static void Derive(Type type, params (DependencyProperty Property, object Value)[] setters)
    {
        var style = Basis(type);

        foreach (var (property, value) in setters)
            style.Setters.Add(new Setter(property, value));

        Current.Resources[type] = style;
    }

    /// <summary>Setzt die Sprache. Leer bedeutet: der Einstellung des Systems folgen.</summary>
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
    /// Fehlt der Schlüssel, wird der Schlüssel selbst angezeigt. Das ist so
    /// gewollt: eine vergessene Übersetzung soll auffallen und nicht als
    /// leere Fläche unbemerkt bleiben.
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

        // Erst das neue Wörterbuch hinzufügen, dann das alte entfernen. So ist
        // zwischenzeitlich kein Schlüssel unauffindbar.
        Current.Resources.MergedDictionaries.Add(next);
        if (current is not null) Current.Resources.MergedDictionaries.Remove(current);

        current = next;
    }

    /// <summary>Liest, welches Farbschema Windows für Anwendungen eingestellt hat.</summary>
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
            // Ohne lesbaren Wert bleibt es hell. Das ist die Vorgabe von Windows.
            return false;
        }
    }
}
