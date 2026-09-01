using System.IO;
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

    /// <summary>Wohin ein Absturz geschrieben wird.</summary>
    private static string FehlerDatei => Path.Combine(
        Path.GetDirectoryName(SyncTClient.Mount.AppConfig.DefaultConfigPath()) ?? ".",
        "fehler.log");

    private static bool _fehlerGemeldet;

    /// <summary>
    /// Schreibt auf, was das Programm sonst wortlos beenden wuerde.
    /// </summary>
    /// <remarks>
    /// Bisher stand ein Absturz nur in der Ereignisanzeige von Windows. Dort
    /// steht er vollstaendig, aber niemand sucht ihn dort, und wer ihn sucht,
    /// findet ihn zwischen tausend fremden Eintraegen.
    ///
    /// Die Datei liegt neben der Konfiguration. Angehaengt wird, nicht
    /// ueberschrieben: der zweite Absturz ist oft der aufschlussreichere, und
    /// der erste soll dabei nicht verschwinden.
    /// </remarks>
    private static void Notieren(Exception exception, string herkunft)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FehlerDatei)!);
            File.AppendAllText(FehlerDatei,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{herkunft}]{Environment.NewLine}" +
                exception + Environment.NewLine + Environment.NewLine);
        }
        catch (Exception)
        {
            // Wenn nicht einmal das Aufschreiben geht, ist nichts mehr zu
            // retten. Ein Fehler beim Melden eines Fehlers hilft niemandem.
        }
    }

    /// <summary>
    /// Haelt fest, dass dieses Programm laeuft.
    /// </summary>
    /// <remarks>
    /// Ein zweiter Client waere kein Duplikat, sondern ein Widerspruch: er
    /// belegt denselben Port, denselben Pipe-Namen und dieselbe Geraete-ID.
    /// Die Gegenstelle wirft die zweite Verbindung weg, und das Protokoll
    /// fuellt sich mit Meldungen, deren Ursache nirgends steht.
    ///
    /// Je Benutzer, nicht je Sitzung: zwei angemeldete Benutzer duerfen ihren
    /// eigenen Client haben, denn sie haben eigene Ordner und eigene
    /// Zertifikate.
    /// </remarks>
    private static Mutex? _einmal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _einmal = new Mutex(true, @"Local\SyncTClient.Instanz", out var alleine);

        if (!alleine)
        {
            MessageBox.Show(
                S("M.AlreadyRunning"), "SyncTClient",
                MessageBoxButton.OK, MessageBoxImage.Information);

            Shutdown();
            return;
        }

        // Ein Fehler in einem Rueckruf der Oberflaeche beendete bisher das
        // ganze Programm -- ein Klick auf einen Verweis in der Tabelle
        // genuegte. Aufgeschrieben und weitergemacht ist besser: der Abgleich
        // laeuft in anderen Faeden und ist von einem verungluecken Klick
        // nicht betroffen.
        DispatcherUnhandledException += (_, args) =>
        {
            Notieren(args.Exception, "Oberflaeche");
            args.Handled = true;

            if (_fehlerGemeldet) return;
            _fehlerGemeldet = true;

            MessageBox.Show(
                args.Exception.Message + Environment.NewLine + Environment.NewLine +
                "Aufgeschrieben in:" + Environment.NewLine + FehlerDatei,
                "SyncTClient", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        // Diese beiden lassen sich nicht abfangen, nur aufschreiben.
        AppDomain.CurrentDomain.UnhandledException +=
            (_, args) => Notieren(args.ExceptionObject as Exception ?? new Exception("unbekannt"), "Hintergrund");

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException +=
            (_, args) => Notieren(args.Exception, "Aufgabe");

        // Vor dem ersten Fenster anwenden. Sonst erscheint es kurz hell,
        // bevor die Einstellung gelesen ist.
        ApplyTheme(null);
        ApplyLanguage(null);

        // Legt die Zustandsplaketten als Bild ab und beendet sich wieder.
        // Ohne das laesst sich ihre Lesbarkeit nur im Infobereich beurteilen,
        // also erst nachdem jemand das Programm gestartet hat.
        if (e.Args.FirstOrDefault() == "--badges")
        {
            SchreibePlakettenbild(e.Args.ElementAtOrDefault(1) ?? "plaketten.png");
            Shutdown();
        }
    }

    /// <summary>Alle Zustaende in allen Groessen, hell und dunkel hinterlegt.</summary>
    private static void SchreibePlakettenbild(string pfad)
    {
        var zustaende = Enum.GetValues<TrayStatus>();
        var groessen = new[] { 16, 20, 24, 32, 48 };

        var spalte = 70;
        var breite = 90 + zustaende.Length * spalte;
        var hoehe = groessen.Sum(g => g + 26) + 20;

        using var bild = new System.Drawing.Bitmap(breite, hoehe);
        using var g2 = System.Drawing.Graphics.FromImage(bild);
        using var schrift = new System.Drawing.Font("Segoe UI", 8);

        g2.Clear(System.Drawing.Color.FromArgb(246, 246, 246));
        g2.FillRectangle(System.Drawing.Brushes.Black, 90 + (zustaende.Length * spalte) / 2, 0,
                         breite, hoehe);

        using var plaketten = new TrayBadge();
        var y = 10;

        foreach (var kante in groessen)
        {
            g2.DrawString($"{kante} px", schrift, System.Drawing.Brushes.Gray, 8, y + kante / 2 - 8);

            var x = 90;
            foreach (var zustand in zustaende)
            {
                using var symbol = plaketten.For(zustand, kante).ToBitmap();
                g2.DrawImage(symbol, x, y, kante, kante);
                if (kante == groessen[0])
                    g2.DrawString(zustand.ToString(), schrift, System.Drawing.Brushes.Gray, x - 6, y - 2 + kante);

                x += spalte;
            }

            y += kante + 26;
        }

        bild.Save(System.IO.Path.GetFullPath(pfad), System.Drawing.Imaging.ImageFormat.Png);
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
        // Der Balken zuerst: die Vorlage haengt an keiner Farbe des Themas
        // und gilt fuer jedes Fenster, das einen zeigt.
        if (Current.TryFindResource("BalkenVorlage") is ControlTemplate balken)
            Derive(typeof(ProgressBar),
                (Control.TemplateProperty, balken),
                (Control.BackgroundProperty, Current.TryFindResource("Feld") ?? Brushes.Gray),
                (Control.ForegroundProperty, Current.TryFindResource("Ein") ?? Brushes.SteelBlue));

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
