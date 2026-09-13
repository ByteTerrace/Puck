# Puck.AdvancedGamingBrick.Post

This executable verifies the deterministic AGB-native machine and hosts the
cycle, render, state, and co-simulation diagnostics used for accuracy work.

## Run the battery

```powershell
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release -- --fetch-corpora
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release -- --bios <GBA_bios.rom>
```

The first command fills the local corpus cache from the archives pinned in
[corpora.json](corpora.json); it is needed once per version bump. Without a
BIOS the BIOS-dependent stages skip.

| Argument | Purpose |
|---|---|
| `--tier A|B|C` | Run one tier. |
| `--filter <text>` | Run stages whose names contain the text. |
| `--parallelism <n>` | Cases measured at once inside a corpus stage; default one per logical processor. |
| `--bios <file>` | The 16 KiB comparison/test BIOS; a zeroed diagnostic stub without it. Bundled-firmware stages always use the packaged Puck image. |
| `--roms <directory>` | A conformance-corpus root instead of the cached `gba-tests` corpus. |
| `--fuzz <directory>` | A fuzz-corpus root instead of the cached `fuzzarm` corpus. |
| `--games <directory>` | The commercial-ROM directory for the render-hash floors. |
| `--accuracy-suite <file>` | The accuracy-suite ROM; its stage skips without it. |
| `--ags <file>` | The AGS aging-cartridge ROM; its stage skips without it. |
| `--link-game <file>` | A commercial multiplayer cartridge for `link-game-replay`; skips without it. |
| `--solar-rom <file>` | A commercial solar-sensor cartridge for `solar-replay`; skips without it. |
| `--artifacts <directory>` | Override `artifacts/agb-post`. |
| `--fetch-corpora` | Fill the corpus cache from the manifest and exit. |
| `--corpus-cache <directory>` | Cache root used for fetching and resolving corpora; default is `Puck/corpora` under the OS local application-data directory. |

Exit code 0 means every selected stage passed or skipped. Exit code 1 means a
check failed. Exit code 2 means infrastructure prevented a stage from running.
Every run writes a text report, a JSON summary and JUnit results under the
artifacts directory: by default `artifacts/agb-post/post-report.txt`,
`artifacts/agb-post/summary.json` and `artifacts/agb-post/results.junit.xml`.
JUnit contains one test case per conformance ROM, fuzz ROM, or render floor.
Corpus stages run their cases on every processor at
once; the self-contained Tier-A stages run at the same time as each other,
except the throughput and zero-alloc measurements, which run alone.

## Tiers

| Tier | Coverage | Assets |
|---|---|---|
| A | CPU and bus smoke vectors; bundled firmware services, reset, presentation and legacy sound; exhaustive BG/OBJ priority and transparency combinations with window/effect cases; text tile-row sampling; cycle-budget execution parity; determinism; state round trip; fork determinism; save round trip; bounded queued-host backpressure and immutable frame publication; throughput; zero-alloc-per-frame | none; selected firmware and execution-parity fixtures additionally use a verified retail BIOS when available |
| B | firmware result oracle; conformance CPU/save/misc suites; ARM fuzz corpus; render hashes; accuracy suite; AGS aging cartridge | assets listed below; stages skip when absent |
| C | normal serial clock ownership; native firmware download; deterministic multiplayer cable replay and a commercial link-game replay | synthetic replay and firmware download need none; commercial replay needs a retail BIOS and `--link-game` |

The asset-free `lifecycle` stage checks HALT/STOP cycle budgets, keypad wake
and snapshot replay, DMA during HALT, the fourteen-bit DMA0–2 and sixteen-bit
DMA3 count boundaries, and audio disable/drain/re-enable. `host-persistence`
checks BIOS option validation, clean-snapshot save restoration, forced flush,
and disposal. On Windows it also holds a destination open without delete
sharing to prove a failed replacement preserves the old save and retries
without another emulated write. BIOS-independent lifecycle and persistence
probes use explicit zero-filled images; host option probes also exercise the
bundled default and its independent cold/fast selection.

`embedding` checks the synchronous public core without host infrastructure,
including per-machine option isolation, trace callbacks, configured forks,
typed snapshot compatibility, input, video, audio and replay.

`bundled-firmware` always executes the bundled image, independently of `--bios`.
It checks image identity and copy isolation, cold/fast startup selection and
snapshot interoperability, then calls the real ARM and Thumb SWI vectors for
arithmetic, transfers, affine transforms, codecs, sound control and interrupt
waits. Its VBlank callback exercises native IRQ dispatch and return. These
are functional vectors, not retail timing or complete music/link conformance.

`firmware-reset` executes nonreturning SoftReset into original ROM and EWRAM
entry programs, checks cleared registers and banked stacks, and exercises
HardReset through the native cold presentation. Selective RAM/register reset
and Halt/Stop/CustomHalt fixtures check memory boundaries, wake-source filtering
and snapshot replay. These sleep checks do not establish hardware STOP clock
freezing or retail instruction timing. Sound reset must clear stale playing
samples and partial writes, then queue silence; it does not leave empty FIFOs.
The stage runs 44 bundled-image cases and, when available, 42 verified-retail
controls. HardReset is Puck-only: its
original calling cartridge does not satisfy the retail boot header gate.

`firmware-presentation` also always uses the bundled image. It compares all
38,400 native pixels with readable PUCK/BYTETERRACE geometry and the
ink/ivory/mint palette, captures fade-in, full wordmark, fade-out and cartridge
handoff under `firmware-presentation/`, and checks centered two-note PCM at
48 kHz. A mid-boot snapshot must replay and fork to identical machine bytes,
pixels and fresh PCM; typed and raw restore must discard queued samples.
Original cartridges with different logo fields and deliberately malformed
metadata pin the firmware's unchecked-header policy. These checks do not prove
commercial-game compatibility or retail timing parity.

The separate `render-hash` stage uses fixed instruction budgets. Keep the BIOS
image identical across compared builds: another firmware implementation may
reach a different frame within that budget. A shifted floor needs an explanation,
not an automatic update merely because a replacement BIOS can draw a picture.

`firmware-oracle` compares the bundled image with a locally supplied, verified
retail BIOS through original ARM and Thumb calling cartridges. It compares
documented arithmetic results, affine outputs (including zero and odd byte
strides), transfers, bit unpacking and
decompression in RAM, plus SoundBias direction and output-resolution bits in
its I/O register. It reports bounded mismatch examples and per-service
totals; it neither reads out firmware code nor treats timing or unspecified
register clobbers as functional equality. Without a verified retail image,
this stage skips instead of claiming independent evidence.
Explicit non-unit numerators divided by zero are reported separately: both
images must remain within BIOS for a two-million-instruction observation,
advance clocks and preserve snapshot replay. That is bounded nonreturn evidence,
not a proof of infinite execution or a successful arithmetic result. Every
unexpected timeout in a returning-result case remains a failure.

`firmware-legacy-sound` checks music-player management, exported commands,
installed command/note/PSG callbacks and all four PSG voice layouts. It also
checks exact stereo PCM blocks, fractional-rate interpolation, release and fade
progression, modulation, reverb feedback and mixer overflow. A verified retail
image additionally runs the same black-box fixtures. These bounded cases do not
establish exhaustive sequencer, mixed-audio or retail timing parity.

`firmware-sound-guards` uses an original native callback to observe the busy
marker and attempt bounded reentry into SoundDriverMain. Separate fixtures check
that rejected calls preserve caller-owned sound state and that VSyncOff/On
transitions handle the guard, DMA controls and PCM buffers consistently. These
are state contracts, not sound timing measurements.

`firmware-sequence` runs original native songs through jumps, one- and
three-level pattern calls and returns, bounded observations of endless repeat,
finite repeats, note gates with optional length extensions, explicit and implicit
tied-note endings, and a fixed-rate waveform loop. It checks authored command
positions and note-lifetime boundaries, then compares selected caller-owned
records and 96 stereo 8-bit PCM samples per tick with a verified retail BIOS
when supplied. These fixtures are not complete sequencer or timing conformance.

`firmware-multiboot` connects an original native sender cartridge invoking
SWI `0x25` to the bundled cold-boot receiver. A separately written protocol
model checks the wire exchanges, encryption and checksum before the downloaded
EWRAM program proves its handoff. It covers normal 32-bit modes at 256 kHz and
2 MHz, plus multiplayer with one and three children. The single-child
multiplayer fixture also exercises mid-transfer snapshot/reconnect replay and
rejects a corrupted wire checksum. Empty and erased cartridge slots enter the
receiver. This is not evidence for Joybus download, physical cable behavior or
retail timing.

`serial-clock` checks that an externally clocked normal receiver remains busy
until its sender supplies the clock. It covers both normal widths and rates,
delayed startup, final-clock completion, clock changes while armed, cancellation
of superseded events and one interrupt per completed transfer. The 32-bit cases
also verify payload exchange in both directions.

## External assets

None of these ship with the repository; each stage skips cleanly when its asset
is absent. The two public corpora are pinned by archive, version, and SHA-256
in [corpora.json](corpora.json) and fetched once into
`Puck/corpora` under the OS local application-data directory, or the explicit
`--corpus-cache <directory>`;
everything else is a per-machine file named on the command line.

| Asset | Source | Configuration |
|---|---|---|
| 16 KiB AGB BIOS | (retail dump) | `--bios` |
| conformance + PPU corpus | jsmolka `gba-tests` | `corpora.json`, or `--roms` |
| ARM fuzz corpus | `DenSinH/FuzzARM` | `corpora.json`, or `--fuzz` |
| commercial-ROM root | (user ROMs) | `--games` |
| accuracy-suite ROM | `mgba-emu/suite` (built from source) | `--accuracy-suite` |
| TCHK10 AGS aging cartridge | (aging-cartridge dump) | `--ags` |
| commercial multiplayer cartridge | (user ROM) | `--link-game` |
| commercial solar-sensor cartridge | (user ROM) | `--solar-rom` |

Diagnostics that depend on retail BIOS timing reject replacement or unknown
images unless `--allow-replacement-bios` is supplied deliberately. Missing
optional assets skip the corresponding stage.
An explicitly supplied `--bios` must be readable and exactly 16 KiB; an invalid
file exits 2 rather than silently selecting the stub.

## Performance diagnostics

`--bench [--bench-rom <rom>] [--bench-frames N] [--bench-fleet N,N,...]` runs
the machine-fleet bench: fleet scaling (independent/choir streams ×
serial/parallel), burst catch-up, and `Create`/`Snapshot`/`Restore`/`Fork`
latency and allocation. `--bench-rom` defaults to the same zero-asset
synthetic cartridge the throughput stage runs. Every fleet cell ends with a
serial-vs-parallel bit-lock check; a divergence exits 1. Mirrors the Humble
Post's `--bench`.

Add `--bench-warmup-frames N` to start every measured fleet and burst cell
from the same snapshot after `N` frames with keys released. This separates
later execution from boot cost; construction and restoration happen outside
the stopwatch. The input script begins at frame zero after restoration.
Without this option, cells start at direct boot as before. Audio output is
disabled in this benchmark, and latency probes retain their separate
120-frame warm-up. Repeat both builds on a quiet machine and compare the
same ROM, BIOS, warm-up and frame budget; a short boot run does not predict
gameplay throughput.

For performance refactors, retain the original executable and compare raw
`--dump-snapshot` images across builds. Same-build replay checks alone cannot
detect a deterministic regression. The `ppu-composition` stage separately
checks 131,072 pixels against a fully sorted layer model, including window
masking, transparent backgrounds, priority ties and semi-transparent OBJ.
The `cartridge-fetch` stage checks wide reads against byte reads at sensor
overlays, empty and odd-sized ROM boundaries, and cartridge page boundaries.

The `ppu-text-row` stage compares 245,760 pixels with an independent scalar
memory-image model. It covers 4/8 bpp tiles, all four map sizes and character
bases, scrolling across tile and screen-block boundaries, flips, transparency,
palette writes between lines, out-of-range tile data, and mosaic.

## Determinism diagnostics

`--compare-execution <rom> [--frames N]` compares manual instruction stepping
with `RunCycles` at every frame-sized budget (600 by default). It compares
instruction counts, full snapshots, and drained 32 kHz stereo PCM, with the
same varying key inputs on both machines. The reference uses a forwarding
timer decorator, which keeps its bus on the interface-driven clock path;
the candidate uses the built-in controllers' published readiness cache.
Exit 1 means a mismatch; exit 2 means a missing ROM or invalid frame count.
Reported execution times exclude state/audio comparisons, alternate which
machine runs first, and include JIT warm-up and the reference decorator's
overhead. Use frozen-build fleet benchmarks for performance claims.

The Tier-A `cycle-budget-execution` stage uses the same comparison at zero,
negative, and irregular positive budgets. Its ARM and Thumb loops cover
instruction overshoot, and its IWRAM case edits running code, restores an
earlier snapshot, and replaces executing code through DMA3. With a verified
retail BIOS, four additional micro-ROMs cover timer IRQs, cascaded timers,
and interrupt-enable delay. These checks establish execution-path equivalence,
not independent hardware accuracy. Keep frozen-build snapshot comparisons
and the external conformance suites as separate evidence.

`--hash-divergence [rom] [--frames N] [--fine] [--perturb-at N]` compares two
machines and identifies the first differing snapshot section and byte. Use
`--state-roundtrip <rom>` to inspect a focused snapshot/restore case and
`--save-test <rom>` to inspect cartridge backup persistence.

## ROM and render diagnostics

- `--render <rom> <output>` captures a framebuffer.
- `--render-hash <rom>` prints deterministic render hashes.
- `--pctrace`, `--statetrace`, and `--trace-crash` record execution state at
  different levels of detail.
- `--iodump` records I/O state.
- `--gen-rom` writes a hand-assembled diagnostic cartridge.
- `--oracle` runs the self-contained cycle-probe set.
- `--ags` and `--accuracy-suite` run their configured evidence cartridges.
- `--dump-snapshot [--frames N] [--rom <path>] [--out <file>]` boots the
  synthetic cartridge (or `--rom`), runs `N` frames (default 300), and writes
  the raw snapshot image plus a `<file>.sections.txt` sidecar (name/offset/
  length per component) to `artifacts/agb-post/snapshot.bin` by default.
  Prints the output paths and the snapshot's FNV-1a fingerprint. Offline input
  for diffing two builds' snapshot images byte-for-byte—`--hash-divergence`
  only proves a single build's internal determinism.

## Co-simulation

`--lockstep <rom> <steps> [direct] --ares <executable> --bios <image>` compares
Puck with an explicitly supplied `ares-cosim` co-simulator. Both executable
and BIOS paths are required. `--trace-cycles` compares instruction timing with
the cosim oracle. Normalize the traces before interpreting a mismatch:

- the oracle's cumulative cycle count restarts at frame boundaries, so compare
  per-instruction deltas;
- Puck's pipeline representation exposes PC four bytes ahead of the oracle's trace;
- direct-boot and full-BIOS runs have different initial state. Lockstep boots
  the BIOS by default; its positional `direct` selects direct boot on both
  sides. The `--render` and `--probe` inspectors default to direct boot and
  accept `--full-boot` to start from BIOS reset.

Use a minimal self-checking ROM when isolating one timing rule. Store the result
in emulated memory and compare the observable value before using an instruction
trace to explain it.
`--ags-trace` enables detailed AGS tracing. `--no-rtc`, `--no-prefetch` and `--bus-trace`
map to `AgbMachineOptions`; the trace goes to standard error. These overrides
are supported by `--render`, `--probe`, `--ags`, `--accuracy-suite`,
`--lockstep`, `--pctrace`, `--statetrace`, `--trace-cycles`, `--trace-crash`,
`--iodump` and `--link-init-trace`. Battery, benchmark, oracle and snapshot
comparison runs use normal hardware settings and reject these overrides.

## Accuracy workflow

1. Reproduce the behavior in a focused diagnostic or reference ROM.
2. Confirm BIOS identity, boot mode, stop condition, and trace normalization.
3. Compare hardware documentation and at least one independent implementation
   when the behavior is not directly hardware-tested.
4. Change the smallest hardware model that explains the evidence.
5. Run Tier A and the affected evidence stages. Run Tier C for SIO or link
   changes.

Reference-suite totals are measurements, not substitute specifications. Record
itemized failures and preserve the ROM, BIOS profile, and command needed to
reproduce them.

## Documentation

📚 [Machine emulation manual](../../docs/emulation/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
