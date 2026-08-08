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
- `Puck.Demo` and `Puck.World` are greenfield game composition roots. Run them
  to verify game behavior; use `Puck.Post` only for shared engine contracts.
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
2026-08-02) — out of the solution, out of the build, and off limits. Do not
run it, cite it, or write a stage for it.

So the shared engine contract it used to gate — the cross-backend render path,
the SDF VM ISA, the document schemas, the deterministic numerics, the
differential fuzzer — **currently has no automated gate at all.** Say that
plainly when it matters; do not imply coverage that does not exist, and do not
reach into `experimental/` to manufacture some. An engine change is verified
today by running what is still in the build and by argument, and a change that
would once have been gated should say in its own commit what was and was not
checked.

Still in the build and still applicable: the architecture gate (every build),
the two emulator batteries below, `dotnet build Puck.slnx -c Release`, and
running `Puck.World`.

| Change | Minimum verification |
|---|---|
| Fixed-point, commands, input routing, bindings, world documents | Tier A |
| Same-device GPU code, kernels, compositor, capture | Tiers A and B |
| Shared shaders, either backend, surface sharing | Full battery, including Tier C |
| Present pacing, device loss, backend switching | Full battery, including Tier D |
| Suspected backend divergence | Differential fuzzer with `--filter fuzz` or one seeded fuzz stage |

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

`Puck.World` is the only composition root that runs. `Puck.Demo` is a library
with no entry point, quarantined at `experimental/Puck.Demo` and OUT of the
solution and root build (owner ruling, 2026-08-01) — every `dotnet run` against
it, including the former `--validate-overworld` check, is void. Nothing plans
bringing its capabilities across: the port plan that used to sequence that work
was deleted with the quarantine. Do not add a `--validate-*` mode or a
`Puck.Post` stage for game-specific behavior unless explicitly requested.

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
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release
```

Self-contained tiers run without external ROMs. Reference-ROM stages skip
when their licensed corpus is unavailable. The Advanced battery also exposes
lockstep, trace, I/O-dump, render-hash, and divergence diagnostics; see its
project README.

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

`src/Puck.World.Data/README.md` documents the serializer's construction
behavior; the procedure above is the complete add-a-field procedure.

## Configuration and diagnostics

`Puck.World` does not use `PUCK_*` configuration variables. Durable
configuration belongs in the world document; live operations belong in console
verbs.

The remaining environment variables are engine, launcher, emulator, or
content-development diagnostics:

| Variable | Purpose |
|---|---|
| `PUCK_RAY_QUERY` | Permit or deny the ray-query path. |
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
- Windows App Control on the reference system blocks loading never-seen Debug
  binaries (`FileLoadException` `0x800711C7`), which broke the file-based
  `dotnet run <script>.cs` programs at their default Debug configuration —
  relocating the runfile cache did not help; `-c Release` loaded cleanly. Kept
  because the App Control behaviour is a property of the machine and will bite
  the next thing that loads a fresh Debug binary, not because those scripts are
  reachable: they are quarantined under `experimental/` and off limits.

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
7. Verify a handed-off defect is still live — against the worktree **and** the
   commit — before recording it anywhere. A fix and a report can cross, and a
   wrong belief committed to a shared ledger outlives every session that could
   refute it. When one is already written down, correct it in place rather than
   leaving the claim standing.
