# Puck.GamingBricks.Tests

This xUnit v3 suite checks the shared GamingBricks host substrate: rational
rate pacing, boot options and content admission, queued worker lifecycle,
snapshots, and linked-machine disposal and pacing. Console-specific CPU and
hardware behavior belongs to the Humble and Advanced suites.

## Verification

```powershell
dotnet test tests/Puck.GamingBricks.Tests/Puck.GamingBricks.Tests.csproj -c Release
```

## Documentation

📚 [Machine emulation manual](../../docs/emulation/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
