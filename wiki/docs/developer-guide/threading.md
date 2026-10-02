# Threading, progress and cancellation

Playnite SDK reads belong to its supported UI/event context. `Capture` builds
plain payloads, ignore state, game names/IDs, and resolved image references before
the client first awaits. Startup calls that same entry point from the event;
it does not wrap SDK access in `Task.Run`. File/image reads and save hashing run
on workers. Network calls use asynchronous WebRequest/WebClient APIs with
`ConfigureAwait(false)` inside transport code. UI orchestration awaits on its
context, and notifications/progress use the dispatcher.

The existing sync client has one mutation semaphore, covering remote list,
match and mutation. Snapshot capture happens before waiting for it. A single
plugin-level cancellation source covers a settings/menu/startup sync or preview;
concurrent requests are rejected with an actionable message. Game-stop updates
also use the client's mutation semaphore. The save manager serializes save
operations, uses detached configurations, and rejects configuration writes while
busy. Its store locks only snapshot/commit sections, not remote requests.

Cancellation scopes pass the current token through existing transports. A request
registration aborts `HttpWebRequest` on cancellation or after 60 seconds. Artwork
WebClients use cancellation registrations. File staging loops and commits check
tokens; a cancelled commit invokes rollback. Lifetime cancellation is triggered
by `OnApplicationStopped`, also cancelling active save and library sources.
Do not synchronously wait for network work in that event or on the UI thread.

Library progress reports completed/total and game name; dispatcher updates keep
settings responsive. Preview exposes the same Cancel action. Settings format both
failures and artwork warnings. Automatic errors are notifications rather than
log-only events. Cancel retains writes already accepted by the server.

Automatic save download cannot run fire-and-forget alongside game startup. The
supported `CancelStartup` flag pauses launch, asynchronous download posts its
result, and a one-use readiness flag permits the next user launch. Manual
downloads also block launch for that game while active. No UI-thread wait or
unsupported auto-launch/authentication shortcut is introduced. These event flows
still need the [Windows runtime checklist](../user-guide/screenshots.md).
