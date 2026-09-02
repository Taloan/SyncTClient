namespace SyncTClient.Bep;

/// <summary>
/// Der Handschlag stand, aber die Gegenstelle blieb ohne Zertifikat.
/// </summary>
/// <remarks>
/// Eigene Ausnahme, weil dieser Fall eine eigene Ursache hat und nicht mit
/// einem gescheiterten Handschlag zu verwechseln ist: TLS steht, das Hello
/// ist ausgetauscht, nur die Identitaet fehlt. Unter Windows trifft das jede
/// Gegenstelle, deren Zertifikat auf Ed25519 beruht.
/// </remarks>
public sealed class MissingPeerCertificateException(string message) : Exception(message);
