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
The rising edge notifies the cartridge; the falling edge completes the CPU
transaction and then notifies the cartridge. Edge timestamps count integer
half master ticks, so no rounding is needed. The clock retains its position
within the divider in snapshots.
These separate edges provide insertion points for the later PPU, APU, and DMA
arbiter. PAL and Dendy dividers belong to console-model capabilities when those
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
even when RDY is low. This is the input a later DMA arbiter can drive.
There is no DMA transfer engine in this package.

## Reproducible power-up and snapshots

`HgdPowerOnProfile` names the choices hardware leaves indeterminate. Its
default alignment is zero and its default work-RAM byte is zero. All four
alignment phases are accepted; none is chosen randomly. Components ask
capability questions on `HgdConsoleModel`, with NTSC RP2A03G as the implemented
model.

Each component lists its state once in `TransferState`. The machine writes
those lists into a `SnapshotImage` with clock, CPU, bus, and mapper sections.
`HgdMachineIdentity` pins the layout version, console, named power-up profile,
decoded header configuration, and complete-image, PRG, and CHR fingerprints.
Restore rejects a different identity. Forks use the shared `MachineInstance`
and `MachineFork` lifecycle: immutable image bytes are shared, while every
RAM array and CPU latch belongs to one machine.

## The bus, loader, and NROM

The CPU's 2 KiB work RAM mirrors through $1FFF. The register windows at
$2000–$3FFF and $4000–$401F return CPU open bus and ignore writes until their
owners are implemented. Every read or write leaves its value on the CPU data
bus, including a write to an unowned register. Peeking does not change it.
$4020–$FFFF routes to the cartridge.

The loader resolves the board from the header, without a game database.
It decodes iNES and [NES 2.0](https://www.nesdev.org/wiki/NES_2.0), including
twelve-bit mapper and submapper numbers, exponent-multiplier ROM sizes,
separate volatile and nonvolatile memory sizes, trainer presence, timing,
console details, miscellaneous-ROM count, and expansion-device metadata.
Damaged or ambiguous legacy reserved fields produce an explicit diagnostic.
The legacy zero PRG-RAM-size convention produces a diagnostic explaining its
8 KiB default; NES 2.0 can declare no RAM explicitly.

NROM maps 16 KiB PRG twice or 32 KiB PRG once at $8000. It has 8 KiB of CHR
ROM or RAM, optional PRG-RAM at $6000, and header-selected nametable wiring.
A trainer initializes $7000–$71FF when the board provides enough RAM.
Unimplemented boards, submappers, console types, timing families, and memory
layouts are refused explicitly. PPU pattern-memory hooks, PPU address-line
observations, M2 observations, and a mapper IRQ line form the board interface
for later packages.

## Battery and corpora

Run the battery from the checkout:

```powershell
dotnet run --project src/Puck.HumbleGamingDeck.Post -c Release -- --fetch-corpora
dotnet run --project src/Puck.HumbleGamingDeck.Post -c Release
```

Tier A needs no external images. It covers independent-machine determinism,
zero steady-state allocation, fork ownership, every master phase of
mid-instruction replay, loader and bus cases, and original reset, interrupt,
and RDY fixtures. Throughput reports actual elapsed master ticks and never
gates correctness. `--tier A` and `--filter <text>` select focused runs.

Tier B compares every `nes6502-sst` opcode file: 256 families of 10,000
vectors. Each vector checks registers, touched RAM, and every bus access.
The report includes per-opcode cases and `nes6502-opcodes.csv`. A discrepancy
is a failure unless a documented hardware/oracle conflict supports a named
skip with its reason. The implementation declares no conflict skips.

The `nestest` stage starts at the $C000 automation entry and compares PC, A,
X, Y, P, SP, and cumulative CPU cycles through the reference log's last line.
The PPU column is outside this package. Both stages skip an absent corpus and
reject an incomplete corpus supplied as present.

The corpus manifest uses the shared
[per-file form](../../../src/Puck.Machines.Post/README.md#individually-pinned-corpus-files)
for the instruction vectors, avoiding the much larger upstream repository
archive. The reference image and log use the `nes-test-roms` archive.
Downloads belong in the per-user corpus cache and never in source control.
`--corpus-cache`, `--sst`, and `--roms` supply explicit paths. The manifest's
hashes are the authority for downloaded bytes; a hash mismatch is refused.

The battery writes `post-report.txt`, `summary.json`, and `results.junit.xml`
under `artifacts/hgd-post`, or the directory selected by `--artifacts`.
The test project launches the Tier A executable and separately pins component
snapshot layouts, M2 edge timing at every alignment, and loader-header cases.

[Humble Gaming Deck](README.md) · [Machine emulation](../README.md)
