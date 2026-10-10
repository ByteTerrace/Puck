# Puck.Embeddings.Tests

These laws hold the OpenAI embedding generator: the request shape and its identity bearer token, an omitted dimensions field, the default Azure credential, refusal of a count or length mismatch, reordering of out-of-order indexes, an error response that keeps its status and body without the token, and batch splitting.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Embeddings.Tests/Puck.Embeddings.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Embeddings](../../src/Puck.Embeddings/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
