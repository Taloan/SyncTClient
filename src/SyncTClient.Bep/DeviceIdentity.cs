using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SyncTClient.Bep;

/// <summary>
/// Das Geraetezertifikat dieses Clients. Syncthing kennt keine CA -- die
/// Identitaet eines Geraets <em>ist</em> der SHA-256-Hash seines Zertifikats.
/// Ein selbstsigniertes Zertifikat ist daher nicht Notloesung, sondern
/// genau das vorgesehene Verfahren.
/// </summary>
public sealed class DeviceIdentity
{
    /// <summary>Wie Syncthing: ECDSA P-384, CN "syncthing", 20 Jahre Laufzeit.</summary>
    private const string CommonName = "syncthing";
    private const int LifetimeDays = 20 * 365;

    /// <summary>
    /// PKCS#12 unterscheidet "kein Passwort" von "leeres Passwort", und
    /// verschiedene Werkzeuge meinen Verschiedenes damit. Ein fester,
    /// nicht-leerer Wert umgeht die Mehrdeutigkeit. Schutzwirkung hat er
    /// keine -- die Datei liegt ohnehin unverschluesselt daneben, genau wie
    /// Syncthings eigene key.pem.
    /// </summary>
    private const string PfxPassword = "synctclient";

    public X509Certificate2 Certificate { get; }
    public DeviceId Id { get; }

    private DeviceIdentity(X509Certificate2 certificate)
    {
        Certificate = certificate;
        Id = DeviceId.FromCertificate(certificate.RawData);
    }

    /// <summary>
    /// Laedt das Zertifikat aus <paramref name="homeDirectory"/> oder legt beim
    /// ersten Aufruf eines an. Die Geraete-ID bleibt damit ueber Neustarts stabil.
    /// </summary>
    /// <remarks>
    /// Liegen <c>device.crt</c> und <c>device.key</c> im PEM-Format vor, werden
    /// sie uebernommen. So laesst sich ein bereits auf dem Peer freigegebenes
    /// Zertifikat weiterverwenden, ohne dort etwas neu bestaetigen zu muessen.
    /// </remarks>
    public static DeviceIdentity LoadOrCreate(string homeDirectory)
    {
        Directory.CreateDirectory(homeDirectory);

        var pfxPath = Path.Combine(homeDirectory, "device.pfx");
        var certPath = Path.Combine(homeDirectory, "device.crt");
        var keyPath = Path.Combine(homeDirectory, "device.key");

        if (File.Exists(pfxPath))
            return new DeviceIdentity(LoadPfx(File.ReadAllBytes(pfxPath)));

        if (File.Exists(certPath) && File.Exists(keyPath))
        {
            using var imported = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            var importedPfx = imported.Export(X509ContentType.Pfx, PfxPassword);
            File.WriteAllBytes(pfxPath, importedPfx);
            return new DeviceIdentity(LoadPfx(importedPfx));
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new CertificateRequest($"CN={CommonName}", key, HashAlgorithmName.SHA256);
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(LifetimeDays));

        var pfx = generated.Export(X509ContentType.Pfx, PfxPassword);
        File.WriteAllBytes(pfxPath, pfx);
        File.WriteAllText(certPath, generated.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());

        return new DeviceIdentity(LoadPfx(pfx));
    }

    /// <summary>
    /// Der Umweg ueber PKCS#12 ist auf Windows noetig: Schannel akzeptiert
    /// Client-Zertifikate nur, wenn der private Schluessel in einer Form
    /// vorliegt, die es selbst verwalten kann.
    /// </summary>
    private static X509Certificate2 LoadPfx(byte[] pfx)
        => X509CertificateLoader.LoadPkcs12(
            pfx,
            PfxPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
}
