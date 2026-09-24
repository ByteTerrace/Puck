# Dashboard host

This package is the dashboard's browser shell. It provides authentication,
the host service context, and the module federation host used by the portal.
`main.tsx` creates the page's MSAL instance, token credential, and account
setup actor (`onboardingMachine`), and `HostProvider.tsx` hands them to the
portal. Until the portal arrives, `LoadingScreen.tsx` shows the Puck
mark on the brand ground from `src/host.css`, which follows the system color
scheme.

Use the [dashboard development workflow](../../README.md#run-and-check) to
install dependencies and run the applications. This package's `dev`, `build`,
and `preview` scripts are declared in [package.json](package.json). The Vite
configuration owns local ports and proxy routes; `src/remote.tsx` registers
and loads the portal. The [federation contract](../../README.md#federation)
describes what the host and portal share.

## Documentation

📚 [Dashboard web workspace](../README.md) · 🛠️ [Development](../../../../docs/development/README.md)
