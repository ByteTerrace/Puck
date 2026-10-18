# Puck.Cli.Shaders.Tests

These laws hold `puck shaders` and the shared shader build targets it drives. The classes over
`ShaderBuildTargetsLaws` (`ShaderBuildTargetsLawTests`, `ShaderBuildBytecodeLawTests`, `ShaderSourceChangeLawTests`,
`ShaderOutputChangeLawTests` and `ShaderBuildLifetimeLawTests`) run the shared shader targets over isolated
projects through `ShaderBuildFixture`, with a CPU-only compiler stand-in, each class beside the others. Every
`dotnet msbuild` run costs seconds, so they hold what only the targets decide: which input changes rerun the build,
backend toggles, restored include inputs, temporary cleanup, the publication lock, the orphan sweep, the build task's
request files, cancellation and core requests, and collection of existing Direct3D 11 kernels without compiling
during pack. The shader build's own decisions (cache publication, include closures, why a check refuses an output)
are held in-process by `ShaderBuildLawTests` in `Puck.Shaders.Tests`. `ShadersCompareLawTests` holds `puck shaders compare` and
`collect` to exactly the SPIR-V. `ShaderBuildCoreProtocolLawTests` holds the core brokers the build task and
the generator share. `ShaderDeclarationBuildLawTests` runs the generator targets with the built
`Puck.Shaders.Generator` host. It checks repair of drift and missing declarations and unchanged file times. The suite references `Puck.Cli.Shaders` and `Puck.SdfVm`, whose generated kernel
interfaces the generate laws hold on the real tree.

Shared fixtures (`ConsoleCapture`, `GitScratchCheckout`, the thread-pool floor and kin) live in [`tests/Shared/Cli`](../Shared/Cli/README.md) and are linked into each suite that uses them.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.Cli.Shaders.Tests/Puck.Cli.Shaders.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Puck.Cli.Shaders](../../src/Puck.Cli.Shaders/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
