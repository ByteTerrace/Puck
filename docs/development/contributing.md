# Contributing to Puck

This guide explains how to investigate, change, and verify Puck. Use the
[project map](../project-map.md) for project ownership and dependency rules,
the [architecture guide](../architecture/README.md) for the engine's boundaries,
and the [CI guide](ci.md) for hosted validation and release procedures.

## Start here

- A run uses a versioned `puck.world.definition.v1` JSON document. CLI conveniences
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
Normalize platform-produced paths to `/` at Puck's output and storage boundaries
with `Puck.Abstractions.PuckPaths.Normalize`, which resolves a path to its full
form and replaces every platform separator with `/`. Compare two file-system
paths for equality, containment, or distinctness with `PuckPaths.Comparer` (a
`StringComparer`) or `PuckPaths.Comparison` (its `StringComparison`
equivalent): case-insensitive on Windows, where the file system itself ignores
case, and ordinal everywhere else. This is a file-system rule, not a text
rule — a document NAME (`Puck.Assets.WorldDocumentName.NameComparer`) is
case-insensitive on every platform, on purpose, and never shares this
comparer. Keep resolution semantics explicit: `Path.Combine` can replace an
earlier base with a rooted argument, whereas `Path.Join` only joins
components. They are not interchangeable. Preserve native device-path syntax
only at an interop boundary that requires it; backslashes in string escapes
and regular expressions are not file separators.

## Per-user directory

Puck keeps per-user state and caches in one directory, `Puck` under your local
application data: `%LOCALAPPDATA%/Puck` on Windows and `~/.local/share/Puck` on
Linux, or the temporary directory when the platform names no such folder
(`PuckUserDirectory` in `Puck.Abstractions`). Each owner keeps one lower-case
subdirectory there:

| Subdirectory | Owner |
|---|---|
| `world` | The game's state root: profiles and replays (`--state-dir` replaces it) |
| `compiled-worlds` | The compiled worlds boots derive, shared by every boot whatever its state root |
| `bakes` | The creation bakes presentations make, shared the same way |
| `world-builds` | The shared Release builds of `Puck.World` the CLI gates run |
| `compilations` | The `.puck` compile cache the game and the CLI share |
| `corpora` | The conformance corpora the emulator batteries fetch |

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

Use `puck format --file-list files.json` from the repository root, with a JSON array
of paths relative to the working directory, such as `["src/Puck.Azure.Resources/bootstrap.cs"]`. The CLI converts a
standalone app to a disposable SDK project, preserves its references and linked
helpers, compiles and formats the copy, then copies back only the selected source
with its file directives restored. `--check` leaves the original source
untouched. Ordinary project files still need their owning projects restored
and built in the configuration the run resolves symbols against—`Release` unless
`--configuration` says otherwise—or the semantic passes skip them and say so. See
[automatic PR formatting](ci.md#automatic-pr-formatting) for the CI bot and the
fork-PR patch path. Never run a repository-wide sweep to fix one entry point.

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

`puck affected --run` is how a change is verified: it runs the suites, canaries
and parity the change can reach, chosen from the project graph and recorded
canary coverage (see [`puck affected`](../reference/cli.md#puck-affectedthe-checks-a-change-needs)),
and nothing wider. The full sets run only when the owner asks for them.

`puck canary --merge` runs the full canary set. It runs every
[real-World canary](../reference/cli.md#puck-canaryreal-world-behavioral-proofs)
a merge needs: the automatic set (headless, no environmental requirements) and
every canary requiring `gpu`, which includes the offscreen proofs on both
backends. A bare `puck canary` runs only the automatic set, so it never runs a
GPU proof. Run the merge gate with no competing build or GPU work on the
machine, from a copy of the candidate's own CLI. `puck canary --merge --plan`
prints what the gate would run without a GPU, and a gate that outgrows its
declared ceiling is refused before it builds.

`puck canary`, `puck parity`, `puck test`, and `puck docs citations` share one
Release build of `Puck.World` per source state. The build lives in
`world-builds` in the [per-user directory](#per-user-directory), never in the
checkout. It is keyed by
git's view of the World's project closure, including uncommitted and untracked
changes. Running a second gate over an unchanged checkout does not rebuild, and
an edit to documentation or tests leaves the build in place. An edit under a
project the World builds produces a new key, so the next gate builds again.
[Where the World artifact is built](../reference/cli.md#where-the-world-artifact-is-built)
lists what the key covers and how old builds are pruned.

A verification check proves its own behavior independently: derive the
expected outcome another way than the code being checked, and prove that
behavior the check doesn't touch stays unchanged. Running the same replay
twice is an additional determinism check, never the proof of correctness,
because a replay hash covers only the explicitly hashed state trajectory, not
the journal or the whole document — undo and checkpoint restore need their
own assertions. A change that alters a persisted format (a world document,
an owned-identity document, an authority checkpoint or its journal, a replay
tape, or a release fixture) states which format it touches and whether old
data can still load: old data must never load under a new interpretation, so
a renamed or reshaped field makes the strict parser refuse the old form
rather than silently reinterpreting it.

For changes under `src/Puck.Maths`, also run the maths law suite. A plain
`dotnet test` runs the default tier (Smoke and Default), the everyday gate;
`smoke`, `deep` and `exhaustive` are selected by their committed run settings:

```powershell
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/smoke.runsettings
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings
dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/exhaustive.runsettings
```

Each tier is a filter on the `tier` trait. A `--filter` on the command line is
combined with the default tier's filter rather than replacing it, so
`--filter "tier=Exhaustive"` selects no test; select an opt-in tier with its
`--settings` file.

### Game changes

Run the application to check composed game behavior, alongside relevant World tests:

```powershell
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 2
```

Use `Puck.World` for game verification. Do not add a `--validate-*` mode or
a `Puck.Post` stage for game-specific behavior unless explicitly requested.

The console and process stdin use the same command registry. Accepted results
are written to stdout and refusals to stderr; host log lines go to stderr too,
so stdout carries only answers. A driver that needs their combined
arrival order can merge the streams. End a run with `wire.errors` and check
`[wire.errors: 0 rejected]` when the result depends on every command being accepted.
The [World guide](../../src/Puck.World/README.md) owns the current scripting and
replay procedures. Creation review—isolated creation turntables with pinned
content time and camera poses—has no runnable host in `Puck.World`, and no plan
adds one. Treat it as an absent capability, not a pending one.

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

Performance is judged by counted work, code and disassembly, never by time.
`puck bench` is the only tool that measures wall-clock time, and it runs only
when the owner asks for a timing.

Two tools read the counts. For a live World workload, wait for rendered
frames, then sample `world.counters`: its `gpu` section is the per-pass counted
work (dispatches, barriers, uploads, created objects), its `allocation` section
says whether reading every count allocates, and every other section is a
registered engine counter, including the server's `state.arena`, `state.rules`
and `state.search`, and in a rendering World the shader compiler's
`shaders.compiler`, the shader loads under `shaders.sdf-kernels`,
`shaders.fullscreen-pass` and `shaders.set-manifest`, and, on Vulkan,
`procedures.vulkan`. An owner whose counts need nothing more than a named set
of kinds holds a `WorkCounterSet` rather than writing its own source.
`world.budget` distinguishes live program size from
reserved capacity. For a repeatable reading, run
[`puck counters`](../reference/cli.md#puck-counterswork-counter-collector): it
boots the authored counters workload offscreen on each backend, writes a report
with every count tagged by class, and fails when a deterministic count differs
between the backends. Keep the report from before a change and compare it with
the one after, using `puck counters compare`.

Compare the same document, camera, resolution, quality settings, backend and
build configuration before and after the change. Report these conditions with
the result. See
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

The `.puck` authoring surface (a mounted workspace, `CompileSource`,
`ComposeSource`, and the language server's `Lsp`/`LspIdle` round trip) has its
own harness over its own fixtures, run from the repository root:

```powershell
node --test tests/Puck.World.Browser.Tests/wasm/sources.test.mjs
```

Both harnesses skip themselves by name (never silently pass) when the AppBundle
is absent. To re-record the determinism-canary baseline both the native tests
and the Node harness compare against:

```powershell
puck baselines browser-parity
```

See `src/Puck.World.Browser/README.md` for the AppBundle's real file layout
and sizes, the exact `[JSExport]` surface, the trim-warning baseline, and the
one verified scope boundary (no emulator core, so a document authoring a
`screens[].source.machine` engine—the shipped island's arcade district among
them—refuses by name rather than crashing).

`tests/Puck.Cli.Tests/Official/OfficialBuildCommandTests.cs` builds a real
`puck.official.manifest.v1` tree from this checkout's own worlds and the
browser AppBundle, so it needs that AppBundle published first:

```powershell
dotnet publish src/Puck.World.Browser -c Release
dotnet test tests/Puck.Cli.Tests -c Release --filter "FullyQualifiedName~OfficialBuildCommandTests"
```

CI's `artifacts` workflow always publishes the browser before any test project
runs, so this is a local-run-only step; the fixture's own failure message
names the command when the bundle is missing.

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

No environment variable switches Puck. Durable configuration belongs in the
world document (genlock, for example, is the document's `host.genlock`); live
operations belong in console verbs; a run's diagnostics are flags; and a
committed test baseline is regenerated by a verb with a `--check` twin.

| To | Use |
|---|---|
| Turn on the backend's validation layer | `Puck.World --debug-layers`, `puck canary --debug-layers`, or a release profile's `debugLayers` for `puck qualify` |
| Re-record a test baseline | `puck baselines <artifact>`, and `puck baselines <artifact> --check` to compare (see the [CLI reference](../reference/cli.md#puck-baselinestest-baselines)) |
| Run an opt-in test harness | the test's own explicit tests or fixture, named in its project's README |

The `ENV001` analyzer in `Puck.Analyzers` holds this in every build: it refuses
any read of the process environment whose variable is not named, with its
reason, in `EnvironmentReadAllowlist`. That list holds only values the operating
system, the .NET SDK, or the CI host defines, such as `PATH` and the
`GITHUB_*` variables the CI-facing verbs read. Both emulator batteries take
their inputs on the command line.

## GPU support and shader builds

The supported GPU floor covers RTX 2070, RTX 4070, the RDNA3 Steam Machine,
and the RDNA2 Steam Deck. Shaders target Vulkan 1.3 / SPIR-V 1.6 and Shader
Model 6.6. Do not raise that floor without evidence for every supported GPU.

DXC compiles the same HLSL sources to SPIR-V and DXIL during the build. `dxc`
must be on `PATH` for these built-in kernels, and live pipeline sources compile
with the same DXC, resolved as the [shader guide](../reference/shaders.md#one-off-shaders)
describes. The `Puck.World` build also packages every pipeline source a shipped
world names into the [package store](../reference/shaders.md#the-builds-package-store)
beside the worlds, so a pipeline source row does one of two things. In a
developer checkout with DXC, an unedited shipped source loads its stored package
and an edited or new source compiles live, so authoring keeps its loop. In a
packaged runtime with no DXC, the shipped sources load their packages, nothing
compiles, and a source with no stored package is refused by
`SHADERPKG_ABSENT`. The Direct3D 11 camera and probe kernels and the Direct3D 12
compositor's blit compile at build, so no device compiles them. A change to the
SDF C# ISA
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
  infrastructure is built ahead for re-hosting and has no live check.
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
  binaries (`FileLoadException` `0x800711C7`). A file-based app run at its
  default Debug configuration fails to load, and relocating the runfile cache
  does not help; build or run it with `-c Release`.
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

- Every durable artifact declares its own falsifier. A canary names what in
  the observation is bound to the variable under test — a pixel diff where
  nothing in frame tracks the variable proves nothing. A design document
  states the premises that would kill it, as re-runnable checks.
- Never write a status column. A status claim duplicates what the code
  answers better; a decision records what the code cannot answer — why, what
  was rejected, where a boundary sits — and stays irreplaceable even when
  stale. Keep decisions, delete status, and generate inventories or do
  without them.
- Security claims default the other way from feature claims. For a feature,
  unverified means not-done. For an escalation, unverified means still open —
  the cost of the other default is shipping a hole because its citation
  rotted.
- Verify by running, and by content. Exit code 0 is not success; audit the
  streams. A commit hash absent from a branch does not mean its content is
  absent, and a search hit is not a repository fact until the file is
  tracked.
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
- Allocation laws, Post stages and bench diagnostics measure through
  `AllocationWindow` (`Puck.Abstractions.Counting`), the one helper over
  `GC.GetAllocatedBytesForCurrentThread`; nothing else reads the counter.
  `Least` is the zero law. It takes the least of up to 16 windows, because a
  method's first run can charge the runtime's own work to whichever window is
  open. When every window allocated, it runs the body again under the runtime's
  `AllocationSampled` event and fails naming the window, the GC mode, and the
  types sampled on the measuring thread; the event carries no stack, so the
  culprit is named by type. `Measure` returns the same least as a count, for a
  ceiling. Both run the body more than once, so a window must leave its subject
  able to run the same path again: advance to fresh ticks or rows rather than
  repeating ones already consumed. `Total` runs a body exactly once, for a flow
  that cannot repeat, one tick sampled for a median, or a batch divided into a
  mean; a zero law never reads it. A background collection inflates the
  counter: it retires the thread's allocation context without subtracting the
  unused remainder, up to about 8 KiB the thread never allocated. Test and validation hosts therefore run
  with blocking collections (`ConcurrentGarbageCollection` in
  `Directory.Build.targets`). Setting `DOTNET_gcConcurrent=1` brings the false
  failures back, so don't set it when you run these suites.
- XML documentation is a compile-time dependency. With warnings treated as
  errors, an unresolved member reference produces CS1574; verify documentation
  changes with the compiler when they affect member references.

## Code and documentation conventions

- Public APIs use XML documentation that describes current behavior, parameter
  units, ownership, lifetime, failure behavior, and determinism where relevant.
  Do not narrate the change that introduced the API.
- A comment earns its place by stating what the code cannot and a reader would
  act on wrongly without: an invariant, a sign convention or unit, a packing
  layout, an external specification or hardware citation, or a coupling the
  compiler cannot check. Name the other side of such a coupling, and prefer
  removing the duplication that makes it necessary. If deleting a comment loses
  nothing the code and git history can't recover, delete it. Never write dates,
  commit references, citations of plans, reviews, or conversations, accounts of
  a defect or its fix, changelogs, commented-out designs, obsolete alternatives,
  capitals for emphasis, or counts and line numbers that drift. Delete a stale
  comment rather than replacing it with a longer one.
  `puck scan --only comment-smells` classifies the inline comments that break
  these rules.
- `*Options` denotes configuration-bound data.
- Command-module conventions are documented on `ICommandModule`; screen-slot
  claim arbitration is documented on `ScreenSlotPriority`; the split between the
  headless core and the presentation layer that adds the GPU host is documented
  on `WorldBootComposition`.
- CA1502, CA1505, and CA1506 are suggestion-level design signals. Simplify a
  design when they identify real coupling; do not add facades solely to change
  a metric.
- No source file over 2000 lines: `FileLengthAnalyzer` fails the build (LEN001)
  unless `FileLengths.json` already records the file, and a recorded file may
  only shrink (LEN002/LEN003). Split, then `puck lengths`—the ledger
  never grows.
- No new comment smell: `CommentSmellAnalyzer` fails the build (SMELL001)
  when a file `CommentSmells.json` does not record carries an inline comment
  in a named smell bucket, and a recorded file's count may only fall
  (SMELL002/SMELL003). Rewrite or delete the comment, then
  `puck comment-smells`. Both ledgers are
  [ratchet ledgers](../reference/cli.md#puck-lengths-and-puck-comment-smellsratchet-ledgers).
- A call through an unmanaged function pointer (`delegate* unmanaged`, any
  calling convention) may not use a signature that mentions a type parameter
  except behind a pointer: `Puck.Analyzers` fails the build with INTEROP001,
  because the call would throw `MarshalDirectiveException` at run time. Call
  through a closed signature, or cast the entry point to a view that passes the
  value as `T*`; declaring or passing a generic signature is fine.
- Code never reads an environment variable to switch itself: `Puck.Analyzers`
  fails the build with ENV001 on any `Environment.GetEnvironmentVariable`,
  `GetEnvironmentVariables`, or `ExpandEnvironmentVariables` call outside
  `EnvironmentReadAllowlist`, and on any read whose name is not a compile-time
  constant. Make a switch a flag, a document or profile setting, or a test
  fixture instead ([configuration and diagnostics](#configuration-and-diagnostics)).
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

Docs name no dates and no commit SHAs; verification evidence belongs in the
commit message that lands a change (see
[Write examples and claims precisely](documentation.md#write-examples-and-claims-precisely)).
Retire rollout logs, audits, migration diaries, commit archaeology, and
superseded plans. When moving or retiring a document, move every live contract,
limitation, and procedure to its canonical home before removing the old copy.
The root [README](../../README.md) routes to the document set; update its
routing whenever the set changes.
