# Puck.Cli.Tests

This xUnit suite tests [Puck.Cli](../../src/Puck.Cli/README.md), including
formatting, authored-content tooling, canary accounting, parity comparison,
process handling, branding asset synchronization, and MCP behavior. Individual fixtures define their inputs
and service or process setup; the suite is not a replacement for running a
hardware-dependent CLI operation in its intended environment.

## Verification

From the repository root, run in PowerShell or another shell:

```powershell
dotnet test tests/Puck.Cli.Tests/Puck.Cli.Tests.csproj -c Release
```

A successful run reports passing tests and exits with code zero. Use the
framework's test filter to narrow an investigation. Keep the test project's
fixtures with the test when evaluating what a passing result establishes.
The [CLI reference](../../src/Puck.Cli/README.md) owns command syntax and
operational prerequisites.

## Documentation

📚 [Puck.Cli](../../src/Puck.Cli/README.md) · 🛠️ [Development](../../docs/development/README.md)
