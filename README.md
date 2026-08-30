# SyncTClient

Ein Syncthing-Client für Windows, der Dateien als **Platzhalter** darstellt und
Inhalte erst bei Zugriff nachlädt — mit selbstverwaltetem lokalem Cache.

## Warum

Kein existierender Sync-Dienst kann alle vier Dinge gleichzeitig:

| | |
|---|---|
| a) Platzhalter wie OneDrive | Nextcloud, Seafile ✓ · Syncthing ✗ |
| b) selbstverwalteter Cache mit Größenlimit | Seafile, rclone ✓ · Nextcloud ✗ |
| c) Client-zu-Client im selben Netz | Syncthing, Resilio ✓ · Nextcloud ✗ |
| d) bestehende Verzeichnisse einbinden | Syncthing, Resilio ✓ · Seafile ✗ |

Syncthing kann (c) und (d) von Haus aus. Dieses Projekt ergänzt (a) und (b).

## Dass das geht, ist kein Wunschdenken

Syncthings globaler Index enthält für **jede** Datei eines Ordners den Namen,
die Größe und die vollständige Blockliste — auch für Dateien, die lokal nicht
materialisiert sind. Und das Block Exchange Protocol (BEP) hat eine
`Request`-Nachricht mit *(Ordner, Name, Offset, Größe, Hash)*: wahlfreier
Blockzugriff über Netzwerk.

Damit ist der Katalog für die Platzhalter schon da, und die Hydration ist ein
gezielter Block-Request statt eines vollständigen Pulls.

Ein Vorläufer in Go hat das gegen ein unverändertes Syncthing v2.1.3 bestätigt:
32,8 MB in 263 Blöcken, alle einzeln gegen ihren Hash verifiziert, 32 MB/s bei
16 parallelen Requests, ~14 ms Latenz bis zum ersten Block.

### Ehrliche Ankündigung ist eingebaut

`FileInfo.setLocalFlags()` in Syncthing ruft `setNoContent()` auf, was
Blockliste und Größe verwirft. Ein Gerät kündigt also automatisch nur an, was
es wirklich hält — Peers fragen uns nie nach Daten, die wir nicht haben. Das
gilt es nicht zu umgehen, sondern zu nutzen.

## Aufbau

```
src/SyncTClient.Bep/       Protokollbibliothek (plattformunabhängig)
  Protos/bep.proto         aus syncthing/proto/bep/bep.proto
  DeviceId.cs              Base32 + Syncthings Prüfziffernverfahren
  DeviceIdentity.cs        Gerätezertifikat; ID = SHA-256 des DER
  BepFraming.cs            Draht-Rahmung + LZ4-Blockformat
  HelloExchange.cs         Vor-Authentifizierungs-Handshake
  BepConnection.cs         TLS, Leseschleife, Request/Response-Zuordnung
  FolderIndex.cs           empfangener Index (Grundlage der Platzhalter)
  FileFetcher.cs           blockweises Laden mit Hash-Prüfung

src/SyncTClient.Probe/     Konsolenwerkzeug zum Nachweis
src/SyncTClient.Mount/     der Client: hängt Freigaben als Platzhalter ein
src/SyncTClient.Gui/       Einstellungen: Modus, Cache-Budget, Auswahlbaum
```

Später kommt `src/SyncTClient.Vfs/` dazu: der CfAPI-Provider, der den Index als
Platzhalter im Explorer projiziert und `FetchRangeAsync` an Windows'
Hydrations-Callback hängt.

## Benutzung

```bash
dotnet run --project src/SyncTClient.Probe -- --id
```

Die ausgegebene Device-ID auf dem Peer als Gerät eintragen und den Ordner damit
teilen. Dann:

```bash
dotnet run --project src/SyncTClient.Probe -- --addr 192.168.1.42:22000 --target <PEER-ID> --folder <FOLDER-ID>
```

Das holt den Index und zeigt ihn an, **ohne irgendetwas auf die Platte zu
schreiben**. Mit `--fetch <datei>` wird zusätzlich eine einzelne Datei
blockweise geladen und verifiziert.

## Stand

**Läuft:**

- BEP-Protokoll in C#: Rahmung, Hello, Geräte-ID, Index, blockweiser Abruf
- Platzhalter im Explorer über die Cloud-Filter-API, Inhalte beim Zugriff
- Überlagerungssymbole — Wolke, Kringel, grüner Haken — über den Anheft-Zustand
- Cache mit Budget, Verdrängung nach letztem Zugriff, Invalidierung bei Änderung
- Index in SQLite mit Wiederaufnahme: beim Neustart kommen nur Änderungen
- Eine Verbindung je Gegenstelle, die alle ihre Ordner trägt
- Oberfläche als Sync-Dienst: Server verwalten, angebotene Ordner übernehmen,
  Bindungen lösen, Teilbaum-Auswahl, Übertragungen mit Fortschritt, Protokoll
- Vorschaubilder aus dem Dateikopf gewinnen — ein Block je Bild, ohne Hydration
- Vorschau-Anbieter, den die Shell erfolgreich aufruft: als DLL im Prozess, im
  DLL-Surrogat und aus dem laufenden Client heraus
- Messwerkzeuge für die Vorschaukette: `--providertest`, `--cachecheck`,
  `--shellprops`, `--pin`, `--register-thumbs`

**Offen:**

- *Vorschaubilder im Explorer.* Die Kette funktioniert nachweislich, nur der
  Explorer nimmt sie nicht. Über `IThumbnailCache` mit `WTS_FORCEEXTRACTION`
  liefert unser Anbieter für einen dehydrierten Platzhalter eine echte Vorschau
  (256x171, ohne Hydration); die Bildpunkte unterscheiden sich je Datei, es ist
  also kein hochskaliertes Symbol. Explorer selbst fragt trotzdem nie.

  Gemessen und ausgeschlossen als Ursache: MSIX (Nextcloud arbeitet
  nachweislich unverpackt -- `GetPackageFullName` meldet
  `APPMODEL_ERROR_NO_PACKAGE` -- und wird vom Explorer bedient); der
  Anheft-Zustand; die Dateiattribute (auf `0x401620` angeglichen); der
  Sync-Root-Status `IDLE`; die Ordnermarkierung mit `Desktop.ini` und
  System-Bit; ein fehlender `CustomStateHandler`; der Trennstrich in
  `UserSyncRoots`; ein Neustart der Shell; eine bilderarme Ansicht.

  Die Shell erkennt unsere Dateien vollständig: `System.StorageProviderId` ist
  `SyncTClient`, `System.FilePlaceholderStatus` ist 8 -- beides identisch zu
  Nextcloud.

  Der eine unerklärte Befund: `CoCreateInstance` mit `CLSCTX_LOCAL_SERVER`
  gelingt für Nextclouds CLSID und scheitert mit `REGDB_E_CLASSNOTREG` für
  jede von uns angelegte -- auch für eine frisch erfundene, die auf Nextclouds
  eigene DLL mit Nextclouds eigener AppID zeigt. Registrierung, Hive und
  Zugriffsrechte sind dabei Feld für Feld gleich. Das deutet auf einen
  Zwischenspeicher, den COM beim Anmelden aufbaut; die Vermutung lässt sich
  mit einem Neustart prüfen -- danach wäre der erste Griff:

  ```
  synctmount --providertest <bild> --ctx local
  ```

  Kommt dort eine Vorschau statt `0x80040154`, war es der Zwischenspeicher.

  Microsofts CloudMirror-Beispiel hilft nur halb weiter: es deklariert seine
  Handler im `Package.appxmanifest` und läuft verpackt. Übernommen haben wir
  daraus die Laufzeit-Anmeldung mit `CoRegisterClassObject` aus dem
  Anbieterprozess -- damit gelingt die Aktivierung über `LOCAL_SERVER`.
- *Schreibweg.* Der Client ist rein lesend; eigene Ordner anbieten geht nicht.
- *CustomStateHandler* für die Spalten „Status" und „Verfügbarkeit".
