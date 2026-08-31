using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>
/// Was andere über dieses Gerät wissen müssen -- und wie es sich beim Anmelden
/// und beim Schließen verhält.
/// </summary>
public partial class DeviceWindow : Window
{
    private readonly AppConfig _config;
    private readonly Action _save;

    /// <summary>Während des Füllens sollen die Kästchen nichts auslösen.</summary>
    private bool _loading;

    /// <param name="save">
    /// Schreibt die Konfiguration und zieht nach, was daran hängt. Dieses
    /// Fenster hat keinen Speichern-Knopf -- jeder Haken gilt sofort.
    /// </param>
    public DeviceWindow(string deviceId, string configPath, AppConfig config, Action save)
    {
        InitializeComponent();

        _config = config;
        _save = save;

        OwnIdBox.Text = deviceId;
        ConfigPathBox.Text = configPath;
        ShowQrCode(deviceId);

        _loading = true;

        // Der Autostart kommt aus der Registry: nur dort steht, ob Windows
        // dieses Programm wirklich startet.
        AutostartBox.IsChecked = Autostart.Enabled;
        StartMinimizedBox.IsChecked = config.StartMinimized;
        CloseToTrayBox.IsChecked = config.CloseToTray;

        _loading = false;
    }

    /// <summary>
    /// Dieselbe Kennung als Bild. Auf einem Mobilgerät ist Abtippen keine
    /// Möglichkeit -- 63 Zeichen ohne Tastatur sind eine Zumutung.
    /// </summary>
    private void ShowQrCode(string deviceId)
    {
        // Ohne Identität gibt es nichts zu zeigen; der Platzhalter "—" wäre
        // ein Code, der auf nichts zeigt.
        if (deviceId.Length < 20)
        {
            QrPanel.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            using var generator = new QRCodeGenerator();

            // Die Device-ID ist Base32 mit Bindestrichen und passt damit in
            // den alphanumerischen Modus: derselbe Inhalt, weniger Module.
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
            // Ein Bild weniger ist kein Grund, das Fenster nicht zu zeigen.
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
    /// Der Windows-Autostart. Er wirkt sofort, denn er steht nicht in der
    /// Konfiguration, sondern in der Registry -- und danach wird nachgelesen,
    /// was dort wirklich steht.
    /// </summary>
    private void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        try
        {
            Autostart.Set(AutostartBox.IsChecked == true);
            Hint.Text = "";
        }
        catch (Exception ex)
        {
            Hint.Text = App.S("D.AutostartFailed", ex.Message);
        }

        _loading = true;
        AutostartBox.IsChecked = Autostart.Enabled;
        _loading = false;
    }

    /// <summary>
    /// Wie sich das Fenster beim Start und beim Schließen verhält. Beides
    /// gehört in die Konfiguration und wird sofort geschrieben.
    /// </summary>
    private void OnWindowOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _config.StartMinimized = StartMinimizedBox.IsChecked == true;
        _config.CloseToTray = CloseToTrayBox.IsChecked == true;
        _save();
    }
}
