# Working on Puck

This guide explains how to investigate, change, and verify Puck. Use
[project-map.md](project-map.md) for project ownership and dependency rules.
There is deliberately no feature inventory document — ask the code, or run
`Puck.World`.

## Start here

- A run is a versioned `puck.world.def.v1` JSON document. CLI conveniences
  synthesize the same model; they do not create a second execution path.
- Vulkan and Direct3D 12 implement the same neutral GPU contracts. Shared GPU
  changes must be verified on both backends.
- `Puck.World` is the greenfield game composition root. Run it to verify game
  behavior; no automated gate covers the shared engine contract today, as
  Verification below sets out.
- Emulator cores live under `src/` (`Puck.HumbleGamingBrick`, `Puck.AdvancedGamingBrick`)
  with hosting folded into the cores. Each core has its own POST battery.
- Authoritative simulation uses fixed-point values and per-tick command
  snapshots. Wall-clock time, ambient randomness, and floating-point state do
  not enter replay-bearing simulation.

Load the matching skill under `.claude/skills/` before working on the SDF
world, emulators, ROM forge, verification, semantic C# analysis,
or .NET performance.

## Analyze C# semantically

Use text search for file discovery, literals, JSON, HLSL, and project files. Use the
compiler for questions such as who references a symbol, what implements an
interface, whether code is unused, or whether a rename is safe. Text matching
misses extension methods, aliases, overload resolution, generated code, and XML
`cref` references.

The `symbol-analysis` skill owns those questions and documents the traps.
Prefer the cheapest correct tool:

1. `puck search` for orientation and non-C# text (the `content-search` skill;
   `rg`, `grep` and `Select-String` are not used in this repo).
2. `puck declarations` for declaration, member, attribute, base-list, and XML-doc
   inventories — parse-only, no build.
3. `puck references` for cross-project symbol questions: references,
   implementers, overrides, derived types, dead-code candidacy.
4. `dotnet build Puck.slnx -c Release` after a refactor or documentation edit
   that changes `cref` values.

## Verification

### Engine changes — THERE IS NO ENGINE GATE TODAY

**`Puck.Post` is quarantined** (`experimental/Puck.Post`, owner ruling
2026-08-02) — out of the solution and out of the build. Read it as prior art;
do not run it, cite it as coverage, or write a stage for it.

So the shared engine contract it used to gate — the SDF VM ISA, the document
schemas, the deterministic numerics, the differential fuzzer — **currently has
no automated gate.** Say that plainly when it matters; do not imply coverage
that does not exist, and do not reach into `experimental/` to manufacture
some. An engine change is verified today by running what is still in the build
and by argument, and a change that would once have been gated should say in
its own commit what was and was not checked.

The one narrow cross-backend check that exists is `puck parity`: for each
authored pattern world under `tests/Puck.Parity/` (gradient, edges,
modifiers, glyphs, film grain — each stressing one contract slice) plus the
shipped default world,
it boots the real `Puck.World` windowed on Vulkan and on Direct3D 12,
screenshots the same fenced simulation moment in each run, and compares the
backend pair under the relaxed envelope (benign ±1-LSB shader-codegen noise
passes; a missing, relocated, or recolored region fails). There are no stored
baselines, so content changes cannot fail it — only a cross-backend
divergence can. Two different patterns from the same backend must fail the
envelope on every run. It needs a display and both GPU devices, so run it on
hardware for any render-path, shader, presenter, or capture change; it covers
composed-frame agreement and nothing else.

Still in the build and still applicable: the architecture gate (every build) —
including `PUCKARCH008`, which fails a denied project's build when its
compiled output references an assembly `build/Architecture.props`'s
`PuckArchitectureDeniedApi` denies it (`Puck.World.Server` and
`System.Console` today) — the two emulator batteries below,
`dotnet build Puck.slnx -c Release`, `puck parity`, and running `Puck.World`.

For changes under `src/Puck.Maths`, also run the maths law suite. The default
tier is the everyday gate; `deep` and `exhaustive` are the opt-in volumes:

```powershell
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/default.runsettings
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings
```

### Game and demo changes

Run greenfield composition roots instead of adding engine gates:

```powershell
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 2
```

`Puck.World` is the only composition root that runs. `Puck.Demo`, the
composition-root-less library that once sat quarantined at
`experimental/Puck.Demo`, is deleted — each folder's capability either has a
live successor in `Puck.World` (see `experimental/README.md`) or is simply
absent from it, with no plan bringing it over. Do not add a `--validate-*`
mode or a `Puck.Post` stage for game-specific behavior unless explicitly
requested.

The console is the scriptable control plane. On-screen input and process stdin
use the same registry; an ACCEPTED result echoes to stdout and a REFUSED one to
stderr, so a driver merging the two streams reads submission order while still
telling the two apart. A run that must prove no step silently no-opped ends with
`wire.errors` and asserts `[wire.errors: 0 rejected]`. The runnable proofs live
in the proof suite — which is quarantined under `experimental/` and off
limits, so those proofs are not runnable today and the console-scripting
contract they demonstrated has no executable witness.

The `review-creation` scenario — isolated creation turntables with pinned content
time and camera poses — has no runnable host today, and no plan to get one. It
does not exist in `Puck.World`; the document that scheduled the move was deleted
with the quarantine. Treat creation review as an absent capability, not a
pending one.

### Emulator changes

```powershell
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --fetch-corpora
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --lane gate
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release -- --fetch-corpora
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release -- --bios <GBA_bios.rom>
```

The Humble battery's reference corpora are declared in its `corpora.json`
(pinned archive, version, SHA-256); `--fetch-corpora` fills the local cache
once and the stages resolve it without configuration. `--lane gate` measures
every row recorded as passing and must stay green; `--lane frontier` measures
the recorded fails and inconclusives; a plain run measures both. Every run
writes `summary.json`, `results.junit.xml`, and a candidate ledger under
`artifacts/gb-post`; `--accept` promotes the candidate under the refusal rules
in the project README. Iterate with `--filter`; never chain runs to record.
The Advanced battery works the same way: its corpora are pinned in its own
`corpora.json`, the BIOS and commercial cartridges are command-line flags, and
it exposes lockstep, trace, I/O-dump, render-hash, and divergence diagnostics;
see its project README.

### Performance changes

**There is no way to score engine performance today, and no code in the build
that could.** `Puck.Bench` was quarantined to `experimental/Puck.Bench` on
2026-08-02 — it had been compiling on every build while ZERO projects referenced
it: nothing implemented its scene controller, nothing registered its
`bench.list`/`bench.run`/`bench.abort`/`bench.sweep` verbs. Its host, the
headless `--bench` entry point, and the suite registration went with `Puck.Demo`;
the plan that scheduled re-homing them and the benchmark plan itself were
deleted. So the suite, the scoring formula, and the reference configuration are
not written down anywhere.

**Treat every engine-performance claim as unmeasurable.** Do not quote historical
numbers as current — they were taken on a machine and a suite nothing in the tree
can reproduce. If performance work becomes necessary, the honest first step is
building an instrument in a real project, not reviving a quarantined one.

### Browser engine changes (`Puck.World.Browser`)

`unset C_INCLUDE_PATH` first if the machine has a Cosmocc toolchain installed
(see "Hardware and toolchain cautions" below) — otherwise every native asset
compile in the steps below fails with cryptic libc header collisions that have
nothing to do with the change under test.

```powershell
dotnet build src/Puck.World.Browser -c Release
dotnet test tests/Puck.World.Browser.Tests -c Release
dotnet publish src/Puck.World.Browser -c Release
```

`tests/Puck.World.Browser.Tests` links `Engine/*.cs` as source and runs under
the ordinary net10.0 test host — no wasm runtime needed to exercise the pure
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
`screens[].source.machine` engine — the shipped island's arcade district among
them — refuses by name rather than crashing).

## World documents

The validator is the thick semantic gate. A valid document must be buildable;
builders do not repeat validator checks. When a document field or polymorphic
kind changes:

1. Update the nullable model and XML documentation.
2. Add all semantic validation to `WorldDefinitionValidator`, which runs over
   the entire composed candidate document rather than the changed section
   alone — including an owned identity's document, which is validated the
   same way.
3. Register the type in `WorldJsonContext`; a polymorphic kind also needs its
   `[JsonDerivedType]` line.
4. Verify by RUNNING `Puck.World` and round-tripping the document over stdin.

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
| `PUCK_RAY_QUERY` | Permit or deny the ray-query path. |
| `PUCK_GENLOCK=0` | Disable the launcher genlock control law. The document equivalent is `host.genlock`. |
| `PUCK_PRESENT_TIMING` | Log measured present intervals. |
| `PUCK_TEST_DEVICE_LOSS=<seconds>` | Request synthetic device loss for live verification. |
| `PUCK_D3D12_DEBUG` | Opt in to the Direct3D 12 debug layer. |
| `PUCK_CAPTURE_FRAME=<number>` | Delay one-shot capture for a world-document run. |
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
  errors from `libc/calls/calls.h`). `unset C_INCLUDE_PATH` before building or
  publishing that project; this is host contamination, not a project or
  workload defect.

Engineering doctrine — the current request outranking artifacts, gates
asserting observable contracts only, supergreen, determinism pinning the
mapping — lives in [CLAUDE.md](../CLAUDE.md)'s core rules and is not restated
here.

## Verification doctrine

Rules earned the hard way during the capability-channels campaign, general to
all verification work here. Each keeps one compressed instance as evidence.

- **Never verify with the parameters the documentation uses.** Every worked
  example in a document is a cell someone already ran and found working — the
  single worst cell to verify against; the defect sits one value over. Evidence:
  an addon-drive regression check passed only against the one body the mount
  line's own example names; any other body produced 7418 error lines in 31
  seconds. Pick a different value, and pick it before you know the answer.
- **Run the control in the same configuration as the test.** The control and
  the cell must differ in exactly one thing. Evidence: a control measured
  before an addon had moved anything, compared against a cell where it had —
  two variables, not one. Corollaries: neutralising a confound beats recording
  it (remove the boulder, do not document its coordinates); when testing a
  path rather than an effect, prove you are on the path (a verb with the same
  observable effect can run a different principal down a different path); and
  a measurement must be capable of distinguishing the hypotheses before it is
  worth running (choose an axis the other driver cannot produce).
- **A control must fail for the RIGHT reason — by its message, never merely by
  failing.** Evidence: a grant-gating check against a nonexistent path produced
  a refusal line and incremented the error counter — both signals a reader
  checks — while never reaching the grant check at all. Read the reason, every
  time.
- **A derivation that fires less often than its source is a retirement wearing
  enforcement's clothes.** When mechanising a prose rule, measure whether the
  mechanism ADMITS anything the prose forbade — it fails in the direction that
  never produces a failure. Evidence: a closure rule derived for the
  architecture gate came out wide enough to permit direct backend dependencies
  it existed to forbid, and would have flagged nothing, forever. Corollary:
  empty-because-too-wide and empty-because-clean are indistinguishable in a
  report and opposite in meaning — one honest named exception is worth more
  than a clean sheet produced by not asking.
- **Too strict announces itself; too wide sits silent.** A rule that is too
  strict files its own bug report on first contact; a rule that is too wide
  never complains. When a rule's scope is uncertain, err strict, and arm the
  gate early — arming is what settles scope. Evidence: the architecture gate's
  terminal-kind rule was correctly narrowed only when it fired on an analyzer
  test suite.
- **A correction that lands in prose while the artifact it condemns survives is
  not a correction.** It is a second, contradictory source of truth, and the
  artifact is what gets read and copied. When retracting a claim, hunt the
  tables, examples, comments, and cross-references that embody it in the same
  change. Evidence: a corrected lanes argument left a condemned channel table
  printing unchanged a hundred lines below.
- **Hunt echoes in the summaries first.** A summary restates a conclusion
  without the qualifications that made it true, so it is where a retracted
  claim survives longest and reads most confidently — checklists,
  by-construction lists, phase tables, and README overviews all count.
  Evidence: a fence claim was corrected in the section that argues it and
  survived in the by-construction list one screen away.
- **"Doc-only" is not a safety class.** A `cref` is a compile-time dependency:
  with `TreatWarningsAsErrors`, an XML comment naming a private, renamed, or
  deleted member fails the build (CS1574) exactly like broken code. Risk
  categories describe intent; the compiler does not care about intent. Verify
  by the mechanism that will actually judge the change.

### Git in a shared working tree

- Commit with explicit paths — `git commit -- <paths>` — never add-then-commit.
  A stage command is only as scoped as its narrowest pattern; nothing about
  "I only touched my files" stops `-A` from sweeping a sibling session's work.
- `git add` with multiple pathspecs is all-or-nothing on a bad spec: one stale
  path silently aborts the entire staging, leaving a commit whose message
  describes contents it lacks.
- An amend commits the index, not your diff — the one shape that silently
  swallows a sibling's staged work. Run `git diff HEAD@{1} HEAD` after every
  amend, announce staged work, and prefer plain commits in a shared tree.
- A verification scoped to the warning you received is not a verification of
  the operation you performed. Answer the question the command itself raised,
  not the narrower one handed to you; a true answer to a narrower question
  leaves no trace of the gap.
- A relocation is not complete until something that consumed the old location
  has been run at the new one. Evidence: a `git mv` left seventeen dangling
  `ProjectReference`s behind an exit-0 restore — a moved tree that nobody has
  built is a claim, not a state.

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
- No source file over 2500 lines: `FileLengthAnalyzer` fails the build (LEN001)
  unless `FileLengths.json` already records the file, and a recorded file may
  only shrink (LEN002/LEN003). Split, then `puck lengths --write` — the ledger
  never grows.
- A document field that carries a state, zone, rule, table, pattern, topology,
  generator, field, or dynamics name is registered in `WorldNameRegistry`
  (`src/Puck.World.Schema`); `puck registry --check` fails on an unregistered
  name-shaped member or a stale `docs/world-name-registry.md`, and
  `puck registry` rewrites the table.
- Derive descriptor counts, pool sizes, strides, and capacities from the data
  that defines them.
- .NET 10 is the only target. Consult `dotnet10-performance` before preserving
  a hand optimization or making a runtime-performance claim.

## Documentation policy

Everything under `docs/` must be useful in the current tree. Current reference
material, research with a live decision index, measured baselines, and active
roadmaps are acceptable. Completed rollout logs, audits, migration diaries,
commit archaeology, and superseded plans belong in version control history.

A document owned by a single project lives with that project — its README or a
sibling file beside the code — never under `docs/`, which holds only
cross-project material. When retiring a document, move any still-live contract,
limitation, or procedure into its canonical reference before deleting it. The
root [README](../README.md) routes to the document set; update its routing
whenever the set changes.

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
7. Verify a handed-off defect is still live — against the worktree **and** the
   commit — before recording it anywhere. A fix and a report can cross, and a
   wrong belief committed to a shared ledger outlives every session that could
   refute it. When one is already written down, correct it in place rather than
   leaving the claim standing.
