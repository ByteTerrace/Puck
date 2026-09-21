# Dashboard web workspace

This npm workspace builds the dashboard's host and portal. Start with
[Puck.Dashboard](../README.md) for setup, the official content server, and
verification commands; that page owns the complete development workflow.

| Directory | Purpose |
|---|---|
| [Host](host/README.md) | Browser shell, authentication, and shared service context |
| [Portal](portal/README.md) | World Studio and the authenticated portal pages |
| [Shared](shared/README.md) | TypeScript contracts and helpers imported by both applications |

The root package is private. Its npm workspace list contains `host` and
`portal`; `shared` is consumed through source imports. Keep changes to shared
contracts compatible with both applications.

## Documentation

📚 [Puck.Dashboard](../README.md) · 🛠️ [Development](../../../docs/development/README.md)
