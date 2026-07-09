# Verdict Index

Master cross-reference of every verdict-bearing technique from the GBA/AGB SOTA
survey — one row per technique, with its verdict, effort, provenance (which
gatherer digest surfaced it and which review ruled), and the survey shortlist row
(or "folded" for the edge refinements the survey folded into its topic sections).

When this index and the survey disagree, **the survey is authoritative on
priority; this wiki is authoritative on coverage.** The full reasoning and
implementation seam for every row lives on its topic page, grouped by subsystem
rather than by source review.

## Legend

**Verdict vocabulary** (the survey's, reused unchanged):

| Term | Meaning |
|---|---|
| adopt now | Build immediately; determinism-clean, no blocking dependency. |
| test-first | Bring the proving ROM in as evidence and confirm we diverge before touching engine code. |
| adopt later | Well-scoped, but schedules behind the front-loaded wins. |
| skip | Ruled out — breaks the determinism contract, or a poor fit / no-audience by design. |
| implemented (this arc) | Shipped by the evidence-first accuracy wave (commit range up to `bb58166`). |
| already at SOTA | The core already does this correctly at accurate-core tier; nothing to build. |
| surveyed, not deep-reviewed | Touched by a gatherer sweep only; no review ruled a seam. |

**Effort scale:** S ≤ a day · M a few days · L a week+ · XL multi-week /
structural.

**Provenance:** `digest-N` = the gatherer that surfaced it; `review-a` = CPU +
DMA/timer/IRQ/open-bus, `review-b` = PPU + APU, `review-c` = performance +
architecture + cartridge + determinism.

## Implemented this arc

The evidence-first accuracy wave shipped six survey rows (and folded refinements):

| Technique | Survey row | Provenance | Note |
|---|---|---|---|
| Direct Sound FIFO 7-ring + 1-playing model + overflow-reset + narrow-write streaming | #4 | digest-3 / review-b A1–A3 | `DirectSoundFifo`; self-checking `--oracle` gate |
| Flat whole-machine savestate (~551 KB, mid-frame round-trip, `agb.snap`/`agb.restore`) | #5 | digest-8 / review-c A1 | `AgbMachineSnapshot`; `--state-roundtrip` |
| Per-game override database (header-game-code keyed) | #3 | digest-7 / review-c A6 | `AgbGameOverrides` |
| BIOS pre-flight SHA-1 classification + parity gate | #2 (gate half) | digest-8 / review-c A5 | `AgbBiosProfile`; refuses non-retail BIOS |
| `nba-emu/hw-test` corpus as an `--oracle` cycle-probe battery | #1 | digest-4 / review-a A16 | `OracleProbes.cs`; our-harness measurements |
| Per-channel DMA read latch — **premise was stale** | #7 | digest-4 / review-a A4 | always `uint[4]`; `dma/latch-per-channel` gate proves it |

The per-tick hash-divergence *fine-diff localizer* (the other half of survey #2)
remains **adopt later** — the savestate it rides on now exists, but the localizer
itself is not built.

## Master table

### CPU pipeline, prefetch, waitstates — [page](cpu-pipeline-prefetch-waitstates.md)

| Technique | Verdict | Effort | Provenance | Survey row |
|---|---|---|---|---|
| Multiply carry-flag value (Booth final-iteration carry-out) | adopt later | S–M | digest-1 / review-a A2 | #13 |
| Region/alignment THUMB open bus (per-region LSW/MSW + WRAM OldHI/OldLO) | adopt later | M | digest-1 / review-a A9 | #19 |
| Fast-EWRAM waitstate register (`0x4000800`) | adopt later | M | digest-1 / review-a A8 | folded |
| Prefetch-disable timing anomaly (WAITCNT bit 14 = 0) | test-first | S–M | digest-1 / review-a A1 | folded |
| 8-halfword prefetch FIFO with idle-only fill | already at SOTA | — | digest-0 / review-a §B | — |
| Per-mirror WAITCNT N/S tables + 128 KiB re-charge | already at SOTA | — | digest-0 / review-a §B | — |
| Multiply early-termination byte-scan (timing) | already at SOTA | — | digest-0 / review-a §B | — |
| ARM open bus (`[PC+8]`) | already at SOTA (coarse) | — | digest-1 / digest-0 | — |
| Look-ahead prefetch classification (interpreter pattern) | surveyed, not deep-reviewed | — | digest-1 §6 | — |

### PPU rendering — [page](ppu-rendering-models.md)

| Technique | Verdict | Effort | Provenance | Survey row |
|---|---|---|---|---|
| OBJ per-line cycle budget + sprite dropout (1210/954-cyc) | test-first → adopt | M | digest-2 / review-b P2 | #10 |
| Affine internal reference-point latch (VBlank reload + mid-frame write) | test-first | S–M | digest-2 / review-b P4 | #11 |
| Mosaic sampled from unmosaiced source (vertical + affine) | test-first | S–M | digest-2 / review-b P5 | folded |
| Window coordinate clamp (X2>240 / X1>X2) — **conflict, do not blind-flip** | test-first | S | digest-2 / review-b P6 | folded |
| Blend fine rules (semi-transparent OBJ / brightness suppression / no self-blend) | adopt later / test-first | S–M | digest-2 / review-b P7 | folded |
| VRAM/OAM access-conflict waitstates + OAM HBlank gate | adopt later | M | digest-2 / review-b P3 | folded |
| Per-dot (mid-scanline) PPU | adopt later / test-first | XL | digest-2 / review-b P1, review-c A15 | #21 |
| VRAM background-fetch open bus (`sbb_reg`) | defer (per-dot arc) | — | digest-4 / review-a A15 | folded |
| Dirty-flag scanline cache + palette-variant blend LUT | adopt later (perf) | M | digest-2 / review-b P9 | folded |
| HBlank-DMA scanline-granular raster effects | skip (already SOTA) | — | digest-2 / review-b P8 | folded |
| Per-scanline render with per-scanline register re-eval | already at SOTA | — | digest-0 / review-b §a | — |

### APU / Direct Sound — [page](apu-and-direct-sound.md)

| Technique | Verdict | Effort | Provenance | Survey row |
|---|---|---|---|---|
| FIFO 7-ring + 1-playing model + ≥4-empty DMA gate | implemented (this arc) | M | digest-3 / review-b A1 | #4 |
| FIFO overflow auto-reset to empty | implemented (this arc) | S | digest-3 / review-b A2 | folded |
| Narrow (8/16-bit) FIFO register writes | implemented (this arc) | S | digest-3 / review-b A3 | folded |
| FIFO-mode DMA honors configured destination | adopt later | S | digest-3 / review-b A4 | folded |
| Exact integer mix formula + absolute levels | adopt later / test-first | M | digest-3 / review-b A5 | #18 |
| SOUNDBIAS PWM resolution/rate modeling | adopt later (with A5) | S | digest-3 / review-b A8 | folded |
| Band-limited (BLEP / blip_buf) output resampling | adopt later | L | digest-3 / review-b A6 | folded |
| PSG carried forward from DMG/CGB (512 Hz sequencer) | already at SOTA | — | digest-3 / review-b §a | — |
| MP2K / "Sappy" HQ float mixer | skip | — | digest-3 / review-b A7 | skip |

### DMA, timers, interrupts, open bus — [page](dma-timers-interrupts-open-bus.md)

| Technique | Verdict | Effort | Provenance | Survey row |
|---|---|---|---|---|
| DMA-lingering CPU open bus ("Holy Grail") | test-first → adopt | M | digest-4 / review-a A3 | #6 |
| Per-channel DMA read latch — **premise stale** | implemented (this arc) | S | digest-4 / review-a A4 | #7 |
| I/O-register read masks (distinct from open bus) | test-first | M | digest-4 / review-a A10 | #8 |
| DMA 2-cycle start delay (`==20`) | test-first | S | digest-4 / review-a A6 | folded |
| DMA forces following CPU fetch non-sequential (`==88`) | test-first | S | digest-4 / review-a A7 | folded |
| DMA source/dest/count snapshotting at start | test-first / audit | S | digest-4 / review-a A5 | folded |
| Timer reload write-race + start/stop latency window | test-first | S | digest-4 / review-a A14 | folded |
| IRQ dispatch latency by handler region (92/112/120) | test-first | S–M | digest-4 / review-a A12 | folded |
| HALTCNT exit-timing variants | test-first | S–M | digest-4 / review-a A13 | folded |
| SRAM 8-bit-bus width mirroring | test-first / audit | S | digest-4 / review-a A11 | folded |
| `nba-emu/hw-test` corpus (evidence infrastructure) | implemented (this arc) | S–M | digest-4 / review-a A16 | #1 |
| ARMwrestler stage-0 smoke ROM | skip | — | digest-1 / review-a A17 | skip |
| Game Pak DRQ/DREQ-driven DMA | skip | — | digest-4 / review-a A18 | skip |
| Video-capture DMA3 / priority preemption / undrivable-source latch | already at SOTA | — | digest-0 / review-a §B | — |
| Two-stage IE/IF/IME synchronizer | already at SOTA | — | digest-0 / review-a §B | — |

### Cartridge, saves, RTC, peripherals — [page](cartridge-saves-rtc-peripherals.md)

| Technique | Verdict | Effort | Provenance | Survey row |
|---|---|---|---|---|
| Per-game override database | implemented (this arc) | S–M | digest-7 / review-c A6 | #3 |
| GPIO sensors as replayable commands (solar/tilt/gyro/rumble) | adopt later | M | digest-7 / review-c A8 | #17 |
| Undersized-ROM modulo mirror vs true open bus | test-first | S | digest-7 / review-c A12 | #12 |
| STOP-mode LCD/sound power-down | adopt later | S | digest-7 / review-c A13 | folded |
| Exotic peripherals (e-Reader/RFU/Play-Yan/link/BattleChip) | skip | — | digest-7 / review-c A16 | folded |
| EEPROM 512 B/8 KB protocol + DMA gate | already at SOTA | — | digest-0 / review-c §B | — |
| Flash one-impl-per-capacity-tier | already at SOTA | — | digest-0 / review-c §B | — |
| Deterministic S-3511A RTC from tick | already at SOTA | — | digest-0 / review-c B4 | — |

### Performance and architecture — [page](performance-techniques.md)

| Technique | Verdict | Effort | Provenance | Survey row |
|---|---|---|---|---|
| Software fastmem (region pointer table) | test-first (lean adopt) | M | digest-5 / review-c A10 | #9 |
| In-process work-stealing fleet pool | adopt later | M | digest-5 / review-c A9 | #14 |
| Cached (block-linking) interpreter | test-first | L–XL | digest-5 / review-c A7 | #20 |
| Per-game idle-loop DB (heuristic) | skip (heuristic); adopt later (fixed-const, if profiled) | S | digest-5 / review-c A11 | folded |
| Threaded PPU/APU · JIT/dynarec · cothread scheduler · GPU-side PPU | skip | — | digest-5/6 / review-c §C | skip |
| Function-pointer dispatch tables | already at SOTA | — | digest-0 / review-c B2 | — |
| Event-queue scheduler | already at SOTA | — | digest-0 / review-c B1 | — |
| `StepClocks` quiescent-span collapse | already at SOTA | — | digest-0 / review-c B3 | — |

### Determinism, savestate, replay — [page](determinism-savestate-replay.md)

| Technique | Verdict | Effort | Provenance | Survey row |
|---|---|---|---|---|
| Flat whole-machine savestate | implemented (this arc) | M–L | digest-8 / review-c A1 | #5 |
| BIOS/ROM pre-flight hash gate | implemented (this arc) | S | digest-8 / review-c A5 | #2 |
| Per-tick hash-divergence fine-diff localizer | adopt later | M | digest-8 / review-c A5 | #2 |
| Rewind (delta-against-base ring) | adopt later | L | digest-8 / review-c A2 | #15 |
| Runahead, two-instance | adopt later | M | digest-8 / review-c A3 | #16 |
| Rollback / lockstep cross-instance sync | test-first | L–XL | digest-8 / review-c A4 | #22 |
| No-float/no-RNG discipline · deterministic RTC · dual co-sim | already at SOTA | — | digest-0 / review-c B4–B6 | — |
| TAS/movie formats + console verification | surveyed, not deep-reviewed | — | digest-8 | — |

## Open questions (recorded, not resolved)

The `--oracle` battery's measured-vs-documented divergences are recorded on
[test-roms-and-evidence.md](test-roms-and-evidence.md#our---oracle-cycle-probe-battery-landed-this-arc)
as **probe-shape differences, not proven core bugs**: `dma/start-delay` (measured
133 vs documented 20), `dma/force-nseq` (115 vs 88), `haltcnt/exit direct` (0 vs
12), and the timer rows; `irq/dispatch` (~90) sits inside the documented 92/112/120
band. A future session should reconcile the probe ROM shape with the
`nba-emu/hw-test` source before treating any as a defect, and must not retune what
already matches ares (a shared hardware-truth frontier).

## Where to go next

For the ranked "what to build first" across everything in this index — the
effort-adjusted shortlist — see [../agb-sota-survey.md](../agb-sota-survey.md). For
the full reasoning, cost/benefit, determinism analysis, and implementation seam
behind each row, follow the row to its topic page.
