# Machine-fleet performance briefing — the pre-plan

Status: the plan this briefing calls for exists, with the ▢ blanks filled by
measurement: [machine-fleet-plan.md](machine-fleet-plan.md) (the `--bench`
instrument, the scaling/latency numbers, the M3 verdict, the levers in
measured order). This doc is the workload-class and vision-posture record the
plan serves; §1 is the place new workload classes land.

Per-player machine CONTROL is live in the overworld —
proximity takeover (a player claims a booted machine off the shared
timeline and drives it alone; host-side input routing, never sim state),
and the buff/debuff reboots travel the persisted battery save
(cartridge-move semantics, RTC footer included). Details:
[overworld-demo-plan.md](overworld-demo-plan.md) status block. The first realtime
snapshot-migration promote/demote form is in place too: the Bricks page
can live-change a running Humble machine's `ConsoleModel` through
snapshot/restore without resetting its timeline cursor. The broader gameplay
rules for promotion/demotion remain §1 design work.

Read in this order: this doc →
[ideal-gaming-brick-plan.md](ideal-gaming-brick-plan.md) §5 (M3, the revised
execution order) + §7 (the perf mandate) →
[overworld-demo-plan.md](overworld-demo-plan.md) (the live integration + timeline)
→ `experimental/Puck.HumbleGamingBrick.Post/Stages/TrioLockstepStage.cs`
(the existing throughput measurement).

## 1. Why a fleet (the workload the plan must serve)

The demo's overworld (3 machines) is the SMALL case. The design target — from
day one, not speculative — is **many machines running simultaneously**, in
shifting combinations of:

- **Lockstep fleets** — one shared input timeline, N cursors (the overworld's
  `OverworldBrickTimeline` pattern). Same-costume machines are
  bit-identical by construction.
- **Independent fleets** — per-machine input streams; nothing shared but the
  clock.
- **Dynamic state churn** — machines booting, powering off, and having their
  state changed WHILE RUNNING: the mushroom/lightning promote-demote
  mechanics (`speed`/`runAs` are the boot-time half; the realtime half is
  snapshot → rebuild-as-new-config → restore, whose migration rules are the
  open design in overworld-demo-plan.md).
- **Diegetic machines** — a machine discovered IN the game world (the overworld
  stand is the demo's stand-in), with ROMs found in the world as items. One
  planned ROM renders a top-down/isometric view of the PLAYER *through the
  emulated device* — a lens that reveals things the normal view hides. That
  means an **engine → machine sensor feed** (world render → downsample →
  cartridge sensor), the generalization of the reserved camera-as-GB-camera
  peripheral seam. The ROM itself is deliberately undesigned.
- **Recursive composition** — machines feeding machines, the same way the
  compositor already nests screens (hosted child surfaces are a recursive
  node tree, Post C5). Expect chains: world → machine A's sensor → machine
  A's framebuffer → machine B's sensor…
- **ROM-first sessions** — the game can START inside a machine: the opening
  is a fullscreen, seemingly ordinary GB game (authored by us; beatable in
  5–60 minutes with branching exits), and only a later "zoom-out moment"
  reveals the avatar playing a humble device in the overworld. Presentation
  cost is just an inverted layout start (pane fullscreen, room hidden); the
  machine keeps running through the reveal because a pane IS the machine.
- **Machine → engine events** — the inverse of the sensor feed, and the
  channel the whole in-fiction economy rides on: an item found INSIDE a ROM
  promotes the real device (mushroom from within), an in-ROM embedded
  device asks the host to "insert" a cartridge (host-side mechanic — e.g.
  watch a mount point/drive letter and boot a nested machine from the file
  found there), an exit branch tells the host which door the player took.
  Natural carriers: the host posing as a link-cable peer (the serial-link
  seam is landed — `SerialLinkSession` in Puck.HumbleGamingBrick — though a
  host-as-peer endpoint is not), or a host-watched cartridge-RAM mailbox.
  Seam class to reserve, not design.
- **Link cable as GAMEPLAY** — trading between players/machines is a
  designed mechanic, which makes the ideal plan's M5 (two-machine link +
  cross-generation bit-lock) a gameplay dependency, not only the rule-3
  determinism milestone.
- **Short- and long-term interactions** — the interaction rhythm splits in
  two: SHORT-TERM is a realtime link-cable-class connection (machine PAIRS,
  cycle-locked co-stepping — rides M5); LONG-TERM is the **mailbox check**
  (wake a dormant machine, run K frames, read its cartridge RAM, re-freeze).
  In-fiction partners come first — everything stays inside the one
  deterministic world; real-human exchange is a later arc (reserve the
  exchange/network seam, design nothing).

Settled postures:

- **Scale is hundreds+** — machines as particles — reached via tiered
  fidelity, never brute force. **Both fleet shapes are core**: the plan must
  state a target for choirs (many machines, few timelines — amortizable) AND
  for independents (per-machine streams), e.g. "one ▢-machine choir plus ▢
  independents at realtime, simultaneously".
- **Dormancy = simulate on demand.** An unattended machine FREEZES as a
  snapshot and fast-forwards the elapsed span when observed or checked (or
  replays from epoch). Determinism makes this exact; hundreds of dormant
  machines cost ~zero; the budget that matters is **burst catch-up speed**
  (max machine-frames/s, one machine and K in parallel).
- **Weighting**: time/counterfactual mechanics (`Fork()` ghost echoes,
  run-ahead prophecy, rewind), the snapshot economy (save states as
  tradeable world items), the choir spectacle (bit-locked walls,
  one-divergent-pixel puzzles, unison demotion), and the interaction rhythms
  above lead; recursive composition stays reserved but sits below them.
  Snapshot/fork latency is thereby a *gameplay-facing* budget, not tooling.
- **ROM authoring stays deferred** until the machine→engine event carrier is
  designed.

These are examples, not the boundary — the direction of travel is
Frog-Fractions-grade whimsy, so treat "a machine is a first-class world
citizen with bidirectional, recursive seams" as the invariant and the
scenarios as samples. A ROM-authoring workstream (our own cartridges, with
host-mailbox conventions) is implied but undesigned; the GBA Post exe's
`--gen-rom` diagnostic is the existing ROM-generation tooling precedent.

Tone check for the plan: gameplay direction is deliberately surprising
(Frog Fractions-class genre shifts) — so the perf plan must buy GENERALITY
(headroom + elasticity), not a point-optimization of the 3-machine overworld.

## 2. What is already measured (do not re-measure blind)

| Fact | Value | Source |
|---|---|---|
| Emulator throughput, single-threaded | **532 machine-frames/s** with M3 devirtualization (full curves in [machine-fleet-plan.md](machine-fleet-plan.md)) | Humble Post `trio-lockstep` stage, RTX 4070 dev box |
| Per-machine-frame CPU cost | **≈1.8 ms** with M3 devirtualization (sizes the overworld catch-up cap of 4 segments/frame) | overworld-demo-plan.md; machine-fleet-plan.md |
| Hero-world GPU render | ≈0.9 ms/frame — the GPU is nowhere near the bottleneck | Post D1 `gpu-budget` |
| Brick present path | one CPU repack of the 160×144 framebuffer (≈92 KB) + one upload per machine-frame — the only CPU copy | `GamingBrickChildNode.UploadFramebuffer` |
| Fleet stepping | task-per-machine PARALLEL (prepare serial on the render thread, execute fanned out) | `WorldProducerNode.StepBricks` + `GamingBrickChildNode.PrepareStep`/`ExecuteStep` |

**The axes the bench measures (▢ = blanks the plan fills):** ▢ where the
per-frame cost actually goes (decode vs bus vs PPU vs clock fan-out); ▢
`MachineFactory.Create` cost (a full DI container per machine — matters for
spawn-on-stumble); ▢ `Snapshot()`/`Restore()`/`Fork()` latency and
allocation profile (the promote-demote budget, also the
ghost-echo/snapshot-economy budget); ▢ per-machine working set; ▢ the
machines-vs-throughput scaling curve, in BOTH fleet shapes (independent
streams vs one shared choir stream — the delta exposes memory-bandwidth
falloff at high N); ▢ burst catch-up rate (uncapped max machine-frames/s,
one machine and K in parallel — the simulate-on-demand dormancy budget); ▢
the mailbox-check cycle (restore → run K frames → read cart RAM → snapshot).

## 3. Standing constraints (contract — the plan must not break these)

- **Per-tick loop mandate** (ideal plan §7): no virtual dispatch, no DI
  resolution, no delegate/`Func<>`, no allocation, no float in emulated
  state. Currently held.
- **Timer-before-serial** component ordering at equal timestamps is
  load-bearing Contract (§3.5) — any tick-path restructuring needs a
  frame-hash regression guard across the change.
- A machine is a **pure function of (configuration, consumed input stream)**
  — every optimization must preserve this; the batteries (Humble Post Tier
  A, `--validate-overworld`, trio-lockstep) are the gates.
- Machines share NOTHING (per-machine DI container) — parallel stepping is
  architecturally safe; only the shared timeline needs a concurrency story.
- The ARM7TDMI (AdvancedGamingBrick) core has **no snapshot layer** — fleet
  features built on snapshot/fork are Humble-only until that lands.
- Document-model gotcha: optional fields on polymorphic-derived records must
  be nullable + normalized (initializers are skipped by the parse path).

## 4. Known levers, ranked by prior expectation (validate with the profile)

1. **M3 devirtualization** of the per-tick `IClockedComponent.Tick()`
   fan-out (sealed field-cached components). Explicitly PROFILE-GATED — the
   ideal plan's own revised order is: thin bench → profile → M3 only if the
   fan-out is genuinely hot.
2. **Idle-span fast-forward** (HALT/STOP spans jump to the next event; the
   AGB core's `StepClocks` pattern is earmarked for grafting).
3. **Parallel fleet stepping** — task-per-machine; needs a timeline-access
   rule (today `Fill` mutates a cursor on the render thread) and a
   decision on where machine stepping leaves the render thread.
4. **Zero-copy framebuffer → texture** (kills the 92 KB repack per
   machine-frame; the stated ideal in the four-quad provenance).
5. **Adaptive catch-up** (spend actual frame headroom instead of the static
   4-segment cap).
6. **Container/machine pooling** for spawn-heavy scenes (stumble-upon
   moments) — pre-warm `MachineFactory.Create`, reuse snapshot buffers.

## 5. The procedure (what the next session actually does)

1. **Build the bench.** A Humble Post diagnostic (`--bench`, beside
   `--render`/`--lockstep` in the Post exe — NOT a new demo flag): N
   machines × M frames, scripted input, stopwatch + per-phase counters;
   report machine-frames/s at N = 1, 2, 4, 8, 16, 32, 64, 128, 256 (the
   scale posture is hundreds+), in BOTH fleet shapes (independent
   per-machine input streams and one shared choir stream), single- and
   multi-threaded, plus burst catch-up rate (uncapped max speed, 1 and K
   machines), the mailbox-check cycle (restore → run K frames → read cart
   RAM → snapshot), per-machine working set, and `Create`/`Snapshot`/
   `Restore`/`Fork` latencies. Every fleet run ends with a same-input
   `ContentEquals` determinism spot-check. This is the reusable instrument;
   keep it.
2. **Profile N=1 and N=16** under `dotnet-trace` (or the VS profiler) on
   the bench. Attribute the 3 ms. Only now decide M3's fate.
3. **Write the plan** as a peer doc: chosen levers in measured-payoff order,
   each with its regression guard (frame hash + battery), and a stated
   fleet target (pick the number from the workload classes in §1 — e.g.
   "▢ machines at realtime on the dev box, ▢ with headroom for render").
4. **Reserve the seams** the workloads need but don't build them in the
   perf pass: the sensor-feed peripheral (engine→machine, incl. the GPU
   readback question for world-rendered lens views), realtime
   promote/demote migration rules, recursive feed chains (latency = one
   frame per hop?). Each is a workload the bench should be able to MODEL
   (e.g. a synthetic per-frame sensor write) without designing the feature.

## 6. Pointers

- Fleet integration surface: `src/Puck.Demo/Overworld/` (`OverworldBrickTimeline`,
  `GamingBrickChildNode` — `PowerSource`/`SegmentSource`/`RunTicks`).
- Machine surface: `experimental/Puck.HumbleGamingBrick/`
  (`MachineInstance.Fork`, `Machine.Snapshot/Restore`, `MachineFactory`,
  `MachineConfiguration`, `ComponentClock`).
- Existing measurement + diagnostics: `Puck.HumbleGamingBrick.Post`
  (trio-lockstep, `--render`), `Puck.AdvancedGamingBrick.Post` README (the
  diagnostics-toolbox pattern to imitate for `--bench`).
- Verification routing: the `verifying-puck-changes` skill;
  [agent-guide.md](agent-guide.md).
