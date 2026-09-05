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
  `ConsoleModel`, which is revision-valued (`Dmg0`…`DmgC`, `Mgb`, `Sgb`,
  `Sgb2`, `Cgb0`…`CgbE`, `Agb`, `Ags`). Components ask a named question on
  `ConsoleModelExtensions` and cache the answer through `ApplyModel`; do not
  fork the core, and do not switch on a revision inside `Sm83`/`Ppu`/`Apu`.
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

Integer PCM12/PCM34 channel outputs are the emulated contract, and they are
latched: a channel publishes a new level only when its generator steps, its
envelope moves, or a register write reaches it, and a channel whose DAC is off
holds the level it last published. Floating-point mixing is presentation only
and must never feed state.

The unit runs on two clocks. Its CPU-domain tick follows the DIV-APU bit —
bit 12 of the DIV counter, or bit 13 in double-speed mode: the falling edge
advances the 512 Hz divider (length at 256 Hz, sweep at 128 Hz, the envelope
reload pre-count at 64 Hz), and the rising edge half a period later arms the
envelope clocks, which the next falling edge steps. The generators — duty
positions, the wave fetcher, and the noise counter that clocks the LFSR — run
on a fixed 2 MiHz audio clock, two whole dots per tick, derived by halving the
per-dot stream `ApuGeneratorClock` delivers. The noise channel is a 14-bit
counter with an edge detector on the NR43-selected bit, not a shift-scaled
period, which is why two (divisor, shift) pairs naming one rate step the LFSR
at the same instants.

A read and a write reach the unit at different points inside their machine
cycle (`Sm83.Decode`'s `LeadingTCyclesBeforeRead`/`Write`), so a read observes
one more audio tick than the write that set the event up. The generators carry
that skew in their countdown loads; the write-side predicates undo it by
looking one tick ahead (`PeekSquare`, `PeekWaveFetch`). Move one and the other
has to move with it.

Where the skew is *observable* follows from how a machine cycle divides into
audio ticks, and the two speeds differ. At normal speed a machine cycle spans
two audio ticks, so the read strobe stays inside the same 1 MiHz half that the
duty counter and the noise counter are quoted against and the skew moves no
edge a reader can see. Under double speed a machine cycle is exactly one audio
tick, so the same skew carries the first generator edge a whole step past where
the reader expects it — which is why a square or noise *trigger* loads the skew
only under double speed (`ApuComponent.TriggerReadStrobeSkew`), while the wave
fetcher and the sweep unit, quoted against the 2 MiHz clock directly, carry it
at both speeds. Applying it unconditionally costs ten sample-accurate cases;
omitting it costs seven. This is the single mechanism behind the whole
`channel_*_align` / `_align_cpu` / `_duty` family.

Chasing an audio timing case is trace-led, not swept: run the co-simulation
(`--cosim <rom> --sameboy … --boot … --model cgb --kind cpu`, budget past the
boot animation), then walk the two `artifacts/gb-post/cosim/*.cosim.bin`
streams for the first divergence in *content* rather than cycle stamp — the
cycle column diverges harmlessly on a shared prologue long before any test
runs. The SameSuite alignment ROMs are unrolled sweeps that retrigger a
channel, burn one more `NOP` per iteration, and read PCM12/PCM34; the
divergence lands on the iteration where the observed edge moves, which names
the tick the model is off by.

## 3. Snapshots and host boundaries

Both machines implement mid-frame `Snapshot`, `Restore`, and `Fork` through
`Puck.GamingBricks`: `StateWriter`, `StateReader`, `SnapshotSection`, the FNV-1a
fingerprint, and `SnapshotImage`, behind `ISnapshotable` components. Component
identity and discovery order remain machine-specific.

The DI-owning instance/fork/pool lifecycle also lives in `Puck.GamingBricks`, as
one generic triad closed over an `ISnapshotableMachine` marker interface:
`MachineInstance<TMachine, TConfiguration>`, `MachineFork<TMachine,
TConfiguration>`, and `MachineInstancePool<TMachine, TConfiguration>`. Each
brick re-exposes its own closure under its historical bare name (`MachineFork`/
`MachineInstance` for `Puck.HumbleGamingBrick`, `AgbMachineFork`/
`AgbMachineInstance` for `Puck.AdvancedGamingBrick`) through a `global using`
alias in that project's `GlobalUsings.cs` — searching for a declared
`class AgbMachineInstance` finds nothing; the type lives in
`Puck.GamingBricks.MachineInstance.cs` under the generic name.

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

- A visible line's schedule, in dots from the line boundary: LY register write
  and OAM STAT pulse at 2, polled mode 2 at 3, polled mode 3 with the memory
  locks at 83, the pixel loop engaging at 88, screen column 0 at 96 + `SCX % 8`,
  the internal mode-3-to-0 edge (the 160th pixel popping) with the polled STAT
  flip and the VRAM unlocks at 255 + `SCX % 8`, and the mode-0 interrupt at
  256 + `SCX % 8`. The internal edge drives HDMA.
- SameBoy's own line runs three dots ahead of that, its schedule numbered one
  dot higher (its LX 3 is our dot 2). The gap accumulates on the first line
  after an LCD enable, which we run 452 dots long where SameBoy runs 448 —
  measured exactly on `lcdon_timing-GS`, whose line-0 origin both emulators put
  at the same cycle, with SameBoy showing polled mode 3 at LX 78 (object memory
  closing to writes at 76), its pixel loop at 83, mode 0 at 250, and line 1
  starting 448 dots in.
- Setting our first line to 449 dots with line 0's own group at 78/78/83 puts
  every polled STAT and LY edge, first line included, on SameBoy's exact dot —
  verified against the per-dot trace on two separate LCD enables — and takes the
  mealybug/AGE error from 59.6k to 51.3k differing pixels. It also fails
  `lcdon_timing`, `lcdon_write_timing` and `intr_2_mode0_timing_sprites`,
  because a dot-identical PPU is not enough: `Sm83`'s read dot-phase latches an
  I/O read on the access's third T-cycle where SameBoy latches it on the first
  (`LeadingTCyclesBeforeRead`, itself pinned by the memory-timing family), so
  with identical PPUs the two CPUs sample STAT two cycles apart. Our 452-dot
  first line is the compensation for that. Four decompositions were measured and
  refuted: shifting the whole schedule a dot (breaks the interrupt family, which
  is sampled at the instruction boundary and not through the read path), giving
  the polled register view its own phase of one or three dots (breaks
  `hblank_ly_scx_timing`, which reads LY), and matching SameBoy's read
  dot-phase (breaks more than it fixes). Closing this is the open frontier: it
  costs three to four columns on every mid-mode-3 register signature, and it is
  what keeps `m3_lcdc_win_en_change_multiple` off pixel-exact.
- CPU-visible state trails the internal edge by: polled STAT, VRAM read and
  VRAM write, and OAM write by zero dots; OAM read by zero on monochrome and
  one on Color; the mode-0 interrupt by one, reduced to zero on Color at single
  speed. Double speed adds one dot to the polled STAT flip.
- STAT mode 0 and VRAM-read availability change together. Pokémon Gold's
  Trade Center poll-STAT-then-read path depends on that ordering.
- The OAM STAT pulse fires on the LY write, one dot before STAT shows mode 2.
  Its tail overlaps the comparison-valid dot so a held LY=LYC condition does
  not retrigger.
- OAM writes remain available during the scan's first machine cycle and the
  first four entry-latency dots. VRAM writes also land there; reads do not.
- The window hand-over is immediate — the WX match drops the background FIFO
  and rewinds the fetcher, and the classic window penalty is the refill. There
  is no separate activation stall beyond the one extra dot monochrome silicon
  spends when WX is 0 with a non-zero `SCX % 8`.
- Tick the object fetcher before the pixel pop. From the second background
  tile onward, `ObjectFetchDot` may treat the high-data-byte address dot as
  ready once the first push has landed; the first fetch retains the read-dot
  threshold. The reason is oracle skew, not hardware: the SameBoy oracle
  advances its fetcher after the pop, so from the line's second tile the
  check-time fetcher state trails it by one step, and the allowance
  reconciles the two while the object stall stays dot-exact against SameBoy,
  including `intr_2_mode0_timing_sprites`. Reordering to pop-then-fetch and
  dropping the allowance is only correct together with the three-dot line
  alignment above; on its own it moves every mid-line register sample a dot
  the wrong way.
- The remaining PPU frontier is the mealybug `m3_*` sub-dot register
  signatures: the line alignment above, then the CPU-side register write
  conflicts SameBoy models in `sm83_cpu.c` (`tile_sel_glitch`,
  `wx_just_changed`, `disable_window_pixel_insertion_glitch`), which several
  `m3_lcdc_*` and `m3_wx_*` signatures turn on.

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
