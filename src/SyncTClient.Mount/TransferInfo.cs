using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SyncTClient.Mount;

public enum TransferState
{
    /// <summary>Angefordert, wartet auf einen freien Platz.</summary>
    Wartet,
    Laeuft,
    Fertig,
    Fehler
}

/// <summary>Wohin die Bytes laufen.</summary>
public enum TransferDirection
{
    /// <summary>Wir holen eine Datei von der Gegenstelle.</summary>
    Herein,

    /// <summary>Die Gegenstelle holt eine Datei bei uns.</summary>
    Hinaus
}

/// <summary>Eine Datei, die gerade geholt oder ausgeliefert wird.</summary>
/// <remarks>
/// Meldet Aenderungen auf dem Kontext, den die Oberflaeche gesetzt hat. Die
/// Rueckrufe von CfAPI kommen aus dem Threadpool, Bindungen in WPF muessen
/// aber vom Oberflaechen-Thread bedient werden.
/// </remarks>
public sealed class TransferInfo : INotifyPropertyChanged
{
    /// <summary>Setzt die Oberflaeche einmalig beim Start.</summary>
    public static SynchronizationContext? UiContext { get; set; }

    private TransferState _state = TransferState.Wartet;
    private long _doneBytes;
    private string? _error;

    public TransferInfo(string folderId, string relativePath, long totalBytes,
        TransferDirection direction = TransferDirection.Herein)
    {
        FolderId = folderId;
        RelativePath = relativePath;
        TotalBytes = totalBytes;
        Direction = direction;
        Started = DateTimeOffset.Now;
        Touched = Environment.TickCount64;
    }

    public string FolderId { get; }
    public string RelativePath { get; }
    public long TotalBytes { get; }
    public TransferDirection Direction { get; }
    public DateTimeOffset Started { get; }

    /// <summary>
    /// Wann zuletzt etwas ankam oder hinausging.
    /// </summary>
    /// <remarks>
    /// Nur fuer den ausgehenden Weg. Beim Holen weiss der Aufrufer, wann er
    /// fertig ist; beim Ausliefern weiss es niemand -- die Gegenstelle fragt
    /// Block fuer Block und sagt nicht, wann sie aufhoert. Bleibt eine
    /// Auslieferung stehen, wird sie nach einer Weile beendet.
    /// </remarks>
    internal long Touched { get; set; }

    public string Name => System.IO.Path.GetFileName(RelativePath.Replace('/', '\\'));

    public string Folder
    {
        get
        {
            var slash = RelativePath.LastIndexOf('/');
            return slash < 0 ? "" : RelativePath[..slash];
        }
    }

    public long DoneBytes
    {
        get => _doneBytes;
        set { _doneBytes = value; Notify(); Notify(nameof(Percent)); Notify(nameof(Progress)); }
    }

    public double Percent => TotalBytes <= 0 ? 0 : 100.0 * _doneBytes / TotalBytes;

    public string Progress => TotalBytes <= 0
        ? ""
        // Feste Nachkommastelle. Mit "0.#" faellt sie bei glatten Werten weg,
        // und die Breite der Zeile aendert sich bei jedem Fortschritt.
        : $"{_doneBytes / (1024.0 * 1024.0):0.0} / {TotalBytes / (1024.0 * 1024.0):0.0} MB";

    public TransferState State
    {
        get => _state;
        set { _state = value; Notify(); Notify(nameof(StateText)); }
    }

    public string? Error
    {
        get => _error;
        set { _error = value; Notify(); Notify(nameof(StateText)); }
    }

    public string StateText => _state switch
    {
        TransferState.Wartet => "wartet",
        TransferState.Laeuft => Direction == TransferDirection.Hinaus ? "sendet" : "lädt",
        TransferState.Fertig => "fertig",
        TransferState.Fehler => _error ?? "Fehler",
        _ => ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? property = null)
    {
        var handler = PropertyChanged;
        if (handler is null) return;

        var args = new PropertyChangedEventArgs(property);
        if (UiContext is null) handler(this, args);
        else UiContext.Post(_ => handler(this, args), null);
    }
}

/// <summary>
/// Eine Datei wurde nicht geholt, weil eine Grenze es verbietet.
/// </summary>
/// <param name="UsageLimit">
/// Wahr: das Verbrauchs Limit ist die Grenze. Falsch: das Freihalte Limit.
/// </param>
/// <param name="Limit">Der Wert der Grenze, die gegriffen hat.</param>
public sealed record CacheLimitHit(
    string FolderId,
    string Name,
    long Needed,
    bool UsageLimit,
    long Limit);
