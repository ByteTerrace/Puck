# byteterrace-host

This package is the dashboard's browser shell. It provides authentication,
the host service context, and the module federation host used by the portal.
`HostProvider.tsx` connects the MSAL account, token credential, onboarding,
and shared store.

Use the [dashboard development workflow](../../README.md#run-and-check) to
install dependencies and run the applications. This package's `dev`, `build`,
and `preview` scripts are declared in [package.json](package.json). The Vite
configuration owns local ports, proxy routes, and federation settings.

## Documentation

📚 [Dashboard web workspace](../README.md) · 🛠️ [Development](../../../../docs/development/README.md)
