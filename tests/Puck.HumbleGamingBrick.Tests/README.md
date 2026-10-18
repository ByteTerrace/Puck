# Puck.HumbleGamingBrick.Tests

This xUnit v3 suite proves the shared battery's hang guard on an injected clock: a window with no finished stage and
no processor time abandons the run, and a stage that keeps working is never abandoned. A layout law pins every
component's snapshot bytes: each mutable field is seeded from its own name, the saved bytes must match the recorded
length and SHA-256, and loading them into a differently seeded twin must reproduce them exactly.

The Humble GamingBrick Post battery is not run here. Its gate lane runs on every change in CI's runtime verification
workflow and from the battery project
([Post harness and conformance testing](../../docs/emulation/hgb/post-and-conformance.md#execution-lanes)), which
also owns hardware accuracy, corpus provenance, and stage limits.

## Verification

```powershell
dotnet test tests/Puck.HumbleGamingBrick.Tests/Puck.HumbleGamingBrick.Tests.csproj -c Release
```

## Documentation

📚 [Machine emulation manual](../../docs/emulation/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
