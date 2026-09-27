# Humble Gaming Deck

The Humble Gaming Deck (`Puck.HumbleGamingDeck`, short form HGD, type prefix
`Hgd`) is a proposed third deterministic emulator core: the NES and Famicom,
alongside the Humble Gaming Brick (Game Boy family) and the Advanced Gaming
Brick (Game Boy Advance). "Brick" names the handheld line and "Deck" the home
line, after the name the console unit was sold under; the adjective names the
generation, so the SNES would later be the Advanced Gaming Deck (AGD). The
names are generic by design and carry no trademark.

The goal is a state-of-the-art core. That means accuracy at the level of the
emulators that lead today, measured against a published matrix of silicon,
regions, boards, and test corpora rather than one benchmark, while keeping
every contract the two bricks already honour: integer clocks, complete
snapshots, forks, rewind, runahead, and replay. This page belongs to
[Machines and cartridges](machines-and-cartridges.md). Cartridge authoring for
the Deck (a 6502 emitter in the forge) is a separate, later decision and is out
of scope here.

## Implementation status

- **Landed:** nothing. The shared layer the Deck will stand on exists as
  `Puck.GamingBricks`; its hosting, pacing, snapshot, fork, and time-travel
  machinery is hardware-free, and the Advanced Gaming Brick already showed that
  a very different CPU fits on it.
- **Not started:** every package below.

## What state of the art means here

Accuracy on the NES has moved past the suites that separated emulators a decade
ago. The blargg-era test collections are now passed in full by every leading
emulator, so they no longer rank them. The suite that does is
[AccuracyCoin](https://github.com/100thCoin/AccuracyCoin), an MIT-licensed NROM
test cartridge of 144 verdict tests and five informational screens, targeting
the RP2A03G CPU and RP2C02G PPU. Its tests reach the behaviour that separates
the leaders:

- **CPU:** dummy reads and writes, every unofficial opcode including the
  unstable stores (`SHA`, `SHX`, `SHY`, `SHS`), interrupt-flag latency, and
  NMI hijacking of `BRK` and IRQ.
- **DMA:** DMC and OAM DMA against each other and against the CPU, including
  the repeated reads a halted CPU makes of `$2007`, `$2002`, and the controller
  ports, and DMA aborts.
- **PPU:** VBlank and NMI races at single-dot resolution, `$2002` flag timing,
  OAM corruption, stale shift registers, and stress tests on `$2004` and
  `$2007`.

TriCNES, whose author also wrote AccuracyCoin, passes every test. It is
NTSC-only, has no audio output, and supports a limited set of mappers, so
matching it on one cartridge is not overall parity. MesenCE (the continuation
of Mesen 2) is the most complete emulator across regions, boards, audio, and
tooling, and sits a few AccuracyCoin tests behind. The Deck's target is the
union: every applicable AccuracyCoin verdict, every applicable
[SingleStepTests `nes6502`](https://github.com/SingleStepTests/65x02) bus-cycle
vector, the blargg and board suites, and divergence-free co-simulation against
a pinned Mesen build, across the boards and regions the matrix commits to.

## Architecture

### Clock and scheduling

The Deck runs on its own integer master clock: 236,250,000/11 Hz on NTSC
(about 21.48 MHz) and 53,203,425/2 Hz on PAL (about 26.60 MHz). The CPU divides
it by 12 and the PPU by 4 on NTSC; PAL divides by 16 and 5, and the Dendy clone
by 15 and 5. The reference execution mode steps the master clock one tick at a
time, with explicit sub-phases inside each CPU cycle, because the observable
behaviour lives in the order of edges within a cycle: an instruction-sized CPU
step followed by a bulk PPU advance cannot express a `$2002` read racing the
VBlank flag. A faster catch-up mode is allowed later only across intervals
proven free of observable edges, sharing the same transition functions, and a
chunk-partition equivalence stage gates it.

The CPU and PPU start at one of four phase alignments relative to each other,
and software can detect which one it got. On hardware the alignment is random;
in the Deck it is explicit configuration with a deterministic default, as are
power-up RAM contents and the PPU's open-bus decay, which on silicon varies
with temperature and is expressed here as per-bit integer deadlines under a
named hardware profile.

### CPU, DMA, and buses

The 2A03 is a resumable per-cycle microsequencer: every bus access, including
dummy reads, read-modify-write double writes, and page-crossing fetches, is its
own cycle, and interrupt polling happens where the silicon polls. Unstable
opcodes model the bus-latch and RDY interactions that make them unstable rather
than a single magic constant. One DMA arbiter owns bus mastery for OAM and DMC
transfers, which the APU requests; a counter that stalls the CPU for N cycles
cannot express repeated reads, alignment, overlap, or aborts. CPU open bus, the
PPU I/O latch, and the `PPUDATA` read buffer are separate state, each with a
driven-bit mask.

### PPU and console models

The PPU performs its fetches, shifts, sprite evaluation, sprite-overflow bug,
sprite-zero hit, and odd-frame dot skip on their actual dots. Hardware variants
(2C02 revisions, the PAL 2C07, the Dendy clone) are capability questions on an
`HgdConsoleModel`, copying the pattern `ConsoleModel` established for the Game
Boy family, where components cache named answers rather than switching on a
revision.

The authoritative pixel is a nine-bit code: six palette bits and three emphasis
bits, plus frame and signal-phase metadata. Turning that into colour, whether
through a palette generated from the 2C02's measured voltage levels or through
full NTSC composite emulation, is presentation work and stays outside the core.

### Audio

The pulse, triangle, noise, and DMC channels, and any expansion audio, publish
timestamped integer outputs; those are the emulated contract. The nonlinear
mixer, board gains, filtering, and band-limited resampling are presentation
work, and execution with audio disabled is identical.

### Cartridges and mappers

The loader parses NES 2.0 headers completely and accepts iNES headers with
explicit diagnostics where they are ambiguous. Board identity, memory sizes,
region, and default peripherals resolve into immutable configuration, with
overrides recorded in the machine's identity and no game-specific patches in
the CPU or PPU. The mapper interface sees CPU and PPU transactions, has
side-effect-free peeks, and observes address-line and M2 edges directly: the
MMC3's scanline counter filters PPU address line A12 against M2, which a
per-scanline callback cannot express. Mappers own their IRQ line and any
expansion audio.

Mappers 0 through 4 and 7 cover most of the licensed library; MMC1 and MMC3
alone cover more than half of it. The long tail (MMC2, MMC4, MMC5, the Konami
VRC family, Sunsoft FME-7, Namco 163, and the rest) is added board by board,
with unsupported boards refused by name.

### Snapshots and identity

Snapshots follow the bricks' single `TransferState` traversal and carry
everything execution depends on: mid-instruction CPU state, DMA arbitration,
pending edges and interrupt latches, decay deadlines, controller shift
registers, mapper state, disk position, and the pacing remainder. The typed
identity pins the configuration, the console model, the alignment, and the
image hashes. Forks share immutable images but own their mutable memory and
never flush saves or disk writes.

## Shared-layer changes

The shared layer is hardware-free, but several of its contracts are shaped
around the Game Boy family and have to generalise before the Deck fits without
special cases. Each is a real change to existing code.

- **Rational clock rates.** `IQueuedMachineCore.CyclesPerSecond` and its group
  equivalent are `ulong`, and neither NES master clock is a whole number of
  hertz. Rates become reduced numerator-and-denominator pairs, the host budget
  becomes `divrem(remainder + ticks × numerator, EngineTicks.PerSecond ×
  denominator)` in checked wide arithmetic, and `QueuedMachineCheckpoint`,
  which rejects any remainder of a second or more, changes its encoding in the
  same change.
- **Native frames.** `ITimeTravelMachineCore.Framebuffer` promises packed
  `0x00RRGGBB`. The Deck's authoritative frame is its nine-bit code, so the
  contract gains a native frame and a presentation-conversion seam, and NES
  palette conversion stays out of the shared worker.
- **Multi-port input.** `QueuedMachineHost` publishes one `"controls"` port and
  checkpoints encode one pad. The Four Score's four players on one console, the
  Famicom's second-controller microphone, and the Zapper need atomic, ordered,
  multi-port input images through queueing, checkpoints, replay, and runahead,
  with pointer and analog values quantised at admission. `MachineLinkPads`
  means one controller per linked machine and is the wrong tool for this.
- **Optional firmware.** Cold and fast boot handoff, and "no image means
  bundled firmware", become optional capabilities: an ordinary NES cartridge
  starts through its reset vector.
- **Content policy.** `GamingBrickContentPolicies` hard-codes
  `puck.cartridge.v1` and belongs with that format, not with hosting.
- **Post probes.** `CoreEmbeddingProbe` assumes one pad and RGB output, and
  `MachineStageProbes` derives throughput from a constant cycles-per-frame,
  which the NTSC odd-frame skip breaks; both measure what actually elapsed.
- **Link pacing.** `LinkPacer` compares budgets in each participant's own clock
  units. It stays as it is; a group mixing different clocks needs a common
  time basis first, and the Deck needs no console-to-console link.

## Verification

The Deck gets its own Post battery, `Puck.HumbleGamingDeck.Post`, over the
shared scaffold, keeping the tier meanings: Tier A is self-contained, Tier B
runs external corpora and skips when they are absent, Tier C covers sustained
scenarios, peripherals, and cross-machine contracts. It keeps the Humble
battery's outcome ledger, which pins recorded results so they cannot regress,
and adds an accuracy report that requires zero applicable failures, because a
ledger can stay green while preserving a recorded failure.

External corpora stay outside the repository and are fetched by the battery's
pinned, hash-checked manifest, as the Humble battery already fetches its
Game Boy test collection. The manifest gains licence, revision, and
hardware-applicability fields.

| Corpus or oracle | Use | Licence |
|---|---|---|
| AccuracyCoin | The accuracy gate for RP2A03G and RP2C02G; revision skips explained. | MIT |
| SingleStepTests `65x02`, `nes6502` set | Every opcode, compared bus cycle by bus cycle on a flat-memory harness. | MIT |
| nestest and its reference log | The first CPU milestone, from the `$C000` automation entry. | None stated |
| blargg's suites | CPU timing, interrupts, dummy accesses; PPU VBlank, NMI, sprites, open bus; APU; PAL. | None stated |
| `mmc3_test_2`, `mmc3_irq_tests`, the DMC DMA tests | Board and DMA behaviour, with per-revision expectations. | None stated |
| Mesen (pinned, instrumented) | Co-simulation: a versioned binary trace compared to the first divergence. | GPLv3, kept outside the repository |
| Visual 2C02 | Transistor-level evidence for PPU edge disputes. | Dataset terms unconfirmed |

Co-simulation extends what the Humble battery does against SameBoy: its trace
compares exact timestamps only for CPU events, and the Deck's needs master
timestamps and sub-phases for CPU bus ownership, RDY, M2, IRQ, and NMI edges,
PPU bus and A12 activity, raw pixels, and integer audio transitions. No
emulator is the final authority when hardware evidence disagrees, and each
understood discrepancy becomes an original Tier A regression.

## Packages

### 1. The shared-layer rename

**Owns:** `Puck.GamingBricks` and `Puck.GamingBricks.Post`, renamed
`Puck.Machines` and `Puck.Machines.Post`, with every reference, test,
architecture input, document, and skill. The forge, transpiler, and their tests
keep the `GamingBricks` name because they serve the bricks' cartridge format.

**Delivers:** a mechanical rename with no behaviour change and no aliases.

**Check:** both brick batteries, the affected link stages, and a semantic
reference audit showing no remaining use of the old names.

### 2. Neutral contracts

**Owns:** the shared-layer changes above.

**Delivers:** rational rates, native frames with a presentation seam,
multi-port queued input, optional firmware, the content policy moved to its
format, and generalised Post probes.

**Check:** both brick batteries unchanged, plus new fixtures for fractional
rates, checkpoint round trips at those rates, and multi-port input.

### 3. The CPU and the bus

**Owns:** `Puck.HumbleGamingDeck` and its Post battery, CLI registration, the
NES 2.0 loader, NROM, the bus, the 2A03, and snapshots.

**Delivers:** a per-cycle CPU with every official and unofficial opcode, on the
master-clock scheduler.

**Check:** every `nes6502` vector at bus-cycle granularity, and the nestest log
from `$C000`.

### 4. A complete NTSC machine

**Owns:** the PPU, the integer APU, standard controllers, reset, and the video
and audio host adapter.

**Delivers:** a machine that boots cartridges through the reset vector and
presents picture and sound.

**Check:** nestest's menu from a normal boot; raw-pixel, integer-audio, and
replay gates; mid-cycle snapshot and fork replay.

### 5. NTSC accuracy

**Owns:** the DMA arbiter, unstable-opcode behaviour, PPU races, open bus and
decay, and the co-simulation trace.

**Delivers:** accuracy at the state of the art on NROM.

**Check:** every applicable AccuracyCoin verdict, the blargg suites, and every
disagreement with an oracle explained and pinned.

### 6. Common boards

**Owns:** MMC1, UxROM, CNROM, AxROM, MMC3 and MMC6, board RAM, and bus
conflicts.

**Delivers:** most of the licensed library.

**Check:** the board suites, IRQ traces per MMC3 revision, and save and fork
restoration.

### 7. Regional hardware

**Owns:** PAL and Dendy console models and every supported alignment phase.

**Delivers:** the same accuracy discipline outside NTSC.

**Check:** the regional corpora and clock, pixel, and audio gates; no NTSC
pass is extrapolated to another region.

### 8. Long-tail boards

**Owns:** MMC2, MMC4, MMC5, the VRC family, FME-7, Namco boards, Bandai boards,
and discrete and clone variants.

**Delivers:** board coverage published as an evidence matrix.

**Check:** per-board evidence; unsupported boards refused by name.

### 9. Expansion audio

**Owns:** VRC6, VRC7, MMC5, Namco 163, and Sunsoft 5B generators, with
presentation gains.

**Delivers:** Famicom expansion sound.

**Check:** integer transition gates plus independent waveform evidence.

### 10. The Famicom Disk System

**Owns:** the RAM adapter, drive timing, its IRQ, writable disk overlays, FDS
audio, and a user-supplied BIOS.

**Delivers:** disk games, with the disk as persistent state.

**Check:** disk operations and mid-transfer snapshot and fork replay; firmware
identity checks.

### 11. Peripherals

**Owns:** the Four Score, the Zapper, the Famicom microphone, then selected
paddles, mats, and keyboards.

**Delivers:** the input devices games rely on. The Zapper's light sense is
computed from the core's own pixel history at the beam position and a modelled
sensor response, never from the presented image.

**Check:** serial-protocol fixtures, beam-response fixtures, and replay across
attachment changes.

### 12. World cabinets

**Owns:** engine and content-provider registration, multi-port routing, ordered
operations, and replay receipts.

**Delivers:** the Deck in a World cabinet beside the bricks.

**Check:** a real World canary running independent cabinets with input, audio,
video, saves, and restoration. Two existing World limits block full
multiplayer cabinet replay: an engaged display folds input into one port, and
provider operations are refused while recording.

## Open decisions

- **The accuracy matrix.** Which silicon revisions, regions, boards, and
  peripherals each milestone commits to. The proposal starts with RP2A03G and
  RP2C02G on NTSC.
- **Deterministic defaults** for what hardware leaves to chance: the CPU and PPU
  alignment, power-up RAM, and open-bus decay.
- **Corpora without a stated licence** (nestest, blargg's suites, the board
  tests, and the nes20db game database): fetched at run time by pinned hash, as
  the Humble battery already does, or cleared with their authors first.
- **The co-simulation oracle:** an instrumented Mesen build, supplied by the
  user as the Humble battery's SameBoy build is.
- **FDS firmware:** a user-supplied BIOS only, or an original Puck FDS BIOS.
  Games call BIOS services throughout play, so an original image is far larger
  work than the bricks' boot handoff and needs its own verification.
- **Performance:** a target for master-clock stepping on the Steam Deck floor,
  judged by counted work.

---

[Machines and cartridges](machines-and-cartridges.md) · [Plans](README.md)
