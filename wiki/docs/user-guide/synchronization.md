# Library synchronization

## Full and selected-game synchronization

Use **Upload Playnite Library** in extension settings, or **Sync library to
Unnamed Tracking** in Playnite's main menu. For a subset, select games and use
**Unnamed Tracking → Sync selected games to Unnamed Tracking** in the game
context menu. Selected-game sync uses the same client, matching, ignore filter,
and cancellation rules as full sync.

The extension sends Playnite metadata and state to your authenticated account.
It creates missing games and updates linked games. This is one-way library sync:
it does not import remote edits into Playnite. A subsequent upload can overwrite
remote edits to the mapped fields. Other application-only fields are left alone.

GUID matching keeps the game linked when its title changes. The remote storage
folder stays stable after linking. Ambiguous duplicate GUIDs or folders belonging
to another GUID are reported as failures; they are not guessed by title.

Playtime is cumulative seconds, favourites retain their boolean state, and
completion statuses are normalized to the host's finite enum. Unknown custom
Playnite statuses become `BACKLOG`, even when playtime is nonzero. Only an absent
or blank status uses playtime to choose `PLAYED` or `BACKLOG`. The
[API mapping table](../developer-guide/api-contract.md) lists every mapped field.

Available covers and backgrounds are uploaded as key art and banner. Artwork
failures appear as warnings after a successful metadata update; they can be
retried by running another library sync.

## Preview

Click **Preview sync** to see totals and game names for creates, updates, and
ignored entries. Preview reads the remote library but performs no creates,
updates, relationships, artwork uploads, or save transfers. It is a point-in-time
comparison; the next actual sync reads the remote library again.

## Progress and cancellation

Settings display the current game and completed/total count. Menu synchronization
and startup synchronization use Playnite notifications for progress or results.
A second settings/menu sync is rejected while a sync or preview is active.

Use **Cancel** in settings, **Cancel sync** in the Unnamed Tracking sidebar, or
**Cancel Unnamed Tracking synchronization** in the main menu. Cancellation aborts
in-flight API requests and prevents subsequent operations. Requests have a
60-second deadline. Changes already accepted by the server are retained; library
sync is not a multi-game transaction. Run sync again to reconcile partial progress.
Artwork warnings, game failures, and cancellation are distinct results.

## Startup sync

Enable **Sync the library when Playnite starts** to run the same full sync after
startup. It can create games as well as update them. SDK data is captured on the
Playnite context; network requests run asynchronously. Startup results and errors
appear in notifications. Cancel from the main menu or sidebar if needed.

## Game-stop sync

Enable **Sync a game after it stops** to update a linked game's mapped metadata,
playtime, favourite, status, and relationship after a session. It does not create
an unlinked game and does not re-upload artwork. Use full or selected-game sync
first. This setting is independent of per-game save upload-on-stop.

## Ignored games

Games with the configured tag are skipped by full sync, selected sync, preview,
game-stop sync, manual save sync, and automatic save transfers. Tag comparison
trims names and ignores case. Clear the ignore-tag setting to disable filtering;
an empty value remains empty after restarting Playnite. Ignoring does not delete
previously uploaded games or archives. Local save configuration can still be
edited for an ignored game.
