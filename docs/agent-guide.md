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
32 fail-isolated stages, expected 32/32 green on a healthy machine with an
RTX-class GPU (on iGPU-only machines like the Surface, `rt` SKIPs and
`reverse-share` FAILs — hardware conditions, not regressions).

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
funnel) · **B** same-device GPU smoke (compute, resample, viewports, pixelate,
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
  cabinet to the camera cart type — it opens the default device on the GPU
  zero-copy tier; the share path itself is Post C6.
- The overworld (the default `Puck.Demo` run — see
  [overworld-demo-plan.md](overworld-demo-plan.md); the demo's other faces are
  `--run <document>`, which runs `world` documents live through the shared
  renderer on the host backend, and `--rom <path>`, which synthesizes a
  fullscreen one-machine world document) is GREENFIELD — verify demo work by
  RUNNING it, not by a gate. It keeps exactly one OPTIONAL demo-resident
  self-check, `--validate-overworld` (pure-CPU determinism + replay), which you
  may run by hand (Puck.Post can't reference the demo). Only genuine ENGINE
  contracts — determinism/replay, the paged binding-profile resolver, and
  cross-backend world parity — live in the Puck.Post battery above; a demo
  feature is not promoted there unless the user explicitly asks (see the
  anti-calcification doctrine, rule 5). The overworld's staged boot layouts are captured headlessly with
  `PUCK_OVERWORLD_DEBUG_BOOT` + `PUCK_OVERWORLD_CAPTURE_FRAME` (env table below). Whole-app
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

## Environment variables

| Variable | Effect |
|---|---|
| `PUCK_TIMING=1` | Per-pass GPU-ms timestamps + share, both backends (document: `host.timing`) |
| `PUCK_RAY_QUERY` | Permit/deny the ray-query path (document: `host.rayQuery`) |
| `PUCK_GENLOCK=0` | Disable the genlock phase-align control law (document: `host.genlock`) |
| `PUCK_PRESENT_TIMING` | Log measured present intervals |
| `PUCK_D3D12_DEBUG` | Opt-in D3D12 GPU validation layer — **see gotcha below** |
| `PUCK_PARITY_STRICT=1` | Opt into the STRICT pixel-perfect parity calibrations (the long-term ideal). The default posture is RELAXED: cross-backend gates keep only the mean/spread guards a real divergence cannot dodge and shrug at FP-noise classes (±1 redistribution, boundary-winner flips), which re-roll with every shader-codegen change. Both `ParityThresholds` copies (Demo + Post) honor it |
| `PUCK_CAPTURE_FRAME=N` | Delay the one-shot `--capture` to the Nth produced frame on a `world`-document run (the engine node's capture; overworld runs use their own variable below) |
| `PUCK_OVERWORLD_CAPTURE_FRAME=N` | Overworld runs: delay the one-shot `--capture` to the Nth produced frame — the plain `--capture` grabs frame 0 (black, pre-boot). Frames accrue ≈25–40/s of wall time (startup + emulators) — keep N ≲ 200 and give `--exit-after-seconds` headroom. **The deterministic-screenshot recipe**: this + `--capture <png>` (+ `PUCK_CREATOR_LOAD` for creator scenes) |
| `PUCK_OVERWORLD_DEBUG_BOOT=t0,t1,t2` | Boot overworld console 0,1,2… at those sim ticks (240 ticks/s) — walks every layout stage headlessly for captures |
| `PUCK_OVERWORLD_DEBUG_PLAYERS=N` | Bare-room overworld only: spawn N scripted players that walk apart (split-screen capture without controllers) |
| `PUCK_OVERWORLD_CELL=N` | Place the overworld room at a far world cell (planet-scale coordinate demo) |
| `PUCK_OVERWORLD_CREATOR=1` | Enter creator mode at startup (headless authoring screenshots). **The 3D-capture recipe**: the demo BOOTS SEATED INSIDE A BRICK GAME, so a naive `--capture` photographs the fullscreen brick pane — set this on EVERY capture run that wants the SDF room; companions and the workbench subject both render in that view |
| `PUCK_CREATOR_LOAD=<name-or-path>` | Load a saved creation (a `./creations/*.creation.json` name or a file path) and enter creator mode at startup — the headless proof hook for creator scenes |
| `PUCK_COMPANION_LOAD=<names>` | Spawn comma-separated saved creations as wandering room companions at boot (resolves CAS refs, then `./creations/`) — the headless companion-capture hook |
| `PUCK_WORLD_ROUNDTRIP=1` | Boot-time bit-for-bit proof for the world sculptor: save the live world (through the walk-grid bake), reload from disk, byte-compare — prints `[world-roundtrip] MATCH/MISMATCH` to stderr. Never a gate |
| `PUCK_LINK_CABLE_PROBE=1` | Fake a linked cabinet pair (0,1) so the diegetic link cable renders headlessly — presentation-debug only |
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
- **Camera zero-copy is DXVA→ARGB32**, D3D12 simultaneous-access targets
  written via RTV (not UAV). The NV12/ycbcr path does not work here.
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

- **Comments/docs describe the code as it is**, not the change history.
- **Derive, don't hardcode** (descriptor counts, pool sizes, strides come from
  the data that defines them).
- **Coupling ceilings are guardrails** — CA1506/CA1502 run as errors and
  actively guard `OverworldRenderNode`, `EnsureResources`, `Program`'s Main,
  and the command modules' `GetCommands`. Compose new demo subsystems INSIDE
  `OverworldFrameSource` behind primitive-typed forwarder members, extract
  helpers, and split registration iterators into sub-iterators — never raise
  the limits.
- **.NET 10 everywhere**; consult the `dotnet10-performance` skill
  (`.claude/skills/`) before micro-optimizing or asserting "X is slow".
- **Merges**: feature branches land on `main` as a single squash commit with a
  hand-written summary — no WIP noise, no merge bubbles, no `Co-Authored-By`
  trailers.
- **Determinism is a feature**: no wall-clock, RNG, or floats in gameplay
  state; input becomes per-tick `CommandSnapshot`s; fixed-point math comes
  from `Puck.Maths`. If your change can break replay, Post A3 (engine
  determinism/replay) is your gate.

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
