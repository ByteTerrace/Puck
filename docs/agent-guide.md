# Working on Puck — the guide for future users and agents

How to orient, verify, and avoid the traps. Read
[capability-catalog.md](capability-catalog.md) for *what exists* and
[project-map.md](project-map.md) for *where it lives*; this doc is *how to
work here without re-learning hard-won lessons*.

## Orientation in 60 seconds

- Puck is an **everything-as-data** engine: a run is a `puck.run.v1` JSON
  document; `Puck.Demo` is just the composition root that turns a document
  (or legacy CLI flags, which synthesize a document) into a node graph on a
  real GPU. One code path — there is no separate "flag mode".
- There are **two GPU backends at parity** (Vulkan, Direct3D 12) behind
  `Puck.Abstractions`. Anything you change in the shared path must hold on
  both; the validation story below exists precisely for this.
- `experimental/` (GamingBrick emulators, BareMetal) is **decoupled** from
  `src/` in both directions. Don't add references across that line.
- Not everything is equally settled — check the **stability tiers** in
  [project-map.md](project-map.md#stability) before reshaping an API or
  treating existing code as precedent. Backend bindings are stable; parts of
  the `Puck.Abstractions` GPU seam have a queued rename/reshape backlog; the
  Commands legacy-vs-snapshot duality is an open decision.
- All functionality lives in the split `Puck.*` projects; there is no
  `src/Puck` / `src/Puck.Avatars` monolith. Never add references to those paths.

## How to verify anything

**The default answer is `Puck.Post`.** It is the engine's power-on self-test:
fail-isolated stages across four tiers, expected all-green on a healthy
machine with an RTX-class GPU (on iGPU-only machines like the Surface, `rt`
SKIPs and `reverse-share` FAILs — hardware conditions, not regressions). Don't
hardcode the stage count here — it has drifted before (25 → 32 → 33); the
battery's own summary line (`N stage(s): N pass, ...`) is always current.

```powershell
dotnet run --project src/Puck.Post -c Release                  # full battery
dotnet run --project src/Puck.Post -c Release -- --tier A      # CPU-only pre-flight (no GPU needed)
dotnet run --project src/Puck.Post -c Release -- --filter world
dotnet run --project src/Puck.Post -c Release -- --stage binding-page
dotnet run --project src/Puck.Post -c Release -- --fuzz-seed 12345
dotnet run --project src/Puck.Post -c Release -- --artifacts out/
```

Exit codes: 0 all pass, 1 a check failed, 2 infrastructure failure. Tiers:
**A** CPU pre-flight (fixed-point, WorldCoord3, determinism/replay, CLI⇒document
synthesis, paged binding-profile resolver, genlock control law, run-document
funnel, victory-gate layout) · **B** same-device GPU smoke (compute, resample, viewports, pixelate,
capture, world pipeline, dynamic transforms) · **C** cross-backend
(export/import both directions, indirect args, world + world-child +
world-screen + RT parity, instancing/swarm exactness, camera share,
differential fuzz) · **D** live subsystems (GPU budget,
present cadence, device-lost, backend hot-switch — D3/D4 relaunch the exe as
`--probe` children).

**Rules of thumb:**

- Touched shared GPU code, shaders, or either backend → run the full battery,
  or at minimum Tier B + C.
- Touched sim/input/document/bindings code → Tier A is fast and usually
  sufficient (`--stage binding-page` isolates the paged binding-profile
  resolver).
- Suspect a backend divergence → the differential fuzzer: Post `--filter fuzz`
  (or `--stage fuzz --fuzz-seed N` for one seed), or a run document with a
  `fuzzing` section.
  The standing bar: backends bit-equivalent modulo ±1 LSB.
- Live hardware peripherals (a real webcam) are proven by cycling an overworld
  cabinet to the camera cart type — it opens the default device on the
  CPU-pixel tier (feeding the emulated Pocket Camera sensor), not the GPU
  zero-copy tier, which is built and hardware-verified but has no live
  consumer wired today (see the hardware gotcha below). The generic zero-copy
  handle-import mechanism, exercised with a synthetic frame, is Post's
  `camera-share` stage (`--filter camera-share`).
- The overworld (the default `Puck.Demo` run — see
  [overworld-demo-plan.md](overworld-demo-plan.md), whose top-section
  "unification contract" is the demo's north star) is ONE session, not a menu
  of launch modes: every capability is reachable in-game (a diegetic act, a
  pad chord, or a console verb), never only behind a flag. The composition
  root can ALSO load a bare `world` document (`--run <document>`, live through
  the shared renderer on the host backend) or boot straight into an immersed
  ROM (`--rom <path>`, synthesizing a fullscreen one-machine world document) —
  these are developer/CI launch conveniences, not separate products. This is
  GREENFIELD — verify demo work by RUNNING it, not by a gate. It keeps exactly
  one OPTIONAL demo-resident
  self-check, `--validate-overworld` (pure-CPU determinism + replay), which you
  may run by hand (Puck.Post can't reference the demo). Only genuine ENGINE
  contracts — determinism/replay, the paged binding-profile resolver, and
  cross-backend world parity — live in the Puck.Post battery above; a demo
  feature is not promoted there unless the user explicitly asks (see the
  anti-calcification doctrine, rule 5). The overworld's staged boot layouts are captured headlessly by piping a
  console-verb script over stdin — `boot <i>` / `step <n>` / `settle` / `capture <png>` (see the console-verb
  section below; the demo `PUCK_*` capture env vars are gone). Whole-app
  capture PNGs are only MARGINALLY stable across runs (tick allocation sits
  near a wall-clock boundary) — the pixel proof style is pane-vs-pane bit-lock
  WITHIN one capture, never cross-run file equality.
- Emulator work → the matching battery:
  `dotnet run --project experimental/Puck.HumbleGamingBrick.Post -c Release`
  (or `...AdvancedGamingBrick.Post`). Tier A needs no assets; Tier B skips
  cleanly when the ROM corpus is absent. GBA diagnostics (`--lockstep`,
  `--trace-cycles`, `--iodump`, …) live in the same Post exe — see its
  [README](../experimental/Puck.AdvancedGamingBrick.Post/README.md).

**The Demo is greenfield — don't gate it.** A change under `src/Puck.Demo/`
(overworld, cabinets, forge ROMs, creator mode, presentation, layout) is
verified by RUNNING the demo, not by a gate: don't add a `--validate-*` flag,
and don't add a Post stage for a demo feature unless the user explicitly asks.
Post is for the shared engine contract; promoting a demo-born feature into it is
the user's call, not yours. See the anti-calcification doctrine (rule 5) below.

## Analyzing C# code: compiler and Roslyn before grep

This is a .NET 10 / C# codebase — text matching answers *where is* questions,
but it cannot answer *semantic* questions (who uses this symbol, what
implements this interface, is this dead, is this rename safe). For those, use
the `roslyn-first-analysis` skill (`.claude/skills/roslyn-first-analysis/`):
it carries **tested, copy-paste** `dotnet run` file-based scripts —
`SymbolFinder` find-references over the whole `.slnx`, and a ~2 s syntax-tree
sweep — plus the traps that made grep-based audits nearly delete live code
here (extension methods, name collisions, doc-cref trivia). Rules of thumb:

- `dotnet build` is the cheapest full-semantic check — after a rename or
  signature change, build; don't grep for stragglers.
- Grep/Glob remain correct for orientation, literal strings, and non-C#
  assets (HLSL, JSON, MSBuild). Use them freely there.
- Never assert "unused/dead" from text search alone.

## External assets (not in the repo)

Reference corpora and BIOS/ROM images live outside the tree (licensing).
Batteries skip-with-note when they're absent.

| What | Where it's looked for |
|---|---|
| GBA BIOS (real, 16 KiB) | `PUCK_GBA_BIOS` (dev box: `D:\Source\ByteTerrace\Temp\GBA_bios.rom`) |
| jsmolka gba-tests / mGBA suite / AGS checker / commercial GBA ROMs | `PUCK_GBA_TESTROMS`, `PUCK_GBA_MGBA_SUITE`, `PUCK_GBA_AGS` (TCHK10 dump only), `PUCK_GBA_GAMES` |
| GB/GBC test suites (blargg, mooneye) | `--roms` flag, else `PUCK_GB_TESTROMS` |

## Driving the demo over stdin (the agent-facing control surface)

Per the unification contract (rule 3,
[overworld-demo-plan.md](overworld-demo-plan.md#the-unification-contract-read-first--the-north-star-for-this-arc)),
the on-screen console and process **stdin** drive ONE command registry, and
results echo to **stdout** — pipe a verb script (or `--script <file>`) into a
run and read the echoed results. This is the agent-facing control +
deterministic-testing surface: it is how a script or another agent reaches a
demo capability (boot a cabinet, load a world, wire a companion's feed, spawn
players, capture a frame) without a GUI and without a bespoke env var per
capability, and it is how demo changes get verified — by RUNNING the demo,
now reproducibly instead of by hand. The stdin→registry→stdout transport
already exists in the launcher; verb COVERAGE (naming a console verb for
every capability below) is the in-progress work — see the migration table in
the unification contract for which verbs replace which env vars today vs. as
TODOs.

## Reviewing a creation: the scenario harness (THE capture recipe)

To photograph a `puck.creation.v1` creation, do NOT hand-roll env vars — use
the scenario harness (landed 2026-07-06, `src/Puck.Demo/Configuration/` +
`Overworld/CaptureSequencer.cs`):

```
dotnet run --project src/Puck.Demo -c Release -- \
  --scenario review-creation \
  --scenario-set Scenario:Creation=docs/examples/creations/lantern-fish.creation.json \
  --scenario-set Scenario:Capture:Directory=artifacts/my-review
```

That is an 8-shot orbit turntable on a NEUTRAL STUDIO backdrop (flat gray,
even lighting; no room, cabinets, easel, placement ghost, goal markers, or
selection highlights — the creature alone). Per shot the harness applies the
camera pose, settles ≥`SettleSeconds` (default 0.5 s) of wall clock AND ≥3
produced frames, captures, and advances; the run exits by itself after the
last shot (`[scenario] complete — N/M shots written` on stderr;
`ExitAfterSeconds` is only a generous safety net). Wall clock never touches
rendered content — each shot's camera pose and content time are pinned from
the plan (`StartTime + shot·TimeStep`), so runs are byte-identical at any
frame rate, including under GPU contention. `Scenario:Backdrop=room` gives
in-context arcade shots; scenario JSON < `PUCK_*` env vars < `--scenario-set`
in configuration precedence. Scenario files live in `src/Puck.Demo/scenarios/`.

The env vars below remain the low-level hooks (they are a configuration
provider now) — reach for them for room/layout/boot captures, not for
creation review. As with the rest of the table below, prefer the console-verb
form (piped over stdin) where one already exists.

## Environment variables — the demo `PUCK_*` surface is GONE

Per the unification contract (rule 2), **the demo's entire `PUCK_*`
environment surface was REMOVED** (the unification arc's Slice 3). There is no
`PUCK_OVERWORLD_*`, `PUCK_COMPANION_*`, `PUCK_CREATOR_LOAD`,
`PUCK_LINK_CABLE_PROBE`, `PUCK_WORLD_ROUNDTRIP`, or `PUCK_CONSOLE_OPEN` any
more — setting one is inert. Reach every former capability through a **console
verb** (piped over stdin, per the section above) or a **run-document field**:

- Driving / capture: `boot <i>`, `cart <i> <type>`, `reveal`, `link <i> <j>`,
  `player.add` / `join`, `capture <png>` (after `step <n>` / `settle`), `state`.
- Authoring: `creator`, `creator.load <name>`, `companion.add`, `world.wire`,
  `companion.face`, `world.verify`.
- Durable config: the overworld node's `world` and `cell` fields (and each
  console's cart — set live with `cart`).
- **The 3D-capture recipe** (was `PUCK_OVERWORLD_CREATOR=1`): the demo boots
  seated INSIDE a brick game, so a naive `--capture` photographs the fullscreen
  brick pane. To shoot the SDF room, drive `creator` (or a `--scenario`) so the
  authoring view renders — the workbench subject and companions render there.
  For a settled deterministic shot, `boot`/`step`/`settle` then `capture <png>`.

The authoritative migration table lives in
[overworld-demo-plan.md](overworld-demo-plan.md#the-unification-contract-read-first--the-north-star-for-this-arc).

Non-demo **engine / launcher diagnostics** are a separate concern and are
UNTOUCHED (these are not in the demo `Demo:*` config; three are still mapped as
launcher toggles):

| Variable | Effect |
|---|---|
| `PUCK_TIMING=1` | Per-pass GPU-ms timestamps + share, both backends (document: `host.timing`) |
| `PUCK_RAY_QUERY` | Permit/deny the ray-query path (document: `host.rayQuery`) |
| `PUCK_GENLOCK=0` | Disable the genlock phase-align control law (document: `host.genlock`; launcher toggle) |
| `PUCK_PRESENT_TIMING` | Log measured present intervals (launcher toggle) |
| `PUCK_TEST_DEVICE_LOSS=<sec>` | Synthetic device-loss after N seconds (launcher toggle; used by Puck.Post) |
| `PUCK_D3D12_DEBUG` | Opt-in D3D12 GPU validation layer — **see gotcha below** |
| `PUCK_PARITY_STRICT=1` | Opt into the STRICT pixel-perfect parity calibrations (the long-term ideal). The default posture is RELAXED: cross-backend gates keep only the mean/spread guards a real divergence cannot dodge and shrug at FP-noise classes (±1 redistribution, boundary-winner flips), which re-roll with every shader-codegen change. Both `ParityThresholds` copies (Demo + Post) honor it |
| `PUCK_CAPTURE_FRAME=N` | Delay the one-shot `--capture` to the Nth produced frame on a `world`-document run (the engine node's capture; the `--run`-launch developer/CI affordance) |
| `PUCK_FLAGSHIPS_REGENERATE=1` | Turn `--forge-flagships` into the content-UPDATE mode: rewrite `docs/examples/creations/*.creation.json` from the recipes (the add-a-field ritual for creation-schema evolutions). Default mode stays the loud byte-identical assertion |

## Hardware gotchas (hard-won; verify before "fixing")

Each of these is a real-hardware finding. If a symptom pattern-matches one of
these, the listed conclusion is settled — don't re-litigate it.

- **D3D12 debug layer breaks device creation** on this Win11 26200 / RTX 4070
  box: `EnableDebugLayer` → the next `D3D12CreateDevice` fails with
  `0x887A0007`. That's why the layer is opt-in via `PUCK_D3D12_DEBUG`.
- **Vulkan import of a D3D12 shared texture needs handle type `0x40`**
  (`D3D12_RESOURCE`), not `0x10`, on NVIDIA.
- **D3D12 compute heap slots are PACKED in binding order** — a binding index
  is a logical id, not a heap offset. Use `WriteStorageBufferReadOnly`
  (stride-4 SRV) for small read-only structured buffers, and derive descriptor
  pool sizes via `GpuDescriptorPoolSizes.ForSets` — never hand-count.
- **In-process Vulkan cannot survive full GPU removal on NVIDIA**: the ICD
  wedges and `vkCreateInstance` fails forever until a new process. A TDR
  (Win+Ctrl+Shift+B) is absorbed fine. Don't chase "recovery" past that line.
- **Camera zero-copy (DXVA→ARGB32, D3D12 simultaneous-access targets written
  via RTV not UAV — the NV12/ycbcr path never worked) is built and
  hardware-verified but has no live consumer today.** Its child render node
  (the old per-viewport camera pane) was retired when rendering centralized
  into `SdfWorldEngine`/`SdfEngineNode`; `LiveCameraSource` is rejected at
  graph-build time as a result. `ICameraCaptureService.TryOpenSharedDefault`,
  `Win32MediaFoundationSharedCameraSession`, and
  `DirectXGpuSurfaceExportFactory.CreateSimultaneousAccessStorageImage` are
  kept intentionally for the re-host (confirmed unreachable via Roslyn
  `SymbolFinder`, not a dead-code deletion candidate — each carries a doc
  comment saying so). The live camera path that runs today is the CPU-pixel
  tier (`TryOpenDefault`), used only by the overworld's Pocket Camera
  peripheral. The generic cross-API zero-copy mechanism the camera tier rode
  is still real and gated (Post `camera-share`) — it carries a synthetic
  frame, not a camera frame.
- **Steam Deck bare metal**: no serial — GOP is the only console; the
  framebuffer must be mapped **write-combining** and the global `wbinvd`
  removed (AMD DCN doesn't snoop CPU cache). Deck reports no-x2APIC and
  no-IOMMU; both are handled.
- **mGBA co-sim quirks**: cumulative cycle counters rebase every frame
  (compare per-instruction deltas), and our PC reads +4 vs mGBA's
  (pipeline representation).
- **Shader toolchain**: DXC compiles the single HLSL source to both SPIR-V and
  DXIL at build time; `dxc` must be on PATH (Vulkan SDK or Windows SDK). There
  is no GLSL and no `glslc` step anywhere in the build. The C# ISA in
  `Puck.SdfVm` and the `sdf-vm` HLSL must change **together**.
- **Stale build artifacts fake regressions.** After heavy cherry-picking (or a
  crashed process), a checkout's incremental build can serve STALE committed
  shader bytecode (or a corrupted `obj` ref assembly) from `bin/` — the
  symptom is perf/behavior that matches NO commit in history (e.g. new C#
  emitting a sentinel an old kernel mishandles). The fast diagnostic, BEFORE
  any bisect: build and run the same commit in a fresh `git worktree` — if
  that is healthy, the code is innocent; `rm -rf` the dirty checkout's
  `obj/` + `bin/` (at minimum `Puck.SdfVm` + `Puck.Demo`) and rebuild.

## Anti-calcification doctrine

Extensive skills and unit tests that pin internal structure lead agents to
defend the artifacts against demanded refactors. These rules exist to keep
that from happening.

1. **Precedence.** The user's current instruction outranks everything
   written: this guide, CLAUDE.md, skills, gates, code comments, and
   precedent in the code. When a written artifact argues against a change
   the user is demanding, the artifact is stale by definition — **update it
   in the same change**. Never soften, defer, or revert the demanded change
   to keep an artifact green or self-consistent.
2. **Gates assert contracts, not shapes.** Engine validation lives in the POST
   batteries (the greenfield Demo is exempt — rule 5), and a stage may assert
   only *observable outcomes*: pixel/frame
   hashes, cross-backend parity, determinism/replay identity, exit codes,
   measured budgets. No mocks of internal interfaces, no call-sequence
   assertions, no tests that break when a type is renamed but behavior is
   unchanged. If a refactor breaks a gate, first ask whether the gate pinned
   an implementation detail — if so, the gate is the bug.
3. **Skills are few, and factual or procedural only.** A skill may carry
   facts (hardware behavior, .NET performance reality) or procedures (how to
   verify a change). A skill may **not** prescribe architecture, code
   structure, or design patterns — that's what turned skills into a noose.
   Every skill must state what it does *not* govern and include the
   precedence clause from rule 1. If a skill ever makes you resist a
   requested change, that is the signal it has gone stale: say so and fix
   the skill. The roster (`.claude/skills/`) is six project skills:
   `verifying-puck-changes` (which gate proves what),
   `gaming-bricks` (the emulators' settled contract facts),
   `roslyn-first-analysis` (semantic C# analysis over grep),
   `sdf-world` (the SDF VM + world renderer + render-assembly contract
   pairs),
   `run-document` (the document doctrine + the add-a-field ritual), and
   `rom-forge` (the forge's settled facts: the SM83 game framework, the
   SDF→brick bake pipeline, Brickfall) — plus the
   general `dotnet10-performance` fact pack.
4. **Stability tiers gate friction, not change.** The
   [tiers in project-map.md](project-map.md#stability) tell you how much
   evidence a reshape deserves — *stable* means "bring a reason," never
   "refuse." Nothing in this repo is change-proof.
5. **The Demo is greenfield — never gate it, never promote it.** `Puck.Demo`
   (the overworld + everything under `src/Puck.Demo/`) is the playground:
   expected to churn and be rewritten, never settled precedent. Demo changes
   are NOT engine changes — verify them by RUNNING the demo
   (`dotnet run --project src/Puck.Demo -c Release -- --exit-after-seconds 2`;
   0 or less runs until the window is closed),
   not with Post, a `--validate-*` flag, a hash, or a `*DeterminismNode` hook.
   **Never move a demo-born feature into Post on your own initiative** — a Post
   stage/gate for a demo feature is the user's explicit call. Post (rule 2)
   gates the shared engine contract only — the cross-backend render path, the
   SDF VM ISA, the run-document schema, the deterministic numerics/backends;
   when a demo change also touches one of those, gate only that engine part.
   This rule exists because demo work kept getting gated and demo features kept
   landing in Post unasked — the exact calcification these rules forbid.

## Conventions

- **Where each convention is WRITTEN (the law lives next to the code — read
  the doc comment at the site before reworking that area):**

  | Concern | The written rule |
  |---|---|
  | Config naming (`*Options` = config-bound POCO; why `HostSettings` isn't one; env→config is the ONE direction; `*CliSeams` = the CLI-surface escape) | `src/Puck.Demo/Configuration/DemoConfiguration.cs` type doc |
  | Command modules (ctor pattern; when a `*CommandModule`/`*Commands` split is warranted; Tracker's documented exception) | `src/Puck.Commands/ICommandModule.cs` type doc |
  | Screen-attach seam (the token-claimant contract) | `src/Puck.Demo/Overworld/ScreenSlotLedger.cs` type doc (+ the bullet below) |
  | GPU host composition (the ORDER-MATTERS Vulkan-wins rule) | `src/Puck.Demo/GpuHostComposition.cs` |
  | SDF C#↔HLSL contract pairs (incl. parked instances) | the `sdf-world` skill |
  | Document evolution (the add-a-field ritual) | the `run-document` skill |

  One vocabulary, one idea per name (Arc 4): layout tiling =
  `ScreenLayoutDirector`, capture sequencing = `CaptureSequencer`, the offscreen
  feed pool = `CameraFeedPool`, lock-free publish buffer = `PublishBuffer<T>`,
  JSON persistence = `*DocumentStore`, static registrar = `*Registrar`, static
  content table = `*Tables`. Don't reintroduce a second name for one of these
  ideas — and don't let a feature noun (a specific creature/game) become a type
  or seam name; the primitive gets the name, the feature is its first content.

- **Comments/docs describe the code as it is**, not the change history.
- **Derive, don't hardcode** (descriptor counts, pool sizes, strides come from
  the data that defines them).
- **Coupling ceilings are guardrails** — CA1506/CA1502 run as errors and
  actively guard `OverworldRenderNode`, `EnsureResources`, `Program`'s Main,
  and the command modules' `GetCommands`. Compose new demo subsystems INSIDE
  `OverworldFrameSource` behind primitive-typed forwarder members, extract
  helpers, and split registration iterators into sub-iterators — never raise
  the limits.
- **How a subsystem gets content onto the render (the screen-attach seam)** —
  there is ONE blessed way to put content on a diegetic screen surface:
  register a claim with the `ScreenSlotLedger` (directly, or through
  `OverworldFrameSource.RegisterScreenClaimant` for callers outside the frame
  source). A claimant is an opaque, reference-stable OWNER TOKEN plus a
  `ScreenSlotPriority` band and an optional preferred slot; the ledger
  arbitrates slots role-blind (identity lives in the token, never in the
  ledger), and the caller supplies its own source/light/transform providers.
  Cabinets, the creator easel, and companion faces all ride this seam.
  **Do NOT** invent a new attach mechanism for a screen source — no raw
  `Func<nint>`/`Func<Vector3>` callback drilled through `OverworldRenderNode`,
  no direct `m_*` field poll in `OverworldFrameSource`. Those older drills
  exist only for non-screen wiring (camera pose, link-pair reporting, the
  frame source's OWN composed sub-objects); a screen source rides the ledger.
  See `Overworld/ScreenSlotLedger.cs`'s type doc for the full contract (band
  semantics, token identity, the per-pass re-claim convention).
- **.NET 10 everywhere**; consult the `dotnet10-performance` skill
  (`.claude/skills/`) before micro-optimizing or asserting "X is slow".
- **Merges**: feature branches land on `main` as a single squash commit with a
  hand-written summary — no WIP noise, no merge bubbles, no `Co-Authored-By`
  trailers.
- **Determinism is a feature**: no wall-clock, RNG, or floats in gameplay
  state; input becomes per-tick `CommandSnapshot`s; fixed-point math comes
  from `Puck.Maths`. If your change can break replay, Post A3 (engine
  determinism/replay) is your gate.

## Leading a wave of agents (the orchestration playbook)

Puck's last four arcs were built by parallel worker agents led by one
orchestrator. This is the distilled method — it works even when the lead is
a smaller model, because the discipline, not the intelligence, carries it.

**The shape of a wave.** Read-only AUDIT fan-out first (scouts produce
reports, touch nothing) → consolidate → bring the forks to the USER as
explicit either/or questions BEFORE rewriting anything → then implementation
workstreams in parallel git worktrees, one agent per workstream, the lead
integrating. Prompt the user liberally at every genuine fork; never
self-defer scope ("do the whole ask" is standing policy — hedging and
partial delivery are the real hazard, git is the rollback).

**A worker brief must carry** (every omission here has burned a session):
1. The exact BASE COMMIT to check out — worktrees sometimes spawn pinned to
   a stale commit; the first instruction is always `git log --oneline -1`,
   verify, `git checkout --detach <tip>` if wrong, confirm clean status.
2. FILE OWNERSHIP — the explicit list of files/dirs the agent owns, and the
   files concurrent agents own that it must NOT touch. Parallel waves stay
   mergeable only when ownership is disjoint; when overlap is unavoidable,
   tell the agent to keep its edits in the shared file minimal and
   mechanical, and merge the semantic change first, the mechanical one on top.
3. The RELEVANT SKILL(s) to load first, and the audit-report section (by
   path) it implements — agents re-derive everything you don't hand them.
4. Analyzer-ceiling warnings: CA1502/CA1506 run as errors and several hot
   types sit at EXACT limits; name the repo's escapes (frame-source
   composition, static logic classes, `*CliSeams`) — never suppression.
5. The VERIFICATION recipe, concretely: which runs to make (piped console-verb
   scripts over stdin + the scenario harness), what "working" looks like, and
   the standing rule — demo work is verified by RUNNING, engine work by the
   Post battery. Include the capture gotcha (drive `creator` or a `--scenario`
   so the SDF room renders, not the fullscreen brick pane) in every brief that
   captures.
6. Commit discipline: logical commits in the worktree, hand-written
   messages, no `Co-Authored-By` trailers.

**Judging and integrating workers.**
- Judge a background agent by its WORKTREE (git status/log + artifact
  mtimes), never by its transcript, which flushes lazily or never.
- Integration = cherry-pick the worktree commits onto the mainline, oldest
  first; after each workstream, the lead INDEPENDENTLY re-verifies (build +
  the workstream's own run recipe) — a worker's green report is evidence,
  not proof.
- When a worker stalls, diagnose from its worktree diff and SEND it the repo
  precedent it's missing (a message, not a relaunch); when a worker starts
  doing something the user wouldn't want, a mid-flight message redirects it
  cheaply.
- After integration, before trusting any perf number: fresh-worktree
  cross-check (see the stale-artifact gotcha above), and note GPU/CPU
  contention from still-running agents — timing taken during a wave is
  evidence of SHAPE (share-of-frame), never of absolute cost.

**Standing decisions a lead must not re-litigate** (each was an explicit
user ruling): the demo is greenfield (rule 5); parity posture is RELAXED by
default; built-ahead scaffolding (`WindowProbe`, `LinkModuleVerify`, the
GPU camera tier) is KEPT — deleting intentionally-future code requires the
user's sign-off even when it's provably unreachable, and such code carries a
"BUILT-AHEAD, NOT YET WIRED" doc comment precisely so sweeps don't re-flag
it; volatile facts (stage counts, hashes) are never hardcoded into living
docs — point at the live output that prints them.

## Docs taxonomy — everything in docs/ is LIVING

`docs/` carries only living references and active plans — trust every file
there; the index at [docs/README.md](README.md) lists them. Completed
design/investigation write-ups are NOT kept in the tree: they live in git
history only, with their durable knowledge distilled into a living home first
(the skills carry the settled contract facts — e.g. the GB PPU mid-mode-3
root cause lives in `gaming-bricks`). When a living doc stops being true,
fix it or retire it the same way — distill, then delete.

- **Living references**: capability-catalog, project-map, agent-guide (this
  file), feature-parity-summary/table, platform-display-kinds, `examples/`,
  plus the per-project READMEs (`Puck.Input`, `Puck.Demo`,
  `Puck.Demo/Forge/Framework`, `Puck.Demo/Forge/Bake`, `Puck.BareMetal`,
  `AdvancedGamingBrick.Post`, …).
- **Active plans**: overworld-demo-plan (the demo's plan of record),
  game-studio-plan (the in-engine studio's road ahead),
  sdf-world-render-centralization-plan (status block up top),
  machine-fleet-plan + machine-fleet-briefing (fleet performance),
  ideal-gaming-brick-plan (the GB/GBC/GBA north star).
