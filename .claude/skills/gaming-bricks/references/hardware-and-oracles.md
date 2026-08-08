# GamingBrick hardware and oracle contract

This reference holds the specialized cross-generation facts that are not
obvious from the repository layout. Verify changing stage inventories,
diagnostic switches, asset variables, and known limitations against current
source and the two Post READMEs.

## Contents

- [Architecture](#1-architecture)
- [Clock and event order](#2-clock-and-event-order)
- [Snapshots and host boundaries](#3-snapshots-and-host-boundaries)
- [GB PPU timing](#4-gb-ppu-timing)
- [Oracle discipline](#5-oracle-discipline)

## 1. Architecture

- Emulated state advances only from the integer tick clock and explicit
  inputs or configuration. Do not introduce wall-clock time, randomness, or
  floating point into emulated logic.
- GB software runs through one shared SM83 core parameterized by
  `ConsoleModel` capabilities for `Dmg`, `Cgb`, and `Agb`. Do not fork the core
  into per-console implementations merely to express hardware capabilities.
- The ARM7TDMI core is the separate GBA-native machine. Cartridge selection
  chooses between the compatibility costume and native machine; both share
  the master-clock and link abstractions where the hardware does.
- Cross-generation link sessions must remain replay-identical. Different
  console generations may change capabilities, but not the determinism
  contract.

## 2. Clock and event order

### Master clock

The unified integer tick base is 2²⁴ ticks per second:

- DMG and normal-speed CGB run at 2²² ticks per second.
- Double-speed CGB runs at 2²³ ticks per second.
- GBA runs at 2²⁴ ticks per second.
- One GBA tick is one 16.777216 MHz CPU cycle; one frame is 280,896 cycles.

Keep all conversions exact and carry remainders. Do not confuse GBA CPU cycles
with display dots.

### GB serial and timer

- The serial shifter advances on each falling edge of DIV counter bit 8 in
  normal mode or bit 3 in CGB-fast mode.
- Detect the edge on the free-running counter. Writing SC arms a transfer but
  does not reset or re-phase the clock. These semantics supersede an older
  "bit 7, one shift per two falling edges" phrasing — equivalent for natural
  increments, divergent under DIV writes or SC re-phasing. The acceptance
  `boot_sclk_align` case pins the rule: edges align to the counter's reset
  time, never to when SC is written.
- TIMA increments on the falling edge of the TAC-selected DIV bit while
  enabled. A DIV write can therefore cause a timer increment, and the
  four-tick reload-delay precedence remains observable.
- At equal timestamps, advance the timer before serial. This tie-break is
  load-bearing for cross-generation link lock.

The Humble `link-churn` stage and acceptance ROMs are the executable evidence.
For a DMG result-signature reader, observe the intended SB write through
`SerialComponent.ByteQueued`; the output routine can re-arm an unfinished
normal-clock transfer.

### Audio

Integer PCM12/PCM34 channel outputs are the emulated contract. Floating-point
mixing is presentation only and must never feed state. The frame sequencer is
DIV-driven from bit 12, or bit 13 in double-speed mode.

## 3. Snapshots and host boundaries

Both machines implement mid-frame `Snapshot`, `Restore`, and `Fork` through
`Puck.Snapshots`: `StateWriter`, `StateReader`, `SnapshotSection`, the FNV-1a
fingerprint, and `SnapshotImage`, behind `ISnapshotable` components. Component
identity and discovery order remain machine-specific.

- Snapshot bytes are the state-of-record determinism surface.
- `--hash-divergence` localizes a mismatch between two executions in one
  process and one build.
- Use `--dump-snapshot` and an offline section-table diff for cross-build byte
  comparisons.
- Host audio drains integer `short` samples from presentation-side rings.
  Attaching a sink may gate presentation work, never emulated audio advance.
- Convert host delta time to cycles with exact rational arithmetic and carry
  the remainder between updates.

## 4. GB PPU timing

Treat the STAT and memory-lock schedule as one coupled contract. Do not adjust
one lag constant in isolation.

- Mode 0 emerges when the 160th pixel is popped: normally dot 251 plus
  `SCX % 8`; the first line after LCD enable has no entry latency and is four
  dots earlier. This internal mode-3-to-0 edge drives HDMA.
- CPU-visible state trails that edge through `PpuTimingParameters` and `Ppu`:
  polled STAT by four dots; mode-0 interrupt by five dots, reduced by one on
  Color at single speed; VRAM-read unlock by four; VRAM-write and OAM-write
  unlock by five; OAM-read unlock by six.
- STAT mode 0 and VRAM-read availability change together. Pokémon Gold's
  Trade Center poll-STAT-then-read path depends on that ordering.
- The OAM STAT pulse fires one dot after the LY write. Its tail overlaps the
  comparison-valid dot so a held LY=LYC condition does not retrigger.
- OAM writes remain available during the scan's first machine cycle and the
  mode-3 entry-latency dots. VRAM writes also land during entry latency;
  reads do not.
- Tick the object fetcher before the pixel pop. From the second background
  tile onward, `ObjectFetchDot` may treat the high-data-byte address dot as
  ready once the first push has landed; the first fetch retains the read-dot
  threshold. The reason is oracle skew, not hardware: the SameBoy oracle
  advances its fetcher after the pop, so from the line's second tile the
  check-time fetcher state trails it by one step, and the allowance
  reconciles the two while the object stall stays dot-exact against SameBoy,
  including `intr_2_mode0_timing_sprites`.
- The remaining PPU frontier is the mealybug `m3_*` sub-dot register
  signatures.

The acceptance cases pinning this schedule by name: `hblank_ly_scx_timing`
(its 51/50/49 SCX pattern), the `intr_2_*` family, `intr_1_2`,
`stat_irq_blocking`, and the `lcdon_timing`/`lcdon_write_timing` tables —
moving one lag constant unbalances several of them at once. Diagnose failures
with the Humble Post `--stat-trace` and `--render` tools, then confirm the
relevant stage rather than treating a trace as a gate.

## 5. Oracle discipline

External suites and co-simulators are evidence, never repository gates. A
self-checking Post stage or golden replay is a gate.

- Normalize mGBA or ares traces before comparing them: cumulative cycle
  counters can reset at frame boundaries, so compare per-instruction deltas;
  Puck's exposed ARM PC is four bytes ahead because of pipeline
  representation; direct boot and BIOS boot have different initial state.
- Use the TCHK10 AGS aging-cartridge revision when reproducing its configured
  evidence. Other revisions need not match the same patch offsets.
- The SingleStepTests/sm83 corpus has documented conflicts for STOP (`10`) and
  EI (`fb`). The corpus models STOP as a flat one-byte instruction, while the
  core consumes the pad byte when no interrupt is pending and leaves it when
  one is pending. The corpus also re-arms EI when IME is already enabled,
  while the acceptance `ei_sequence` behavior pins the core's no-op. Keep
  these as explicit oracle-conflict skips, not silent exclusions.
- BESS import/export is diagnostic cross-emulator evidence. Its
  self-consistency checks do not turn a foreign emulator into a gate.
- The Advanced commercial link-game stage currently documents an unmodeled
  Multiplayer SIOCNT SD/SI ready-line derived from cable-partner presence.
  Verify `AgbSerialController.PackSioControl` and
  `LinkGameReplayStage` before relying on this limitation; when implemented,
  update the stage and this reference together.

Keep ROMs, BIOS images, and external corpora outside the repository. Use the
current Post README and command-line parser as the authority for environment
variables and supported diagnostic switches.
