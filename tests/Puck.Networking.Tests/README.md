# Puck.Networking.Tests

This suite exercises bounded wire framing and the peer networking substrate.
It covers frame readers and writers, handshake encoding and refusal, endpoint
capabilities, persistent request lanes, stream draining, and peer lifecycle
fixtures including delivery and closure behavior.

## Running

```powershell
dotnet test tests/Puck.Networking.Tests/Puck.Networking.Tests.csproj -c Release
```

## Documentation

📚 [Puck.Networking](../../src/Puck.Networking/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
