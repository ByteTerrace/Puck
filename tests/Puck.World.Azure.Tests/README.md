# Puck.World.Azure.Tests

This xUnit v3 suite targets `net10.0` and verifies the optional Azure Resource Manager extension against scripted in-memory services. The tests cover application health, delegated observations, resource inventory and metrics, scheduled events, resource operations, and Entra world authentication, including pagination, disclosure, preconditions, continuation, URL containment, byte limits, and ambiguous failures.

## Verification

Run the focused suite from the repository root:

```powershell
dotnet test tests/Puck.World.Azure.Tests/Puck.World.Azure.Tests.csproj -c Release
```

The project references only `Puck.World.Azure`; tests do not require an Azure account or perform live resource changes.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
