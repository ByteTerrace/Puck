# GBA emulation engineering priorities

This assessment ranks the remaining work for `Puck.AdvancedGamingBrick` by
correctness value, available evidence, determinism fit, and implementation cost.
The complete technical reference is [agb-wiki/README.md](agb-wiki/README.md),
and the capability matrix is [agb-wiki/verdict-index.md](agb-wiki/verdict-index.md).

## Baseline

The core provides ARM7TDMI execution, memory timing and prefetch, DMA, timers,
interrupts, scanline PPU rendering, PSG and Direct Sound, cartridge backups,
whole-machine snapshots, deterministic link sessions, and demo-side rewind and
runahead. Tier A verifies deterministic state, snapshot/restore/fork behavior,
save persistence, and throughput. Optional suites and commercial ROMs provide
accuracy evidence.

## Priority order

### 1. DMA and CPU bus arbitration

The largest remaining cluster of timing-suite differences crosses DMA startup,
CPU fetch sequencing, interrupt observation, and pipeline phase. Treat it as one
arbitration investigation rather than a collection of local constants.

Required approach:

1. Reproduce one item with the original source ROM or an equivalent
   self-checking micro-ROM.
2. Record BIOS identity, WAITCNT, DMA registers, CPU mode, pipeline addresses,
   and the exact stop condition.
3. Compare per-instruction cycle deltas with mGBA and ares after trace
   normalization.
4. Change the shared bus-arbitration model only when one mechanism explains
   multiple rows.

### 2. Long-multiply carry behavior

The timing model already terminates multiplication from operand byte patterns.
The remaining question is the carry flag produced by the final Booth iteration
for long multiply variants. A small ROM that stores result and CPSR values is
the right proof; instruction-count tests alone cannot settle the flag.

### 3. Focused PPU edge rules

Before considering a per-dot renderer, close inexpensive scanline-model
questions with dedicated ROMs:

- vertical and affine mosaic source sampling;
- inverted or out-of-range window coordinates;
- semi-transparent OBJ and brightness-selection precedence;
- OAM and VRAM access-conflict waitstates.

A per-dot PPU is justified only by content or hardware evidence that the
scanline register-sampling model cannot represent.

### 4. Cartridge and sensor completeness

Add deterministic GPIO sensor support when a supported cartridge needs solar,
tilt, gyro, or rumble behavior. Sensor values must enter through recordable
commands. Confirm undersized-ROM mirror behavior with a focused cartridge-bus
probe before changing the current mapping.

### 5. Performance after accuracy profiling

Profile the current interpreter and region dispatcher before introducing
software fast memory or block linking. Any prototype must preserve bus timing,
open-bus latches, self-modifying RAM execution, DMA visibility, snapshot
completeness, and deterministic stop conditions.

## Work that is not prioritized

- A floating-point high-quality audio mixer inside emulation. Presentation
  resampling belongs after the deterministic integer sample boundary.
- Heuristic idle-loop databases.
- Threaded PPU/APU execution or GPU-side rendering that weakens event ordering.
- JIT compilation without a measured workload that the current interpreter
  cannot meet.
- Full rollback networking without a product protocol; snapshots, fork, and
  deterministic link sessions already provide the substrate.

## Evidence checklist

Every accuracy change should preserve:

- a minimal reproducer or named suite case;
- BIOS, ROM, and boot-mode identity;
- observable expected and actual results;
- an independent source when hardware evidence is unavailable;
- Tier A results and the affected Tier B or Tier C stages;
- a note when a reference-suite number is not comparable because its ROM or
  stopping rule differs.

Use `--hash-divergence` for deterministic-state changes, `--trace-cycles` for
mGBA timing comparisons, `--lockstep` for ares comparisons, and `--render-hash`
for framebuffer changes.
