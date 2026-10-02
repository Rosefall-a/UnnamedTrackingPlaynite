# Troubleshooting

| Symptom | Check or action |
| --- | --- |
| Connection test fails with 401 | Generate a current user `utk_` key for the intended active account; a revoked key cannot authenticate. |
| 403 | Check account permissions and that you did not use a plugin-management token. |
| 404, redirect rejection, or HTML instead of JSON | Use the final server base URL, including any reverse-proxy prefix; omit `/api` and login redirects. |
| TLS or unreachable server | Check the URL, trusted certificate, server availability and Windows networking. Do not disable certificate validation. |
| 409 duplicate folder | Resolve the conflicting remote entry; GUIDs are not replaced by title matching. |
| 422 on metadata | Inspect the per-game error. Host field lengths and ranges still apply; correct oversized/invalid Playnite metadata. |
| More than one remote game shares a GUID | Decide which remote entry is authoritative and remove/relink the duplicate in Unnamed Tracking before retrying. |
| Artwork warnings | Check local image files, remote image URLs, server limits, and key-art/banner endpoints. Metadata success is retained. |
| No save transferred | Check configured paths, files, ignore tag and whether a remote version exists. Empty locations are skipped. |
| Local save error or rejected path | Check permissions, drive availability, unresolved variables, overlapping paths and junctions. Reconfigure an actual save directory. |
| Game launch paused | Wait for the cloud-save notification and launch again after success; after failure retry or disable download-on-start. |
| Save restore failed | Local rollback is attempted. Preserve the reported backup/recovery manifest and follow the recovery guide. |
| Sidebar says up to date but another PC uploaded | The dashboard compares local content against its last successful fingerprint; download explicitly to check remote changes. |
| Browser requests login despite successful connection test | API-key authentication and web session authentication are separate. Sign in normally or use your usual browser. |

Library cancellation keeps server changes already completed. Retry reconciles by
GUID. Save cancellation rolls back a restore that has started committing files,
when the process is able to finish rollback. See [save recovery](saves.md).

Playnite records the extension logger in its profile logs. Logs include request
IDs, endpoints, timings and HTTP status, without request/response bodies or API
keys. Settings show per-game HTTP errors and artwork warnings. Redact personal
game names, paths, server addresses and account data before sharing diagnostics.
The currently configured key is redacted if the server echoes it in an API error.

A clean package contains `extension.yaml` and `UnnamedTrackingPlaynite.dll` at its
root. Do not install development backups or private Playnite assemblies. If an
installation fails, see [packaging validation](../developer-guide/build.md).
