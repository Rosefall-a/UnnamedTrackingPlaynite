# Screenshot availability and runtime verification

No Windows Playnite installation, desktop session, user account/browser session,
or real game/save library is available in this Linux build environment. No real
Playnite screenshots were obtained for this change. Generated illustrations,
mock UI renders, and screenshots of a different application would not demonstrate
this extension, so none are presented as evidence.

| Required view | Current evidence | Capture procedure on Windows |
| --- | --- | --- |
| Extension settings | Unavailable | Open Add-ons → Extension settings → Unnamed Tracking; conceal the key and personal server address. |
| Sync preview | Unavailable | Use a small library with one linked, one new and one ignored game; click Preview sync. |
| Sync progress | Unavailable | Run the same library sync and capture current game/count plus the Cancel control. |
| Game context menu | Unavailable | Right-click selected games and expand the Unnamed Tracking and Save sync submenus. |
| Save sync UI | Unavailable | Capture the sidebar and configuration dialog with test folders and enabled/disabled flags. |
| Embedded Unnamed Tracking view | Unavailable | Open the website from the sidebar and use a real ordinary sign-in; redact account information. |

For each future screenshot record extension version, Playnite version, Windows
version, capture date, and action shown. Save sanitized PNGs under
`wiki/docs/assets/screenshots/`, add descriptive alt text on the relevant guide
page, and replace this availability row only when the screenshot exists.

## Manual runtime acceptance checklist

Use disposable test saves and a test account. Verify settings persistence,
connection errors, full/selected sync, rename without duplication, custom status
normalization, ignore tags, preview with no writes, progress/cancel, artwork
warnings, startup and game-stop events. Test multiple folders and a single file,
automatic upload versioning, launch pause and the second launch, cancel/failure
with original saves intact, shutdown during transfer, and normal browser sign-in
and unavailable-server behavior. Confirm PEXT installation and extension reload.

Headless helper tests and compilation do not establish actual Playnite event,
WPF, notification, CEF, or install behavior. This task makes no claim of full
Playnite runtime testing. The checklist remains an explicit release gate.
