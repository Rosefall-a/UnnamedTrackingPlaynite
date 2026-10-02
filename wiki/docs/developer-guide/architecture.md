# Architecture and the Playnite SDK boundary

The extension has one existing synchronization client and one existing save
manager. Both consume the application's authenticated game REST API. It remains
a `GenericPlugin` on PlayniteSDK 6.17.0, targeting `net462`; no private Playnite
assemblies or new runtime dependencies are added.

| Component | Responsibility |
| --- | --- |
| `UnnamedTrackingPlugin` | SDK entry point, menus/sidebar, events, notifications and operation lifetimes |
| `UnnamedTrackingSettings` / settings view | Persist connection/library preferences, test, preview, progress and cancellation |
| `UnnamedTrackingSyncClient` | Capture detached library values, normalize metadata/status, page the remote library, match, create/update and upload artwork |
| `SaveSyncManager` | Existing per-game save configuration, GUID resolution, archive/version transfer and content fingerprints |
| `SaveSyncStore` | Normalize old configuration, return detached copies, atomically persist local configuration |
| `SaveArchive` | Focused local path/ZIP validation, staging, backup and rollback helper used by the save manager |
| `ApiConnection` | Shared URL, redirect, deadline and cancellation policy for the existing transports |
| `SaveSyncDashboard` / `SavePathDialog` | Existing local save UX and access to the existing embedded web view |

`SyncGameSnapshot` is a detached copy of metadata, ignore state and resolved image
references. Capture SDK objects and resolve Playnite database image paths on the
supported UI/event context before awaiting network operations. No SDK `Game`,
related collection, or database method is consulted in library worker continuations.
Save methods read game ID/name before scheduling workers; dashboard rows capture
SDK data before background filesystem scans. UI actions use Playnite's dispatcher.

The snapshot/helper boundary is not another synchronization architecture. Keep
new behavior inside the existing clients and helpers. Do not replace SDK models,
introduce a gateway or host runtime, access the host database/filesystem, or copy
application models into this repository. See [threading](threading.md),
[API contract](api-contract.md), and [save internals](save-sync.md).
