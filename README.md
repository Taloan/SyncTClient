**English** · [Deutsch](README.de.md)

<img src="docs/logo.png" alt="SyncTClient" width="110" align="right">

# SyncTClient

A Syncthing client for Windows that presents files as **placeholders** and
transfers their content on first access — with a self-managed cache per volume.

The files stay ordinary files in the filesystem. No container, no virtual
drive, no proprietary format: whatever the client mounts, any other program can
read at the same time.

> [!CAUTION]
> ## No warranty. No liability. Use entirely at your own risk.
>
> **This software transfers, replaces, evicts and deletes files.** It changes
> operating system settings, registers sync roots and hooks into the file
> manager.
>
> **The author accepts no liability of any kind, on any legal basis, for any
> damage whatsoever** arising out of or connected with the installation, use,
> malfunction or unusability of this software — including loss, deletion,
> overwriting or corruption of data on this computer and on every other device
> exchanging data with it, leakage of data to peers, changes left behind on the
> system, lost profit, business interruption and cost of recovering data. This
> applies even if the possibility of such damage was pointed out. **Any and all
> claims for damages are hereby expressly rejected.**
>
> **Full responsibility rests with the user alone.** You alone decide whether
> and for what purpose to use this software, and you alone bear the
> consequences. **Never use it without an independent, verified backup, and
> never as the only place where data lives whose loss you could not accept.**
>
> Full text: [Terms of Use](setup/EULA.en.txt) · the installer shows the German
> version, which must be accepted before installing.

![The SyncTClient main window](docs/interface.png)

*Shares with status, progress, backlog and throughput. The unfolded share
shows what the peers carry, what is held locally, and what has gone over the
line so far.*

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

#### When a file may become a placeholder

![The placeholder threshold setting](docs/placeholder-threshold.png)

Discarding a file's content is the one operation here that can lose data, so it
is the one hedged the most. Content is never dropped just because a cache is
full. It is dropped only once the file has been *proven* recoverable.

The threshold sets the number: how many other nodes must carry the file **in
full** before its bytes may go here. The default of 1 is the normal case against
a single server. Set 2 if you do not want your files depending on one other
device. Below 1 is not allowed — the last copy in the network must not be able
to disappear.

Counting is strict. A peer that merely *knows* the file does not count: its
index entry has to carry a block list and a size greater than zero. Syncthing
announces without a block list whenever it does not hold the content, so an
honest count falls out of the protocol itself.

And the threshold is a necessary condition, not a sufficient one. Every one of
these has to hold before a single byte goes:

| Condition | Why |
|---|---|
| enough peers carry it in full | the threshold itself |
| they carry **this** version | compared by version vector, not by timestamp. A timestamp says which change happened later, not whether one side knew about the other. Get this wrong and the placeholder fetches the older version on next access — silently, because what stays behind is a valid file, just the wrong one |
| the announcement matches this content | the name proves nothing on its own. A peer may well hold a different file under the same name, and a local change nobody has seen yet would be deleted for it |
| the blocks match | computed, not assumed |
| the announcement is old enough | BEP never tells a sender that the receiver has finished. Worse: the peer echoes our own announcement, block list included, long before it fetches a byte. Treating that as proof of possession deletes the file before it arrives anywhere, so the client waits instead |
| the file is not pinned and not *always local* | an explicit instruction outranks a cache limit |
| the file is neither open nor locally modified | Windows refuses it anyway; checking makes the pending change visible instead of letting it fail silently |

This applies to every path that discards content: the context menu, the volume
tree, the automatic eviction when a volume runs into its limit, and the button
that empties the cache. *Free up space* is a request, not an authority. What
gets evicted first when a limit is reached is whatever went longest untouched.

One exception, for empty files: a file of zero bytes never reaches the threshold,
because the count demands blocks and it has none. It is released when it is
empty here **and** empty on the peer — and its own size decides that, not the
announced one, because a peer reports size zero for a large file it merely knows
about as well.

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

![Subtree selection](docs/directory-select.png)

*Subtree selection: which branches live on this device. Every node carries its
file count and size, so the choice is made against numbers rather than guesses.*

## Installation

Ready-made package under [Releases](https://github.com/Taloan/SyncTClient/releases/latest).

Requires Windows 10 version 2004 (build 19041) or newer, 64-bit.
**No administrator rights are needed.** The program registers its Explorer
integration itself on first start, under `HKEY_CURRENT_USER`, and removes it
again on request.

### SmartScreen will warn you

![The Microsoft Defender SmartScreen warning](docs/smartscreen.png)

When you start the installer, Microsoft Defender SmartScreen says "Windows
protected your PC" and reports the publisher as unknown. There are two reasons
for that, and neither is a finding:

- **There is no code signing certificate.** A certificate Windows trusts costs
  money every year. For a program I write alone and earn nothing from, that is
  not on the cards.
- **SyncTClient is new.** SmartScreen also judges by reputation: something that
  has rarely been downloaded is unknown to it, and it warns to be safe. That
  fades as the number of installations grows, but it can take a while.

To continue: **More info**, then **Run anyway**.

To make sure the file is the one published here, compare its checksum with the
one given on the
[release](https://github.com/Taloan/SyncTClient/releases/latest):

```powershell
Get-FileHash .\SyncTClient-0.9.1-setup.exe -Algorithm SHA256
```

If the two match, nothing was altered on the way. It does not prove who built
the file — only a signature could do that.

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
`tools\Veroeffentlichen.cmd` turns that into the installer and the release.

The console tool prints a peer's index **without writing anything to disk**:

```
dotnet run --project src/SyncTClient.Probe -- --id
dotnet run --project src/SyncTClient.Probe -- --addr 192.168.1.42:22000 --target <PEER> --folder <FOLDER>
```

## Status

Version 0.9.1. The client is in daily use against Syncthing v2.
Every change is listed in the [changelog](CHANGELOG.md) (German).

One thing is still open: consolidating the two paths by which content arrives
for "always local". Pinning makes Windows request the content, while the
background pass fetches the same file through a side file. When the two meet,
Windows reports a lock on the file.

## Contributing

This repository is read-only for everyone but its author. Feel free to
download, read and use the code; bug reports through issues are welcome. There
is a single source for changes, and that is deliberate.

## Liability

None. See the warning at the top of this page and the full
[Terms of Use](setup/EULA.en.txt). The installer presents these terms as a page
that has to be accepted before anything is written to disk.

## License

Apache 2.0 — see [LICENSE](LICENSE).
