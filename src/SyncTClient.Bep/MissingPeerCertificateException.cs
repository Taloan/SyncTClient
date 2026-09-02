namespace SyncTClient.Bep;

/// <summary>
/// Der Handschlag stand, aber die Gegenstelle blieb ohne Zertifikat.
/// </summary>
/// <remarks>
/// Eigene Ausnahme, weil dieser Fall anders behandelt wird als ein
/// gescheiterter Handschlag. Er tritt eingehend unter Windows in TLS 1.3
/// regelmaessig auf und ist kein Grund, die Gegenstelle abzuweisen -- die
/// Verbindung laesst sich in der Gegenrichtung aufbauen.
/// </remarks>
public sealed class MissingPeerCertificateException(string message) : Exception(message);
