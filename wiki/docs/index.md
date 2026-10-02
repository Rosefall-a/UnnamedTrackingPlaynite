# Unnamed Tracking for Playnite

Version **0.1.2** is the next, unreleased companion update. The extension remains a
Playnite 10 generic plugin targeting .NET Framework 4.6.2 with PlayniteSDK 6.17.0.
It synchronizes your Playnite library to an Unnamed Tracking account, manages
configured save archives, and provides access to the application's website.

Start with [installation and configuration](user-guide/installation.md), then use
a [preview](user-guide/synchronization.md) before your first full upload. Save
synchronization is separately enabled per game; read the
[save and recovery guide](user-guide/saves.md) before turning it on.

Synchronization uses a normal user API key. This extension is installed in
Playnite as a `.pext`, and is not an Unnamed Tracking `.utp` plugin. Plugin
management tokens and gateway permissions are a separate application feature.

The [developer guide](developer-guide/architecture.md) describes the existing
client, SDK boundary, transport, tests, packaging, and release process.
The [audit](developer-guide/audit.md) records contract evidence and limitations.
Actual Playnite screenshots and runtime validation are currently unavailable;
see the [screenshot record](user-guide/screenshots.md).
