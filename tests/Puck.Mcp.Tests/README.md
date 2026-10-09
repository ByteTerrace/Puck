# Puck.Mcp.Tests

These laws hold the MCP server, its remote proxy and its hosting over a fixture World server: standard input that ends
only at a well-formed end of file, a blocked standard output's deadline and bounded reply queue, cancellation, closed
and followed attachments, hosted configuration and readiness, and grant revocation and the validated caller. The `puck mcp` process interop laws run the composed
`puck` tool and live in [`Puck.Cli.Tests`](../Puck.Cli.Tests/README.md).

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Mcp.Tests/Puck.Mcp.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Mcp](../../src/Puck.Mcp/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
