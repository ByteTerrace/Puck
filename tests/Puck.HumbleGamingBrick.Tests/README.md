# Puck.HumbleGamingBrick.Tests

This xUnit v3 suite proves the shared battery's hang guard on an injected clock: a window with no finished stage and
no processor time abandons the run, and a stage that keeps working is never abandoned. A round-trip law seeds every
mutable field of each component from its own name, saves it, loads the bytes into a differently seeded twin and
requires the twin to save the same bytes with the reader consumed exactly, so a field saved but not loaded fails; no
layout or digest is recorded.

The self-contained-battery fact launches the Humble GamingBrick Post battery's real executable with an empty
corpus cache, whatever this machine has fetched: every self-contained stage runs, every corpus stage skips, and the
run must exit 0 and leave its JUnit report. The corpus suites run in CI's runtime verification
workflow and from the battery project
([Post harness and conformance testing](../../docs/emulation/hgb/post-and-conformance.md#running-the-battery)), which
also owns hardware accuracy, corpus provenance, and stage limits.

## Verification

```powershell
dotnet test tests/Puck.HumbleGamingBrick.Tests/Puck.HumbleGamingBrick.Tests.csproj -c Release
```

## Documentation

📚 [Machine emulation manual](../../docs/emulation/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
