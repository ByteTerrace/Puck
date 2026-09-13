# Contributing to Puck

This guide explains how to investigate, change, and verify Puck. Use the
[project map](../project-map.md) for project ownership and dependency rules,
the [architecture guide](../architecture/README.md) for the engine's boundaries,
and the [CI guide](ci.md) for hosted validation and release procedures.

## Start here

- A run uses a versioned `puck.world.def.v1` JSON document. CLI conveniences
  synthesize the same model; they do not create a second execution path.
- Vulkan and Direct3D 12 implement the same neutral GPU contracts. Shared GPU
  changes must be verified on both backends.
- `Puck.World` composes the running application. Run it to verify rendering and
  complete game interaction. The World test suite covers document, protocol,
  authoritative simulation, and shipped game state programs; other checks below
  cover their own specific contracts.
- Emulator cores live under `src/` (`Puck.HumbleGamingBrick`, `Puck.AdvancedGamingBrick`)
  with hosting folded into the cores. Each core has its own POST battery.
- Authoritative simulation uses fixed-point values and per-tick command
  snapshots. Wall-clock time, ambient randomness, and floating-point state do
  not enter replay-bearing simulation.

## Find code and references

Use text search for file discovery, literals, JSON, HLSL, and project files. Use the
compiler for questions such as who references a symbol, what implements an
interface, whether code is unused, or whether a rename is safe. Text matching
misses extension methods, aliases, overload resolution, generated code, and XML
`cref` references.

Prefer the cheapest correct tool:

1. Puck search for orientation and non-C# text.
2. `puck declarations` for declaration, member, attribute, base-list, and XML-doc
   inventories—parse-only, no build.
3. `puck references` for cross-project symbol questions: references,
   implementers, overrides, derived types, dead-code candidacy.
4. `dotnet build Puck.slnx -c Release` after a refactor or documentation edit
   that changes `cref` values.

## File paths

Use `/` in authored paths, configuration, stored path identities, and path output,
including on Windows. Prefer current `System.IO` APIs that accept these paths
directly; do not convert `/` to the platform separator before file access.
Normalize platform-produced paths to `/` at Puck's output and storage boundaries.
Keep resolution semantics explicit: `Path.Combine` can replace an earlier base
with a rooted argument, whereas `Path.Join` only joins components. They are not
interchangeable. Preserve native device-path syntax only at an interop boundary
that requires it; backslashes in string escapes and regular expressions are not
file separators.

## C# file apps

Repository automation is Puck CLI: `puck --help` lists the verbs and
`puck <verb> --help` its options. Deployment, publishing, QUIC probes, and WASM
refresh are `puck` verbs, not scripts and not file apps.

`src/Puck.Azure.Resources/bootstrap.cs` is the repository's one C# file app, an
identity-team operation run outside CI. It uses the same `.editorconfig`,
compiler warnings, and Puck formatting conventions as the project-based code;
its file directives declare its dependencies, so keep package versions pinned.
`Directory.Build.props` links `build/RepositoryPaths.cs` into it so it can locate
checkout data at runtime without building Puck CLI. Invoke it from within the
checkout. Compiler source paths can be remapped by CI and are not runtime file
locations.

Compile it without executing its operational code:

```sh
dotnet build src/Puck.Azure.Resources/bootstrap.cs -c Release
```

Use `puck format . -Files files.json` with a JSON array of repository-relative
paths, such as `["src/Puck.Azure.Resources/bootstrap.cs"]`. The CLI converts a
standalone app to a disposable SDK project, preserves its references and linked
helpers, compiles and formats the copy, then copies back only the selected source
with its file directives restored. `-WhatIf` and `-Verify` leave the original
source untouched. Ordinary project files still need their owning projects restored
and built. See [automatic PR formatting](ci.md#automatic-pr-formatting) for the CI
bot and the fork-PR patch path. Never run a repository-wide sweep to fix one entry
point.

## Verification

### Engine changes and verification coverage

`Puck.Post` remains quarantined under `experimental/Puck.Post` and outside the
solution. Its historical stages are prior art, not current verification. Use
the live checks appropriate to the changed contract and report what remains
untested; no single check covers the complete engine.

The [World tests](../../tests/Puck.World.Tests/README.md) cover documents,
protocol, authoritative simulation, and shipped game state programs. The
architecture gate runs during builds, including `PUCKARCH008`: it rejects a
compiled dependency denied by `PuckArchitectureDeniedApi` in
`build/Architecture.props`, such as `System.Console` from `Puck.World.Server`.
The emulator batteries, Maths laws, build, and running application provide
additional checks with distinct scopes.

`puck parity` boots the [authored parity world](../../tests/Puck.Parity/README.md)
offscreen once on Vulkan and once on Direct3D 12. Tick-scheduled captures receive
three verdicts: valid content, exact simulation-state hash agreement, and pixel
agreement under per-tile thresholds. Missing content or a camera inside geometry
fails before comparison; state and pixel checks are evaluated separately after
the content check passes. A failure records frames, a delta heatmap, and verdicts.

This comparison uses the contract beside the parity world, not a stored image
baseline. It requires both GPU backends but does not take over a display. Use it
for render-path, shader, presenter, or capture changes. Its authored stations
exercise specific contracts; passing them does not establish correctness for
every possible scene. See `puck parity --help` for the current command surface.
For changes under `src/Puck.Maths`, also run the maths law suite. The default
tier is the everyday gate; `deep` and `exhaustive` are the opt-in volumes:

```powershell
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/default.runsettings
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings
```

### Game changes

Run the application to check composed game behavior, alongside relevant World tests:

```powershell
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 2
```

Use `Puck.World` for game verification. `Puck.Demo`, the
composition-root-less library that once sat quarantined at
`experimental/Puck.Demo`, is deleted—each folder's capability either has a
live successor in `Puck.World` (see `experimental/README.md`) or is simply
absent from it, with no plan bringing it over. Do not add a `--validate-*`
mode or a `Puck.Post` stage for game-specific behavior unless explicitly
requested.

The console and process stdin use the same command registry. Accepted results
are written to stdout and refusals to stderr. A driver that needs their combined
arrival order can merge the streams. End a run with `wire.errors` and check
`[wire.errors: 0 rejected]` when the result depends on every command being accepted.
The [World guide](../../src/Puck.World/README.md) owns the current scripting and
replay procedures. Historical console proofs in the quarantined engine harness
are not an executable verification target.
The `review-creation` scenario—isolated creation turntables with pinned content
time and camera poses—has no runnable host today, and no plan to get one. It
does not exist in `Puck.World`; the document that scheduled the move was deleted
with the quarantine. Treat creation review as an absent capability, not a
pending one.

### Emulator changes

```powershell
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --fetch-corpora
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --lane gate --artifacts artifacts/hgb-post
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release -- --fetch-corpora
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release -- --bios <GBA_bios.rom>
```

The Humble battery's reference corpora are declared in its `corpora.json`
(pinned archive, version, SHA-256); `--fetch-corpora` fills the local cache
once and the stages resolve it without configuration. `--lane gate` measures
every row recorded as passing and must stay green; `--lane frontier` measures
the recorded fails and inconclusives; a plain run measures both. The recipe above
matches CI's `artifacts/hgb-post` directory for `summary.json`, `results.junit.xml`,
and the candidate ledger; `--accept` promotes the candidate under the refusal rules
in the project README. Iterate with `--filter`; never chain runs to record.
The Advanced battery works the same way: its corpora are pinned in its own
`corpora.json`, the BIOS and commercial cartridges are command-line flags, and
it exposes lockstep, trace, I/O-dump, render-hash, and divergence diagnostics;
see its project README.

### Performance changes

There is no maintained cross-engine benchmark score. `Puck.Bench` remains
quarantined under `experimental/`; do not build or revive its old suite.

For a live World workload, use `world.timing on`, wait for rendered frames,
then sample `world.gpu` and `world.fps`. `world.budget` distinguishes live
program size from reserved capacity. Compare the same document, camera,
resolution, quality settings, backend and build configuration before and after
the change. Report these conditions and repeated samples with the result;
GPU-pass time alone is not the delivered frame rate. See
[World graphics options](../../src/Puck.World/README.md#graphics-options).

### Browser engine changes (`Puck.World.Browser`)

Remove `C_INCLUDE_PATH` from the build shell first if the machine has a Cosmocc toolchain installed
(see "Hardware and toolchain cautions" below)—otherwise every native asset
compile in the steps below fails with libc header collisions that have
nothing to do with the change under test.

```powershell
dotnet build src/Puck.World.Browser -c Release
dotnet test tests/Puck.World.Browser.Tests -c Release
dotnet publish src/Puck.World.Browser -c Release
```

`tests/Puck.World.Browser.Tests` links `Engine/*.cs` as source and runs under
the ordinary net10.0 test host—no wasm runtime needed to exercise the pure
core. The wasm-specific proof is the Node harness, which needs the AppBundle
the `dotnet publish` line above produces and Node reached through fnm, since
Node is not on `PATH` on the reference system
(`FNM_DIR="$APPDATA/fnm" fnm exec --using=26.5.1 -- node ...`):

```powershell
dotnet publish src/Puck.World.Browser -c Release
cd src/Puck.Dashboard/src/portal
$env:FNM_DIR = "$env:APPDATA/fnm"; fnm exec --using=26.5.1 -- node --test tests/engine-wasm.test.cjs
```

That harness skips itself by name (never silently passes) when the AppBundle
is absent. To re-record the determinism-canary baseline both the native tests
and the Node harness compare against:

```powershell
$env:PUCK_BROWSER_PARITY_RECORD = "1"
dotnet test tests/Puck.World.Browser.Tests -c Release --filter "FullyQualifiedName~BrowserParityRecordingTests"
Remove-Item Env:\PUCK_BROWSER_PARITY_RECORD
```

See `src/Puck.World.Browser/README.md` for the AppBundle's real file layout
and sizes, the exact `[JSExport]` surface, the trim-warning baseline, and the
one verified scope boundary (no emulator core, so a document authoring a
`screens[].source.machine` engine—the shipped island's arcade district among
them—refuses by name rather than crashing).

## Working with world documents

The validator checks the complete document's semantics. A valid document must be buildable;
builders do not repeat validator checks. When a document field or polymorphic
kind changes:

1. Update the nullable model and XML documentation.
2. Add all semantic validation to `WorldDefinitionValidator`, which runs over
   the entire composed candidate document rather than the changed section
   alone—including an owned identity's document, which is validated the
   same way.
3. Register the type in `WorldJsonContext`; a polymorphic kind also needs its
   `[JsonDerivedType]` line.
4. Verify by running `Puck.World` and round-tripping the document over stdin.

`src/Puck.World.Schema/README.md` documents the serializer's construction
behavior; the procedure above is the complete add-a-field procedure.

## Configuration and diagnostics

`Puck.World` does not use `PUCK_*` configuration variables. Durable
configuration belongs in the world document; live operations belong in console
verbs.

The remaining environment variables are engine, launcher, or
content-development diagnostics (both emulator batteries take their inputs on
the command line; see their READMEs):

| Variable | Purpose |
|---|---|
| `PUCK_GENLOCK=0` | Disable the launcher genlock control law. The document equivalent is `host.genlock`. |
| `PUCK_PRESENT_TIMING` | Log measured present intervals. |
| `PUCK_TEST_DEVICE_LOSS=<seconds>` | Request synthetic device loss for live verification. |
| `PUCK_D3D12_DEBUG` | Opt in to the Direct3D 12 debug layer. |
| `PUCK_FLAGSHIPS_REGENERATE=1` | Regenerate committed flagship creation documents. |
| `PUCK_AGB_BIOS`, `PUCK_ARES_COSIM`, `PUCK_AGB_FULLBOOT`, `PUCK_AGS_TRACE`, `PUCK_AGB_SUITE_FOCUS` | Read only by the Advanced battery's diagnostic modes (lockstep co-simulation, full-boot renders, AGS tracing, suite focus); the battery itself takes every input on the command line. |

GPU timing has no environment variable. Arm it with the `gpu.timing` feature
switch, the `world.timing` verb, `host.timing`, `--timing`, or the benchmark
harness.

## GPU support and shader builds

The supported GPU floor covers RTX 2070, RTX 4070, the RDNA3 Steam Machine,
and the RDNA2 Steam Deck. Shaders target Vulkan 1.3 / SPIR-V 1.6 and Shader
Model 6.6. Do not raise that floor without evidence for every supported GPU.

DXC compiles the same HLSL sources to SPIR-V and DXIL during the build. `dxc`
must be on `PATH` for these built-in kernels. Live one-off GLSL/Shadertoy
authoring uses the separate toolchain described in the [shader guide](../../src/Puck.Shaders/README.md#one-off-shaders). A change to the SDF C# ISA
must update the HLSL decoder in the same change. The SDF VM README lists
the exact C# and HLSL contract pairs and bytecode rebuild procedure.

Only the RTX 4070 is normally available for local testing. Claims about the
other supported GPUs require vendor or driver documentation and should be
framed as unverified when no device run exists.

## Hardware and toolchain cautions

- On the reference Windows/RTX 4070 system, enabling the Direct3D 12 debug
  layer can make `D3D12CreateDevice` fail with `0x887A0007`; it is opt-in.
- Vulkan import of a Direct3D 12 shared texture on NVIDIA uses handle type
  `D3D12_RESOURCE` (`0x40`).
- Direct3D 12 compute descriptor slots are packed in binding order. Derive
  pool sizes with `GpuDescriptorPoolSizes.ForSets`; do not treat a binding
  number as a heap offset.
- Full GPU removal can wedge the in-process NVIDIA Vulkan ICD. TDR recovery is
  supported; physical removal may require a new process.
- The live Pocket Camera path uses CPU pixels. The zero-copy camera export
  infrastructure is intentionally built ahead for re-hosting and remains
  covered by the synthetic `camera-share` stage.
- RADV may select wave32 or wave64. New wave-intrinsic kernels must be
  subgroup-size-independent or explicitly request a supported size.
- Incremental builds can retain stale committed shader bytecode or corrupted
  reference assemblies. Confirm suspicious behavior in a fresh worktree
  before attributing it to source changes, then clean only the affected
  `bin`/`obj` directories.
- GBA co-simulation compares instruction deltas because mGBA rebases cumulative
  cycle counters each frame. Puck's exposed PC is four bytes ahead of mGBA's
  pipeline representation.
- Windows App Control on the reference system blocks loading never-seen Debug
  binaries (`FileLoadException` `0x800711C7`), which broke the file-based
  `dotnet run <script>.cs` programs at their default Debug configuration —
  relocating the runfile cache did not help; `-c Release` loaded cleanly. Kept
  because the App Control behaviour is a property of the machine and will bite
  the next thing that loads a fresh Debug binary, not because those scripts are
  reachable: they are quarantined under `experimental/` and never run.
- A machine with a Cosmocc toolchain installed may carry `C_INCLUDE_PATH`
  pointing at its `include` directory in the ambient shell environment. That
  path leaks into every `clang`/emscripten invocation a `Puck.World.Browser`
  `browser-wasm` build or publish shells out to and collides with
  emscripten's own libc headers (`COSMOPOLITAN_C_START_` redefined, `bool32`
  unknown type, dozens of "expected function body after function declarator"
  errors from `libc/calls/calls.h`). Remove `C_INCLUDE_PATH` from the shell environment before building or
  publishing that project; this is host contamination, not a project or
  workload defect.

## Verification practices

These practices keep each result tied to the behavior and configuration it is
meant to establish.

- Choose verification inputs that differ from worked documentation examples.
  A nearby value can expose a boundary defect that the example does not.
- Run a control in the same configuration as the test and change exactly one
  variable. Remove confounding state, prove that the exercised command reaches
  the intended path, and choose a measurement that distinguishes the hypotheses.
- Read the refusal or failure reason. A control must fail at the intended check,
  with the intended message and error signal, rather than merely returning a
  failure.
- When converting a prose rule into an automated check, ensure the derived rule
  rejects every prohibited case. Prefer a strict, observable check when the
  intended scope is uncertain; an overly broad check can pass without testing
  anything.
- When correcting a claim, update its tables, examples, summaries, comments,
  and cross-references in the same change so no contradictory source remains.
- XML documentation is a compile-time dependency. With warnings treated as
  errors, an unresolved member reference produces CS1574; verify documentation
  changes with the compiler when they affect member references.

## Code and documentation conventions

- Public APIs use XML documentation that describes current behavior, parameter
  units, ownership, lifetime, failure behavior, and determinism where relevant.
  Do not narrate the change that introduced the API.
- Comments explain invariants and non-obvious constraints. Remove commented-out
  designs, commit references, dated rollout notes, and obsolete alternatives.
- `*Options` denotes configuration-bound data. `*CliSeams` owns a command-line
  surface that must stay out of the main composition method.
- Command-module conventions are documented on `ICommandModule`; screen claim
  arbitration is documented on `WorldScreenBinder`; GPU-host ordering is
  documented on `GpuHostComposition`.
- CA1502, CA1505, and CA1506 are suggestion-level design signals. Simplify a
  design when they identify real coupling; do not add facades solely to change
  a metric.
- No source file over 2500 lines: `FileLengthAnalyzer` fails the build (LEN001)
  unless `FileLengths.json` already records the file, and a recorded file may
  only shrink (LEN002/LEN003). Split, then `puck lengths --write`—the ledger
  never grows.
- A document field that carries a state, zone, rule, table, pattern, topology,
  generator, field, or dynamics name is registered in `WorldNameRegistry`
  (`src/Puck.World.Schema`); `puck registry --check` fails on an unregistered
  name-shaped member or a stale `docs/world-name-registry.md`, and
  `puck registry` rewrites the table.
- Derive descriptor counts, pool sizes, strides, and capacities from the data
  that defines them.
- .NET 10 is the only target. Verify runtime behavior and measure before preserving a hand optimization or making a performance claim.

## Documentation policy

Follow [Writing documentation](documentation.md) for prose, titles, filenames,
and navigation. Keep each document useful to the current tree, and give each fact one
authoritative home. Architecture manuals belong under docs/architecture;
contributor and CI procedures belong under docs/development; active plans and
settled cross-project decisions belong under docs/plans and docs/decisions.
Project-owned contracts and implementation detail belong beside their source
project. Other documents summarize and link to the owner.

Place detailed evidence with the plan, decision, or verification surface that
still depends on it. Retire completed rollout logs, audits, migration diaries,
commit archaeology, and superseded plans when they no longer explain current
behavior. When moving or retiring a document, move every live contract,
limitation, and procedure to its canonical home before removing the old copy.
The root [README](../../README.md) routes to the document set; update its
routing whenever the set changes.
