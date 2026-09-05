**English** · [Deutsch](README.de.md)

# SyncTClient

A Syncthing client for Windows that presents files as **placeholders** and
transfers their content on first access — with a self-managed cache per volume.

The files stay ordinary files in the filesystem. No container, no virtual
drive, no proprietary format: whatever the client mounts, any other program can
read at the same time.

## Why

No existing sync service does all four of these at once:

| | |
|---|---|
| a) placeholders like OneDrive | Nextcloud, Seafile ✓ · Syncthing ✗ |
| b) self-managed cache with a size limit | Seafile, rclone ✓ · Nextcloud ✗ |
| c) client-to-client on the same network | Syncthing, Resilio ✓ · Nextcloud ✗ |
| d) mount existing directories | Syncthing, Resilio ✓ · Seafile ✗ |

Syncthing does (c) and (d) out of the box. This project adds (a) and (b) — as a
node of its own that speaks the Block Exchange Protocol. On the other side
stands an unmodified Syncthing.

## This is not wishful thinking

Syncthing's global index carries the name, the size and the complete block list
for **every** file in a folder — including files that are not materialised
locally. And BEP has a `Request` message taking *(folder, name, offset, size,
hash)*: random block access over the network.

So the catalogue behind the placeholders already exists, and providing a file's
content is a targeted block request rather than a full pull.

### Honest announcement is built in

`FileInfo.setLocalFlags()` in Syncthing calls `setNoContent()`, which discards
the block list and the size. A device therefore announces only what it actually
holds — peers never ask us for data we do not have. That is not something to
work around, but something to build on.

## What the client does

### Placeholders

- Placeholders in Explorer through the Cloud Filter API; content is transferred
  when someone opens the file
- Overlay icons — cloud, ring, green tick — derived from the pin state
- **A mode per file and per folder**, kept in the index: *placeholder* or
  *always local*. A folder passes its mode down and overrides the entries
  beneath it
- Cache limit **per volume**, eviction by last access, invalidation on change
- Eviction only against proof: a copy is released only once a peer carries it
  completely in its index (`Size > 0` plus a block list). How many peers are
  required is configurable per share
- Locally modified files are never evicted
- The hourly scan reconciles the pin attributes in the filesystem with the
  database — whatever was changed in the file manager applies here afterwards

### Transfer

- BEP in C#: framing, Hello, device ID, index, block-wise fetch, LZ4
- Its own TLS layer, because Windows does not support Ed25519
- **Both directions.** The client accepts shares *and* offers its own folders:
  Index and IndexUpdate go out, incoming connections are accepted
- Local discovery and discovery servers; peers with a dynamic address are found
- One connection per peer carrying all of that peer's folders; dropped peers
  are picked up again automatically
- Index in SQLite with resumption: after a restart only changes come in
- Conflicts follow Syncthing's pattern, with the device name in place of the
  short ID: `name.sync-conflict-YYYYMMDD-HHMMSS-DEVICE.ext`
- Replaced and deleted revisions under `.stversions`, retention configurable;
  optionally through the recycle bin
- Ignore patterns per share

### In the file manager

A shell extension, built native, registers itself under `HKEY_CURRENT_USER` —
no administrator rights:

| Entry | Effect |
|---|---|
| Always keep on this device | mode *always local*, content is transferred |
| Free up space | discard content, the placeholder stays |
| Hide this folder | take the branch out of synchronisation |
| Offer as a share … | turn any folder into a share |

The entries show what currently applies, and they work on a multiple selection.

Plus thumbnails on demand rather than in advance: when the file manager asks
for an image, the client transfers its head — one 128 KiB block — and cuts out
the embedded EXIF preview. Measured: 42 ms median for a new image, 2 ms for a
known one. The placeholder stays a placeholder throughout.

### User interface

- Manage shares, accept offered folders, release bindings, subtree selection,
  view filter
- Placeholder management per volume as a tree, across all shares
- Transfers with progress, throughput chart, backlog in both directions
- Log window, tray icon with a status badge
- German and English, light and dark theme
- Daily backup of the configuration including the device certificate

## Installation

Ready-made package under [Releases](https://github.com/Taloan/SyncTClient/releases/latest).

Requires Windows 10 version 2004 (build 19041) or newer, 64-bit.
**No administrator rights are needed.** The program registers its Explorer
integration itself on first start, under `HKEY_CURRENT_USER`, and removes it
again on request.

## Layout

```
src/SyncTClient.Bep/               protocol library, platform-independent
  Protos/bep.proto                 from syncthing/proto/bep/bep.proto
  DeviceId.cs                      base32 + Syncthing's check-digit scheme
  DeviceIdentity.cs                device certificate; ID = SHA-256 of the DER
  BepFraming.cs                    wire framing + LZ4 block format
  BepTls.cs                        TLS, because Windows cannot do Ed25519
  HelloExchange.cs                 pre-authentication handshake
  BepConnection.cs / BepListener   read loop, request/response, inbound
  LocalDiscovery / GlobalDiscovery discovery on the network
  FolderIndex / PersistentFolderIndex   index in memory and in SQLite
  FileFetcher.cs                   block-wise transfer with hash verification

src/SyncTClient.Vfs/               Cloud Filter API
  WinRtSyncRoot.cs                 sync root registration, hydration policy
  CloudFilterMount.cs              filesystem callbacks
  HydrationCache / CacheLimits     accounting and eviction per volume

src/SyncTClient.Mount/             the client: shares, synchronisation, commands
src/SyncTClient.Gui/               WPF user interface
src/SyncTClient.ExplorerProvider/  shell extension, NativeAOT
src/SyncTClient.ThumbHost/         host process for the thumbnail provider
src/SyncTClient.Probe/             console tool for verification

setup/SyncTClient.iss              Inno Setup script
tools/Veroeffentlichen.ps1         build the installer and cut the release
```

## Building from source

You need the .NET 10 SDK and the "Desktop development with C++" workload for
the NativeAOT part.

```
dotnet build SyncTClient.slnx -c Release
```

Runs as `win-x64` only; any other RuntimeIdentifier fails with a message.
Publishing goes through the `FolderProfile` profile into `BIN`, after which
`tools\Veroeffentlichen.ps1` turns that into the installer and the release.

The console tool prints a peer's index **without writing anything to disk**:

```
dotnet run --project src/SyncTClient.Probe -- --id
dotnet run --project src/SyncTClient.Probe -- --addr 192.168.1.42:22000 --target <PEER> --folder <FOLDER>
```

## Status

Version 0.9.1. The client is in daily use against Syncthing v2.

One thing is still open: consolidating the two paths by which content arrives
for "always local". Pinning makes Windows request the content, while the
background pass fetches the same file through a side file. When the two meet,
Windows reports a lock on the file.

## Contributing

This repository is read-only for everyone but its author. Feel free to
download, read and use the code; bug reports through issues are welcome. There
is a single source for changes, and that is deliberate.

## License

Apache 2.0 — see [LICENSE](LICENSE).
