# Save synchronization and recovery

## Configure locations

From a game's context menu choose **Save sync → Configure save locations**. Add
one or more folders or individual save files. Give each location a distinct name,
such as `Main world` or `Profiles`. Select save data deliberately; an install
folder is often not the save folder.

Paths are local to this Playnite profile and are keyed by the game's Playnite
GUID. Windows environment variables such as `%USERPROFILE%` are expanded when
saved. Paths must be absolute, resolved, unique, and non-overlapping. Drive roots,
junctions and symbolic links are rejected. Location names must be unique valid
file names. File selections retain their file type even if the file later disappears.
For older configurations with a missing single file, select the file again when
it exists so its type can be recorded.

The **Unnamed Tracking** sidebar lists local file counts, configured paths,
automatic upload/download flags, and whether local content differs from the last
successful transfer. “Up to date” refers to that local fingerprint; it is not a
live comparison against the latest remote version. Files are scanned and hashed
in the background. A local permissions/path error appears in that game's row.

## Upload and download now

Use **Upload save now** or **Save sync → Download latest save now**, or the sidebar's
selected-game controls. If the game is unlinked, the extension asks to sync it
first. A game with no configured save locations cannot be transferred. Ignored
games are skipped. Download is refused while the game is running, and game launch
is paused while a manual download for that game is in progress.

Each location has one named remote archive; subsequent uploads create versions.
Automatic uploads skip unchanged content. Empty/missing local locations are
skipped and are not uploaded as a destructive empty archive. Downloads use the
newest version by upload timestamp. A location without a cloud version is skipped.
The completion message reports these possibilities rather than claiming that
every location necessarily transferred. There is no automatic conflict merge or
cross-machine path discovery. Use distinct archive names for distinct save sets.

## Automatic transfers

Enable **Upload on game stop** and/or **Download on game start** from the game's
**Save sync** context submenu. These options are per-game and independent of the
library automation settings. Disabling one direction preserves the other.

For download-on-start, the first launch attempt is paused through Playnite's
supported startup-cancellation flag. The extension checks/downloads saves
asynchronously and posts a notification. **Start the game again after success.**
That next launch consumes a one-time readiness flag and proceeds. Failure or
cancellation keeps launch paused; retry, repair the configuration, or disable
automatic download to use your local saves. This prevents background restores
from racing the game's own writes without blocking Playnite's UI thread.

Only one save operation runs at a time. An overlapping automatic upload is skipped
with a notification; use manual upload afterward. Configuration cannot change
during a transfer. Cancel using the sidebar or main menu. Playnite shutdown
cancels outstanding network/file work; it does not block to finish an upload.

## What stays local and what is remote

| Data | Storage |
| --- | --- |
| Server URL, API key, ignore tag and library automation settings | Playnite extension settings on this machine |
| Local paths, file/folder type, per-game save flags, archive-ID mapping, content fingerprints | `save-sync.json` in Playnite's extension user-data directory |
| Prior configuration | `save-sync.json.bak`; corrupt inputs are preserved as `.corrupt-*` files |
| Original files before a restore and recovery manifest | `save-backups/<Playnite GUID>/<timestamp>-<unique ID>/` in extension user data |
| Archive name, ZIP contents with relative paths, versions and timestamps | Unnamed Tracking account's game save archives |
| Game association | Remote `playnite_guid`, game ID and archive IDs; local configuration remains keyed by Playnite GUID |

Save-upload requests do not send the local absolute root path, local automation
flags, fingerprints, or backup manifests. A selected file's name and relative
subfolder names are part of its ZIP. Game metadata is synchronized separately,
including description, notes, and links. Save ZIPs are not encrypted by this
extension; server account access and server backups determine remote protection.
Changing a configured location name resets its local archive association; the
old remote archive/history remains available. Paths must be configured separately
on a second PC. Do not copy remote archive
IDs between different server/account configurations.

## Restore protection and recovery

The complete ZIP is validated and decompressed into a temporary staging directory
before any local save is changed. Traversal, unknown locations, duplicate targets,
file/folder conflicts, and reparse points are rejected. Archives are limited to
100,000 entries and 2 GiB expanded data. Current targets are backed up with a
`recovery.json` mapping before commit. On an ordinary write failure or cancellation,
changed files are rolled back; new files are removed. A rollback failure reports
the durable backup directory. Restore overwrites archive-listed files and keeps
local files that the archive does not mention.

Transactions are **per location**, so an earlier location may succeed if a later
one fails. A forced process termination, power loss, or concurrent outside writer
cannot be treated as a completed rollback. Keep the game stopped during restore.

For recovery, stop the game and Playnite, open the reported backup directory,
and inspect `recovery.json`. Entries with `Existed: true` map their numbered
`Backup` file to the original `Destination`; copy those originals back. Entries
with `Existed: false` identify files that did not exist before restore. Restore
deliberately, retaining the backup until the game loads correctly. No automatic
backup deletion is performed; remove old backup directories after verifying your
saves and another independent backup. Temporary staging/download files are
removed after the operation when the process can complete its cleanup.
