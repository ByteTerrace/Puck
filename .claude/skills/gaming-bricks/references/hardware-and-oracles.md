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
cycle — a write on the drive instant, a read `LeadingTCyclesBeforeRead` later
(`Sm83.Decode`) — so a read observes one more audio tick than the write that set
the event up. The generators carry
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
- SameBoy's own line runs three dots ahead of ours, its schedule numbered one
  dot higher (its LX 3 is our dot 2): its line origin is one dot before ours and
  it runs the first line after an LCD enable 448 dots long where we run 452, so
  every one of our register edges lands three dots later in absolute time than
  the corresponding SameBoy edge. That gap is not slack — it is the exact
  compensation for the two cores' read conventions. Our CPU latches an I/O read
  two T-cycles into the access (`Sm83.Decode`'s `LeadingTCyclesBeforeRead`), so a
  read whose machine cycle begins at dot C observes every edge through C+2;
  SameBoy reads at the drive instant, and its display state machine runs on a
  half-dot grid whose `GB_SLEEP` executes an event only once time has passed it,
  so its read observes edges strictly before C. Equal observation therefore
  requires our edge at SameBoy's edge plus three, which is what the schedule
  above already produces. The `--cosim` CPU stream on `lcdon_timing-GS`,
  `lcdon_write_timing-GS`, `hblank_ly_scx_timing-GS` and
  `intr_2_mode0_timing_sprites` is divergence-free for 120 frames against a
  boot-ROM-booted SameBoy, which is the evidence.
- Do not re-derive the schedule by moving the PPU onto SameBoy's dots. Both
  decompositions of that idea are measured and refuted. Shortening the first line
  to 449 and giving the register view a +3 polled-event phase (carrying the
  polled mode lags with it) leaves the polled STAT and LY dots exactly where they
  are but moves the interrupt raise and the memory locks three dots early, which
  fails `hblank_ly_scx_timing`, `intr_2_mode0_timing`,
  `intr_2_mode0_timing_sprites`, `intr_2_mode3_timing`, `intr_2_oam_ok_timing`,
  `lcdon_timing` and `lcdon_write_timing`. Moving only the pixel pipeline three
  dots early (`Mode3EntryLatency` 8→5 with the mode-0 group trailing the
  160th pop) keeps every acceptance case green but takes the mealybug/AGE error
  from 65.7k to about 78k differing pixels.
- The read dot-phase costs the pixel stream nothing. Against a boot-ROM-booted
  SameBoy the `--cosim` `ppu-pixel` stream on `m3_bgp_change` runs identical into
  the ROM's own drawing, and the first divergence is a colour on a matching dot,
  not a dot: at LY 1 x 1 both push the pixel on the same master cycle and SameBoy
  has already applied a new BGP where we have not.
- A `--cosim` run under about 60 frames proves nothing about the ROM: the DMG
  boot ROM's logo scroll occupies them, so every ROM produces the same stream and
  the same record count. Give a CPU-stream comparison at least 120 frames, and
  check the record count differs per ROM before believing a clean result.
- `01-read_timing` diverges from SameBoy at frame 107 on a TIMA read (`pc=C2C3`,
  `hl=FF05`: SameBoy 0x01, ours 0x00, on the same master cycle and the same
  instruction) while both pass the ROM's own verdict. It survives disabling the
  display's write phases, so it is the timer's own read-observation gap, not a
  write phase.
- An I/O write does not always reach the component on its machine cycle's drive
  instant, and the display — not the CPU — is what says so. `WriteCycle` routes
  a write inside `LCDC`…`WX` to `ISystemBus.RecordDisplayWrite`, which records it
  in flight (the register, the value held, the value arriving) and returns the
  T-cycles the commit is displaced from the drive instant, negative early. The
  next access's drive instant always stays four T-cycles after this one's. A
  register that also *settles* spends the T-cycle before its commit in
  transition: `OpenDisplayWriteSettle` opens that window, and `Ppu.WriteRegister`
  closes it, so the record is zero at an instruction boundary and no snapshot
  carries it. The phases, in `Ppu.RecordWrite`: a monochrome palette register
  settles from two T-cycles early and commits one early; a Color palette register
  commits one early, two from revision D (`SamplesPaletteWriteEarly`); monochrome
  SCY commits one early and SCX two; the monochrome status register settles from
  the drive instant and commits one T-cycle late; monochrome WX settles from the
  drive instant with the arriving value already on its line. The control
  register's own phase is skipped on the LCD-enable edge — that write is also the
  enable, and pulling it two T-cycles early moves the first line's origin, which
  the LCD-on tables are calibrated against. SameBoy's `conflict_t` map
  (`Core/sm83_cpu.c`) is the corroborating source and carries phases this core
  does not model: both LCDC glitch flags, and two that are measured and held
  back rather than merely absent.
- Color LYC and WX committing one T-cycle late at single speed, together with a
  Color STAT settling window that holds the coincidence source across the
  transition, is refuted: it repairs fourteen gambatte rows and breaks eleven
  recorded passes, all in the `miscmstatirq/lycstatwirq_trigger_*` and
  `lycEnable/late_ff41|ff45_enable_lcdoffset1_*` families. Color SCX committing
  two T-cycles early at double speed (SameBoy's
  `cgb_double_conflict_map[GB_IO_SCX]`) is measured as a net win with a cost:
  four `scx_during_m3/*_ds_*` rows flip to passing and six fall by thousands of
  pixels (9581→8866, 14833→14404, 12096→11810), while
  `scx_during_m3/scx_during_m3_spx2_ds` and its `scx_attrib` sibling rise from 8
  to 16. It needs the two risers explained before it lands, not another sweep.
- The settling view is per consumer, which is the whole reason the record beats a
  value the CPU writes into the register file. Inside the settling T-cycle each
  of the display's own consumers samples the register at its own depth:
  `MixerBackgroundPalette`/`MixerObjectPalette0`/`MixerObjectPalette1` read the
  wire-OR of held and arriving, because a monochrome palette keeps driving while
  the new value lands; `InterruptStatSelect` reads every source enabled, because
  a monochrome status write releases its select lines first; `MixerControl` lets
  only the background-enable bit through; `ObjectControl` additionally drops an
  object-enable bit going low while a fetch runs, or at the start of a column on
  every package but the compact monochrome one
  (`DropsObjectEnableAtColumnStart`). Every other consumer reads the register
  field directly and therefore sees the held value, which is what the fetcher and
  the window comparisons want.
- Expressing those phases is why the bus primitives defer. `ReadCycle` leaves the
  two T-cycles it did not tick on `m_busCycleDebt`, `InternalCycle` banks four
  without ticking, and `WriteDisplayRegisterCycle` can therefore commit before its
  own drive instant. Every read of component state that is not itself a bus
  access (`ServiceInterrupt`'s two mask reads, STOP's and HALT's pending checks,
  `NoteOamCorruption`'s object-scan row) and every machine cycle the CPU spends
  off the bus (`IdleMachineCycle`) settles the debt first, and `StepInstruction`
  settles it before returning, so the debt is always zero at an instruction
  boundary and no snapshot carries it.
- No write phase can close the mid-mode-3 write families, and the reason is
  measured, not suspected. The schedule above puts our register edges three dots
  later in absolute time than SameBoy's precisely so a CPU *read* (latched two
  T-cycles into its access) observes what SameBoy's read (taken at the drive
  instant) does. A CPU *write* gets no such compensation: both cores commit it at
  the same absolute T-cycle, so against the display's own dots our write lands
  three dots early. A case that writes and then reads is still calibrated, which
  is why the acceptance interrupt and LCD-on families are green; a case whose
  verdict is the picture itself has no read to compensate it, which is why every
  mid-mode-3 screenshot family is off by a whole number of columns. On
  `lycint_dmgpalette_during_m3_1` the `ppu-pixel` stream is
  index-aligned and both cores commit BGP at the same absolute cycle, yet SameBoy
  paints from LY 1 x 157 and we paint from x 155 — the two-column error the
  ledger records as 286 differing pixels (143 lines x 2 columns), with the
  `lycint_*_3`/`_4` pair three columns out at 429. The only coherent cures are to
  move both conventions together — read at the drive instant
  (`LeadingTCyclesBeforeRead` 0) and put the line on SameBoy's dots (first line
  448, `LineEventPhase` 0) — or to leave it alone. Every decomposition that moved
  one and not the other is refuted above.
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
  including `intr_2_mode0_timing_sprites`. Pop-then-fetch with the allowance
  dropped rests on moving the line onto SameBoy's dots, which is refuted above,
  so the allowance stays.
- Our first line after an LCD enable is four dots longer than SameBoy's, so each
  enable slides our pipeline four dots against its oracle. On some ROMs the two
  happen to land together (`m3_bgp_change`'s pixel stream is dot-identical); on
  others they sit a dot apart, which is what keeps gambatte's
  `lycint_dmgpalette_during_m3_*` off pixel-exact (their first pixel divergence
  is one column, LY 1 x 155) while their non-interrupt siblings improved. No
  write phase closes both: `MonochromePalette` at one T-cycle early instead of
  two moves those four ROMs from 286/429 to 143 and costs fifteen others. Closing
  it means the first line's length, which the register families pin — see the
  refuted decompositions above.
- The remaining PPU frontier is the mealybug `m3_lcdc_*` and `m3_wx_*`
  signatures. They turn on the write glitches SameBoy models in `sm83_cpu.c`
  (`tile_sel_glitch`, `wx_just_changed`,
  `disable_window_pixel_insertion_glitch`). The write-in-flight record reaches
  the PPU state each of them needs, so they are expressible as settling views on
  the same rule rather than as special cases; what still blocks them is the
  write-versus-read dot-phase asymmetry above, not the write path.

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
