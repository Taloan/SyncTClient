using System.ComponentModel;
using System.Runtime.CompilerServices;
using SyncTClient.Mount;

namespace SyncTClient.Gui;

/// <summary>Woher eine Anfrage stammt.</summary>
public enum AnfrageArt
{
    /// <summary>Eine unbekannte Gegenstelle hat sich verbunden und wartet auf die Aufnahme.</summary>
    Geraet,

    /// <summary>Eine bekannte Gegenstelle bietet einen Ordner an.</summary>
    Ordner
}

/// <summary>
/// Ein Eintrag in der Warteliste der eingehenden Anfragen.
/// </summary>
/// <remarks>
/// Eine Anfrage wird nicht mehr in dem Augenblick beantwortet, in dem sie
/// eintrifft. Sie steht in der Liste, bis jemand sie annimmt oder
/// abweist -- wann immer das passt. Ein Dialog, der sofort aufgeht und
/// nach dem Wegklicken bis zum naechsten Programmstart nicht wiederkommt,
/// war dafuer das falsche Mittel.
///
/// Die Liste lebt im Speicher. Sie muss nicht gespeichert werden: eine
/// Gegenstelle, die aufgenommen werden will, verbindet sich erneut, und
/// einen angebotenen Ordner nennt sie beim naechsten Verbinden wieder.
/// </remarks>
public sealed class Anfrage : INotifyPropertyChanged
{
    public required AnfrageArt Art { get; init; }

    /// <summary>Kennung der Gegenstelle, die die Anfrage stellt.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Name der Gegenstelle, wie sie sich selbst nennt oder hier heisst.</summary>
    public required string Von { get; init; }

    /// <summary>Adresse, unter der die Gegenstelle spaeter erreichbar ist. Nur bei Geraeten.</summary>
    public string Adresse { get; init; } = "";

    /// <summary>Kennung des angebotenen Ordners. Nur bei Ordnern.</summary>
    public string FolderId { get; init; } = "";

    /// <summary>Bezeichnung des angebotenen Ordners, wie die Gegenstelle ihn nennt.</summary>
    public string Label { get; init; } = "";

    /// <summary>Ob der Ordner hier schon eingerichtet ist -- dann kommt nur die Gegenstelle dazu.</summary>
    public bool OrdnerEingerichtet { get; init; }

    /// <summary>Schluessel, unter dem eine wiederholte Anfrage dieselbe bleibt.</summary>
    public string Schluessel => Art == AnfrageArt.Geraet
        ? $"g:{DeviceId.ToUpperInvariant()}"
        : $"o:{DeviceId.ToUpperInvariant()}:{FolderId}";

    private DateTime _zuletzt = DateTime.Now;

    /// <summary>Wann die Anfrage zuletzt einging. Eine Wiederholung setzt die Zeit neu.</summary>
    public DateTime Zuletzt
    {
        get => _zuletzt;
        set { _zuletzt = value; Melde(nameof(Zuletzt)); Melde(nameof(ZeitText)); }
    }

    public string ZeitText => Zuletzt.ToString("t");

    public string ArtText => Art == AnfrageArt.Geraet ? App.S("A.Device") : App.S("A.Folder");

    /// <summary>Was angefragt wird, in einer Zeile.</summary>
    public string Was => Art switch
    {
        AnfrageArt.Geraet => App.S("A.DeviceWants", Adresse, DeviceId),
        _ when OrdnerEingerichtet => App.S("A.FolderKnown", OrdnerName, FolderId),
        _ => App.S("A.FolderNew", OrdnerName, FolderId)
    };

    public string OrdnerName => string.IsNullOrWhiteSpace(Label) ? FolderId : Label;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Melde([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
