# Determinism, snapshots, and replay

The native GBA core treats emulation as a pure function of configuration,
integer cycles, and recorded input. This makes a complete machine snapshot the
common primitive for restore, fork, rewind, runahead, divergence analysis, and
future rollback protocols.

## Deterministic-core contract

Emulated state must not depend on wall-clock time, process scheduling,
unrecorded randomness, or floating-point feedback. RTC and sensor values enter
through recordable inputs. Presentation audio consumes integer samples and does
not affect the machine.

Scheduler events serialize as stable event identities and due cycles, never
delegate or heap addresses. Cartridge identity and BIOS profile are part of the
machine recipe used to validate a restore.

## Whole-machine snapshot

`AgbMachineSnapshot` contains CPU registers and banked state, bus memories,
pipeline and prefetch latches, PPU, APU, DMA, timer, interrupt, scheduler,
serial, cartridge, backup, and master-cycle state. Subsystems implement
`IAgbSnapshotable` through their `*.State.cs` partials.

`AgbMachineFactory` owns construction. `AgbMachineInstance.Fork` rebuilds a
machine from its recipe and restores the snapshot, avoiding shared mutable
component state.

Verification surfaces:

- `state-round-trip` restores both frame-boundary and mid-frame snapshots and
  requires identical continuation;
- `fork-determinism` compares the original and rebuilt machine;
- `--state-roundtrip <rom>` investigates one cartridge;
- `--hash-divergence` identifies the first different snapshot section and byte.

The section table is diagnostic metadata. Tests should assert continued machine
behavior and deterministic content, not a private field layout.

## Rewind

`MachineTimeTravel<TInput>` (`src/Puck.Hosting/MachineTimeTravel.cs`) is the
shared, machine-neutral rewind layer both GamingBrick cores drive through
`ITimeTravelMachineCore<TInput>` — it is not AGB-specific or Demo-specific.
It stores periodic full keyframes and, for the frames between two keyframes,
only the `(input, cycle-budget, host-accumulator)` that produced each one, in
a fixed-capacity segment ring; a rewind restores the nearest keyframe and
deterministically replays the intervening inputs, landing bit-exact by
construction. There is no XOR/RLE delta encode: the type's own remarks call
that dead cost, since a full-state delta stream would spend CPU and
allocation on bytes a keyframe-plus-replay restore never reads.
`QueuedMachineHost`/`QueuedMachineWorker` expose the layer as
`ITimeTravelMachine` (`SetRewindEnabled`/`RewindBy`), but no console verb
currently wires rewind to a booted screen machine. The quarantined
`experimental/Puck.Demo/AgbDebug/AgbDebugCommandModule.cs` (out of the
build) carries a comment naming an aspirational `TimeTravelCommandModule`
that was never built; its own live verbs are `agb.snap`/`agb.restore`
(in-memory savestate slots, a coarser mechanism than the ring) and
`agb.light`/`agb.poke` — none of them rewind.

## Runahead

`MachineTimeTravel<TInput>` also owns runahead: one persistent lookahead
fork, rented from the core's instance pool rather than forked per input
change, is kept a configured number of native frames ahead of the
authoritative machine on predicted (currently-held) input, capped at
`MaxRunaheadFrames` (10). The host presents the lookahead's framebuffer while
the real machine stays the tick-locked authority and the only audio source.
The layer re-advances the lookahead by the authority's own native-frame delta
each submission rather than a fixed per-call step, so the lead holds exactly
N under a mismatched host/native cadence or under fast-forward. As with
rewind, no console verb exposes this outside the quarantined demo debug
scene's own `runahead <n|off>` command (out of the build).

Two-instance runahead is preferred over repeatedly saving and loading the active
machine because it isolates speculative state and matches the fleet execution
model.

## Link sessions and rollback

`AgbLinkCable` connects serial controllers. `AgbLinkSession` advances the
furthest-behind endpoint so a linked set has one deterministic cable timeline.
Tier C verifies replay-identical synthetic multiplayer exchange and a
commercial link-game flow. `AgbSerialController` exposes cable presence,
multiplayer identity, ready lines, and transferred words through the hardware
register model.

The link session and snapshot primitives are sufficient substrate for a future
rollback protocol, but they do not define network prediction, input delay,
resynchronization, or authority. Add those only with a product-level protocol
and observable cross-process verification.

## Divergence detection

Use hashes for coarse detection and section-aware snapshot comparison for
localization:

1. Advance machines with identical budgets and inputs.
2. Compare deterministic snapshot hashes at the selected interval.
3. On mismatch, compare section tables and report the first byte difference
   with a short context window.
4. Narrow to per-scanline or per-instruction stepping when needed.

`AgbBiosProfile` classifies BIOS images. Timing-sensitive diagnostics reject a
replacement or unknown BIOS unless `--allow-replacement-bios` is explicitly
provided. A BIOS mismatch is an input mismatch, not an emulator desync.

## RTC and other external time

RTC values derive from emulated cycles and a deterministic epoch. Live local
time may be sampled only as a recorded command that can be replayed. The same
rule applies to camera, solar, tilt, gyro, and network-derived inputs.

## Movie formats

A movie format would need a versioned machine recipe, ROM and BIOS identities,
initial snapshot, tick-stamped input stream, and optional verification hashes.
The existing `CommandSnapshot` model is the input substrate, but no public movie
container is currently part of the product contract.

## Sources

- [GGPO](https://www.ggpo.net/) for the save/load/advance rollback primitive.
- [Libretro netplay documentation](https://docs.libretro.com/development/retroarch/netplay/)
  for hash and input-delay patterns.
- [binjgb rewind design](https://binji.github.io/posts/binjgb-rewind/) for
  base-plus-delta history.
- [RetroArch runahead documentation](https://docs.libretro.com/guides/runahead/)
  for two-instance speculative execution.
