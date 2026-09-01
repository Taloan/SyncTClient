namespace SyncTClient.Mount;

/// <summary>Ein Name, der noch aussteht.</summary>
/// <param name="Name">Pfad ab der Wurzel der Freigabe.</param>
/// <param name="Bytes">Die Groesse, die der Index fuehrt.</param>
/// <param name="Reason">
/// Warum er aussteht, in Worten. Ob die Datei hier fehlt, anders gross ist
/// oder eine andere Zeit traegt, sind drei ganz verschiedene Lagen -- die
/// erste ist ein Rueckstand, die dritte oft nur eine eigene Aenderung, die
/// noch nicht heraus ist.
/// </param>
public sealed record OutstandingItem(string Name, long Bytes, string Reason);
