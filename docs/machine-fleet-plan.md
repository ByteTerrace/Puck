# Machine-fleet performance plan — measured

Status: **plan of record** (2026-07-03), the successor the briefing
([machine-fleet-briefing.md](machine-fleet-briefing.md)) called for. Every
number below was measured on the dev box (the RTX 4070 machine, 16 logical
processors, .NET 10 Release) with the `--bench` instrument; nothing is
estimated. The vision conclusions the workloads serve are recorded in the
briefing §1 (postures settled 2026-07-03).

## 1. The instrument

`--bench` in the Humble Post exe (diagnostic, not a battery stage):

```
dotnet run --project experimental/Puck.HumbleGamingBrick.Post -c Release -- --bench
  [--bench-rom <path>] [--bench-frames <floor>] [--bench-fleet <csv of N>] [--artifacts <dir>]
```

One run reports the fleet scaling curve in both shapes (independent
per-machine streams / one shared choir stream) single- and multi-threaded,
burst catch-up, `Create`/`Snapshot`/`Restore`/`Fork` latency + allocation,
the mailbox-check cycle, and per-machine footprint; report lands at
`artifacts/gb-post/bench-report.txt`. Built-in honesty guards: every fleet
cell carries a same-stream machine pair that must end byte-identical, and
the multi-threaded cell must end byte-identical to the single-threaded one
— a run that breaks determinism exits 1. Default ROM is the Tier-A
synthetic; the numbers below are from Pokémon Gold (real instruction mix,
`Cgb`), with the synthetic run bracketed where it differs.

## 2. Measured facts (dev box, 2026-07-03, post-M3)

Fleet scaling, machine-frames/s (Gold; synthetic within ±4%). These are the
POST-M3 numbers — the devirtualization landed the same day and bought
**1.6×** across the board (pre-M3 provenance: ~346 1T / ~2 650 MT plateau /
trio-lockstep 332):

| N | independent 1T | independent MT | choir 1T | choir MT |
|---|---|---|---|---|
| 1 | 520 | 529 | 530 | 530 |
| 4 | 530 | 1 926 | 528 | 2 039 |
| 16 | 559 | 4 232 | 540 | 4 495 |
| 64 | 562 | 4 473 | 548 | 4 186 |
| 256 | 556 | 4 471 | 550 | 4 115 |

- **Single-threaded stepping is flat** at ~520–560 machine-frames/s
  (**~9.3 machines at realtime**) from N=1 to N=256 in both shapes:
  per-machine cost is constant, there is no shared-anything falloff, and
  choir compute ≡ independent compute (the choir's cheapness must come
  from stepping FEWER machines, not from stepping the same machine more
  cheaply — lever 2).
- **Parallel stepping pays ~8× today with zero engine changes**: plateau
  ~4 200–4 500 machine-frames/s from N=16 up = **~75 machines at
  realtime**, and the bench proves serial-vs-parallel bit-exactness every
  run (machines share nothing; only the timeline needs a concurrency
  story).
- Per-operation (Gold, post-M3):

  | Op | Latency | Allocation |
  |---|---|---|
  | `Create` | ~3.0–3.5 ms | 268 KB |
  | `Snapshot` | 81 µs | 678 KB; snapshot size 175 KB |
  | `Restore` | **30 µs** | **32 B** |
  | `Fork` | 3.4 ms | 935 KB |

- **Burst catch-up**: one machine replays at 8.8× realtime (a dormant hour
  = 407 s of replay); 16 in parallel sustain 4.5× realtime each.
- **Mailbox check** (restore → run 15 frames → read cart RAM → snapshot):
  35 ms — the freeze/wake ends are noise; the cost IS the emulated
  frames.
- **Footprint**: ~345 KB managed per resident machine (64-machine fleet) —
  300 resident machines ≈ 100 MB; dormant machines are just their
  142–175 KB snapshots. Memory is a non-issue at the vision's scale.
- **Frame-cost attribution** (dotnet-trace, N=1 and N=16 profiles agree;
  share of emulation time): SM83 CPU core ~63–70% (decode/execute with the
  bus access inlined into `StepInstruction`), PPU ~15–20%, per-cycle
  component `Tick()`s (timer, serial, HDMA, APU generator, MBC3) ~7–11%,
  `ComponentClock.AdvanceCpuTCycle` fan-out ~5–8%.

## 3. Verdicts

- **M3 (devirtualize the tick fan-out): LANDED 2026-07-03, measured
  1.6×** — far past the ~1.2× the sampling attribution predicted. The
  lesson is recorded here deliberately: eliminating interface dispatch
  also let the JIT INLINE the component `Tick()` bodies into the fan-out,
  and a sampling profile cannot see inlining headroom — treat
  dispatch-share as a floor on the win, not a ceiling. `ComponentClock`
  now holds every component as a typed sealed field (the cartridge's
  RTC facet is the one remaining interface slot, null-skipped for untimed
  mappers), the constructor verifies each declared `ClockDomain` against
  its hard-coded slot, and Contract §3.5 order (timer before serial) is
  pinned in code. Guards held: identical Gold frame hashes on all three
  costumes, identical battery pass set, bench determinism guards green.
  M3's second half — idle-span fast-forward — is lever 4, still open.
  The CPU core's own ~65% remains a separate, future
  whole-loop-flattening question.
- **The fleet target** (briefing §5 step 3, both shapes per the settled
  posture): **64 realtime-stepped machines** (any mix of choir cursors and
  independents) **with ≥25% frame headroom** on the dev box — already met
  in the bench (75 at realtime = 64 + 17% before lever 1 even reaches the
  engine; the remaining headroom comes from landing it there) — plus
  **hundreds+ resident** via dormancy at ~zero step cost, and **choir
  spectacle of arbitrary width** via lever 2 (step one, present many).

## 4. Levers, in measured-payoff order

0. **Instancing: per-object bounds skip + per-tile instance masks: LANDED
   2026-07-03.** ⚠️ Unlike the rest of this doc, lever 0's numbers were
   measured on the SURFACE (Surface Laptop 5, i7-1255U, Iris Xe iGPU, 12
   logical processors) — the machine that exposed the wall — NOT the RTX 4070
   dev box; expect roughly an order of magnitude more headroom there
   (unverified). The prototype content (shelf + cartridges + animated
   controls, ~45 SDF instructions, ~24 dynamic) exposed the world renderer's
   per-pixel views scaling wall: 206 ms/frame at 1280×800, 96% in kernel. The
   original avatars VM (8 avatars × ~60 instructions ≈ 500 total) had the
   load-bearing mechanism dropped in the port: **per-object instancing** — an
   instance table (instruction range + posed bounding sphere), per-tile
   instance bitmasks from the beam prepass, per-frame re-posed bounds, and
   per-instance march chord clamp. A ray evaluates only ~60 instructions of
   objects overlapping its tile, not the full VM.

   **Measured (Surface):** (1a) exact Union bounding-sphere skip per
   instruction/segment + host-baked bounds table: 206 ms → 71 ms. (1b) The
   instancing layer (`BeginInstance`/`BeginInstanceDynamic`/`EndInstance`,
   1024-instance ceiling with a derived ceil(count/32)-word per-tile mask,
   world set always evaluated via a merged world-segment list so map() costs
   O(world + visible instances' segments) — never O(all segments),
   zero-instance programs byte-identical) + the overworld declaring ~23
   instances: ~65–100 ms with heavy iGPU power-state variance — the Iris Xe
   is bandwidth-starved at full-res marching regardless of culling, so the
   Surface's playability lever is internal render scale, not more culling.
   The dev-box acceptance run (target ≤10 ms views at 1280×800) is PENDING —
   re-measure with `PUCK_TIMING=1` on the 4070 and record it here.

   This is the scaling substrate for the fleet arc: "hundreds of machines" =
   hundreds of instances; diegetic world-lens machines, cartridges, and players
   are all instances. Per-tile instance masks are the win, not per-instruction
   bounds alone. The new `world-instanced` Post stage proves instanced ≡ flat
   pixels (bit-identical on Vulkan).

1. **Fleet stepping task-per-machine: LANDED 2026-07-03.** The
   engine-side split (`GamingBrickChildNode.PrepareStep`/`ExecuteStep`
   driven by `WorldProducerNode.StepBricks`) enforces the
   **timeline-access rule**: PREPARE runs serially on the render thread
   (segment `Fill` advances shared-timeline cursors; the pad service is a
   shared drainer), EXECUTE — the emulation + framebuffer repack — fans
   out one task per machine, and all GPU work stays serial behind the
   `Parallel.For` barrier so submit order is unchanged. Stepping
   eligibility mirrors the produce loop exactly (a pane steps on the
   frame its view exists). Guards held: `--validate-overworld`, the engine
   determinism/replay gate (Puck.Post Tier A), the full engine Post
   battery green, and
   in-capture bit-lock
   (cgb ≡ agb, 0 pixel diffs at frame 150 across staggered boots
   240/480/720). A one-run byte-identical capture against the
   pre-change binary proved the split semantically exact.
   **Capture lesson (recorded deliberately):** whole-app PNG captures
   are only *marginally* stable across runs — the tick-per-frame
   allocation sits near a wall-clock boundary, so cross-run/cross-build
   PNG equality is NOT a valid determinism guard; the repo's own proof
   style (pane-vs-pane bit-lock WITHIN one capture) and the fixed-tick
   validators are the honest gates.
2. **Choir amortization: LANDED 2026-07-03.** Identical-machine consoles
   (key = ROM + boot model (`runAs` wins over costume) + speed policy —
   never presentation) group behind the first as leader; once a follower
   and its leader are both at the shared timeline's head,
   `OverworldRenderNode.ParkConvergedChoirMembers` verifies the two machines
   are BYTE-IDENTICAL (`TryParkBehind`'s `ContentEquals` — a failed
   compare refuses the park loudly and permanently) and the follower stops
   stepping, mirroring the leader's staged framebuffer. A W-wide converged
   choir costs one stepped machine + W presents. Proven with
   [examples/overworld-choir.json](examples/overworld-choir.json) (cgb+cgb+agb,
   staggered boots): park fired after catch-up, leader ≡ parked follower
   AND parked mirror ≡ the independently-stepped agb machine, both at
   0/144 000 pixel diffs — the amortization is observationally invisible.
   The heterogeneous default overworld forms no choirs (key respects machine
   identity). Divergence events later = unpark: `Restore(leader
   snapshot)` (30 µs) and resume stepping — the parked cursor already
   tracks the head.
3. **The dormancy protocol** (simulate-on-demand, settled): the park
   mechanism above IS the freeze machinery — a parked machine stops
   stepping while staying assembled, and wake = `Restore` (30 µs) +
   timeline catch-up. Doctrine: **freeze-and-wake by default; replay only
   when the fiction demands lived time** (replay costs 1/8.8 of the
   dormant span; 16 concurrent replays sustain 4.5× realtime each). The
   attention-keyed consumer (power-off/re-boot, world mailboxes) is the
   overworld plan's next-step territory and lands with its gameplay.
4. **Idle fast-forward: MEASURED AND CLOSED** (2026-07-03, `--halt-share`
   in the Humble Post exe). Gold halts **26.6–30.0%** of machine time —
   but a halted cycle's remaining cost is dominated by un-skippable PPU
   dot-drawing and APU generation (the CPU decode that dominates running
   cost is already absent during HALT), so the lever's ceiling is ~10%
   for an SM83 event-scheduler redesign — the highest timing-risk surgery
   in the codebase (Contract §3.5, mealybug PPU knowledge). Not worth it
   on these numbers. Revisit condition: a future whole-loop flattening of
   the CPU core's ~65% that builds event infrastructure anyway.
5. **Spawn pooling**: `Create` is 3 ms — a dropped frame if done
   mid-frame. Pre-warm a machine pool for stumble-upon moments; note
   pooled-`Create` + `Restore` (48 µs) strictly beats `Fork` (3.5 ms,
   which is Create+Snapshot+Restore in one call) for ghost spawns.
6. **Snapshot allocation diet**: `Snapshot` allocates 3.6–3.9× its own
   payload (the `StateWriter` path). Only matters for high-frequency
   rewind/ghost mechanics (>100 snapshots/s); pick up when a mechanic
   demands it, then guard with `snapshot-round-trip` + `fork-determinism`.
7. **M3 devirtualization**: LANDED (verdict above) — measured 1.6×.
8. **Instancing instance-decoder**: Decode per-tile 64-bit masks to instance
   IDs in the march prepass; the beam prepass already builds them. The
   on-the-fly decode adds noise to beam-sample cost; pre-decoded storage (one
   16-entry array per tile, refreshed per frame if the world changed) is the
   measured-payoff question — measure when port (lever 0.1b) lands.

## 5. Reserved seams — modeled, not built

- **Engine→machine sensor feed** (camera / world-lens): when the
  `peripheral` seam lands, add a bench axis that writes a synthetic sensor
  page per machine-frame before stepping; until then the cost model is
  "one more per-frame copy of ≤ a framebuffer".
- **Link-pair co-stepping** (short-term interactions): M5-gated; a linked
  pair steps as one serialized unit at cycle granularity, so plan
  capacity as pairs ≈ machines/2. The bench gains a pair mode when
  `ISerialEndpoint` exists.
- **Realtime promote/demote**: the COST is already paid — migration =
  `Snapshot` + `Restore` ≈ 300 µs, invisible inside one frame. What
  remains open is the cross-costume migration RULES
  (overworld-demo-plan.md), not performance.
- **Recursive machine-feeds-machine**: expected one frame of latency per
  hop; deprioritized per the settled weighting.
- **Machine→engine events / mailbox conventions**: carrier undesigned
  (briefing §1); the mailbox-check cycle above is the standing cost
  model.

## 6. Regression guards (every lever change)

Humble Post Tier A green (all six stages), the bench's own determinism
guards on a full default run, the trio-lockstep machine-frames/s recorded
before/after in the change description, and `--validate-overworld` when the
change touches engine-side integration. Timer-before-serial ordering at
equal timestamps is Contract — any tick-path restructuring needs a
frame-hash regression guard across the change.
