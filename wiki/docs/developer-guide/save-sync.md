# Save implementation

`SaveSyncManager` resolves remote identity by paging all games and requiring a
unique Playnite GUID. Each configured location maps its canonical local path to
an archive UUID; name matching is a fallback. Stale stored archive IDs are cleared
only after checking the current game archive list; ambiguous names are rejected.
The earlier single `Playnite Save` archive can be renamed for a single configured
location while retaining its history.

`SaveSyncStore` uses DataContract JSON in `save-sync.json`. Old missing/null
collections are normalized; dictionary comparers are restored case-insensitively.
Callers receive detached configurations. Writes serialize to a unique temporary
file, flush, and replace the original atomically, retaining `.bak`. Persistence
errors propagate instead of claiming success. A malformed input is copied to a
unique `.corrupt-*` file before defaults are used.

Path validation resolves environment variables, rejects roots/relative paths,
requires distinct safe names and non-overlapping locations, and rejects reparse
points at roots, ancestors and recursive files. `is_file` persists single-file
selection; old existing files are inferred. ZIP entry names use the location name
and relative path, never the local absolute root. Fingerprints hash content and
paths, detecting same-size edits with unchanged timestamps. A second fingerprint
after archive construction rejects saves that changed during snapshot creation.
Successful locations persist their archive IDs/fingerprints immediately.

The restore helper validates every entry and stages complete decompression before
changing saves. Targets must match the expected location/file; duplicate and
unsafe paths fail closed. Originals and `recovery.json` are retained in extension
user data before commit. Ordinary exceptions and cancellation roll back the
modified subset; rollback failure includes the backup path and both exceptions.
Files absent from the cloud ZIP remain local. Transactions are per location,
not across every configured folder or remote game.

A process crash, power loss, externally modified files, or filesystem replacement
during transfer is outside the ordinary rollback guarantee. Durable recovery
files remain useful for manual restoration. The extension performs no backup
retention cleanup. See [the user recovery guide](../user-guide/saves.md) for exact
local/remote storage and recovery steps.

Remote archive versions are the source of truth for server history. No second
save database, background daemon, sync engine or host storage implementation is
introduced. Tests exercise configuration migration, content-change detection,
pagination, version reuse, HTTP failure, cancellation/shutdown, unsafe paths,
single-file restore, staging and a forced mid-commit failure.
