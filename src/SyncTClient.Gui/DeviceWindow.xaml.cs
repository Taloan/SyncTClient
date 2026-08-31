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

        // Der Autostart kommt aus der Registry: nur dort steht, ob Windows
        // dieses Programm wirklich startet.
        AutostartBox.IsChecked = Autostart.Enabled;
        StartMinimizedBox.IsChecked = config.StartMinimized;
        CloseToTrayBox.IsChecked = config.CloseToTray;
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
    /// Der Autostart ist dabei der Sonderfall: er steht nicht in der
    /// Konfiguration, sondern in der Registrierung. Er wird deshalb getrennt
    /// geschrieben und danach zurueckgelesen, denn nur dort steht, ob Windows
    /// das Programm wirklich startet.
    /// </remarks>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = DeviceNameBox.Text.Trim();
        if (name.Length == 0) name = Environment.MachineName;

        _config.DeviceName = name;
        _config.StartMinimized = StartMinimizedBox.IsChecked == true;
        _config.CloseToTray = CloseToTrayBox.IsChecked == true;
        _save();

        var autostart = AutostartBox.IsChecked == true;
        if (autostart != Autostart.Enabled)
        {
            try
            {
                Autostart.Set(autostart);
            }
            catch (Exception ex)
            {
                // Alles andere ist gespeichert. Das Fenster bleibt offen,
                // damit die Meldung nicht mit ihm verschwindet.
                Hint.Text = App.S("D.AutostartFailed", ex.Message);
                AutostartBox.IsChecked = Autostart.Enabled;
                return;
            }
        }

        DialogResult = true;
    }
}
