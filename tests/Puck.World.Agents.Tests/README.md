# Puck.World.Agents.Tests

This xUnit v3 suite targets `net10.0` and exercises the world agent boundary. It covers `WorldAgentBridge` principal stamping, typed observations and actions, channel refresh, receipts, bounded mailbox dispatch, cancellation, shutdown, and the `WorldAgentHarness` tool policy.

## Verification

Run the focused suite from the repository root:

```powershell
dotnet test tests/Puck.World.Agents.Tests/Puck.World.Agents.Tests.csproj -c Release
```

The project references `Puck.World.AgentBridge`, `Puck.World.AgentHarness`, `Puck.World.Protocol`, and `Puck.World.Schema`; it does not require a model provider or a live world host.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
