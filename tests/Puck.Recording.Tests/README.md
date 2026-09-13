# Puck.Recording.Tests

This suite checks the Matroska seek-head recovery contract in
`MatroskaSeekHeadRecoveryLawTests`: an interrupted recording keeps its
reserved seek-head slot recoverable, and a clean stop writes the finalized
seek head at that slot.

## Running

```powershell
dotnet test tests/Puck.Recording.Tests/Puck.Recording.Tests.csproj -c Release
```

## Documentation

📚 [Puck.Recording](../../src/Puck.Recording/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
