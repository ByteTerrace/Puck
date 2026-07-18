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

The demo's `AgbTimeTravel` stores periodic full snapshots and intermediate
XOR/RLE deltas in a fixed-capacity ring. Rewind reconstructs the requested image
and restores the active machine without rebooting it. The `agb.rewind` and
`agb.rewind.status` console verbs expose this behavior.

Delta generation is pure comparison over immutable snapshot bytes. It may run
off-thread only when no result can feed back into the emulation timeline.

## Runahead

Runahead uses a persistent forked machine to simulate predicted input several
frames ahead while the real machine remains authoritative for state and audio.
The `agb.runahead` verb configures the distance. A prediction change rebuilds or
restores the lookahead from authoritative state before advancing again.

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
