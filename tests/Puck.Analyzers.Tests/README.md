# Puck.Analyzers.Tests

This xUnit suite tests [Puck.Analyzers](../../src/Puck.Analyzers/README.md) using
in-memory Roslyn compilations. It checks the strict-enum,
unmanaged function-pointer, environment-read, any-address-listener, GPU-trait and
`InternalsVisibleTo` rules. The function-pointer
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

## Documentation

📚 [Puck.Analyzers](../../src/Puck.Analyzers/README.md) · 🛠️ [Development](../../docs/development/README.md)
