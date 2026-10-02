# Embedded Unnamed Tracking view

Open the **Unnamed Tracking** sidebar, then **Open Unnamed Tracking**. The existing
Playnite web view opens the configured server. The local save dashboard remains
the sidebar's primary view; it is not replaced by a redesigned integration.

The server URL is validated before navigation. HTTP and HTTPS are supported;
credentials, query parameters, fragments, and other schemes are rejected. The
extension never appends an API key, injects a browser cookie, or puts the API key
in browser storage or JavaScript.

The web UI uses its normal browser sign-in/session-cookie flow. Successful API
connection testing does not sign you into the embedded browser. Authenticate
normally if supported. SSO/OIDC popups, redirects, or an identity provider's
browser restrictions may not work in Playnite's embedded engine. Use your normal
browser if that happens; API-key synchronization remains independent.

A missing web-view implementation or navigation/open exception produces a clear
error and a browser fallback suggestion. A server unavailable after navigation
may produce the embedded engine's own error page; this is not a successful API
connection test. No insecure authentication workaround is provided. On shutdown,
the extension disposes its created web view.
