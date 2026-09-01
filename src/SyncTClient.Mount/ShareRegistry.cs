using System.Collections.Concurrent;
using SyncTClient.Bep;

namespace SyncTClient.Mount;

/// <summary>
/// Haelt je Ordner genau einen <see cref="ShareHost"/>.
/// </summary>
/// <remarks>
/// Vorher legte jede Gegenstelle ihre eigenen an. Nehmen zwei am selben
/// Ordner teil, entstuenden zwei Objekte fuer denselben Sync-Root und
/// dieselbe Index-Datenbank -- sie heisst <c>index-&lt;folderId&gt;.db</c> und
/// nicht nach der Gegenstelle. Zwei Schreiber auf einer SQLite-Verbindung
/// waeren der sichtbare Teil des Schadens.
///
/// Ein Ordner ist ein Ordner: ein Pfad, eine Auswahl, ein Index. Wer daran
/// teilnimmt, haengt seine Verbindung ein und wieder aus.
/// </remarks>
public sealed class ShareRegistry(AppConfig app, DeviceIdentity identity, Action<string> log)
{
    private readonly ConcurrentDictionary<string, ShareHost> _shares = new(StringComparer.Ordinal);

    public IReadOnlyCollection<ShareHost> All => [.. _shares.Values];

    public ShareHost? For(string folderId)
        => _shares.TryGetValue(folderId, out var share) ? share : null;

    /// <summary>
    /// Liefert den Ordner, oder legt ihn an, wenn es ihn noch nicht gibt.
    /// </summary>
    /// <param name="frisch">
    /// Wahr, wenn er bei diesem Aufruf entstanden ist. Nur dann ist er
    /// anzumelden -- ein zweiter Teilnehmer findet einen fertigen vor.
    /// </param>
    public ShareHost GetOrAdd(ShareConfig config, out bool frisch)
    {
        var entstanden = false;

        var host = _shares.GetOrAdd(config.FolderId, _ =>
        {
            entstanden = true;

            // Die eigene Geraete-ID gehoert in jede eigene Ankuendigung: in
            // modified_by und in den eigenen Zaehler des Versionsvektors.
            var neu = new ShareHost(config, app, log) { OwnDeviceId = identity.Id };
            neu.OpenIndex();
            return neu;
        });

        frisch = entstanden;
        return host;
    }

    public bool Remove(string folderId, out ShareHost host)
    {
        var gefunden = _shares.TryRemove(folderId, out var treffer);
        host = treffer!;
        return gefunden;
    }
}
