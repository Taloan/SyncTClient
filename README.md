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

- [x] BEP-Verbindung, Hello, ClusterConfig
- [x] Index empfangen samt Blocklisten
- [x] Blöcke gezielt anfordern, Hashes prüfen
- [x] gegen Syncthing v2.1.3 verifiziert: 263 Blöcke, gleicher SHA-256 wie
      der Go-Vorläufer, 32 MB/s ab 8 parallelen Requests
- [ ] Index persistieren (100k Dateien ≈ 128 MB Blockhashes — gehört auf Platte)
- [x] CfAPI-Platzhalter im Explorer: 35 Verzeichnisse und 544 Platzhalter aus
      579 Index-Einträgen; 994,7 MB logisch, 252 KB auf der Platte. Lesezugriff
      hydriert und liefert den erwarteten Hash
- [ ] Cache-Verwaltung mit Größenlimit und Verdrängung
- [ ] Schreibweg: lokale Änderungen ankündigen und ausliefern
- [ ] Umbenennen, Löschen und Konflikte
