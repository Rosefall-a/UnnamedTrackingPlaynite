# Repository and readiness audit

Audit date: **2026-10-02**. Extension baseline: main commit
`5befa43` (status-validation fix, PR #16). Host contract:
[`plugin-manager` at `45424fe`](api-contract.md#verified-host-source).

## Repository decisions

| Item | Evidence and decision |
| --- | --- |
| `Backup/UnnamedTrackingPlaynite.sln` | Older VS metadata and the same project GUID/path as the root solution; no code/workflow references. Its relative source path would resolve under Backup. Removed as an obsolete solution copy. |
| `BUILD_FIX_MARKER.txt` | Only “Temporary marker for SDK API fix.” No source/build/CI references. Removed; not a build input. |
| Root solution / one source project | Used by the build workflow and fresh-checkout commands. Retained. |
| Generated build output | No tracked generated assemblies/PEXT/bin/obj input. Existing ignores retained and PEXT/wiki output ignores added. |
| Obsolete source | Unused narrow update DTO, legacy folder helper and duplicate toggle method removed. The unused embedded-view method was connected to the sidebar rather than deleted. |
| Packaging scripts | Local Toolbox-only behavior and duplicate CI archive code differed. Shared existing pack script now builds/validates exact PEXT contents with optional Toolbox. Both workflows retained. |
| README | Claimed selected sync/embedded view that were not reachable, folder-first matching, temporary backups, deferred cancellation and runtime evidence not established here. Updated to the current implementation and evidence. |
| Line endings | All 26 newline-bearing tracked baseline files used LF; editorconfig required CRLF. Align editorconfig and Git attributes to LF. Explicit binary rules protect PEXT/ZIP, assemblies and screenshot bytes. No plugin repository attribute rules copied. |

## Synchronization decisions

SDK metadata, ignore tags and artwork paths are captured before asynchronous
lookup. Unique GUID matching precedes safe current/legacy folder fallback;
remote folders persist through title changes. Mutations share the existing
client's semaphore, source GUIDs are deduplicated, and remote lists validate shape,
IDs and pagination before writes. Create sends full metadata with a valid status.
Custom status normalization from PR #16 is preserved. Startup and game-stop use
the same client; all synchronization paths honor ignore tags. Artwork remains a
warning independent of metadata success. See [contract](api-contract.md).

## Save and embedded-view decisions

Save lookup pages beyond 200 games. Configuration writes are atomic and errors
propagate. Locations are validated; file type persists. Restore stages and validates
before durable backups and commit, with rollback for ordinary errors/cancellation.
Backups remain in extension user data. Automatic download pauses launch and runs
asynchronously; the next successful user launch proceeds. Per-location history
and fingerprints are persisted after successful transfers. The existing view is
reachable and reports normal browser-auth limitations; no API key is put into
URLs, cookies, storage or JavaScript. See [save internals](save-sync.md) and
[embedded view](../user-guide/embedded-view.md).

## Evidence and limits

Automated validation compiles the extension, exercises actual production helpers
with HTTP/filesystem fixtures, builds strict documentation, and inspects actual
PEXT contents. Windows CI additionally runs the same harness on .NET Framework.
These checks do not constitute Playnite runtime validation or live host end-to-end
testing. Actual Playnite screenshots remain unavailable and are documented
individually in the [screenshot record](../user-guide/screenshots.md).

The open AFK PR #6 has no direct dependency on this work and is left untouched.
No Playnite SDK model, host contract, runtime dependency, or second synchronization
architecture is introduced. Version 0.1.2 remains unreleased. See
[release gates](releases.md).
