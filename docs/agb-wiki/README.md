# GBA / AGB Emulation Techniques Wiki

A comprehensive reference for Game Boy Advance (AGB / ARM7TDMI) emulation
techniques surveyed for `experimental/Puck.AdvancedGamingBrick` — Puck's
ARM7TDMI-native core, run under the same determinism contract as the rest of the
engine (no wall-clock, RNG, or float in emulated state; integer tick base
2^24/s, 1 tick = one 16.777216 MHz cycle, frame = 280,896 cycles) and validated
by dual live co-simulation against mGBA and ares.

This wiki is the **reference companion** to
[../agb-sota-survey.md](../agb-sota-survey.md). The survey is the *ranked
decision shortlist* — what to build next and why, judged effort-adjusted. This
wiki is the *complete catalog* — every technique the research touched gets an
entry with a source link, a determinism note, and (where a deep review ruled) a
Puck verdict. When the two disagree on a ranking, **the survey is authoritative
on priority; the wiki is authoritative on coverage.**

## Method

- **Date:** 2026-07-08.
- **Corpus:** one implementation sweep (`digest-0`, the ground-truth read of what
  our core ships) + eight parallel web gatherers, each sweeping one axis with
  citation URLs (`digest-1` CPU pipeline/prefetch/waitstates; `digest-2` PPU;
  `digest-3` APU/FIFO; `digest-4` DMA/timers/IRQ/open-bus; `digest-5`
  performance; `digest-6` emulator-architecture landscape; `digest-7`
  cartridge/save/RTC/peripherals; `digest-8` determinism/savestate/rewind) +
  three Opus cross-reviews that judged every gathered technique against the
  implementation (`review-a` CPU + DMA/timer/IRQ/open-bus; `review-b` PPU + APU;
  `review-c` performance + architecture + cartridge + determinism) and produced
  per-partition "already at SOTA" and "dubious claim" lists + this synthesis.
  The digest and review documents live in the research corpus, not in this repo;
  provenance identifiers of the form `digest-0`…`digest-8` and
  `review-a`/`review-b`/`review-c` in the entries and in
  [verdict-index.md](verdict-index.md) refer to them.
- **Verdicts** reuse the survey's vocabulary: **adopt now** · **test-first**
  (bring the proving ROM in as evidence before touching engine code) · **adopt
  later** · **skip**, each with an effort rating (**S** ≤ a day · **M** a few
  days · **L** a week+ · **XL** multi-week / structural). Entries only touched by
  a gatherer sweep and never ruled on by a review are marked **surveyed, not
  deep-reviewed** (the [sdf-wiki](../sdf-wiki/README.md) convention). Entries the
  evidence-first accuracy wave shipped are marked **implemented (this arc)**.
- **Status is as-of-now, not as-of-the-digests.** The digests describe the core
  *before* this session's accuracy wave landed. As of commit `bb58166` the core
  additionally has: the hardware-measured Direct Sound FIFO (7-word ring + a
  32-bit playing buffer, ≥4-empty-word DMA gate, overrun auto-reset, byte-order
  narrow-write streaming); whole-machine savestate (a ~551 KB flat image, a
  `--state-roundtrip` diagnostic covering frame-boundary *and* mid-frame,
  `agb.snap`/`agb.restore` demo verbs); a BIOS pre-flight SHA-1 classification
  (co-sim diagnostics refuse a non-retail BIOS without `--allow-replacement-bios`);
  a per-game override database (`AgbGameOverrides`, keyed on the header game
  code); an `--oracle` cycle-probe battery (`OracleProbes.cs`); and demo-side
  `agb.*` debug verbs with banked SPSR and side-effect-free opcode reads. Every
  entry's **Puck status** reflects this post-wave reality; where the digests'
  premise was already stale at gather time, the entry says so (the DMA read latch
  was **always** per-channel — the survey's row #7 "single shared latch" premise
  was wrong; the `--oracle` `dma/latch-per-channel` gate now proves the
  isolation).

## Pages

### The subsystems
- [cpu-pipeline-prefetch-waitstates.md](cpu-pipeline-prefetch-waitstates.md) —
  the ARM7TDMI pipeline, the 8-halfword Game Pak prefetch FIFO, S/N/I cycle
  attribution, per-mirror WAITCNT waitstates, multiply timing + the Booth
  carry-flag frontier, ARM/THUMB open bus, the fast-EWRAM register.
- [ppu-rendering-models.md](ppu-rendering-models.md) — scanline-batch vs per-dot
  rendering, the OBJ per-line cycle budget + sprite dropout, the affine
  reference-point latch, mosaic sampling, window coordinate clamp, blend fine
  rules, HBlank-DMA raster effects, and the dirty-flag/palette-LUT perf tier.
- [apu-and-direct-sound.md](apu-and-direct-sound.md) — the Direct Sound FIFO ring
  model, overflow auto-reset, narrow FIFO writes, FIFO-mode DMA, the integer mix
  formula + SOUNDBIAS, the PSG carry-forward, band-limited output resampling, and
  the MP2K HQ mixer we reject.
- [dma-timers-interrupts-open-bus.md](dma-timers-interrupts-open-bus.md) — DMA
  start delay / forced-nseq / per-channel read latch / the "Holy Grail"
  DMA-lingering open bus, timer reload/start-stop races, IRQ dispatch latency,
  HALTCNT exit variants, the I/O-register read-mask grind, and SRAM's 8-bit bus.
- [cartridge-saves-rtc-peripherals.md](cartridge-saves-rtc-peripherals.md) —
  save-type detection + the per-game override DB, EEPROM/Flash protocols, the
  S-3511A RTC, GPIO sensors as replayable commands, the Classic NES
  anti-emulation family, undersized-ROM mirroring, and the exotic-peripheral tail.

### Cross-cutting
- [performance-techniques.md](performance-techniques.md) — function-pointer
  dispatch, the cached-interpreter question, scheduler design, deterministic
  idle-skipping, software fastmem, the in-process fleet pool, and the
  threaded-render / JIT paths we deliberately keep off.
- [determinism-savestate-replay.md](determinism-savestate-replay.md) — the
  deterministic-core contract, savestate architectures, rewind, runahead,
  rollback/lockstep, RTC virtualization, hash-based divergence detection, and TAS
  movie formats.
- [emulator-landscape.md](emulator-landscape.md) — mGBA, NanoBoyAdvance, ares,
  SkyEmu, Hades, VBA-M, DSHBA and the long tail; endrift's accuracy taxonomy; the
  reference-doc chain; and the standing "GBATEK can't be trusted uncritically"
  argument.

### Evidence and decisions
- [test-roms-and-evidence.md](test-roms-and-evidence.md) — the
  test-ROM→finding map (the corpus's most directly adoptable artifact), the AGS
  aging cartridge, the mGBA suite categories + scores (with the
  non-comparable-across-versions warning), and our own `--oracle` probe battery
  with its measured-vs-documented divergence rows recorded as open questions.
- [verdict-index.md](verdict-index.md) — every verdict-bearing entry in one
  table: verdict, effort, provenance (digest-N / review-a|b|c), and survey row.

## Standing constraints referenced throughout

- **Determinism is a feature.** No wall-clock, RNG, or float in emulated state;
  input (controller *and* RTC/sensor reads) becomes per-tick `CommandSnapshot`s;
  the RTC derives time from `cycles / 16_780_000` off a fixed epoch, never
  `DateTime.Now`. This is what makes the whole-machine savestate complete by
  construction and rollback tractable.
- **Reference suites are evidence, never CI gates.** hw-test, jsmolka, FuzzARM,
  the mGBA suite and the AGS cartridge run as Post Tier-B batteries that skip
  cleanly when their assets are absent; they inform, they do not gate a merge.
- **Two live oracles beat trusting the doc.** Independently-written emulators
  inherit *identical* GBATEK-sourced bugs (the WAITCNT bit-15 episode), so the
  core is anchored on live mGBA + ares co-simulation (`--lockstep`,
  `statediff.py`), not on GBATEK prose. Where our timer/IRQ boundary already
  matches ares, a residual gap vs a documentation number is a shared
  hardware-truth frontier, not our bug — do not retune it.
- **Code identifiers carry no external proper nouns.** External emulator and
  product names are fine in these docs (they are citations); vendor IDs and
  console codes are kept as technical facts. But table values, enums, and
  identifiers in `experimental/Puck.AdvancedGamingBrick` must not embed them.
- **The ARM7TDMI-native core is a separate thing from the "Agb costume."** This
  wiki covers only the native GBA core. The GB/GBC-native backlog and the "Agb
  costume of the SM83 core" story (KEY0 latch, AGB OBJ quirk, palette/color
  correction, post-boot DIV seed) live in
  [../ideal-gaming-brick-plan.md](../ideal-gaming-brick-plan.md), not here.
