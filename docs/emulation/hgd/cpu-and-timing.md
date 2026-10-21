# CPU and timing

The Deck's smallest unit of simulated time is one master tick. On NTSC,
236,250,000 ticks span exactly 11 seconds. `MachineCycleRate` retains that
fraction rather than rounding it to a whole number of hertz. The CPU takes
twelve master ticks per cycle. A call to `HgdMachine.RunCycles` adds to a
cumulative target, so dividing a budget into smaller calls produces the same
state. Any overshoot from manual stepping remains part of that accounting.

## Clock edges and the CPU

The reference scheduler advances one master tick at a time. Each CPU cycle
has equal internal φ1 and φ2 halves. The cartridge's M2 signal differs: on
RP2A03G it rises 4½ master ticks into the cycle and falls at tick twelve,
remaining high for 15/24 of the cycle, as described by
[NESdev's CPU pinout](https://www.nesdev.org/wiki/CPU_pinout).
The rising edge notifies the cartridge. The cycle's transaction (the CPU's or
the DMA unit's) runs at the read point, eight master ticks in, the middle of
M2's high phase: a read takes its data there, after the dot that ends on that
tick, and a write waits on the bus until M2 falls. The falling edge lands that
write, samples the CPU's IRQ and NMI inputs (on DMA cycles too), clocks the
APU, and then notifies the cartridge. Edge timestamps count integer half master
ticks, so no rounding is needed. The PPU takes four master ticks per dot, so
three dots pass in every CPU cycle. The NMI line follows the PPU after every
dot and after the cycle's write, so a $2000 write that enables NMI during
vertical blank reaches that cycle's sample. The IRQ line combines the
cartridge's and the APU's requests; the board's output trails its inputs, so a
request raised on the tick M2 falls (by that tick's dot or the landing write)
reaches the next cycle's sample. blargg's `ppu_vbl_nmi`, `cpu_interrupts_v2` and
`mmc3_test_2` suites pin these points to the PPU dot. The clock retains its position within both dividers in
snapshots. PAL and Dendy dividers belong to console-model capabilities when those
models are implemented.

`HgdCpu<TBus>` is a microsequencer: it retains the current opcode, cycle index,
addresses, operands, and internal latches between calls. One `StepCycle` makes
exactly one bus access. Operand fetches, page-crossing reads, stack accesses,
dummy reads, and both writes of a read-modify-write instruction all reach the
bus. A struct bus adapter specializes the same CPU for the NES address map
and the flat 64 KiB instruction-test memory.

The decode matrix includes all 256 encodings, including unofficial
instructions and JAM. Decimal status is stored, while ADC and SBC always
perform binary arithmetic on the 2A03. Unstable immediate instructions retain
their special-bus feedback across RDY repeats. Unstable indexed stores retain
the address-carry and halted-dummy-read state that affect their data and address.
The console capability supplies the unhalted precharge mask.

JAM reads the opcode and the following byte, then reads $FFFF, $FFFE, and
$FFFE before repeating $FFFF until reset. The program counter stays at the
byte after the opcode. Each part of this sequence is retained in snapshots,
and RDY holds its current read just as it does during other instructions.

## Interrupts, reset, and RDY

Interrupt polling uses the sampled inputs at the instruction's poll point.
CLI, SEI, and PLP poll before their final status change. A same-page taken
branch does not poll on its extra cycle; a page-crossing branch has another
poll before the high-byte correction. NMI edges latch independently of the
interrupt-disable flag and can replace BRK's or IRQ's vector while preserving
the stack values those sequences push. The interrupt-disable flag is set on
the low-vector read, after the status push. NMI is sampled when a CPU cycle
completes, including RDY repeats; a pulse between samples is not latched.
Recognition is blocked during vector selection and fetches. A late pulse
that ends in that window is lost; a sustained request can interrupt after
the handler's first instruction. The
[NESdev interrupt description](https://www.nesdev.org/wiki/CPU_interrupts)
explains these timing cases.

Power-on begins a seven-cycle reset sequence. Reset makes three stack reads
and decrements the stack pointer instead of writing stack memory. It then
fetches the reset vector. A later reset preserves A, X, Y, and RAM.

`Ready = false` holds a read cycle. The read repeats through the actual bus,
including any register side effects; time continues to advance. Writes finish
even when RDY is low. The [DMA unit](input-and-dma.md#dma) drives RDY, and
`NextAccessIsWrite` tells it when the CPU is about to write and cannot be
halted.

## Reproducible power-up and snapshots

`HgdPowerOnProfile` names the choices hardware leaves indeterminate. Its
default alignment is zero and its default work-RAM byte is zero. All four
alignment phases are accepted; none is chosen randomly. Components ask
capability questions on `HgdConsoleModel`, with NTSC RP2A03G as the implemented
model.

Each component lists its state once in `TransferState`. The machine writes
those lists into a `SnapshotImage` with clock, CPU, DMA, bus, PPU, nametable,
APU, controller, and mapper sections.
`HgdMachineIdentity` pins the layout version, console, named power-up profile,
decoded header configuration, and complete-image, PRG, and CHR fingerprints.
Restore rejects a different identity. Forks use the shared `MachineInstance`
and `MachineFork` lifecycle: immutable image bytes are shared, while every
RAM array and CPU latch belongs to one machine.

## The bus, loader, and boards

The CPU's 2 KiB work RAM mirrors through $1FFF. The PPU's eight registers
repeat through $3FFF, and $4000–$4017 hold the APU, OAM DMA, and controller
registers; $4018–$401F decode nothing and return CPU open bus. Every read or
write leaves its value on the CPU data bus, except a $4015 read, which is
internal to the 2A03 and leaves the bus as it was. Peeking changes nothing.
$4020–$FFFF routes to the cartridge.

The loader resolves the board from the header, without a game database.
It decodes iNES and [NES 2.0](https://www.nesdev.org/wiki/NES_2.0), including
twelve-bit mapper and submapper numbers, exponent-multiplier ROM sizes,
separate volatile and nonvolatile memory sizes, trainer presence, timing,
console details, miscellaneous-ROM count, and expansion-device metadata.
Damaged or ambiguous legacy reserved fields produce an explicit diagnostic.
The legacy zero PRG-RAM-size convention produces a diagnostic explaining its
8 KiB default; NES 2.0 can declare no RAM explicitly.

A board sees the PPU's whole window below $3F00 with the console's nametable
RAM, and routes nametable accesses to a page of it or to its own memory. It
also observes every PPU address and M2 edge, drives an IRQ line, and exposes
the battery-backed RAM a save file keeps. Every board keeps its RAM in one
`HgdBoardMemory`: PRG-RAM at $6000 (volatile bytes, then PRG NVRAM), CHR-RAM
(volatile, then CHR NVRAM), a four-screen image's own nametable RAM, and the
battery save (PRG NVRAM, then CHR NVRAM). A trainer initializes $7000–$71FF
when the board provides enough RAM. Unimplemented boards, submappers, console
types, timing families, and memory layouts are refused explicitly, each by
name.

- **NROM** (mapper 0) maps 16 KiB PRG twice or 32 KiB PRG once at $8000. It has
  8 KiB of CHR ROM or RAM, optional PRG-RAM, and header-selected nametable
  wiring.
- **[MMC1](https://www.nesdev.org/wiki/MMC1)** (mapper 1, `HgdMmc1`) takes
  32–512 KiB of PRG, up to 128 KiB of CHR ROM or CHR-RAM, and up to 32 KiB of
  PRG-RAM. Writes to $8000–$FFFF feed a five-bit shift register, lowest bit
  first; the fifth loads the control, CHR 0, CHR 1 or PRG register that address
  bits 13–14 select, and a write with bit 7 set empties the register and sets
  PRG mode 3. A write on the CPU cycle after another write is ignored, whatever
  its value, so a read-modify-write instruction reaches the port only with the
  value it writes first; the board counts cycles from M2's falling edges. PRG
  modes switch 32 KiB, or 16 KiB with the first or last bank fixed; CHR modes
  switch 8 KiB or two 4 KiB halves; mirroring is one-screen from either page,
  vertical or horizontal; PRG bank bit 4 disables PRG-RAM. On SUROM and SXROM
  (over 256 KiB of PRG) CHR register bit 4 selects the 256 KiB half, and on
  SOROM and SXROM (16 or 32 KiB of PRG-RAM) its bits 3, or 3 and 2, select the
  8 KiB PRG-RAM bank; in 4 KiB CHR mode the register PPU A12 last selected
  supplies those bits. Power-on sets the control register to $0C and clears the
  rest. SNROM's PRG-RAM disable through CHR bit 4 is not modelled: the header
  cannot tell SNROM from the boards that use that bit otherwise.
- **[MMC3](https://www.nesdev.org/wiki/MMC3)** (mapper 4, `HgdMmc3`) takes
  32–512 KiB of PRG, up to 256 KiB of CHR ROM or CHR-RAM, and up to 8 KiB of
  PRG-RAM. $8000/$8001 select and load the eight bank registers: R6 and R7
  switch 8 KiB of PRG with the second-last bank at $C000 or, with bank select
  bit 6, at $8000; R0–R1 switch 2 KiB and R2–R5 1 KiB of CHR, bank select bit 7
  swapping the pattern-table halves. $A000 selects vertical or horizontal
  mirroring (a four-screen image ignores it) and $A001 enables PRG-RAM (bit 7)
  and denies writes to it (bit 6). The scanline counter clocks on a PPU A12
  rise that follows three falling M2 edges with A12 low, each counted only when
  A12 was already low as M2 rose, so the eight low dots at the start of a line
  with the background at $1000 never clock it; with background
  patterns at $0000 and sprites at $1000 it clocks once a line at the sprite
  fetches. A clock reloads a zero counter, or one a $C001 write asked to
  reload, from the $C000 latch and decrements it otherwise; reaching zero with
  IRQs enabled ($E001) raises the IRQ until $E000 acknowledges and disables it.
  Submapper 4, the MMC3A, raises it only when the counter reaches zero from a
  nonzero value or through a requested reload. The hardware leaves the
  registers undefined at power-on; the board powers on with R0–R7 at 0, 2, 4,
  5, 6, 7, 0, 1, mirroring from the header, PRG-RAM enabled and writable, and
  the counter clear. MMC6 (submapper 1) and the other submappers are refused.

## Battery and corpora

Run the battery from the checkout:

```powershell
dotnet run --project src/Puck.HumbleGamingDeck.Post -c Release -- --fetch-corpora
dotnet run --project src/Puck.HumbleGamingDeck.Post -c Release
```

Tier A needs no external images. It covers independent-machine determinism,
zero steady-state allocation, fork ownership, every master phase of
mid-instruction replay (determinism, forks and replay each run an original
fixture per board: NROM's register loop, MMC1 mid-serial-write, MMC3 across a
scanline-counter IRQ), loader and bus cases, and original reset, interrupt,
and RDY fixtures. Throughput reports actual elapsed master ticks and never
gates correctness. The shared host-contract probes run against the Deck host
(see [Hosting](hosting.md#verification)). Original fixture cartridges, built by
`DeckProgram`, the forge's small assembler, check the rest of the machine against
independent oracles: `ppu-picture` compares every pixel of a rendered picture
with a reference renderer that maps each pixel straight from the same tables,
`shipped-cartridges` reads the pictures of the
[test cartridges](hosting.md#test-cartridges) worlds mount and checks that they
answer the pad,
`controllers` reads both ports through the pad mapping, `oam-dma` times the DMA
on both cycle parities and checks the copy with unused attribute bits cleared,
and `apu` checks a pulse waveform, length counters, delayed frame-counter
resets on both parities, the frame-interrupt period, and the DMC's initial
sample address and length against published figures. `embedding` also checks
that restore discards queued audio and that saves retain PRG and CHR NVRAM
while leaving volatile RAM out of the file.
`--tier A` and `--filter <text>` select focused runs.

Tier B compares every `nes6502-sst` opcode file: 256 families of 10,000
vectors. Each vector checks registers, touched RAM, and every bus access.
The report includes per-opcode cases and `nes6502-opcodes.csv`. A discrepancy
is a failure unless a documented hardware/oracle conflict supports a named
skip with its reason. The implementation declares no conflict skips.

The `nestest` stage starts at the $C000 automation entry and compares PC, A,
X, Y, P, SP, the PPU line and dot, and cumulative CPU cycles through the
reference log's last line, which checks the CPU and PPU clocks against each
other. `nestest-boot` boots the same image through its reset vector and runs
both menu pages from the controller, verifying the menu identity and absence
of a prior result before each page runs, then requiring OK beside "Run all
tests".

`blargg-suites` runs blargg's MMC3 (`mmc3_test_2` 1-5; 6 checks the MMC3A,
which a plain mapper 4 header does not select), CPU interrupt
(`cpu_interrupts_v2`), VBL and NMI (`ppu_vbl_nmi`) and APU (`apu_test`) ROMs
from the `nes-test-roms` archive and reads each verdict from $6000, where the
suites publish it; every ROM is one case, and the stage fails while any fails
or never finishes.

`accuracy-coin` runs every test of
[AccuracyCoin](https://github.com/100thCoin/AccuracyCoin), the suite that
separates today's most accurate emulators, by pressing Start on its menu, and
reads each test's result byte from RAM page 4; the ROM's own test table names
them. Each test is one case in the report: a pass, a skip for the ROM's $FF
marker, or a failure carrying the ROM's error code; the stage fails while any
test fails or never reports, and names them. The failures are the NTSC
accuracy package's work list. No file records an expected outcome. The Tier B stages skip an absent corpus and reject an
incomplete corpus supplied as present.

The corpus manifest uses the shared
[per-file form](../../../src/Puck.Machines.Post/README.md#individually-pinned-corpus-files)
for the instruction vectors, avoiding the much larger upstream repository
archive. The reference image and log use the `nes-test-roms` archive.
Downloads belong in the per-user corpus cache and never in source control.
`--corpus-cache`, `--sst`, `--roms`, and `--accuracy-coin` supply explicit
paths. The manifest's
hashes are the authority for downloaded bytes; a hash mismatch is refused.

The battery writes `post-report.txt`, `summary.json`, and `results.junit.xml`
under `artifacts/hgd-post`, or the directory selected by `--artifacts`.
The test project launches the Tier A executable and separately checks M2 edge
timing at every alignment, loader-header cases, and the AccuracyCoin stage's
result reporting against a fixture cartridge.

[Humble Gaming Deck](README.md) · [Machine emulation](../README.md)
