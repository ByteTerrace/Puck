# Puck.HumbleGamingBrick.Post

This executable verifies the deterministic GB/GBC/AGB-compatibility machine and
provides focused diagnostics.

## Run the battery

```powershell
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release
```

Optional battery arguments:

| Argument | Purpose |
|---|---|
| `--tier A|B|C` | Run one tier. |
| `--filter <text>` | Run stages whose names contain the text. |
| `--roms <directory>` | Override the GB reference-ROM root. |
| `--sst <directory>` | Override the SingleStepTests/sm83 corpus root. |
| `--artifacts <directory>` | Override `artifacts/gb-post`. |

Exit code 0 means every selected stage passed or skipped. Exit code 1 means a
check failed. Exit code 2 means infrastructure prevented a stage from running.

## Tiers

| Tier | Coverage | Assets |
|---|---|---|
| A | determinism; snapshot and battery-save round trips; victory metadata; fork determinism; AGB costume; trio lockstep; camera capture; throughput; zero-alloc-per-frame | none |
| B | SingleStepTests/sm83 per-instruction vectors (498 of 500 families asserted; `10`/`fb` are documented oracle-conflict skips); conformance-ROM CPU, timing, and audio suites; acceptance-ROM timer, PPU, interrupt, serial, DMA, instruction, and miscellaneous suites | `--sst`/`PUCK_GB_SST` for the vector corpus; `--roms`/`PUCK_GB_TESTROMS` for the conformance (blargg) + acceptance (mooneye) ROMs |
| C | synthetic link exchange for DMG/CGB/AGB costume pairings; snapshot churn; commercial link-game replay; cross-gen trade-cart save acceptance and complete trade | synthetic stages need none; commercial stages use `PUCK_GB_LINKROM` or `PUCK_GB_TRADEROM` |

Missing optional assets produce a skip rather than a failure. ROMs and boot
images are never committed to the repository.

## Snapshot identity

`MachineSnapshot` contains a machine identity, deterministic state bytes, and
an ordered section table used by diagnostics. Restore rejects incompatible
machine or cartridge identities. The section table is diagnostic metadata; the
serialized machine state remains the determinism surface.

Use `--hash-divergence [rom] [--frames N] [--fine] [--perturb-at N]` to compare
two fresh machines and report the first differing section and offset.

## SingleStepTests/sm83 vectors

The `sst-sm83` Tier-B stage drives the shared SM83 core through every vector in
the [SingleStepTests/sm83](https://github.com/SingleStepTests/sm83) corpus —
500 opcode families &times; 1000 hand-generated per-instruction cases, each
carrying the initial/final registers, the flat-RAM bytes the instruction
touches, and its M-cycle bus-pin trace. The core is isolated on a flat 64&#160;KiB
`ISystemBus` (mirroring the Advanced core's `FlatTestBus`-driven smoke
harness), so the corpus's own "64K of flat RAM, no registers or memory
mapping" assumption holds exactly; setting IME/halted and reading them back
after the step goes through the CPU's existing `SaveState`/`LoadState` seam
(`Sm83StateCodec`), not a new one. It validates the one-shared-SM83-core
doctrine instruction-by-instruction, off-ROM — evidence, never a gate: it
skips cleanly when the corpus is absent. Clone the corpus to
`D:\Source\ByteTerrace\Temp\sm83-sst` (the established corpus-clone
location pattern) or point `--sst`/`PUCK_GB_SST` at it.

Two opcode families are documented ORACLE-CONFLICT skips — excluded from
pass/fail, reported in the stage output with vector counts and a reason naming
both oracles, per "external suites are evidence, never gates":

- `10` (STOP): this corpus's reference models STOP as a one-byte
  opcode (PC+1), while this core's `ExecuteStop` deliberately reads a second
  operand byte (PC+2) — real-hardware STOP behavior is a long-debated
  two-interpretation question.
- `fb` (EI): this corpus's reference re-arms EI's delay countdown
  even when IME is already set; this core's EI-as-no-op-when-already-enabled
  is pinned by the acceptance suite's `ei_sequence` test (which stays green),
  so the acceptance suite is the oracle of record here. 485/1000 vectors differ,
  all on exactly that already-armed-IME edge.

No other family is skipped; a genuine mismatch fails loudly with the first
divergent field.

## BESS savestate interchange

`--bess-export <out> [--rom <path>] [--frames N]` writes a
BESS ("Best Effort Save State")-compliant file — `NAME`/`INFO`/`CORE`/an optional `MBC `
block/`END`, over the raw register/memory buffers the `CORE` block's
size/offset table points to — then proves the export/import round trip is
self-consistent by importing the bytes into a second, freshly built machine
and comparing a fingerprint over exactly the BESS-modeled state. `XOAM` is
legitimately omitted (this core does not model the extra OAM range); `RTC`,
`HUC3`, `TPP1`, `MBC7`, and `SGB` are out of scope for this first pass. Three
addresses are deliberately not replayed as plain register writes on import,
each because the spec itself flags the hazard: DIV (0xFF04, whose write
resets rather than sets it — restored through the timer's own snapshot seam
instead), and DMA-start/HDMA-start (0xFF46/0xFF55, whose write begins a
transfer). KEY1's double-speed bit is restored the same snapshot-splice way
DIV is (a plain write can only arm it, not force the live speed). STAT's mode
bits, LY, and NR52's channel-active bits are hardware-derived read-only status
the PPU/APU recompute live, so they are captured for interop but excluded from
the self-consistency fingerprint.

`--bess-import <file> [--rom <path>]` loads a BESS file — ours or a foreign
one — into a machine and reports the restored registers, IME/IE/IF,
LCDC/STAT/LY, and the cartridge's current ROM/RAM bank, so a state can be
eyeballed against another BESS-compliant tool. A reference emulator ships a
prebuilt tester binary, but its CLI has no savestate-import flag, so a live
cross-emulator round trip is not invokable headlessly; both commands print a
note to that effect, and the file's block/footer structure was instead
hand-verified against the BESS spec.

## Diagnostics

- `--bench` measures fleet scaling, catch-up, snapshot operations, allocation,
  and memory. Use `--bench-rom`, `--bench-frames`, and `--bench-fleet` to select
  the workload. Repeat `--bench-rom` (e.g. `--bench-rom a.gbc --bench-rom
  b.gbc`) to additionally run the mixed-mapper fleet section — machine `i`
  boots `rom[i % count]`, round-robin, and the bit-lock guard compares
  machine `i`'s serial snapshot against its own parallel snapshot for every
  `i`. Not a comma list: real ROM filenames often carry commas of their own
  (region tags like `"(USA, Europe)"`).
- `--halt-share <rom>` measures the proportion of emulated time spent halted.
- `--stat-trace <rom>` records instruction-level STAT, LY, and interrupt state.
- `--render <rom>` writes a framebuffer capture for a selected ROM.
- `--link-explore <rom>` records a two-machine link trace. `--model`,
  `--frames`, `--dump-every`, and `--out` refine the run.
- `--trade-explore`, `--trade-export`, and the related `--trade-*` arguments inspect
  the cross-gen-cart trade harness. These are diagnostics, not battery stages.
- `--dump-snapshot [--frames N] [--rom <path>] [--out <file>]` boots the
  synthetic ROM (or `--rom`), runs `N` frames (default 300), and writes the raw
  snapshot image plus a `<file>.sections.txt` sidecar (name/offset/length per
  component) to `artifacts/gb-post/snapshot.bin` by default. Prints the output
  paths and the snapshot's FNV-1a fingerprint. Offline input for diffing two
  builds' snapshot images byte-for-byte — `--hash-divergence` only proves a
  single build's internal determinism.

Every diagnostic must preserve the same machine construction and stepping
semantics as the battery. A trace is evidence; only a self-checking stage result
is a gate.
