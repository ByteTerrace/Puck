# Working on Puck

This guide explains how to investigate, change, and verify Puck. Use
[capability-catalog.md](capability-catalog.md) for the feature inventory and
[project-map.md](project-map.md) for project ownership and dependency rules.

## Start here

- A run is a versioned `puck.run.v1` JSON document. CLI conveniences
  synthesize the same model; they do not create a second execution path.
- Vulkan and Direct3D 12 implement the same neutral GPU contracts. Shared GPU
  changes must be verified on both backends.
- `Puck.Demo` and `Puck.World` are greenfield game composition roots. Run them
  to verify game behavior; use `Puck.Post` only for shared engine contracts.
- Emulator cores live under `src/` (`Puck.HumbleGamingBrick`, `Puck.AdvancedGamingBrick`)
  with hosting folded into the cores. Each core has its own POST battery.
- Authoritative simulation uses fixed-point values and per-tick command
  snapshots. Wall-clock time, ambient randomness, and floating-point state do
  not enter replay-bearing simulation.

Load the matching skill under `.agents/skills/` before working on the SDF
world, run documents, emulators, ROM forge, verification, semantic C# analysis,
or .NET performance.

## Analyze C# semantically

Use text search for file discovery, literals, JSON, HLSL, and MSBuild. Use the
compiler or Roslyn for questions such as who references a symbol, what
implements an interface, whether code is unused, or whether a rename is safe.
Text matching misses extension methods, aliases, overload resolution, generated
code, and XML `cref` references.

The `roslyn-first-analysis` skill provides tested syntax and semantic query
templates. Prefer the cheapest correct tool:

1. `rg` for orientation and non-C# text.
2. A syntax-tree walk for declaration, member, attribute, trivia, and XML-doc
   inventories.
3. `MSBuildWorkspace` and `SymbolFinder` for cross-project symbol questions.
4. `dotnet build Puck.slnx -c Release` after a refactor or documentation edit
   that changes `cref` values.

## Verification

### Engine changes

`Puck.Post` is the engine power-on self-test:

```powershell
dotnet run --project src/Puck.Post -c Release
dotnet run --project src/Puck.Post -c Release -- --tier A
dotnet run --project src/Puck.Post -c Release -- --filter world
dotnet run --project src/Puck.Post -c Release -- --stage run-document
dotnet run --project src/Puck.Post -c Release -- --fuzz-seed 12345
dotnet run --project src/Puck.Post -c Release -- --artifacts out
```

Exit code 0 means pass, 1 means a check failed, and 2 means infrastructure
failure. The battery prints the current stage count; do not copy that count
into documentation.

| Change | Minimum verification |
|---|---|
| Fixed-point, commands, input routing, bindings, run documents | Tier A |
| Same-device GPU code, kernels, compositor, capture | Tiers A and B |
| Shared shaders, either backend, surface sharing | Full battery, including Tier C |
| Present pacing, device loss, backend switching | Full battery, including Tier D |
| Suspected backend divergence | Differential fuzzer with `--filter fuzz` or one seeded fuzz stage |
| Run-document model or schema | `--stage run-document` after regenerating the schema |

For changes under `src/Puck.Maths`, also run the deep oracle battery:

```powershell
dotnet run -c Release tools/maths-battery.cs
```

### Game and demo changes

Run greenfield composition roots instead of adding engine gates:

```powershell
dotnet run --project src/Puck.Demo -c Release -- --exit-after-seconds 2
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 2
```

`Puck.Demo --validate-overworld` remains its document and deterministic replay
sanity check. Do not add another `--validate-*` mode or a `Puck.Post` stage for
game-specific behavior unless explicitly requested.

The console is the scriptable control plane. On-screen input and process stdin
use the same registry; an ACCEPTED result echoes to stdout and a REFUSED one to
stderr, so a driver merging the two streams reads submission order while still
telling the two apart. A run that must prove no step silently no-opped ends with
`wire.errors` and asserts `[wire.errors: 0 rejected]`. The runnable proofs live
in `src/Puck.World/scripts/proof.cs`.

Use the `review-creation` scenario for isolated creation turntables:

```powershell
dotnet run --project src/Puck.Demo -c Release -- `
  --scenario review-creation `
  --scenario-set Scenario:Creation=docs/examples/creations/lantern-fish.creation.json `
  --scenario-set Scenario:Capture:Directory=artifacts/my-review
```

The scenario pins content time and camera poses. Use
`Scenario:Backdrop=room` for an in-world capture.

### Emulator changes

```powershell
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release
```

Self-contained tiers run without external ROMs. Reference-ROM stages skip
when their licensed corpus is unavailable. The Advanced battery also exposes
lockstep, trace, I/O-dump, render-hash, and divergence diagnostics; see its
project README.

### Performance changes

Use `puck.bench` for engine-performance claims. Run it through the console
(`bench.list`, `bench.run`, `bench.abort`, `bench.sweep`) or use the headless
proof:

```powershell
dotnet run --project src/Puck.Demo -c Release -- --bench standard
```

A clean scored run exits 0. An abort, refusal, or missing GPU timestamps exits
1. The benchmark writes a versioned report under `bench-reports/`. Do not use
the machine during a scored run or run other GPU workloads concurrently. See
[engine-bench-plan.md](engine-bench-plan.md) for the current suite, scoring
formula, reference configuration, and expected duration.

## Run documents

The validator is the thick semantic gate. A valid document must be buildable;
builders do not repeat validator checks. When a document field or polymorphic
kind changes:

1. Update the nullable model and XML documentation.
2. Add all semantic validation to `RunDocumentValidator`.
3. Regenerate `schema/run.schema.json`:

   ```powershell
   dotnet run -c Release tools/Tools.cs schema schema/run.schema.json
   ```

4. Add or update an example.
5. Run the `run-document` POST stage and the relevant live graph.

The `run-document` skill documents serializer construction behavior and the
complete add-a-field procedure.

## Configuration and diagnostics

The demo does not use `PUCK_*` configuration variables. Durable configuration
belongs in the run document; live operations belong in console verbs.

The remaining environment variables are engine, launcher, emulator, or
content-development diagnostics:

| Variable | Purpose |
|---|---|
| `PUCK_RAY_QUERY` | Permit or deny the ray-query path. The run-document equivalent is `host.rayQuery`. |
| `PUCK_GENLOCK=0` | Disable the launcher genlock control law. The document equivalent is `host.genlock`. |
| `PUCK_PRESENT_TIMING` | Log measured present intervals. |
| `PUCK_TEST_DEVICE_LOSS=<seconds>` | Request synthetic device loss for live verification. |
| `PUCK_D3D12_DEBUG` | Opt in to the Direct3D 12 debug layer. |
| `PUCK_PARITY_STRICT=1` | Use strict pixel-perfect parity thresholds instead of the default evidence-calibrated posture. |
| `PUCK_CAPTURE_FRAME=<number>` | Delay one-shot capture for a world-document run. |
| `PUCK_FLAGSHIPS_REGENERATE=1` | Regenerate committed flagship creation documents. |
| `PUCK_GB_TESTROMS` | GB/GBC reference-ROM corpus. |
| `PUCK_GB_LINKROM`, `PUCK_GB_TRADEROM` | Commercial link-game verification inputs. |
| `PUCK_GB_SST` | SingleStepTests/sm83 per-instruction vector corpus (`Sm83SstStage`, skip when absent). |
| `PUCK_AGB_BIOS`, `PUCK_AGB_TESTROMS`, `PUCK_AGB_ACCURACY_SUITE`, `PUCK_AGB_AGS`, `PUCK_AGB_GAMES` | GBA reference inputs. |
| `PUCK_AGB_SOLARROM` | Commercial Boktai (solar-sensor) cartridge for `SolarReplayStage` (skip when absent). |

GPU timing has no environment variable. Arm it with the `gpu.timing` feature
switch, the `world.timing` verb, `host.timing`, `--timing`, or the benchmark
harness.

## GPU support and shader builds

The supported GPU floor covers RTX 2070, RTX 4070, the RDNA3 Steam Machine,
and the RDNA2 Steam Deck. Shaders target Vulkan 1.3 / SPIR-V 1.6 and Shader
Model 6.6. Do not raise that floor without evidence for every supported GPU.

DXC compiles the same HLSL sources to SPIR-V and DXIL during the build. `dxc`
must be on `PATH`; there is no GLSL or `glslc` path. A change to the SDF C# ISA
must update the HLSL decoder in the same change. The `sdf-world` skill lists
the exact C#↔HLSL contract pairs and bytecode rebuild procedure.

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

## Engineering doctrine

1. The current user request outranks documentation, skills, tests, comments,
   and precedent. Update stale artifacts in the same change.
2. Gates assert observable contracts: pixels, hashes, parity, determinism,
   exit codes, and measured budgets. They do not pin internal call sequences or
   type shapes.
3. Skills contain factual or procedural guidance, not immutable architecture.
4. Stability levels determine the evidence required for a change, not whether
   a change is allowed.
5. Greenfield game behavior is verified by running the game. Shared engine
   contracts are verified by the appropriate POST battery.
6. Puck has zero external consumers, so backwards compatibility carries no
   weight. Rename, reshape, and delete freely; update every internal caller in
   the same change. Never add compatibility aliases, deprecation ceremonies,
   migration shims, or read-side tolerance for retired data shapes — migrate
   the data once and delete the old path. The only stability that matters is
   observable behavior under the gates.
7. Determinism pins the mapping, not the values. The contract is
   reproducibility at a fixed code version — same document + same input →
   bit-identical state across runs, machines, and backends — never output
   stability across code versions. A deliberate correction to math or logic is
   expected to change state hashes and is never blocked by that fact. The
   ritual: make the correction, re-run the relevant POST tier (the determinism
   and replay gates are self-referential — they capture and verify within the
   same build and pin no historical constants), and re-record any persisted
   replays or calibrated baselines the correction invalidates in the same
   change. Preserving a wrong result to keep a hash stable, or adding a path
   that reproduces old-wrong behavior, is the defect.

## Code and documentation conventions

- Public APIs use XML documentation that describes current behavior, parameter
  units, ownership, lifetime, failure behavior, and determinism where relevant.
  Do not narrate the change that introduced the API.
- Comments explain invariants and non-obvious constraints. Remove commented-out
  designs, commit references, dated rollout notes, and obsolete alternatives.
- `*Options` denotes configuration-bound data. `*CliSeams` owns a command-line
  surface that must stay out of the main composition method.
- Command-module conventions are documented on `ICommandModule`; screen claim
  arbitration is documented on `ScreenSlotLedger`; GPU-host ordering is
  documented on `GpuHostComposition`.
- CA1502, CA1505, and CA1506 are suggestion-level design signals. Simplify a
  design when they identify real coupling; do not add facades solely to change
  a metric.
- Derive descriptor counts, pool sizes, strides, and capacities from the data
  that defines them.
- .NET 10 is the only target. Consult `dotnet10-performance` before preserving
  a hand optimization or making a runtime-performance claim.
- Merges land on `main` as one squash commit with a hand-written summary, no
  merge bubble, and no `Co-Authored-By` trailer.

## Documentation policy

Everything under `docs/` must be useful in the current tree. Current reference
material, research with a live decision index, measured baselines, and active
roadmaps are acceptable. Completed rollout logs, audits, migration diaries,
commit archaeology, and superseded plans belong in version control history.

When retiring a document, move any still-live contract, limitation, or
procedure into its canonical reference before deleting it. Update
[README.md](README.md) whenever the document set changes.

## Coordinating parallel work

Use parallel agents for independent, disjoint workstreams:

1. Inventory and audit before editing.
2. Give each worker explicit file ownership and applicable skills.
3. Keep shared-file edits minimal and sequence them deliberately.
4. Require a concrete verification command and observable success condition.
5. Inspect the shared worktree and rerun verification from the integrating
   agent; a worker report is evidence, not proof.
6. Avoid performance measurements while other builds or GPU workloads are
   active.
