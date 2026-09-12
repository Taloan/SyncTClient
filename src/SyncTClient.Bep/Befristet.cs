namespace SyncTClient.Bep;

/// <summary>
/// Befristetes Warten auf eine Aufgabe, die danach nicht herrenlos bleibt.
/// </summary>
/// <remarks>
/// <c>Task.WaitAsync(frist)</c> gibt nach Ablauf der Frist auf -- die
/// Aufgabe selbst laeuft weiter und scheitert spaeter, ohne dass noch jemand
/// auf sie wartet. Ein solches Scheitern meldet die Laufzeit als
/// unbeobachtete Ausnahme, und die stand mit Aufrufliste in der
/// Fehlerdatei: 163-mal "The connection timed out from inactivity" von
/// QUIC-Verbindungsversuchen, die nach 15 Sekunden aufgegeben waren, dazu
/// abgebrochene TLS-Lesevorgaenge vom Relay. Keiner davon war ein Fehler
/// des Programms; jeder war die Folge einer Frist, die gegriffen hatte.
///
/// Hier wird nach dem Aufgeben ein Nachlaeufer angehaengt, der das
/// Ergebnis entgegennimmt: eine Ausnahme wird angesehen, ein doch noch
/// gelieferter Gegenstand -- etwa eine QUIC-Verbindung -- wird geschlossen,
/// damit er nicht offen liegenbleibt.
/// </remarks>
internal static class Befristet
{
    public static async Task<T> WartenAsync<T>(this Task<T> aufgabe, TimeSpan frist, CancellationToken ct)
    {
        try
        {
            return await aufgabe.WaitAsync(frist, ct).ConfigureAwait(false);
        }
        catch
        {
            Nachlaufen(aufgabe);
            throw;
        }
    }

    public static async Task WartenAsync(this Task aufgabe, TimeSpan frist, CancellationToken ct)
    {
        try
        {
            await aufgabe.WaitAsync(frist, ct).ConfigureAwait(false);
        }
        catch
        {
            Nachlaufen(aufgabe);
            throw;
        }
    }

    public static Task<T> WartenAsync<T>(this ValueTask<T> aufgabe, TimeSpan frist, CancellationToken ct)
        => aufgabe.AsTask().WartenAsync(frist, ct);

    public static Task WartenAsync(this ValueTask aufgabe, TimeSpan frist, CancellationToken ct)
        => aufgabe.AsTask().WartenAsync(frist, ct);

    /// <summary>Nimmt entgegen, was die aufgegebene Aufgabe noch liefert.</summary>
    private static void Nachlaufen(Task aufgabe)
        => _ = aufgabe.ContinueWith(static t =>
        {
            if (t.IsFaulted)
            {
                _ = t.Exception;
                return;
            }

            if (!t.IsCompletedSuccessfully) return;

            // Ein Ergebnis, auf das niemand mehr wartet. Was sich schliessen
            // laesst, wird geschlossen.
            var ergebnis = t.GetType().GetProperty("Result")?.GetValue(t);
            switch (ergebnis)
            {
                case IAsyncDisposable a:
                    _ = a.DisposeAsync().AsTask().ContinueWith(
                        static s => _ = s.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    break;
                case IDisposable d:
                    try { d.Dispose(); } catch { /* geschlossen ist geschlossen */ }
                    break;
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
