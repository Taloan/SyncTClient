using System.Windows;

namespace SyncTClient.Gui;

/// <summary>Was andere über dieses Gerät wissen müssen.</summary>
public partial class DeviceWindow : Window
{
    public DeviceWindow(string deviceId, string configPath, string thumbnailInfo)
    {
        InitializeComponent();

        OwnIdBox.Text = deviceId;
        ConfigPathBox.Text = configPath;
        ThumbInfo.Text = thumbnailInfo;
    }

    private void OnCopyId(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(OwnIdBox.Text);
        ThumbInfo.Text = "Device-ID in die Zwischenablage kopiert.";
    }
}
