# Puck.World.Protocol.Tests

This xUnit v3 suite targets `net10.0` and checks the wire shapes owned by `Puck.World.Protocol`. Its law tests cover authority, batch mutations, deferred verb echoes, link queries, mutation kind masks and outcomes, peer addresses, reflow proposals and queries, and row-scoped subjects.

## Verification

Run the focused suite from the repository root:

```powershell
dotnet test tests/Puck.World.Protocol.Tests/Puck.World.Protocol.Tests.csproj -c Release
```

The project references `Puck.World.Protocol`; server execution and transport behavior are verified by their owning project checks.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
