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
| `--record` | Regenerate `Expectations.json` from measured outcomes instead of gating against it; see [Recording](#recording). |
| `--require-assets` | A suite's own ledger rows not matched by a discovered case fail infra (exit 2) instead of skipping — catches a corpus missing entirely, not just one absent ROM. |
| `--record-accept-regressions` | With `--record`, acknowledge that at least one case regressed from a recorded `pass` — otherwise the write is refused. |
| `--record-allow-shrink` | With `--record`, acknowledge that at least one recorded case is no longer discovered — otherwise the write is refused. |

Exit code 0 means every selected stage passed or skipped. Exit code 1 means a
check failed. Exit code 2 means infrastructure prevented a stage from running.

## Tiers

| Tier | Coverage | Assets |
|---|---|---|
| A | determinism; snapshot and battery-save round trips; victory metadata; fork determinism; AGB costume; authored-boot-ROM handoff; trio lockstep; camera capture; queued-host substrate contract; live cable-linked queued hosts and their coupled rewind; throughput; zero-alloc-per-frame | none |
| B | SingleStepTests/sm83 per-instruction vectors (498 of 500 families asserted; `10`/`fb` are documented oracle-conflict skips); the ledger-gated corpus — every suite under the resolved `--roms`/`PUCK_GB_TESTROMS` root, see [Corpus ledger](#corpus-ledger) | `--sst`/`PUCK_GB_SST` for the vector corpus; `--roms`/`PUCK_GB_TESTROMS` for the reference-ROM corpus |
| C | synthetic link exchange for DMG/CGB/AGB costume pairings; snapshot churn; commercial link-game replay; cross-gen trade-cart save acceptance and complete trade | synthetic stages need none; commercial stages use `PUCK_GB_LINKROM` or `PUCK_GB_TRADEROM` |

Missing optional assets produce a skip rather than a failure (or, under
`--require-assets`, an infra failure) rather than a check failure. ROMs and
boot images are never committed to the repository.

## Corpus ledger

Every Tier-B ROM suite under the resolved corpus root — blargg and mooneye's
`acceptance/` (the original two), plus AGE, both acid tests, mealybug, both
mooneye-test-suite trees (`acceptance/`, `emulator-only/`, `misc/`,
`manual-only/`) and the wilbertpol fork of them, SameSuite, GBMicrotest,
gambatte, little-things-gb, scribbltests, strikethrough, turtle-tests, bully,
rtc3test, and the MBC3 Bank Tester — is gated the same mechanical way: a
per-(suite, ROM path, model) row in `Expectations.json` records what that case
is expected to do, and each `LedgerRomStage` (`SuiteCatalog` discovers its
cases, `LedgerEvaluator` applies the rules below, `ProbeRunner` dispatches to
the probe named on the row) compares the ROM's actual behavior against it.
`Expectations.json` is generated, never hand-edited — the stage is the runner
that fails when it disagrees with its own source, the same shape as
`FileLengths.json`/`VerifiedCode.json`.

A row records the suite, the ROM's path relative to that suite's on-disk root,
the model (the exact `ConsoleModel` the case ran on — `DmgC`, `CgbE`, `Agb`,
`Mgb`, `Sgb2`, and so on, not a coarse family tag), the probe kind, the ROM
bytes' FNV-1a hash, and the outcome (`pass`/`fail`/`unrunnable`/`inconclusive`),
with a `Reason` for anything but `pass`, a `DiffPixels` count for a screenshot
`fail`, and — for a `screenshot` case — an `ExpectedImageHash`: the winning
expected-image candidate's FNV-1a hash, pinned the same way the ROM's is, so a
swapped or corrupted fixture is a recorded mismatch rather than a silently
different comparison.

Probe kinds:

- `conformance-serial` — the blargg `$A000` result block (`ConformanceRomProbe`).
- `acceptance-fibonacci` — the mooneye Fibonacci-or-`0x42` signature over
  serial (`AcceptanceRomProbe`); mooneye's `emulator-only/` and `misc/` trees
  use the same probe as `acceptance/`, since they are built with the same
  harness.
- `register-signature` — the same Fibonacci-or-`0x42` signature read straight
  from the register file after every frame instead of over serial
  (`RegisterSignatureProbe`), for suites that never transmit it: the
  wilbertpol fork and SameSuite. A register match is trusted only once the
  exit itself is corroborated — either the CPU has locked up
  (`Sm83StateCodec.ReadTail`'s `lockedUp` output, read through the CPU's
  existing `SaveState` seam) or an `0x40 LD B,B` opcode trap has been fetched
  at an instruction boundary (`ICpuTraceSink`) — since the register file can
  otherwise transiently match mid-computation. The run ends early once
  corroborated rather than burning the rest of the frame budget.
- `gb-microtest` — GBMicrotest's `$FF80`-`$FF82` result block, read through
  `SystemBus.DebugReadByte` (`GbMicrotestProbe`). The suite's own howto is
  explicit that `$FF82` is the sole reliable indicator and that `$FF80`/`$FF81`
  are not always set consistently even on a genuine pass, so this probe never
  gates on them agreeing — they are read only to annotate a failure's detail.
- `screenshot` — a fixed-frame-budget framebuffer capture packed through
  `FramebufferRgba`, compared pixel-exact against the first expected PNG (of a
  suite's device-tag fallback list) that exists on disk, decoded through
  `Puck.Assets.PngDecoder` (`ScreenshotProbe`) — extended in the same change to
  read the corpus's own sub-8-bit grayscale and palette-indexed screenshots
  (bit depths 1/2/4, color type 3 via `PLTE`/`tRNS`), not just the 8-bit
  RGB/RGBA/grayscale-alpha PngEncoder itself ever writes. This framebuffer's
  DMG shades and CGB `(X<<3)|(X>>2)` channel expansion already match every
  suite's "common palette" convention except gambatte's own CGB screenshots,
  which are rendered under gambatte's own weighted RGB mix — a case's
  `LedgerCase.Palette` selects that conversion (`GambatteCgbPalette`) before
  the comparison runs. A `ScreenshotProbe` case whose expected image still
  cannot be decoded reports `Inconclusive` instead of throwing, so one bad
  image never takes its whole suite's stage down as an infrastructure
  failure. Before comparing pixels, a shared `LivenessGate` requires the
  machine's program counter to have left a small window around its entry
  point and retired a minimum instruction count — a machine that never ran
  anything (a `jr $-2` reset trap, say) can otherwise leave the framebuffer at
  its power-on color, which coincidentally satisfies a "blank screen"
  expectation without the ROM under test having done anything; that case is
  also `Inconclusive`.
- `hex-pattern` — gambatte's `_out<hex>` convention: the expected hex digits
  are drawn as 8x8 monochrome tiles at the top of the screen, compared against
  a ported copy of `test/testrunner.cpp`'s own glyph table (`HexPatternProbe`).
  A pattern character with no glyph is `Inconclusive` rather than an early
  pass. The cell right after the last digit is deliberately not required to
  be clear — several real cases tile further hex-shaped content immediately
  past their own result digits as part of the screen they draw.
- `audio` — gambatte's `_outaudio0`/`_outaudio1` convention: whether the final
  rendered frame's audio output is constant (silence) or varies (sound),
  drained through `IAudioSink` at half the CPU clock (`AudioProbe`), gated by
  the same `LivenessGate` a `screenshot` case uses — a dead machine's silent
  audio ring would otherwise trivially satisfy `_outaudio0`.

Stage semantics, per case: the gate requires the recorded and actual outcome
to be **equal** — `pass`, `fail`, and `inconclusive` are three distinct
recorded verdicts, none folded into another, so a regression into
inconclusiveness (a liveness-gate catch, an undecodable glyph, a vanished
expected image) is caught exactly like a regression into `fail`.

- Actual equals the recorded outcome → counted (`N pass`, `M recorded-fail`,
  `L recorded-inconclusive`, `K unrunnable`).
- A recorded `fail` that now passes → the stage **fails**
  (`ratchet: recorded fail, now Pass`) — a fix is a deliberate, recorded act,
  never a silently-loosened gate.
- Any other change of outcome (most importantly a recorded `pass` that now
  fails or turns inconclusive) → **fails** as a regression.
- A recorded `fail` whose actual outcome is still `fail` but whose
  screenshot `DiffPixels` changed → **fails**, naming both counts.
- A ROM present on disk with no ledger row → **fails** as `unrecorded`.
- A ROM, or a screenshot case's expected image, whose bytes no longer match
  its recorded hash → **fails** as a hash mismatch (checked before the probe
  runs, so a swapped fixture is never silently absorbed into a pixel diff).
- A ledger row for a suite this run's own discovery does not produce a
  matching case → skipped, or (`--require-assets`) an infra failure. This is
  computed against the ledger's own rows for the suite, so it also catches a
  corpus root that resolves to nothing at all (a typo'd `--roms`, say) —
  discovery finding zero cases is not by itself proof there was nothing to
  find.

## Recording

`--record` measures every ledger stage's cases fresh and diffs the result
against the existing `Expectations.json` before writing anything — a suite
this run did not measure (an unselected `--tier`/`--filter`, or a suite this
corpus checkout simply does not carry) is carried over unchanged, so only a
suite this run actually touched can appear in the diff. Every case whose
outcome changed is printed (`ratchet: ...` for a recorded `fail` resolving to
`Pass`, `regression: ...` for anything moving away from a recorded `Pass`,
`dropped: ...` for a recorded case no longer discovered), and the write is
refused (exit 2, nothing written) when any of the following holds:

- Any stage in this run ended in infrastructure failure — a ledger built from
  an incomplete run is worse than no ledger at all.
- Two measured cases share a (suite, path, model) key — the file is sorted and
  keyed by exactly that triple, so a collision can only mean two stages are
  tagging the same case.
- At least one case regressed from a recorded `Pass`, unless
  `--record-accept-regressions` is passed.
- At least one recorded case is no longer discovered, unless
  `--record-allow-shrink` is passed.

Both flags are acknowledgments, not defaults — running `--record` against a
partial or misconfigured corpus is expected to refuse rather than quietly
erode the ledger. Run `--record` after a deliberate emulator correction
changes a recorded outcome, exactly like re-recording a Post baseline; never
to paper over an unexplained regression, and never routinely against a
corpus checkout that does not carry everything the ledger already records.

## The authored boot ROM

The `boot-rom-handoff` Tier-A stage boots every revision through the image
`Puck.HumbleGamingBrick.Forge`'s `BootRomBuilder` emits, against ten synthetic
headers (both licensee buckets, both color flags, the title checksums the Color
handoff branches on, and the checksums its boot timing tells apart by the fourth
title letter) plus the first few reference-corpus cartridges whose logo and
header checksum the hardware would accept. Each boot is compared, field by field,
against the same cartridge on a machine started at the seeded post-boot state,
and the first differing field is named.

The compared surface is what a cartridge can read at `0x0100`: the processor
register file, the divider counter, every readable high-page register, the
interrupt-enable register, and Color palette RAM. In compatibility mode the
palette data ports read sealed, so what is compared there is the index registers
the image's own compatibility-palette load leaves behind. Outside the surface, and
unreachable by
any executing program: the sub-register phase of the picture processor's pixel
pipeline and of the audio generators, which the seeded handoff sets to captured
constants (the seeded square-channel timer exceeds its own reload period; the
seeded dot phase is odd where every instruction boundary lands on a multiple of
four dots). On the revisions whose seeded handoff parks on the first line the
status register's LY-comparison bit is masked for the same reason: the seeded
state holds that latch clear with LY and LYC both zero, and the running processor
recomputes it every dot.

`MachineIdentity` fingerprints the boot ROM image, so a machine booted through an
authored image has a different identity than a seeded one; their snapshots do not
interchange, and nothing aliases them.

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
- `--render <rom> <out.png> [frames] [dmg|cgb|agb] [--boot puck]` writes a
  framebuffer capture for a selected ROM. Without `--boot` the machine starts at
  the seeded post-boot state; `--boot puck` runs the forge's authored boot ROM
  for the model from reset, so an early capture shows the boot itself.
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

## Co-simulation

`--cosim <rom> --sameboy <sb-trace.exe> --boot <dir> [--model dmg|cgb]
[--frames N] [--kind cpu|ppu|pcm|all] [--out <dir>]` reports the FIRST
divergent conceptual event between Puck and a SameBoy oracle for a ROM, so
PPU/APU accuracy work is trace-led instead of knob-swept. Both sides boot the
SAME boot ROM image from `<dir>/dmg_boot.bin` or `<dir>/cgb_boot.bin`
(SameBoy's own `GB_MODEL_DMG_B`/`GB_MODEL_CGB_E` — DMG-B and DMG-C are
identical for this purpose) rather than Puck's seeded post-boot state, so a
mismatch cannot be a seeded-state artifact the two paths would otherwise
disagree about. `--boot puck` substitutes the forge's authored image for the
model, written to the output directory and handed to both sides, which puts the
authored boot program itself under the oracle. `--sameboy` names a built `sb-trace.exe` (see below); missing
ROM, boot ROM, or `sb-trace.exe` skips (exit 0). Exit 0 means no content
divergence in the requested budget (a trailing run-length difference at the
`--frames` cutoff is reported but does not fail — see below), exit 1 means a
content divergence was found, exit 2 means infrastructure (bad arguments, or
`sb-trace.exe` itself failed).

A run of fewer than about 60 frames compares only the boot ROM: the DMG logo
scroll occupies them, so every ROM yields the same stream and the same record
count. Give a comparison at least 120 frames, and treat two ROMs reporting an
identical record count as evidence the ROMs never ran.

The trace is a stream of conceptual events, not raw internal state — raw
fetcher-step equality would flag the documented object-fetcher oracle skew
(`hardware-and-oracles.md` §4) as a false divergence. Each event carries the
master T-cycle count since reset and one of:

- **cpu**: an instruction boundary — PC and every register, sampled before the
  instruction runs (`Sm83.ICpuTraceSink.OnInstructionBoundary`).
- **ppu**: a polled STAT mode transition (the LY register and the STAT mode
  bits, both as the CPU reads them) or a pixel pop (LY, X, the final packed
  color) (`Ppu.IPpuTraceSink`). Selecting `ppu` also produces the
  pixel-pop stream, which is large — keep `--frames` small for it.
- **pcm**: a PCM12/PCM34 sample. `Puck.HumbleGamingBrick` carries a trace seam
  only in `Ppu.cs` and `Sm83.cs`, so PCM has none of its own; a `pcm`-kind run
  polls `IApu.ReadPcm` from inside the CPU instruction-boundary callback
  instead of every T-cycle. The frame sequencer that drives a PCM change is
  far slower than the CPU's instruction rate, so no distinct level is lost in
  practice.

Puck's own seams (`ICpuTraceSink`/`IPpuTraceSink` in `Sm83.cs`/`Ppu.cs`) are
dormant nullable fields, each guarded by one predicted-not-taken null test —
the same shape `SystemBus`'s debug watchpoints already use — so an ordinary
boot and the whole battery pay nothing; `--tier A`'s `throughput`/`zero-alloc`
stages prove it.

Comparison groups events into `cpu`, `ppu-mode`, `ppu-pixel`, and `pcm`, and
walks each group index-by-index. The two PPU sub-streams carry different
evidence: pixel records are exact and index-aligned on both sides (160 per
visible line, in order), while SameBoy re-reads STAT once per `GB_run()` call
and so cannot observe a polled mode that holds for less than the instruction in
flight — the line-start mode-0 window is one 8 MHz unit wide in
`Core/display.c`. The `ppu-mode` walk therefore steps over a record present on
one side only when the record after it matches, and reports how many it stepped
over. Only `cpu` events compare their cycle stamp exactly — SameBoy samples
STAT/LY/PCM once per `GB_run()` call rather than once per T-cycle, and a
pixel-pop record's cycle is the whole scanline's mode-3-exit cycle shared by
all 160 columns (SameBoy exposes no per-pixel push hook); `ppu-mode`,
`ppu-pixel` and `pcm` divergences compare content only and report Puck's own
(exact) cycle. A group whose content agrees for
every record both sides produced, but whose two streams end at slightly
different lengths, is reported as a `TRAILING LENGTH MISMATCH` and does not
fail — `sb-trace`'s `GB_run()` loop and Puck's `Machine.StepInstruction()`
loop each finish the whole unit of work in flight rather than stopping exactly
on the `--frames` cutoff, so the last instruction near the budget can differ
by a few T-cycles on either side.

### Building `sb-trace.exe`

`sb-trace.exe` is a headless SameBoy build living outside this repository
(`D:\Source\ByteTerrace\Temp\SameBoy`), extended with an `events` mode
(`trace_main.c`) that emits the binary record stream `CosimEvent` in
`CosimTraceRecord.cs` mirrors — keep the two in sync. Rebuild it with:

```
bash build/sbtrace/build.sh
```

run from the SameBoy checkout root. The script recompiles `Core/*.c` (with
`-DGB_INTERNAL`), the `Windows/*.c` compatibility shims, and `trace_main.c`
with clang targeting `x86_64-pc-windows`, then links `build/sbtrace/sb-trace.exe`.
Pass `--display` to only recompile `Core/display.c` when the rest of `Core` is
already built. Note that `trace_main.c` sits outside `Core/`, so a change there
needs its own compile before the link.

`Core/display.c` also carries a dormant per-dot trace, gated on the
`SAMEBOY_PX_TRACE` environment variable naming an output file (and optionally
`SAMEBOY_PX_LINE` naming one LY to restrict it to). It logs every pushed pixel,
every fetcher step, every polled STAT mode change, every raised STAT interrupt,
and the LCDC/WX/palette/scroll writes, each stamped with the exact master
T-cycle. That stamp is what makes the two sides' dot schedules comparable at
one-dot resolution, which the `--cosim` event stream alone cannot do for the
PPU. Set the variables in the process that spawns `sb-trace.exe`; a Git Bash
`VAR=x sb-trace.exe` invocation does not reach it.

SameBoy's `events` mode samples STAT/LY once per `GB_run()` call (the finest
step SameBoy exposes short of its internal `cycles_since_run` counter, which
is unreachable from `trace_main.c` — it is compiled without `GB_INTERNAL`, so
`GB_gameboy_t` is opaque beyond the public accessors). The sample is skipped
entirely while LCDC.7 is clear, matching `Ppu.Tick()`'s own early return
before its trace guard, so an LCD disable/enable pair does not show as a
spurious divergence against a side that emits nothing while off. A finished
scanline's 160 pixels are written just before the mode event that closes the
line out, matching the order Puck's own live pixel-by-pixel pops end in.
