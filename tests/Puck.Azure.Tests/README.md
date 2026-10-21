# Puck.Azure.Tests

This xUnit v3 suite targets `net11.0` and verifies the shared Azure host integration in `Puck.Azure`. It checks that a
stopping host delivers its final Azure Monitor telemetry before its shutdown returns through a stalling ingestion
endpoint, and that an endpoint that never answers holds the shutdown only until the transmission times out.
It also exercises the Functions key-binding service with generated keys: a caller cannot renew another subject's
binding or substitute its key or algorithm, while an owner can renew their own expired binding.
Storage-response tests prove key material reads stop one byte past their bound even when a server ignores the range.

## Verification

Run the focused suite from the repository root:

```powershell
dotnet test tests/Puck.Azure.Tests/Puck.Azure.Tests.csproj -c Release
```

The ingestion endpoint is an in-process HTTP handler, so the tests need no Azure account and make no network calls.

## Documentation

📚 [CI and releases](../../docs/development/ci.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
