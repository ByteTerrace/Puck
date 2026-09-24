# Puck.Analyzers.Tests

This xUnit suite tests [Puck.Analyzers](../../src/Puck.Analyzers/README.md) using
in-memory Roslyn compilations and workspaces. It checks diagnostic contracts,
manifest integrity and ownership, declaration coverage, fingerprints,
the ratchet ledger and its file-length and comment-smell rules, strict-enum,
unmanaged function-pointer and environment-read rules, and code-fix behavior. The function-pointer
cases also call real unmanaged function pointers to show that a pointer to a type parameter and a pointer-typed view of a generic
signature work, and a by-reference type parameter throws.

## Verification

From the repository root, run in PowerShell or another shell:

```powershell
dotnet test tests/Puck.Analyzers.Tests/Puck.Analyzers.Tests.csproj -c Release
```

A successful run reports passing tests and exits with code zero. The harness
loads the analyzer as an ordinary assembly so each test controls its execution;
it does not also attach the analyzer to the test project as a compiler plugin.
The suite embeds the repository's real verified-code marker attribute.

## Documentation

📚 [Puck.Analyzers](../../src/Puck.Analyzers/README.md) · 🛠️ [Development](../../docs/development/README.md)
