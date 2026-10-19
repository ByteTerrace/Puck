# Post harness and conformance testing

`Puck.HumbleGamingBrick.Post` is the executable battery for the Humble
Gaming Brick's deterministic machine contracts and focused diagnostics. Its
repository stages and the corpus ROM suites provide repeatable checks. External
ROM suites and other emulators provide evidence for hardware behavior; a passing
comparison is not, by itself, proof of physical-hardware parity.

## The Post test architecture

The battery runs ordered stages. Self-contained stages check the core,
snapshots, hosting, links, firmware boundaries, and other contracts. Corpus
stages discover ROM cases, dispatch them through `ProbeRunner`, and report what
each ROM did through `SuiteEvaluator`. No file records an expected outcome: a
ROM that does not pass fails its stage on every run, and the failures stay
visible until the emulator is corrected. The complete option table is
maintained in the [Humble Post README](../../../src/Puck.HumbleGamingBrick.Post/README.md).

### Running the battery

Fetch the pinned external corpora once for a cache, then run the battery:

```powershell
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --fetch-corpora
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release
```

Use `--tier A|B|C` or `--filter <text>` to narrow a run. The
`tests/Puck.HumbleGamingBrick.Tests` suite runs the battery with an empty
corpus cache, so its self-contained stages run in every CPU test pass and its
corpus stages skip. CI's runtime verification workflow (`verify.yml`) runs the
whole battery over the pinned corpora. The project README documents optional
commercial-cartridge inputs, artifact paths, and exit codes.

## Test ROM corpora

`corpora.json` names the external archives, versions, SHA-256 values, and roots
the corpus stages read. `--fetch-corpora` downloads and verifies those archives into
the local cache; it does not run the battery. Missing optional assets skip their
cases, or become infrastructure failures with `--require-assets`.

The corpus stages include suites such as the following. Their results are evidence
for the named behavior and are reported per suite, ROM path, and console model.

### Mooneye GB (Gekkio)

- `acceptance/` exercises CPU behavior, DAA, branch timing, and the HALT bug.
- `timer/` exercises DIV and TIMA edge timing, including reload and write cases.
- `ppu/` exercises STAT, mode transitions, and VRAM access restrictions.
- `mbc/` exercises bank switching and RAM protection for supported mappers.

### Blargg's test suites (Shay Green)

- `cpu_instrs` checks individual instruction behavior.
- `instr_timing` checks SM83 opcode timing.
- `mem_timing` checks memory access timing during PPU conflicts.
- `dmg_sound` checks APU length, envelope, and frequency-sweep clocks.

The current project README lists the additional suites, probe kinds, and the
per-case result rules. Do not describe these external suites as an independent
hardware oracle.

## Refuted PPU schedule decompositions

Do not re-derive the schedule by moving the PPU onto SameBoy's dots. All three
decompositions of that idea are measured and refuted, the coupled one included.
Moving both conventions together — a read taken at the machine cycle's drive
instant, the first line after an LCD enable at 448 dots, and every calibrated
constant re-derived so the CPU-observed edges land two dots earlier in the line
and the pixel pipeline two dots earlier still — is reachable and holds the whole
hardware tier: every mooneye acceptance group, blargg including `dmg_sound` and
`cgb_sound`, sst-sm83, the boot handoff, and every Tier A and Tier C stage stay
green, the `--cosim` `cpu` stream stays divergence-free, and the `ppu-pixel`
stream on `lycint_dmgpalette_during_m3_1` becomes content-identical to SameBoy
for 120 frames with both cores painting the mid-mode-3 palette from LY 1 x 157.
It closes what it is for — `lycint_dmgpalette_during_m3_1`/`_2` go pixel-exact
and `_3`/`_4` fall 429 → 143 — and loses far more than it closes: mealybug drops
from 3 exact to 2 and its differing-pixel total goes 26,135 → 43,957 with all 33
moved rows worse, AGE goes 2,886 → 8,462, and gambatte goes 285,900 → 427,834
and 82 cases net. The cost is the pipeline's move and nothing else: restoring
only the pipeline's absolute position (a longer mode-3 entry latency) returns
mealybug bit-for-bit to its earlier differing-pixel counts. Nor can the pipeline be held while
the LYC interrupt moves in its place — a two-dot-later interrupt view takes
`acceptance-ppu` from 34 to 20 — and holding the pipeline while moving only the
read view blocks on `intr_2_mode0_timing_sprites` (DmgC and CgbE), first
divergence at master cycle 11,218,088, `pc=0BDD`, an IF read returning 0xA3 on
SameBoy and 0xA0 here. SameBoy's pixel dots and the screenshot corpus disagree
by two dots, and the corpus is the gate. The two older decompositions fail
sooner: shortening the first line to 449 and giving the register view a +3
polled-event phase (carrying the polled mode lags with it) leaves the polled
STAT and LY dots exactly where they are but moves the interrupt raise and the
memory locks three dots early, which fails `hblank_ly_scx_timing`,
`intr_2_mode0_timing`, `intr_2_mode0_timing_sprites`, `intr_2_mode3_timing`,
`intr_2_oam_ok_timing`, `lcdon_timing` and `lcdon_write_timing`; moving only the
pixel pipeline three dots early (`Mode3EntryLatency` 8→5 with the mode-0 group
trailing the 160th pop) keeps every acceptance case green but takes the
mealybug/AGE error from 65.7k to about 78k differing pixels.

## The BESS save state standard

The Post executable exposes `--bess-export <path>` and `--bess-import <path>`
diagnostics for the BESS (Best Effort Save State) interchange format. Puck's
export and import cover the BESS-modeled scope: CPU registers and execution
state, selected PPU and interrupt fields, covered memory regions, mapper banks,
and CGB palettes where applicable. BESS does not represent every internal
sub-dot detail of the machine, so it is distinct from Puck's complete machine
snapshot.

The export diagnostic imports its file into a freshly built machine and compares
the modeled scope. It also checks that malformed files are rejected without
mutating the target. Importing a file from another BESS-compatible tool can be
used for inspection evidence. The diagnostic does not claim a headless,
cross-emulator gameplay round trip; the implementation records the limits of
what the BESS scope can restore.

## Hash divergence probing

`--hash-divergence` runs two Humble machines in lockstep and compares their
snapshot fingerprints at frame boundaries. `--fine` compares at scanline
boundaries instead. With no ROM path it uses a built-in synthetic cartridge;
with a second ROM or `--perturb-at <frame>` it can deliberately create a
divergence to check the localizer.

The probe uses the repository's FNV-1a snapshot hash and, on a mismatch, walks
the snapshot sections to report the first differing component, byte offset, and
hex window. This is a deterministic self-check and debugging aid. It does not
turn a comparison against another emulator into a physical-hardware oracle.
