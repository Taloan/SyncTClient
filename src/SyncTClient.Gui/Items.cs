using System.ComponentModel;
using System.Runtime.CompilerServices;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>Eine Gegenstelle in der Liste links.</summary>
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

/// <summary>
/// Ein Ordner einer Gegenstelle -- entweder uebernommen oder nur angeboten.
/// </summary>
public sealed class FolderItem : INotifyPropertyChanged
{
    public FolderItem(string folderId, string label, ShareHost? share)
    {
        FolderId = folderId;
        Label = label;
        Share = share;
    }

    public string FolderId { get; }
    public string Label { get; private set; }
    public ShareHost? Share { get; private set; }

    public bool Accepted => Share is not null;

    public string Display => string.IsNullOrWhiteSpace(Label) ? FolderId : Label;

    public string Detail => Share is null
        ? $"{FolderId} · angeboten, nicht übernommen"
        : $"{FolderId} · {Share.IndexCount} Einträge, {Share.IndexBytes / (1024.0 * 1024.0):0.#} MB";

    public string StateText => Share?.State switch
    {
        ShareState.Bereit => "bereit",
        ShareState.Wartet => "wartet ...",
        ShareState.Pausiert => "angehalten",
        ShareState.Fehler => "Fehler",
        ShareState.Gestoppt => "gestoppt",
        _ => "übernehmen"
    };

    public void Attach(ShareHost share, string label)
    {
        Share = share;
        Label = label;
        Refresh();
    }

    public void Detach()
    {
        Share = null;
        Refresh();
    }

    public void Refresh()
    {
        Notify(nameof(Display));
        Notify(nameof(Detail));
        Notify(nameof(StateText));
        Notify(nameof(Accepted));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
