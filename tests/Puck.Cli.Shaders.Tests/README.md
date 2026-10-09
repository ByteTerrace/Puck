# Puck.Cli.Shaders.Tests

These laws hold `puck shaders` and the shared shader build targets it drives. The classes over
`ShaderBuildTargetsLaws` (`ShaderBuildTargetsLawTests`, `ShaderSourceChangeLawTests`, `ShaderOutputChangeLawTests`,
`ShaderCompilerConcurrencyLawTests` and `ShaderBuildLifetimeLawTests`) run the shared shader targets over isolated
projects through `ShaderBuildFixture`, with a CPU-only compiler stand-in, each class beside the others. They check
restored include inputs, unchanged builds, temporary cleanup, the publication lock, refusal of missing compiler
outputs, the build task's request files, cancellation and core requests, and collection of existing Direct3D 11
kernels without compiling during pack. `ShaderBuildCoreProtocolLawTests` holds the core brokers the build task and
the generator share. `ShaderDeclarationBuildLawTests` runs the generator targets with the built
`Puck.Shaders.Generator` host. It checks repair of drift and missing declarations, unchanged file times, and explicit
checking without generation. The suite references `Puck.Cli.Shaders` and `Puck.SdfVm`, whose generated kernel
interfaces the generate laws hold on the real tree.

Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Shaders.Tests/Puck.Cli.Shaders.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Cli.Shaders](../../src/Puck.Cli.Shaders/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
