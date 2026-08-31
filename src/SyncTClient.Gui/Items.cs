using System.ComponentModel;
using System.Runtime.CompilerServices;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>Eine Gegenstelle.</summary>
public sealed class PeerItem(PeerHost host) : INotifyPropertyChanged
{
    public PeerHost Host { get; } = host;
    public PeerConfig Config => Host.Config;

    public string Display => Host.Display;

    public string StateText => Host.State switch
    {
        PeerState.Verbindet => "verbindet ...",
        PeerState.Verbunden => "verbunden",
        PeerState.Fehler => Host.LastError is null ? "Fehler" : "Fehler: " + Kurz(Host.LastError),
        _ => "getrennt"
    };

    public string Detail => Host.State == PeerState.Verbunden
        ? $"{Host.Config.Address} · {Host.ClientVersion}"
        : Host.Config.Address;

    private static string Kurz(string text)
        => text.Length <= 60 ? text : text[..57] + "...";

    public void Refresh()
    {
        Notify(nameof(Display));
        Notify(nameof(StateText));
        Notify(nameof(Detail));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
