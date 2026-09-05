using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SyncTClient.Mount;
using SyncTClient.Vfs;

namespace SyncTClient.Gui;

/// <summary>
/// Was fuer das ganze Programm gilt. Diese Einstellungen haengen an keiner
/// Freigabe. Sonst waeren sie fuer jemanden, der noch keine Freigabe hat,
/// nicht erreichbar.
/// </summary>
public partial class ProgramSettingsWindow : Window
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private readonly AppConfig _config;
    private readonly string _configDirectory;
    private readonly string _home;
    private readonly Func<Task<string>> _clearCache;

    /// <summary>Waehrend des Fuellens sollen die Felder nichts ausloesen.</summary>
    private bool _loading;

    /// <summary>Das Datenverzeichnis zeigt auf einen anderen Ort. Das gilt erst beim naechsten Start.</summary>
    public bool HomeChanged { get; private set; }

    private readonly Func<IReadOnlyList<VolumeUsage>> _volumes;
    private readonly Func<string, Task<string>> _release;
    private readonly Func<(int Files, long Bytes)> _thumbUsage;

    private readonly ObservableCollection<VolumeRow> _rows = [];

    private readonly System.Windows.Threading.DispatcherTimer _takt =
        new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>Eine Zeile je Datentraeger, so wie sie im Fenster steht.</summary>
    /// <remarks>
    /// Die beiden Grenzen stehen hier als Text und nicht als Zahl. Waehrend
    /// jemand tippt, ist das Feld zwischendurch leer oder unvollstaendig; eine
    /// Zahl muesste dann raten, was gemeint ist. Geprueft wird beim Speichern.
    /// </remarks>
    private sealed class VolumeRow : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string Root { get; init; } = "";

        private string _text = "";
        private string _evictText = "";
        private string _buttonText = "";
        private bool _canRelease;

        public string Text { get => _text; set => Setze(ref _text, value); }
        public string EvictText { get => _evictText; set => Setze(ref _evictText, value); }
        public string ButtonText { get => _buttonText; set => Setze(ref _buttonText, value); }
        public bool CanRelease { get => _canRelease; set => Setze(ref _canRelease, value); }

        // Die beiden Grenzen melden keine Aenderung: sie kommen aus dem Feld
        // und gehen nicht dorthin zurueck. Ein Nachziehen wuerde ueberschreiben,
        // was jemand gerade tippt.
        public string MaxGb { get; set; } = "";
        public string MinFreeGb { get; set; } = "";

        private void Setze<T>(ref T feld, T wert, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(feld, wert)) return;

            feld = wert;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }

    /// <param name="volumes">Was auf welchem Datentraeger liegt.</param>
    /// <param name="release">Gibt einen Datentraeger frei und meldet das Ergebnis.</param>
    /// <param name="thumbUsage">Anzahl und Groesse der Vorschaubilder.</param>
    public ProgramSettingsWindow(
        AppConfig config, string configDirectory,
        Func<IReadOnlyList<VolumeUsage>> volumes,
        Func<string, Task<string>> release,
        Func<(int Files, long Bytes)> thumbUsage,
        Func<Task<string>> clearThumbnails)
    {
        InitializeComponent();

        _config = config;
        _configDirectory = configDirectory;
        _volumes = volumes;
        _release = release;
        _thumbUsage = thumbUsage;
        _clearCache = clearThumbnails;

        VolumeList.ItemsSource = _rows;

        // Solange der Dialog offen ist, aendert sich, was freigegeben werden
        // darf: die Gegenstelle kuendigt an, der Aufraeumtakt laeuft. Ein
        // Knopf, der eine Zahl von vorhin nennt, verspricht etwas anderes,
        // als er tut.
        _takt.Tick += (_, _) => ShowUsage();
        _takt.Start();
        Closed += (_, _) => _takt.Stop();

        // Der Autostart kommt aus der Registrierung: nur dort steht, ob
        // Windows das Programm wirklich startet.
        AutostartBox.IsChecked = Autostart.Enabled;
        StartMinimizedBox.IsChecked = config.StartMinimized;
        CloseToTrayBox.IsChecked = config.CloseToTray;

        // In der Datei darf der Pfad relativ stehen. Angezeigt wird er
        // ausgeschrieben, sonst bleibt unklar, worauf er sich bezieht.
        _home = Path.GetFullPath(Path.Combine(configDirectory, config.HomeDirectory));

        _loading = true;

        HomeBox.Text = _home;
        LanguageBox.SelectedIndex = config.Language switch { "de" => 1, "en" => 2, _ => 0 };
        ThemeBox.SelectedIndex = config.Theme switch { "Hell" => 1, "Dunkel" => 2, _ => 0 };

        ThresholdBox.Text = Math.Max(1, config.MinimumCopies).ToString();
        ThumbsBox.IsChecked = config.GenerateThumbnails;
        ShowUsage();
        ShowShellState();
        ParallelismBox.Text = config.Parallelism.ToString();
        ListenBox.IsChecked = config.Listen;
        ListenPortBox.Text = config.ListenPort.ToString();
        LocalDiscoveryBox.IsChecked = config.LocalDiscovery;
        AnnounceBox.IsChecked = config.Announce;
        DiscoveryBox.IsChecked = config.Discovery;
        DiscoveryServerBox.Text = string.Join(Environment.NewLine, config.DiscoveryServers);
        RelaysBox.IsChecked = config.Relays;

        _loading = false;
    }

    /// <summary>
    /// Zeigt, wieviel auf dem Laufwerk des Cache-Verzeichnisses frei ist. Das
    /// ist die Zahl, mit der das Mindestmass verglichen wird.
    /// </summary>

    /// <summary>
    /// Sprache und Farbschema wirken sofort. So ist die Auswahl unmittelbar zu
    /// sehen und muss nicht erwartet werden.
    /// </summary>
    private void OnLookChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        App.ApplyLanguage(LanguageBox.SelectedIndex switch { 1 => "de", 2 => "en", _ => null });
        App.ApplyTheme(ThemeBox.SelectedIndex switch { 1 => "Hell", 2 => "Dunkel", _ => null });
    }

    private void OnBrowseHome(object sender, RoutedEventArgs e) => Browse(HomeBox);

    private void Browse(TextBox box)
    {
        var dialog = new OpenFolderDialog { Multiselect = false };

        // Ein Startpfad, den es nicht gibt, oeffnet den Dialog an unbestimmter Stelle.
        if (Directory.Exists(box.Text)) dialog.InitialDirectory = box.Text;

        if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName;
    }

    /// <summary>
    /// Leert den Cache auf Zuruf. Das Verzeichnis bleibt bestehen, denn es ist
    /// zugleich der Platz der Freigaben. Nur der Inhalt wird wieder zum
    /// Platzhalter.
    /// </summary>
    /// <summary>
    /// Fuellt die Zeilen je Datentraeger und die Zeile der Vorschaubilder.
    /// </summary>
    /// <remarks>
    /// Neben dem Belegten steht, wie viel davon sich ueberhaupt freigeben
    /// laesst. Ohne diese zweite Zahl sieht ein Knopf, der nichts bewirkt,
    /// wie ein Fehler aus -- dabei liegt es meist daran, dass die
    /// Platzhalter-Schwelle noch nicht erreicht ist.
    ///
    /// Das Bestimmen der Zahlen laeuft nebenher. Fuer jede Datei wird
    /// nachgeschlagen, ob die Gegenstelle sie fuehrt; im Faden der
    /// Oberflaeche waere das ein Stocken alle paar Sekunden, mitten im
    /// Tippen.
    /// </remarks>
    /// <summary>
    /// Zeigt, was tatsaechlich in der Registrierung steht.
    /// </summary>
    /// <remarks>
    /// Die Eintraege entstehen beim Start von selbst und koennen dabei
    /// lautlos scheitern -- eine fehlende DLL, ein Eintrag, den etwas anderes
    /// ueberschrieben hat. Wer das nachsehen wollte, musste bisher die
    /// Registrierung von Hand durchsuchen.
    /// </remarks>
    private void ShowShellState()
    {
        var zustand = ThumbnailProviderRegistration.Nachsehen();

        Eintragen(zustand.Mitgeliefert, ShipName, ShipVersion, ShipChanged, ShipFolder);
        Eintragen(zustand.Eingetragen, RegName, RegVersion, RegChanged, RegFolder);

        var text = App.S("M.ShellSummary",
            App.S(zustand.MenuRegistered ? "M.ShellYes" : "M.ShellNo"),
            zustand.SyncRoots);

        // Der Hinweis nur, wenn er zutrifft. Eine Zeile, die immer dasteht,
        // wird nicht mehr gelesen.
        if (zustand.Veraltet && zustand.ClassRegistered)
            text += Environment.NewLine + Environment.NewLine + App.S("M.ShellOutdated");

        ShellStateText.Text = text;

        // Eintragen geht nur mit Datei; ohne sie waere der Eintrag ein
        // Verweis auf nichts.
        ShellRegisterButton.IsEnabled = zustand.Mitgeliefert.Pfad is not null;
        ShellUnregisterButton.IsEnabled = zustand.ClassRegistered || zustand.MenuRegistered;

        // Erzeugen geht nur, wo der Quelltext liegt.
        ShellRebuildButton.IsEnabled = ThumbnailProviderRegistration.FindProject() is not null;
    }

    /// <summary>Traegt eine Datei in ihre Spalte ein.</summary>
    private static void Eintragen(
        ThumbnailProviderRegistration.Datei datei,
        TextBlock name, TextBlock fassung, TextBlock geaendert, TextBlock ordner)
    {
        // Ein Strich, wo nichts ist. Ein leeres Feld sieht aus wie ein Fehler
        // in der Anzeige.
        var fehlt = App.S("M.ShellDash");

        name.Text = datei.Pfad is null ? fehlt : datei.Name;
        fassung.Text = datei.Fassung.Length > 0 ? datei.Fassung : fehlt;
        geaendert.Text = datei.Geaendert.Length > 0 ? datei.Geaendert : fehlt;
        ordner.Text = datei.Pfad is null ? fehlt : datei.Ordner;
    }

    /// <summary>
    /// Erzeugt die DLL der Erweiterung neu.
    /// </summary>
    /// <remarks>
    /// Sie ist ein eigenes NativeAOT-Projekt, das niemand referenziert -- der
    /// Bau der Anwendung erzeugt sie nicht mit. Nach einer Aenderung am
    /// Kontextmenue stand deshalb weiter die alte Datei in der Registrierung,
    /// und die Aenderung wirkte scheinbar nicht.
    ///
    /// Zwei Dinge braucht der Aufruf, die eine gewoehnliche Eingabeaufforderung
    /// nicht hat: vswhere im Pfad, damit der Binder die MSVC-Umgebung findet,
    /// und einen Datei-Manager, der die DLL gerade nicht geladen hat. Das
    /// erste wird hier nachgeholt, das zweite gemeldet -- der Bau nennt in
    /// diesem Fall selbst, wer die Datei haelt.
    /// </remarks>
    private async void OnRebuildShell(object sender, RoutedEventArgs e)
    {
        if (ThumbnailProviderRegistration.FindProject() is not { } projekt) return;

        ShellRebuildButton.IsEnabled = false;
        ShellRegisterButton.IsEnabled = false;
        ShellUnregisterButton.IsEnabled = false;
        ShellStateText.Text = App.S("M.ShellRebuilding");

        var (erfolg, meldung) = await Task.Run(() => Erzeugen(projekt));

        ShowShellState();

        if (!erfolg)
        {
            ShellStateText.Text = App.S("M.ShellRebuildFailed", meldung);
            return;
        }

        ThumbnailProviderRegistration.RegisterClass(
            ThumbnailProviderRegistration.FindLibrary() ?? "");
        ThumbnailProviderRegistration.RegisterMenu(
            ThumbnailProviderRegistration.FindLibrary() ?? "");

        ShowShellState();
        await ExplorerAnbieten();
    }

    /// <summary>Ruft den Bau auf und gibt die letzte Fehlerzeile zurueck.</summary>
    private static (bool Erfolg, string Meldung) Erzeugen(string projekt)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                ArgumentList = { "publish", projekt, "-c", "Release", "--nologo", "-v", "q" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false
            };

            // vswhere liegt beim Installationsprogramm von Visual Studio und
            // steht in einer gewoehnlichen Eingabeaufforderung nicht im Pfad.
            // Ohne es bricht der Binder mit "vswhere.exe ist entweder falsch
            // geschrieben oder konnte nicht gefunden werden" ab.
            foreach (var ordner in new[]
                     {
                         @"C:\Program Files (x86)\Microsoft Visual Studio\Installer",
                         @"C:\Program Files\Microsoft Visual Studio\Installer"
                     })
            {
                if (!File.Exists(Path.Combine(ordner, "vswhere.exe"))) continue;

                start.Environment["PATH"] = ordner + ";" + Environment.GetEnvironmentVariable("PATH");
                break;
            }

            using var lauf = System.Diagnostics.Process.Start(start);
            if (lauf is null) return (false, "dotnet liess sich nicht starten.");

            var ausgabe = lauf.StandardOutput.ReadToEnd() + lauf.StandardError.ReadToEnd();
            lauf.WaitForExit();

            if (lauf.ExitCode == 0) return (true, "");

            // Die letzte Zeile mit "error" sagt, woran es lag -- meist, dass
            // ein Datei-Manager die DLL haelt. Sie nennt ihn beim Namen.
            // Wagenruecklauf und Zeilenvorschub als Zahlen, damit hier
            // keine Fluchtfolge steht, die beim Bearbeiten kippt.
            var zeilen = ausgabe
                .Split([(char)13, (char)10], StringSplitOptions.RemoveEmptyEntries)
                .Select(z => z.Trim())
                .Where(z => z.Contains("error", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return (false, zeilen.Count > 0 ? zeilen[^1] : ausgabe.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async void OnRegisterShell(object sender, RoutedEventArgs e)
    {
        if (ThumbnailProviderRegistration.FindLibrary() is not { } library) return;

        ThumbnailProviderRegistration.RegisterClass(library);
        ThumbnailProviderRegistration.RegisterMenu(library);

        ShowShellState();
        await ExplorerAnbieten();
    }

    /// <summary>
    /// Nimmt die Eintraege wieder heraus.
    /// </summary>
    /// <remarks>
    /// Die angemeldeten Sync-Wurzeln bleiben stehen. Sie zu entfernen hiesse,
    /// die Platzhalter darunter unbrauchbar zu machen -- das geschieht beim
    /// Loesen einer Freigabe und nirgends sonst.
    /// </remarks>
    private async void OnUnregisterShell(object sender, RoutedEventArgs e)
    {
        ThumbnailProviderRegistration.UnregisterMenu();
        ThumbnailProviderRegistration.UnregisterClass();

        ShowShellState();
        await ExplorerAnbieten();
    }

    /// <summary>
    /// Bietet an, den Explorer neu zu starten.
    /// </summary>
    /// <remarks>
    /// Er liest diese Eintraege beim Start und laedt die DLL in seinen
    /// eigenen Prozess. Solange er laeuft, bleibt es bei dem, was er einmal
    /// geladen hat -- die Aenderung wirkt dann scheinbar nicht.
    ///
    /// Gefragt statt getan: offene Fenster gehen dabei zu, und wer gerade
    /// etwas kopiert, will das selbst entscheiden.
    /// </remarks>
    private async Task ExplorerAnbieten()
    {
        var antwort = MessageBox.Show(this,
            App.S("M.ShellRestartAsk"), App.S("S.Settings.ShellGroup"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

        if (antwort != MessageBoxResult.Yes) return;

        try
        {
            foreach (var prozess in System.Diagnostics.Process.GetProcessesByName("explorer"))
            {
                try { prozess.Kill(); } catch (Exception) { /* einer genuegt */ }
            }

            // Windows startet ihn meist von selbst. Tut es das nicht, bleibt
            // der Bildschirm leer -- und das waere schlimmer als alles, was
            // hier behoben werden sollte.
            await Task.Delay(2000);

            if (System.Diagnostics.Process.GetProcessesByName("explorer").Length == 0)
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, App.S("S.Settings.ShellGroup"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ShowUsage()
    {
        IReadOnlyList<VolumeUsage> volumes;
        (int Files, long Bytes) vorschau;

        try
        {
            volumes = await Task.Run(_volumes);
            vorschau = await Task.Run(_thumbUsage);
        }
        catch (Exception)
        {
            // Eine Anzeige. Sie ist es nicht wert, den Dialog zu beenden.
            return;
        }

        Anwenden(volumes, vorschau);
    }

    /// <summary>
    /// Traegt die Zahlen ein, ohne die Zeilen neu aufzubauen.
    /// </summary>
    /// <remarks>
    /// Neu aufgebaut wuerde jede Eingabe verworfen, die noch nicht
    /// gespeichert ist -- und das Fenster zieht im Sekundentakt nach. Was
    /// sich aendert, sind die Zahlen; was jemand getippt hat, bleibt.
    /// </remarks>
    private void Anwenden(IReadOnlyList<VolumeUsage> volumes, (int Files, long Bytes) vorschau)
    {
        foreach (var volume in volumes)
        {
            var frei = volume.FreeBytes < 0 ? App.S("G.FreeUnknown") : Format.Bytes(volume.FreeBytes);

            var text = App.S("M.VolumeLine",
                volume.Root, Format.Bytes(volume.UsedBytes), Format.Count(volume.Files), frei);
            var offen = App.S("M.VolumeEvictable",
                Format.Count(volume.EvictableFiles), Format.Bytes(volume.EvictableBytes));
            var knopf = App.S("M.VolumeRelease",
                Format.Count(volume.EvictableFiles), Format.Bytes(volume.EvictableBytes));

            var zeile = _rows.FirstOrDefault(
                r => r.Root.Equals(volume.Root, StringComparison.OrdinalIgnoreCase));

            if (zeile is null)
            {
                _rows.Add(new VolumeRow
                {
                    Root = volume.Root,
                    Text = text,
                    EvictText = offen,
                    ButtonText = knopf,
                    CanRelease = volume.EvictableFiles > 0,
                    MaxGb = (volume.MaxBytes / Gigabyte).ToString(),
                    MinFreeGb = (volume.MinimumFreeBytes / Gigabyte).ToString()
                });

                continue;
            }

            zeile.Text = text;
            zeile.EvictText = offen;
            zeile.ButtonText = knopf;
            zeile.CanRelease = volume.EvictableFiles > 0;
        }

        // Ein Laufwerk kann verschwinden, waehrend der Dialog offen ist.
        foreach (var weg in _rows.Where(r => volumes.All(
                     v => !v.Root.Equals(r.Root, StringComparison.OrdinalIgnoreCase))).ToList())
            _rows.Remove(weg);

        ThumbUsageText.Text = App.S("M.ThumbUsage",
            Format.Count(vorschau.Files), Format.Bytes(vorschau.Bytes));

        ClearButton.IsEnabled = vorschau.Files > 0;
    }

    private async void OnReleaseVolume(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string root }) return;

        Hint.Text = App.S("G.Clearing");
        try
        {
            Hint.Text = await _release(root);
        }
        catch (Exception ex)
        {
            Hint.Text = App.S("G.ClearFailed", ex.Message);
        }
        finally
        {
            ShowUsage();
        }
    }

    private async void OnClearThumbnails(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            App.S("G.ClearBody"), App.S("G.ClearTitle"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        ClearButton.IsEnabled = false;
        Hint.Text = App.S("G.Clearing");

        try
        {
            Hint.Text = await _clearCache();
        }
        catch (Exception ex)
        {
            Hint.Text = App.S("G.ClearFailed", ex.Message);
        }
        finally
        {
            ShowUsage();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Keine 0: unbegrenzt bedeutet, dass nie Speicherplatz freigegeben wird. Aus
        // "on-demand" wuerde nach ein paar Monaten eine Vollkopie.

        if (!int.TryParse(ThresholdBox.Text.Trim(), out var schwelle) || schwelle < 1)
        {
            Hint.Text = App.S("G.ThresholdInvalid");
            return;
        }


        // Erst pruefen, dann setzen: eine halb uebernommene Liste waere
        // schlimmer als eine abgelehnte.
        var grenzen = new List<(string Root, long Max, long Free)>();

        foreach (var zeile in _rows)
        {
            if (!long.TryParse(zeile.MaxGb.Trim(), out var max) || max < 1 ||
                !long.TryParse(zeile.MinFreeGb.Trim(), out var free) || free < 0)
            {
                Hint.Text = App.S("G.VolumeInvalid", zeile.Root);
                return;
            }

            grenzen.Add((zeile.Root, max * Gigabyte, free * Gigabyte));
        }

        if (!int.TryParse(ParallelismBox.Text.Trim(), out var parallelism) || parallelism < 1)
        {
            Hint.Text = App.S("G.ParallelInvalid");
            return;
        }

        if (!int.TryParse(ListenPortBox.Text.Trim(), out var listenPort) ||
            listenPort < 1 || listenPort > 65535)
        {
            Hint.Text = App.S("G.PortInvalid");
            return;
        }

        var home = HomeBox.Text.Trim();
        if (home.Length == 0)
        {
            Hint.Text = App.S("G.HomeMissing");
            return;
        }

        // Nur aendern, wenn der Pfad wirklich auf einen anderen Ort zeigt.
        // Sonst wuerde aus dem relativen Eintrag der Vorlage ein absoluter Pfad.
        if (!PathsEqual(Path.Combine(_configDirectory, home), _home))
        {
            _config.HomeDirectory = home;
            HomeChanged = true;
        }

        // Der Autostart steht nicht in der Konfiguration, sondern in der
        // Registrierung. Er wird deshalb getrennt geschrieben und danach
        // zurueckgelesen. Zuerst, denn nur er kann fehlschlagen.
        var autostart = AutostartBox.IsChecked == true;
        if (autostart != Autostart.Enabled)
        {
            try
            {
                Autostart.Set(autostart);
            }
            catch (Exception ex)
            {
                Hint.Text = App.S("D.AutostartFailed", ex.Message);
                AutostartBox.IsChecked = Autostart.Enabled;
                return;
            }
        }

        _config.StartMinimized = StartMinimizedBox.IsChecked == true;
        _config.CloseToTray = CloseToTrayBox.IsChecked == true;
        _config.MinimumCopies = schwelle;
        _config.GenerateThumbnails = ThumbsBox.IsChecked == true;
        foreach (var (root, max, free) in grenzen)
            _config.SetLimits(root, max, free);
        _config.Parallelism = parallelism;
        _config.Listen = ListenBox.IsChecked == true;
        _config.ListenPort = listenPort;
        _config.LocalDiscovery = LocalDiscoveryBox.IsChecked == true;
        _config.Announce = AnnounceBox.IsChecked == true;
        _config.Discovery = DiscoveryBox.IsChecked == true;
        _config.DiscoveryServers =
        [
            .. DiscoveryServerBox.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ];
        _config.Relays = RelaysBox.IsChecked == true;
        _config.Language = LanguageBox.SelectedIndex switch { 1 => "de", 2 => "en", _ => "" };
        _config.Theme = ThemeBox.SelectedIndex switch { 1 => "Hell", 2 => "Dunkel", _ => "" };

        DialogResult = true;
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // Ungueltige Pfade gelten als ungleich. Der Fehler zeigt sich beim Speichern.
            return false;
        }
    }
}
