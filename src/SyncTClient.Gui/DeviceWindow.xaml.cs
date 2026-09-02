using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>
/// Zeigt, was andere über dieses Gerät wissen müssen, und stellt ein, wie sich
/// das Programm beim Anmelden und beim Schließen verhält.
/// </summary>
public partial class DeviceWindow : Window
{
    private readonly AppConfig _config;
    private readonly Action _save;

    /// <param name="save">
    /// Schreibt die Konfiguration und zieht nach, was daran hängt — etwa das
    /// Symbol im Infobereich. Wird auf „Speichern“ gerufen, nicht bei jeder
    /// Änderung.
    /// </param>
    public DeviceWindow(string deviceId, string configPath, AppConfig config, Action save)
    {
        InitializeComponent();

        _config = config;
        _save = save;

        DeviceNameBox.Text = config.DeviceName;
        OwnIdBox.Text = deviceId;
        ConfigPathBox.Text = configPath;
        ShowQrCode(deviceId);
        ShowDatabases(configPath, config);
    }

    /// <summary>Eine Index-Datenbank in der Liste.</summary>
    private sealed record DbZeile(string Name, string File, string Size);

    /// <summary>
    /// Zeigt, wo die Indizes liegen und was sie belegen.
    /// </summary>
    /// <remarks>
    /// Je Freigabe eine Datenbank, benannt nach der Ordnerkennung. Gezaehlt
    /// werden auch die Begleitdateien: SQLite legt im WAL-Modus zwei weitere
    /// an, und die koennen groesser sein als die Datenbank selbst.
    ///
    /// Aufgefuehrt wird auch, was zu keiner eingerichteten Freigabe gehoert.
    /// Das ist der interessantere Teil der Liste: eine Datenbank ohne
    /// Freigabe belegt Platz, ohne dass jemand von ihr weiss.
    /// </remarks>
    private void ShowDatabases(string configPath, AppConfig config)
    {
        var verzeichnis = Path.Combine(Path.GetDirectoryName(configPath)!, config.HomeDirectory);
        DataPathBox.Text = verzeichnis;

        if (!Directory.Exists(verzeichnis))
        {
            DbSummary.Text = App.S("S.Device.NoData");
            return;
        }

        try
        {
            var namen = config.Shares.ToDictionary(
                s => s.FolderId,
                s => string.IsNullOrWhiteSpace(s.Label) ? s.FolderId : s.Label,
                StringComparer.Ordinal);

            var zeilen = new List<DbZeile>();
            long gesamt = 0;

            foreach (var db in Directory.EnumerateFiles(verzeichnis, "index-*.db").Order(StringComparer.OrdinalIgnoreCase))
            {
                var kennung = Path.GetFileNameWithoutExtension(db)["index-".Length..];

                // Die Datenbank und ihre beiden Begleiter zaehlen zusammen.
                long groesse = 0;
                foreach (var teil in new[] { db, db + "-wal", db + "-shm" })
                    try { if (System.IO.File.Exists(teil)) groesse += new FileInfo(teil).Length; }
                    catch (Exception) { /* dann eben ohne */ }

                gesamt += groesse;

                zeilen.Add(new DbZeile(
                    namen.TryGetValue(kennung, out var name) ? name : App.S("S.Device.NoShare"),
                    Path.GetFileName(db),
                    Format.Bytes(groesse)));
            }

            DbList.ItemsSource = zeilen;
            DbSummary.Text = zeilen.Count == 0
                ? App.S("S.Device.NoData")
                : App.S("S.Device.DbSummary", Format.Count(zeilen.Count), Format.Bytes(gesamt));
        }
        catch (Exception ex)
        {
            DbSummary.Text = ex.Message;
        }
    }

    /// <summary>
    /// Dieselbe Kennung als Bild. Auf einem Mobilgerät ist Abtippen keine
    /// Möglichkeit, denn 63 Zeichen ohne Tastatur sind zu viel.
    /// </summary>
    private void ShowQrCode(string deviceId)
    {
        // Ohne Identität gibt es nichts zu zeigen. Aus dem Platzhalter "—"
        // entstünde ein Code ohne verwertbaren Inhalt.
        if (deviceId.Length < 20)
        {
            QrPanel.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            using var generator = new QRCodeGenerator();

            // Die Device-ID ist Base32 mit Bindestrichen und passt damit in
            // den alphanumerischen Modus. Der Inhalt bleibt derselbe und
            // braucht weniger Module.
            using var data = generator.CreateQrCode(deviceId, QRCodeGenerator.ECCLevel.M);

            var png = new PngByteQRCode(data).GetGraphic(8);

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(png);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            QrImage.Source = image;
        }
        catch (Exception ex)
        {
            // Ein fehlendes Bild ist kein Grund, das Fenster nicht zu zeigen.
            QrPanel.Visibility = Visibility.Collapsed;
            CopyHint.Text = App.S("D.QrFailed", ex.Message);
        }
    }

    private void OnCopyId(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(OwnIdBox.Text);
        CopyHint.Text = App.S("D.Copied");
    }

    /// <summary>
    /// Uebernimmt alles auf einmal.
    /// </summary>
    /// <remarks>
    /// Frueher galt jeder Haken sofort. Das war bequem, aber es passte nicht
    /// zu einem Fenster mit Abbrechen: was schon geschrieben ist, nimmt kein
    /// Abbrechen mehr zurueck. Jetzt gilt hier dieselbe Regel wie in den
    /// Freigabe-Einstellungen -- vor "Speichern" aendert sich nichts.
    ///
    /// </remarks>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = DeviceNameBox.Text.Trim();
        if (name.Length == 0) name = Environment.MachineName;

        _config.DeviceName = name;
        _save();

        DialogResult = true;
    }
}
