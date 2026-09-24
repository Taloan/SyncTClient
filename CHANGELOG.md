# Changes

Entries are written by hand. They say what changed for the user, not the
subjects of the commits — the commit-by-commit history is in `git log`.

<!-- New versions go in by hand below this line, above the previous one. -->

## 0.9.7 — in progress

**Lightroom**

- Entries from a peer that were held back while a catalogue was open were
  taken out of the queue and not looked at again after it closed. They now
  stay in the queue and are checked on the next pass.

**Synchronisation**

- A peer announces entries it excludes by pattern or deselects as invalid: no
  blocks, size zero. They were treated like any other entry and won on a
  newer version, which put them in the backlog although they can never be
  fetched. They now count as not announced: the peer's entry is dropped, the
  local file stays, and the peer's sequence number is kept so that it resumes
  at the right place.
- Stored entries of that kind are removed once when the index is opened — in
  the background, and only for entries without a size. Before, the pass ran
  over every entry on the user-interface thread.
- Where several peers carry a file in different versions, the backlog was
  measured against the largest size and the most recent time across all of
  them — a version that exists nowhere. It is now measured against the
  version that applies.
- A file another program had open was transferred in full before the attempt
  to replace it failed. It is now checked before the transfer.

**Connections**

- An index or index update could go out ahead of our own folder list, on
  which Syncthing closes the connection. Both now wait until the folder list
  is out.
- The peer's folder list was read before our own folders were registered, so
  folders already set up counted as not accepted. It is read afterwards now.
- The reason a peer gives when it closes is now in the log.
- A connection that ended during the announcement raised a null reference
  instead of a cancellation.

**User interface**

- In the settings, Save and Cancel scrolled with the content and were out of
  view until the panel had been scrolled to its end. They now sit fixed below
  the scroll area, unsaved changes are asked about on closing, and a failed
  autostart entry no longer stops the remaining settings from being saved.
- While an index was arriving, the figure shown was the peer's sequence,
  which counts every change since the folder was created and is not a file
  count. The bar still works from it — a peer states no count — while the
  text now gives the number of entries that have arrived.

## 0.9.6 — 2026-09-13

**Lightroom**

- An open catalogue is no longer touched. As long as the lock file
  `X.lrcat.lock` sits next to `X.lrcat`, no file of the set — `X.lrcat`,
  `X.lrcat-data`, `X Helper.lrdata`, `X Previews.lrdata`,
  `X Smart Previews.lrdata`, `X Sync.lrdata` — is announced, accepted or
  deleted. Independent of the smart database mode.

**Synchronisation**

- A change beside a deletion wins, and a deletion waits until a change that
  has not been announced yet is out. Before, the peer's deletion looked newer
  than a file just written.
- A deletion needs a witness: the scan that last saw the file, or the
  watcher. Entries that never held content here are no longer reported as
  deleted.
- The conflict copy is made by copying; the local file stays where it is
  until the peer's version replaces it. Before, the name was missing in
  between, and that went out as a deletion.
- After winning a conflict the file goes out with a new version. Before, the
  announcement stayed out and the peer held its own version to be the valid
  one.
- Changes that had not been announced when the program ended are picked up on
  the next start.
- A file accepted from a peer takes that peer's modification time; before,
  every scan reported it as changed and hashed it again.
- What has been changed here and not yet announced is shown in the row and in
  the log, instead of "in sync".

**Connections**

- A block request after two minutes of quiet counted as unanswered straight
  away, so every transfer after a pause failed on the first attempt.
- If the connection ended during the announcement, the peer stayed on
  "connecting": no redial, every incoming connection refused.
- Where both sides dial each other at the same time, the local dial gives way
  to the incoming connection.
- Where a peer asks for a file that was announced as present here but is not,
  the announcement is corrected instead of refused again every minute.

**User interface**

- Incoming requests — new peers, offered folders — are in the "Requests" tab
  and are accepted or declined there; no more dialog.
- Version, build commit and build time are in the title bar.

## 0.9.5 — 2026-09-13

**Connections**

- A relay listening on every interface puts the unspecified address
  (`::ffff:0.0.0.0`) into its invitation, and connecting there failed. An
  unspecified address now counts as an empty one: the relay itself.

**Start**

- Two folders may start at once, and the places went to whichever index
  arrived first, so a large folder could hold one for minutes while small
  ones waited. Start now goes in ascending order of index size.
- The sequence numbers of our own announcements could jump backwards between
  two messages. Syncthing accepts that but reports it as a protocol error.
  Everything that goes out is now above everything that ever went out.

**Always local**

- "Always local" also created a placeholder for every new or changed version
  from a peer: the existing file was removed, an empty placeholder took its
  place, and the content followed. If the transfer did not complete, an empty
  placeholder was left where a complete file had been. Now as in Syncthing:
  the peer's version is transferred in full into a side file and only then
  put in place of the old one. Until then the old file is untouched, and a
  new one appears only once it is complete. Placeholders now exist only under
  "on demand".
- Before replacing, it is checked whether the file has been written here in
  the meantime. If so it is not overwritten but announced, and the comparison
  of versions decides next time.
- After the first download the row said "in sync" even when files were
  missing. The next scan now measures the backlog; nothing is finished until
  it confirms.
- A failed fetch was only retried on the next scan over the folder. It now
  stays queued and is retried after a minute.

**Transfer**

- The deadline for a block request applied per request. A peer answers in
  order, so the answer to a request far back in the queue could arrive after
  the deadline although data was coming in the whole time. The deadline now
  applies to the connection: it expires when no answer at all has come in for
  two minutes.

**Error file**

- Tasks given up on a deadline ran on, failed later, and nobody took the
  result — the error file filled with call stacks that described no fault of
  the program. Every task with a deadline now gets a follower that takes the
  result, and a QUIC connection that comes about after it was given up is
  closed instead of left open.

**Our own index**

- On every new connection the whole local index went out in a single message,
  and a break in the middle meant it never arrived; the peer then showed the
  folder as up to date while new files waited here. What the peer says about
  us in its folder list now applies: if it knows our index under its ID up to
  sequence n, it gets only what is above that — as in Syncthing. A full index
  goes out in batches, and a break costs nothing: next time it continues
  where the peer stopped.
- The index goes out in a run of its own instead of in the folder's
  background pass, which can now accept incoming data and start transfers
  while it sends.
- Where the peer's folder list arrived only after the five-second wait, the
  decision about our index was not made and the folder went out in full with
  the next batch. It is now made as soon as the list is there.

**Table**

- A share that was set up but not connected showed a dash in the path column.
  The path from the configuration is now shown even when the peer is not
  connected.
- "not connected" also appeared for folders a peer merely offers and that
  have not been accepted here. Those are now called "offered"; "not
  connected" is left to configured shares without a connection.

**Log**

- A closed accept dialog stood in the log as a cancellation exception with a
  call stack. A cancellation is no longer reported as an error.
- "resuming at sequence n" came again for every folder on every repeated
  announcement. Now only when connecting.
- A line reporting that a response had to wait for the connection appeared
  every few seconds while a peer was fetching blocks. Reported now is only
  what stands behind something else or is taken slowly by the peer.
- The log is also written to a file: `protokoll.log` next to the
  configuration, a new one per start; the previous one stays as
  `protokoll.1.log`.
- A deletion was always announced as a file, even for a directory. Syncthing
  rejects that, and the folder stayed out of sync at the peer. A deletion now
  carries the type of the entry it deletes.

**Several peers on one folder**

- A peer taken out of a share only lost its connection; its announcements
  stayed in the index and went on counting — in the backlog, in the peer
  column, under "always local" as files to fetch, and in the peer count. Only
  what comes from a participating peer now counts; what does not belong is
  discarded on deselection and when opening.


## 0.9.4 — 2026-09-11

This version makes the client usable outside the local network and closes
three gaps through which peers received a wrong state of us — one of them
with deleted files that came back.

**Connections outside the local network**

- New: connections over a relay, in both directions. The switch in the
  settings was already there but had nothing behind it, and a peer reachable
  only over a relay was passed over. A relay is now taken as soon as no
  direct route comes about, and the client registers itself with a public
  relay so that peers can reach it that way. The address is in discovery.
- New: QUIC (UDP 22000), outbound and inbound. That switch had no effect
  either.
- A peer's addresses were tried one after another, each with a ten-second
  deadline, so a peer with many addresses took minutes and its relay came
  last. All direct addresses are now tried at once, then all relays.
- A peer whose attempt ended in an error was never tried again; only
  "disconnected" went into the reconnector. Both states are now picked up
  again, per peer with a growing interval from fifteen seconds to five
  minutes. A change of network resets the intervals.
- A connection counted as dead after three minutes without traffic, which is
  shorter than a device in the background may stay silent; the resulting
  break was logged as the peer's doing. Syncthing allows five minutes, and so
  does this now; the connection is then closed immediately and the line gives
  the reason.
- Where a peer still carries the previous connection after a break, a new one
  counts as a secondary connection for it and it sends a folder list without
  folders. That was taken literally — "offers nothing any more" for every
  share, and a folder list of our own without any knowledge, on which the
  peer sent its entire index from the start. A secondary connection is now
  recognised as one, and without a folder list what we have stored about the
  peer applies. In that state it also follows up every thirty seconds instead
  of with a growing interval.
- The reconnector took one peer after another and waited until that peer's
  shares had started, during which every other peer stayed disconnected. The
  attempts now run side by side.
- Directories could be reported as a conflict on every connection, because
  the local entry kept its old version after the peer's version had been
  applied. It now takes it over.
- While a share's index is still arriving, the columns on the right show
  dashes instead of zeros.

**Several peers on one folder**

- A folder with two peers spoke only to the first. The second received
  neither index nor requests, although it was connected.
- What came from one peer never reached the other. Where two peers are not
  connected to each other, their files go only through us, and we did not
  pass them on. Every accepted file is now announced to the remaining peers
  with its version, and the same goes for deletions.
- A share's settings and "open folder" could be clicked without a connection
  and did nothing. Both now work on the configuration, even when no folder is
  running.
- The "connected only" view now shows the shares with at least one peer,
  regardless of whether it is currently reachable; "not connected only" the
  ones without a peer. Before, the view hung on the connection: if it broke,
  the list was empty.

**What the peer knows about us**

- Files accepted from a peer stood in no announcement of ours, so to its
  peers the client looked like a device that holds almost nothing. Those
  entries are now added, two thousand per scan and only after the block list
  has been recomputed and matches the entry. With large shares that takes a
  while after updating; the log says how many are still to come.
- A deselected branch did not count as backlog here, but it did at the peer,
  which therefore never showed us as complete. Deselected items are now
  reported to the peer as "will not be here", the way Syncthing provides for.
- A share never finished although there was nothing to transfer. A file whose
  time had shifted but whose content was the same counted as open; database
  companion files went on counting in the incoming direction; and a database
  nobody opened any more stayed behind for good, because its journal held
  content. A database now counts as busy when something in the set of file
  and journal has moved in the last thirty seconds — not because the journal
  is filled. The journal is not checkpointed: foreign files are not altered.
- A deletion went out immediately. Syncthing waits sixty seconds, because
  many operations look like a deletion for a moment. Sixty seconds now as
  well; if the name reappears within that time, it is dropped.

**Deletions came back**

- Where files were taken out of a folder carried by two peers, they came back
  as placeholders and were downloaded again. The version of a name that
  applies was chosen by "present before deleted", without comparing the
  version vector: as soon as one peer had confirmed the deletion and the
  other had not, that other one's older announcement won. And our own
  deletion was not compared at all. What carries the newest version vector
  now applies — on a tie by the same rules as Syncthing — and our own
  deletion counts in it.
- The filesystem's notifications do not always come in the order in which
  things happened; a change notification after the deletion notification
  cancelled the queued deletion. It now does that only when the file is
  actually there.
- Between deleting a file and the watcher's record of it lie milliseconds,
  and in that window synchronisation could create the same file anew from the
  peer's announcement, or a running transfer could finish writing it — which
  counted as the file being back. Whatever the client itself created after a
  queued deletion is now taken away again; the deletion stands.

**Scan**

- Names that evaluation had deferred with a deadline — an open database, a
  file still being written — were queued again by every scan and reported as
  new or changed. They now keep their deadline.


## 0.9.3 — 2026-09-08

This version cleans up the interplay with programs that access the same files
while synchronisation is running. Two of the faults could cost data.

**Conflict copies**

- A locally changed file became a conflict copy as soon as an index from a
  peer arrived, regardless of what was in it. On connecting, a peer sends its
  whole index, including what it has from us, so the echo of our own
  announcement was enough: the version just written was put aside and
  replaced by the peer's older one. The version last announced is now held
  against the incoming one — if the peer knows nothing additional, its
  version waits until our change has been announced. A real conflict stays a
  conflict.
- A file deferred because of the settle time was only picked up again by the
  next scan over the folder, which runs hourly. The window in which a change
  is here and not yet stated outwards was therefore up to an hour long. Every
  deferred name now carries its own deadline; names that have come due return
  to evaluation every five seconds while idle.

**Files another program is working on**

- The scan computed the checksums of a changed file immediately, that is at
  the very moment it was being written, so what was announced was an
  intermediate state that never existed. Ten seconds without a change must
  now have passed.
- Some programs work through a file in stages, and with a fixed settle time
  every stage went out on its own. The settle time now applies per file and
  grows with every announcement that follows shortly after the previous one —
  ten seconds, twenty, forty, eighty, at most two minutes. Four minutes
  without a change reset it, and the first announcement of a new file stays
  at ten seconds.
- Three read paths opened foreign files without granting write access, and
  thereby locked out the program the file belongs to: computing for the
  announcement, the verification pass, and the access that starts a transfer.
  All read paths now share.

**Databases**

- New: smart database mode, adjustable under storage management, on by
  default. An SQLite database is a set of file and journal, not one file. In
  WAL mode the newest state is precisely not in the `.db`: committed
  transactions lie in the journal until a checkpoint works them in. Copying
  the `.db` on its own transfers an outdated beginning without any error
  appearing anywhere. It is therefore transferred only when the journal is
  empty — and that in both directions, because otherwise the peer would
  always have the younger version, and every change of its own would put the
  local file aside as a conflict copy.
- The companion files `-shm`, `-wal` and `-journal` are neither announced nor
  accepted. They make sense only together with exactly this `.db` at the same
  instant, and the protocol cannot transfer atomically across two files.

**Log and backlog**

- A program that opens and closes its database repeatedly had its companion
  files coming and going, and each appearance produced its own log line.
  Companion files are no longer recorded at all, and every message now falls
  only once per name.
- The same files stood permanently in the backlog with the reason "not yet
  announced" — a transfer that never comes. They no longer count as backlog
  but are named with a figure of their own.
- Where a peer requests a file whose content no longer matches the
  announcement, every block is refused, and each refusal stood as its own
  line in the log. Now the first refusal per file and reason stands there,
  and the count at the end.
- The reason for that refusal was also misleading: it spoke of the bytes,
  while what is true is that our announcement is out of date. On such a
  refusal the file is now queued for re-evaluation immediately, instead of
  waiting for the next scan and repeating the refusals.
- A deferred file names its reason and the remaining time in the backlog.

**Delivery**

- The installer carried one version too few: built with the old number, named
  with the new one. For the check for a new version that was an endless loop,
  because the installed program reported the older number and went on holding
  the same release to be newer. What is in the built application now decides.
- The screenshots in the READMEs carry width and height, so the page does not
  jump while loading.


## 0.9.2 — 2026-09-05

**Check for a new version**

- New, adjustable under "Start and window": never, at every start, weekly or
  monthly. A single item is requested — which release on GitHub is the
  newest. If a newer one is there, a notice appears above the toolbar with a
  link to the page it is on.
- Nothing is downloaded or executed. The program carries no signature and
  could not verify a downloaded file.
- A failure stays silent — no network, GitHub unreachable. Dismissed applies
  to the version named; a newer one reports again.

**User interface**

- The head of the settings names icon, name, version, build time and the
  address of the source. Before, the version was only in the file's
  properties.
- The limits per volume are now in the placeholder management window, which
  refers to one drive anyway, instead of as a list across all drives in the
  settings.
- The section on volumes could be empty: a failure while determining the
  eviction candidates took the whole enumeration with it. It now applies per
  cache and costs only that cache's candidates.
- The placeholder management window computed while opening and stood still
  doing so. Both computations now run in the background.
- The "rebuild extension" button is gone. It called the compiler on a project
  file that does not exist in an installation.

**Installer**

- The install location is now a choice: for all users into
  `C:\Program Files` with administrator rights, or as before for the signed-in
  user only. Both work, because the data live under
  `%LOCALAPPDATA%\SyncTClient` and not with the program.
- Before the first write access the terms of use are shown and have to be
  accepted — in German or English, according to the chosen language.
- The program icon is on the first and last page of the wizard and small on
  all the pages in between.

**Documentation**

- README.md is now English, README.de.md German. The two link to each other
  and are laid out section for section alike.
- What Microsoft Defender SmartScreen reports when the installer starts, and
  why, is now in both READMEs: there is no signing certificate, and
  SmartScreen also judges by reputation, which new software cannot have. The
  way to continue and the comparison of the checksum are given with it.
- The section on the placeholder threshold names what is counted and which
  conditions additionally apply before content is discarded.
- Two screenshots, the program icon beside the heading, and the disclaimer at
  the very top.
- Removed from the source: the address and device ID of a peer in the launch
  profiles, and a network address in both READMEs.

**Tools**

- Where publishing fails because a file manager is holding the Explorer
  extension, a message now names the cause; the build reported only that it
  could not be determined.
- `tools\Veroeffentlichen.cmd` can be clicked. Windows does not associate
  `.ps1` with PowerShell, and the execution policy is set to Restricted.
- This file exists as of this version.


## 0.9.1 — 2026-09-05

First published version.

**Transfer**

- Block Exchange Protocol in C#: framing, Hello, device ID, index, block-wise
  fetch, LZ4. On the other side stands an unmodified Syncthing v2
- Its own TLS layer, because Windows does not support Ed25519
- Both directions: accept shares and offer folders of our own. Index and
  IndexUpdate go out, inbound connections are accepted
- Discovery on the local network and through discovery servers; peers with a
  dynamic address are found
- One connection per peer for all of that peer's folders, resumed after a
  break
- Index in SQLite with resumption: after a restart only changes come in
- Conflicts follow Syncthing's pattern, with the device name in place of the
  short ID
- Replaced and deleted revisions under `.stversions`, retention configurable,
  optionally through the recycle bin
- Ignore patterns per share

**Placeholders**

- Placeholders in Explorer through the Cloud Filter API; content is
  transferred when someone opens the file
- Overlay icons derived from the pin state
- A mode per file and per folder, kept in the index: placeholder or always
  local, inherited downwards
- Cache limit per volume, eviction by last access
- Eviction only against proof: a copy is released only once enough peers
  carry it completely in their index
- Locally modified files are not evicted
- The hourly scan reconciles the pin attributes in the filesystem with the
  database

**In the file manager**

- Context menu with four entries: always keep, free up space, hide folder,
  offer as a share. They show what currently applies and work on a multiple
  selection
- Thumbnails on demand: the client transfers the head of the file — one
  128 KiB block — and cuts out the embedded EXIF preview. The placeholder
  stays in place

**User interface**

- Manage shares, accept offered folders, release bindings, subtree selection,
  view filter
- Placeholder management per volume as a tree, across all shares
- Transfers with progress, throughput chart, backlog in both directions
- Log window, tray icon with a status badge
- German and English, light and dark theme
- Daily backup of the configuration including the device certificate

**Delivery**

- Installer without administrator rights, with terms of use that have to be
  accepted
