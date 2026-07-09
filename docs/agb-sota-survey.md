# GBA / AGB Emulation SOTA Survey — the ranked decision shortlist

*Written 2026-07-08. This document is the ranked decision shortlist from a
multi-agent online survey of state-of-the-art Game Boy Advance emulation:
**which techniques not yet in `experimental/Puck.AdvancedGamingBrick` would
improve its accuracy, compatibility, or performance**, judged against what the
core actually ships, its determinism contract (no wall-clock/RNG/float in
emulated state; integer tick base 2^24/s, 1 tick = one 16.777216 MHz cycle,
frame = 280,896 cycles), and its dual co-simulation (mGBA + ares) discipline.
It is a delta, not a tutorial — it assumes the reader knows the core from the
`gaming-bricks` skill and the `…AdvancedGamingBrick.Post` README.*

*Method. One implementation sweep produced the ground-truth digest of what our
ARM7TDMI-native core does today (`Arm7Tdmi`, `AgbBus`, `AgbPpu`, `AgbApu`,
`AgbDmaController`, `AgbTimerController`, `AgbInterruptController`,
`AgbCartridge`, `AgbScheduler`, and their Post battery). Eight parallel web
gatherers each swept one axis (CPU pipeline/prefetch/waitstates; PPU;
APU/FIFO; DMA/timers/IRQ/open-bus; performance; the emulator-architecture
landscape; cartridge/save/RTC; determinism/savestate/rewind), carrying
citation URLs. Three Opus cross-reviews then judged every gathered technique
against the implementation — have-it? / determinism fit / adoption cost /
verdict — and produced per-partition "already at SOTA" and "dubious claim"
lists. This synthesis re-ranks the three partitions into one global shortlist,
carries the reviewers' verdicts unchanged, and flags where two reviews or two
digests conflicted. Citations are carried from the gatherers, fetch-verified
where the venue allowed.*

*Reference companion: [docs/agb-wiki/](agb-wiki/README.md) is the complete
catalog — every technique this survey ranks (and the folded edge refinements)
gets an entry with its source link, determinism note, and Puck status. This
survey is authoritative on priority; the wiki is authoritative on coverage.*

*Settled facts this doc does not relitigate: our ARM7TDMI is bit-exact vs the
ares core per co-sim (residual divergence is cycle-attribution only); AGS aging
cartridge 38/38 with a real BIOS; Pokémon Emerald boots into the intro;
reference suites are evidence, never CI gates. The GB/GBC-native backlog and the
"Agb costume of the SM83 core" story live in
[docs/ideal-gaming-brick-plan.md](ideal-gaming-brick-plan.md) — this doc covers
the separate ARM7TDMI-native core only.*

---

## 1. The shortlist

Ranked by effort-adjusted value, front-loading the cheap, determinism-clean
wins the reviews most strongly promoted. Verdict vocabulary is fixed: **adopt
now** · **test-first** (bring the proving ROM in as evidence before touching
engine code) · **adopt later** · **skip**. Cost: S (≤ a day) · M (a few days) ·
L (a week+) · XL (multi-week / structural). The parenthetical after each
technique is the source review (A = CPU/DMA, B = PPU/APU, C = perf/arch/cart/det).

| # | Technique (review) | What it buys | Have it? | Determinism | Cost | Verdict |
|---|---|---|---|---|---|---|
| 1 | `nba-emu/hw-test` evidence corpus (A16) | itemized cycle oracles (`dma/start-delay`=20, `force-nseq`=88, `latch`, `irq-delay`=92/112/120, `haltcnt`, `timer/*`) that turn a dozen "unknown" rows into pass/fail | no | evidence only | S–M | **adopt now** |
| 2 | BIOS/ROM pre-flight hash gate (C-A5) | refuses a parity run on the replacement BIOS; prevents the documented "phantom cycle drift" session | partial | native | S | **adopt now** |
| 3 | Per-game override database (C-A6) | fixes Top Gun (3 conflicting save strings), Classic NES bait-and-switch, correct RTC/peripheral keying without content sniffing | no | perfect | S–M | **adopt now** |
| 4 | FIFO 7-ring + 1-playing model + overflow-reset (B-A1/A2) | kills FIFO-underrun crackle: Mother 3, Sonic Advance 2, Simpsons Road Rage, Super Mario Advance | partial | native | M | **adopt now** |
| 5 | Flat whole-machine savestate (C-A1) | the keystone primitive: rewind, runahead, rollback, and reinstated snapshot Post stages all reduce to it | no | ideal | M–L | **adopt now** |
| 6 | DMA-lingering CPU open bus ("Holy Grail") (A3) | tight-loop hangs without it: Emerald, Sonic Pinball Party, Hello Kitty Collection | partial | clean | M | **test-first** |
| 7 | Per-channel DMA read latch (A4) | cross-channel latch leak (Phantasy Star interlace); cheap once #1 lands | partial | clean | S | **test-first** |
| 8 | I/O-register read masks (A10) | biggest raw test-score gap — mGBA `I/O` 81/130 | partial | clean | M | **test-first** |
| 9 | Software fastmem (region pointer table) (C-A10) | branchless RAM/ROM fast path; best pure-perf lever for single-instance and fleet | partial | deterministic | M | **test-first** |
| 10 | OBJ per-line cycle budget + dropout (B-P2) | overcommitted-OAM scenes drop trailing sprites like hardware (1210/954-cyc budget) | no | clean | M | **test-first** |
| 11 | Affine internal reference-point latch (B-P4) | correct Mode-7-style per-line matrix / affine raster effects | partial | clean | S–M | **test-first** |
| 12 | Undersized-ROM modulo mirror vs true open bus (C-A12) | Classic NES read-past-ROM controls; distinct from open-bus path | partial | neutral | S | **test-first** |
| 13 | Multiply carry-flag value (Booth final iteration) (A2) | mGBA `MulLong` 52/72; a named open frontier | no | clean | M | **adopt later** |
| 14 | In-process work-stealing fleet pool (C-A9) | ~1.5× from utilization vs process/thread-per-instance; the fleet arc's home | partial | native | M | **adopt later** |
| 15 | Rewind (delta-against-base ring) (C-A2) | long rewind window per MB; "rewind to divergence" for the fleet harness | no | ok | L | **adopt later** |
| 16 | Runahead, two-instance (C-A3) | lower input latency; two-instance is also *faster* (skips save/load thrash) | no | ok | M | **adopt later** |
| 17 | GPIO sensors as replayable commands (C-A8) | rumble/gyro/tilt/solar (WarioWare, Boktai, Yoshi) on the built RTC seam | partial | perfect | M | **adopt later** |
| 18 | Exact integer mix formula + levels (B-A5) | closes our known "levels unverified" gap against an mGBA integer reference | partial | clean | M | **adopt later** |
| 19 | Region/alignment THUMB open bus (A9) | mGBA `I/O`/`Memory` corners; MMBN4 Blue Moon canary | partial | clean | M | **adopt later** |
| 20 | Cached (block-linking) interpreter (C-A7) | 10–50% *vs a naïve re-decoder* — unquantified over our table dispatch | partial | ok | L–XL | **test-first** |
| 21 | Per-dot (mid-scanline) PPU (B-P1 / C-A15) | Iridion 3D, SW Ep II sub-line effects; highest accuracy payoff, highest cost | no | neutral | XL | **adopt later** |
| 22 | Rollback / lockstep cross-instance sync (C-A4) | netplay-class link + fleet replay validation; reduces to save/load/step | no | native | L–XL | **test-first** |

Roughly two dozen further edge refinements (window-bound clamp, mosaic
vertical/affine boundary, blend fine-rules, narrow FIFO writes, DMA
start-delay/force-nseq/snapshot audits, HALTCNT variants, timer reload race,
region IRQ latency, SRAM 8-bit bus, STOP power-down, band-limited resampling,
fast-EWRAM waitstate) are folded into the topic sections rather than bloating
the table — most are S-cost `test-first` audits that ride the `hw-test` corpus
(#1) and are probably already right by ares-parity.

> **Implementation update — the evidence-first accuracy wave (landed).** Rows
> #1, #2, #3, #4, and #7 were adopted:
> - **#1** — a self-authored `--oracle` probe battery (`OracleProbes.cs`) reports
>   measured-vs-documented per probe; two rows are self-checking gates (the FIFO
>   model and per-channel DMA latch), the DMA/timer/IRQ/halt cycle rows are honest
>   our-harness measurements (divergences recorded, not chased — the IRQ dispatch
>   probe lands ~90, inside the documented 92/112/120 band).
> - **#2** — the co-sim diagnostics (`--lockstep` / `--statetrace` / `--trace-cycles`)
>   now refuse a non-retail BIOS (identified by SHA-1) unless `--allow-replacement-bios`
>   is passed; the classification is surfaced through the machine to `agb.status`.
> - **#3** — a per-game override DB (`AgbGameOverrides.cs`) keyed by the 4-char game
>   code overrides save/RTC detection before the string scan (Top Gun no-backup,
>   the Classic NES 'F'-family EEPROM force, the known RTC titles).
> - **#4** — the two `Queue<sbyte>` FIFOs are replaced by the 7-word ring + 32-bit
>   playing-buffer model with overrun auto-reset and byte-accurate narrow writes.
> - **#7 correction** — the "single shared latch" premise was **stale**: the DMA read
>   latch was already a per-channel `uint[4]`. The `dma/latch-per-channel` oracle gate
>   proves the isolation. No shared-latch bug existed.

### Prerequisite structure

The shortlist is not independent line items. Three chains dominate:

- **`hw-test` (#1) unlocks the test-first tier.** Rows 6, 7, 8, 10, 11, 12 and
  most folded audits all become pass/fail evidence the moment the corpus is a
  Tier-B battery. Build it first or nothing else in the middle of the list is
  actionable. This is the single highest leverage-per-effort move in the survey.
- **Savestate (#5) is the keystone for the whole determinism/fleet family.**
  Rewind (15), runahead (16), rollback (22), the fine-grained hash-diff
  localizer (part of #2), and reinstated snapshot Post stages all reduce to a
  correct serialize/deserialize round-trip. The one care item is the scheduler
  event queue: `Callback` references must serialize as stable enum/ID tags, not
  raw delegate addresses.
- **The per-game override DB (#3) is the home for later keying.** GPIO sensors
  (17), any curated idle-loop skip, and save-type/RTC forcing all key off the
  same source-generated `FrozenDictionary<uint, SaveProfile>` on the 4-char game
  code. Build the table once.

---

## 2. What we already do at SOTA level

The reviews were unanimous that the core's *architecture* already sits at the
accurate-core tier; the open items are edge fidelity, one evidence corpus, and
two known-hard reverse-engineering details. Honest credit:

- **Dual live co-simulation (mGBA + ares).** A shared trace format, `--lockstep`,
  `statediff.py`. This is elite and is exactly the field's recommended posture —
  independently-written emulators inherit *identical* GBATEK-sourced bugs (the
  WAITCNT bit-15 episode), so anchoring on two live oracles beats trusting the
  doc. AGS 38/38 with a real BIOS is the same public, checkable credibility
  anchor NanoBoyAdvance and SkyEmu built their reputations on.
- **ares-architecture per-cycle model with a fleet-friendly event queue.** The
  `StepClocks` quiescent-span collapse (jump straight to the next scheduled
  event when nothing can change IRQ/timer/pipeline state, "provably identical" to
  per-cycle stepping) gives cycle-accurate semantics *without* ares's costly
  cothread-per-component overhead — a genuinely strong architectural position at
  fleet scale. `AgbScheduler` is a structural twin of mGBA's `mTiming`.
- **Function-pointer dispatch tables** (ARM 4096 / Thumb 256 `delegate*` static
  methods, zero virtual dispatch, zero delegate-invoke) — the SOTA interpreter
  trick, already banking the decode-branch elimination a cached interpreter
  targets.
- **8-halfword prefetch FIFO with idle-only fill** and branch-invalidation
  reset; a ROM-bus miss advances the clock without filling (the earlier over-fill
  bug that caused false speed-ups was found and fixed). The mGBA-blog-hard
  feature, done properly.
- **Per-mirror WAITCNT N/S waitstate tables** with sequential re-charge at the
  128 KiB ROM page boundary; **multiply early-termination byte-scan** with the
  documented signed-vs-unsigned asymmetry (+92 on mGBA `Timing`).
- **Timer prescaler as a real per-cycle state machine** (not a lazy
  `(now-last)>>prescale`), synchronous same-cycle cascade ripple, latched
  control/reload committed one cycle later so the start-up delay is emergent.
- **Two-stage IE/IF/IME synchronizer** producing register-visibility delay and
  IRQ-recognition latency as emergent pipeline behavior; **DMA priority
  preemption 0→3 per-word**; **video-capture DMA3** active on HBlank of scanlines
  2–161 exactly.
- **Deterministic RTC from tick** (`cycles/16_780_000` off a fixed epoch, full
  S-3511A edge-driven serial protocol over GPIO) — the exact posture the field
  converged on (virtualize the clock as a recorded input, never `DateTime.Now`).
- **EEPROM DMA-gate anti-hijack** (`m_dmaActive` at 0x0D) so a cart merely
  embedding `EEPROM_V` — the AGS cartridge — can't be hijacked. **Flash
  one-impl-per-capacity-tier** (64K + 128K, correct vendor IDs, skip Atmel like
  everyone else).
- **Integer-only APU, no float / no wall-clock**, 4-word FIFO DMA burst off the
  timer-overflow event stream, 512 Hz frame sequencer — a *stronger* determinism
  posture than typical emulators, and the correct catch-up-off-the-event-stream
  pattern.
- **Per-scanline rendering with per-scanline register re-evaluation.** HBlank DMA
  + per-line `RenderScanline` captures the overwhelming majority of raster
  effects (the mGBA-class model); only true sub-line writes are uncovered.

Bottom line: we are on the NanoBoyAdvance/ares side of the accuracy line, and
AGS 38/38 backs it. The gaps below are frontier polish, not missing subsystems.

---

## 3. CPU pipeline, prefetch, waitstates

We already ship the hard parts (prefetch FIFO, per-mirror waitstates, multiply
early-termination). The open items are two known-hard details and a cluster of
cheap audits.

**Multiply carry-flag value (shortlist #13).** ARM7TDMI sets C after
MUL/MLA/UMULL deterministically from the barrel shifter's carry-out on the final
Booth iteration — a function of how many early-termination iterations ran, even
though the ARM TRM calls it "meaningless." We have the *timing* right (+92 on
`Timing`) but the carry-flag *value* wrong on Booth-carry cases; this is exactly
the `MulLong` 52/72 gap and a named frontier in the Post README. Pure integer
bit derivation, determinism-clean, bounded ~+20 win — but it needs careful
recoding against a known-accurate core, so it is `adopt later`, not rushed;
co-sim vs ares confirms bit-exactness. Cite:
https://bmchtech.github.io/post/multiply/ ·
http://problemkaputt.de/gbatek-arm-opcodes-multiply-and-multiply-accumulate-mul-mla.htm

**Region/alignment THUMB open bus (shortlist #19).** In THUMB, invalid-address
reads echo two 16-bit halves whose source offsets depend on the region (Main
RAM/Palette/VRAM/ROM → LSW=MSW=[$+4]; BIOS/OAM → [$+4]/[$+6] or [$+2]/[$+4];
32K WRAM → [$+4]/OldHI or OldLO/[$+4], literally one-instruction memory). Our
open bus is the coarse `m_openBus = last ReadRegion result` plus I/O-register
`OpenBusHalf` shifting — it does not compose the per-region halves or the WRAM
one-instruction latch. Historical canary: Mega Man Battle Network 4 Blue Moon.
`adopt later`, incremental against jsmolka `memory`/`io-read` + mGBA `I/O`.
Cite: https://problemkaputt.de/gbatek.htm (Unpredictable Things) ·
https://www.ngemu.com/threads/gba-open-bus.170809/

**Prefetch-disable timing anomaly.** With prefetch off (WAITCNT bit 14 = 0), a
ROM op that takes only internal cycles (MUL, register-shift) gets billed a
non-sequential fetch instead of sequential — a genuine silicon anomaly. Likely
not special-cased. Because commercial games run with prefetch *on*, this is low
compatibility-value but real test-score value; `test-first` against jsmolka
waitstate-with-prefetch-off ROMs. Cite:
https://problemkaputt.de/gbatek-gba-gamepak-prefetch.htm

**Cheap `test-first` audits (fold, ride #1).** IRQ dispatch latency by handler
region (`irq/irq-delay` = 92 IWRAM / 112 EWRAM / 120 ROM, needs a real BIOS);
HALTCNT exit-timing variants (`haltcnt` = 12/4155/4154/125/249, proving wake on
IE AND IF even if asserted pre-HALTCNT); timer reload write-race + start/stop
window (`timer/reload`, `timer/start-stop`). Our latch discipline and
two-stage synchronizer are the right architecture; the known ~1–2-cycle
overflow→recognition shortfall **matches ares exactly**, so it is a shared
hardware-truth frontier, not our bug — do not retune anything that already
matches ares. ARMwrestler is `skip` (redundant with FuzzARM + jsmolka + our
41-vector smoke). Cite: https://github.com/nba-emu/hw-test ·
https://problemkaputt.de/gbatek-gba-timers.htm

---

## 4. PPU model

Our per-scanline batch renderer with per-line register re-evaluation is
mGBA-class for the vast majority of raster effects. The gaps are OBJ cycle
budget, affine reference-register latching, and — at the far end — true per-dot
rendering.

**OBJ per-line cycle budget + dropout (shortlist #10).** We render all 128 OAM
entries with no cycle accounting, so overcommitted scenes render *more* sprites
than hardware. Model the per-line budget (1210 cyc with H-Blank-Interval-Free =
0, 954 with = 1; normal OBJ 1 cyc/px, affine 10 + 2 cyc/px) and stop rendering
sprites for the line when exhausted — the mGBA `spriteCyclesRemaining` behavior.
Cheap, self-contained, and a per-line quantity that works fine while staying
scanline-batched. `test-first → adopt` on a sprite-overcommit ROM + render-hash.
Cite: https://problemkaputt.de/gbatek.htm ·
https://deepwiki.com/mgba-emu/mgba/4.1-gba-video-rendering

**Affine internal reference-point latch (shortlist #11).** BG2X/Y/BG3X/Y copy
into internal working registers at VBlank, increment by dmx/dmy each scanline; a
write *outside* VBlank immediately overwrites the internal register. digest-0
says nothing about internal-vs-external latching, so the batch model risks
snapshotting BGxX/Y at the wrong boundary. `test-first` against the Tonc affbg
demo before assuming a bug — this is the exact class VBA-M had to fix. Cite:
https://www.coranac.com/tonc/text/affbg.htm ·
https://github.com/visualboyadvance-m/visualboyadvance-m/commit/b96da415c1c482a0ce713d527afda1a53648b008

**Per-dot (mid-scanline) PPU (shortlist #21).** Render per-dot (4 cyc/dot, 308
dots/line) so register writes land on the correct pixel. This is the stated
accuracy frontier (Post README §7.E), an XL structural rewrite (the NanoBoyAdvance
PR #258 / SkyEmu path that "removed all per-scanline rendering code"). **Do not
start with the rewrite.** First adopt the candidate proving ROMs (Iridion 3D, SW
Episode II, Tonc demos) as render-hash evidence and quantify how many games we
care about actually break at scanline granularity — the digest itself says
per-scanline-with-re-eval captures "the overwhelming majority." Reserve the XL
rewrite for a proven need. Cite:
https://github.com/nba-emu/NanoBoyAdvance/pull/258 ·
https://github.com/skylersaleh/SkyEmu/blob/main/docs/Accuracy.md

**Folded PPU edge cases (ride #1, mostly S).** VRAM/OAM access-conflict
waitstates + DISPCNT-bit-5 OAM-HBlank gate — widen the existing PRAM contention
mechanism (`ChargeData` / `PramContention`) to VRAM/OAM (`adopt later`, we
already have the PRAM shape). Mosaic vertical/affine boundary — validate against
Tonc `mos_demo.gba`, our sample-coordinate snapping is the right technique
in principle (`test-first`). Blend fine-rules (semi-transparent OBJ always 1st
target + alpha; brightness suppression; OBJ-no-self-blend) — refinements to
`Composite`, low structural risk since our top-two selection already matches the
sprite→BG0→…→backdrop tie-break (`adopt later`). The ares BG-VRAM tile-fetch
open-bus (`sbb_reg`, ares#1113), handed over from the DMA partition, lands here
and collides with the scanline-batch limitation — defer with the per-dot arc.
Cite: https://github.com/mgba-emu/mgba/issues/1008 ·
https://raddad772.github.io/2025/01/02/notes-on-GBA-PPU-windows-and-blending.html

**Conflict — window X1>X2.** GBATEK says garbage `WINxH` (X2>240 or X1>X2) is
interpreted as X2=240 (clamp to full extent); our `InWindow` implements
*wraparound* (left>right / top>bottom). These are genuinely different for
inverted bounds, and hardware behavior here is *contested across emulators*. Do
**not** blind-flip to the GBATEK clamp — write a window edge-case probe and match
hardware / ares-mGBA co-sim first. Cite:
https://rust-console.github.io/gbatek-gbaonly/

---

## 5. APU / FIFO audio

Structure is correct and integer-clean; the gaps are the FIFO trigger model and
unverified absolute levels.

**FIFO 7-ring + 1-playing model + overflow-reset (shortlist #4).** Our two
32-byte `Queue<sbyte>` FIFOs refill "once drained to half" — the naïve GBATEK
"half-empty triggers refill" simplification the research explicitly calls wrong.
Hardware measurement (mGBA #1847) is a **7-word ring + a separate 32-bit playing
buffer**: on each timer overflow, (1) if the ring has ≥4 empty words request the
FIFO DMA (moves exactly 4 words), and (2) *separately*, if the playing buffer is
empty and the ring has ≥1 word, move one word ring→playing — invariant: two DMA
requests can never occur without an intervening overflow. On overrun, hardware
auto-resets the FIFO to empty (drops buffered samples, doesn't wrap). This fixes
named FIFO-underrun crackle (Mother 3, Sonic Advance 2, Simpsons Road Rage,
Super Mario Advance) and is the highest-value APU item. We already move 4-word
bursts off the deterministic timer stream — only the trigger cadence and the
two-stage split are missing. `test-first → adopt now` against the #1847 timing
probe ROM (measures ring size, the double-DMA-impossible invariant,
overflow-resets-to-empty). Cite: https://github.com/mgba-emu/mgba/issues/1847 ·
http://problemkaputt.de/gbatek-gba-sound-channel-a-and-b-dma-sound.htm

**Exact integer mix formula + levels (shortlist #18).** Closes our *known* gap
(structure correct, absolute levels unverified). Match one integer reference
(mGBA `audio.c`): PSG `>> psgShift` (25/50/100%), DirectSound
`(chX << 2) >> !volumeChX`, final `(sample * masterVolume * 3) >> 4`, sum +
SOUNDBIAS + clamp to `0..0x3FF`; per-channel swing PSG ≤ ±0x80, DirectSound ≤
±0x200. All integer, determinism-clean. Making SOUNDBIAS audible changes output,
so gate it behind an audio-hash so render-hash determinism is preserved.
`adopt later / test-first`. Cite:
https://github.com/mgba-emu/mgba/blob/master/src/gba/audio.c ·
https://problemkaputt.de/gbatek-gba-sound-control-registers.htm

**Folded APU items.** Narrow (8/16-bit) FIFO-register writes (replace only the
low byte of the next word — `adopt later`, low incidence for MP2K's whole-word
DMA fills); FIFO-mode DMA honoring the configured (non-FIFO) destination instead
of a hardwired FIFO_A/B (`adopt later`, trivially correct — use the channel's
DAD); SOUNDBIAS PWM resolution/rate modeling (`adopt later`, subsumed by the mix
formula for audible purposes); band-limited (blip_buf / BLEP) output resampling
(`adopt later`, pure presentation polish — safe even as float because it lives
strictly downstream of `GenerateSample` on the host-audio side, never feeding
back into traced state or the verification hash).

**Skip — MP2K/"Sappy" HQ float mixer.** Detect the game's MP2K routine and
substitute a 64 kHz cubic-interpolation float re-implementation. This **violates
the contract** (float in the audio path + HLE that detects game code) and is an
enhancement, not accuracy. Our per-sample synthesis is already SOTA-granularity.
`skip`. Cite: https://github.com/nba-emu/NanoBoyAdvance

**Conflict — FIFO capacity.** GBATEK says 8 words / half-empty refill; hardware
measurement (mGBA #1847) says 7 ring + 1 playing. Trust the hardware measurement
for shortlist #4; treat GBATEK's "8 words" as the known simplification.

---

## 6. DMA, timers, IRQ, open bus

Our DMA/timer/IRQ *shape* is accurate-core tier (priority preemption,
video-capture windows, undrivable-source open bus, latched timers, two-stage
synchronizer). The open items are the CPU-side lingering value and per-channel
latch, plus the I/O read-mask grind.

**DMA-lingering CPU open bus — the "Holy Grail" (shortlist #6).** A
just-completed DMA's last bus value lingers on the external bus for exactly one
instruction; if the next CPU fetch is an invalid-memory read, it observes the DMA
value, not the normal prefetch open-bus value. Games hang in tight loops without
it — Pokémon Emerald, Sonic Pinball Party, Hello Kitty Collection. We keep a
DMA-side `m_dataLatch` and a *separate* CPU `m_openBus`, but nothing bridges "last
DMA bus value → next single CPU fetch." mGBA's fix compares expected-fetch-PC vs
actual and substitutes the DMA value when they match. Strongest correctness
candidate in the CPU/DMA partition. `test-first → adopt` — reproduce the mGBA
HBlank-DMA ROM, confirm we hang/diverge, then implement. We boot Emerald into
the intro today, so we may not currently hit the hang path, but any game gating
logic on this value is at risk. Cite:
https://mgba.io/2020/01/25/infinite-loop-holy-grail/

**Per-channel DMA read latch (shortlist #7).** Each channel (0–3) has its *own*
32-bit read latch; a different channel's leftover leaks into a subsequent
channel's illegal read (Phantasy Star Collection interlaced scanlines). We have
the single-latch mechanics right (`<0x02000000` source doesn't drive, 16-bit read
mirrors into both halves, destination low bit selects the half) — the only miss
is that it is *one* shared latch, not four. Cheap: promote `m_dataLatch` to a
`[4]` array keyed by `m_activeChannel` (already threaded). `test-first` with the
`dma/latch` ROM as oracle — not the NGEMU forum anecdote. Cite:
https://github.com/nba-emu/hw-test (`dma/latch`)

**I/O-register read masks (shortlist #8).** Reads of mapped-but-unused I/O
addresses return fixed/zero/partial bit patterns per register — NOT the prefetch
echo. Our PPU register read-masking is done well, but the general non-PPU I/O map
is where mGBA `I/O` 81/130 lives (digest-0 attributes the gap to applying open
bus where a per-register mask is correct). Highest raw test-score leverage in the
CPU/DMA partition (49-point gap); grind, not cleverness. `test-first` with mGBA
`io-read` as the itemized oracle. Cite: https://github.com/mgba-emu/suite

**Undersized-ROM modulo mirror vs true open bus (shortlist #12).** ROMs < 32 MB
must repeat at `address % romSize` across the cart window — distinct from *true*
open bus on genuinely unmapped regions. digest-0 returns the true open-bus
`(address/2)&0xFFFF` pattern with no mention of modulo mirroring, suggesting the
two may be conflated. Classic NES titles read past nominal ROM expecting the
mirror (controls die otherwise). `test-first` — confirm in `AgbCartridge`/
`AgbBus` whether mirror-wrap is distinct; if conflated, fix (S). Feeds Classic
NES compat alongside the override DB. Cite:
https://deepwiki.com/mgba-emu/gbatek/2.1-gba-memory-system

**Folded DMA/timer audits (ride #1, mostly S, probably already right).** DMA
2-cycle start delay (`dma/start-delay`=20); DMA forces the following CPU fetch
non-sequential (`force-nseq-access`=88); DMA source/dest/count snapshotting at
start (mid-transfer register writes are cosmetic — probably already snapshotted,
audit `RunDmaLoop`); SRAM 8-bit-bus width mirroring + DMA-to-SRAM block. `mGBA`
DMA suite is 1244/1244 for us, so these are confirmations, not expected fixes.
Fast-EWRAM waitstate register (0x4000800) — even mGBA hasn't shipped it (issue
#1276); `adopt later`. DRQ-driven cartridge DMA — `skip` (no commercial cart uses
it). Cite: https://github.com/nba-emu/hw-test ·
https://problemkaputt.de/gbatek-gba-dma-transfers.htm

---

## 7. Performance architecture

The core is already at the interpreter SOTA (function-pointer dispatch tables,
the StepClocks quiescent-span collapse, event-queue scheduler). The perf arc is
about the many-instance fleet, not single-instance speed.

**Software fastmem (shortlist #9).** Split the address space into pages with
parallel read/write pointer arrays; RAM/BIOS/ROM map to a direct host pointer
(branchless `*(uint*)(ptr+off)`), I/O/unmapped route to a callback. GBA's coarse
map (≤16 top-level regions, a switch on bits 27–24) means no full page table is
needed. Best pure-perf $/effort lever for both single-instance and fleet, and
fully deterministic (the *software* variant, not MMU/SIGSEGV trapping — that
would inject OS-timing nondeterminism, impractical in managed .NET). We have a
region-dispatch bus but likely still go through method calls per access. Keep
timing/prefetch/PRAM-contention accounting on the slow path; only side-effect-free
RAM/ROM reads take the fast path. **Measure first** — the .NET JIT often elides
`Span` bounds checks, so the delta to raw pointers may be small; don't reach for
`unsafe` without a benchmark (see the `dotnet10-performance` skill). `test-first`,
leaning adopt. Cite: https://wheremyfoodat.github.io/software-fastmem/

**In-process work-stealing fleet pool (shortlist #14).** Advance N independent
deterministic machines from a shared work-stealing pool — each single-threaded
internally, zero IPC — vs OS-process-per-instance or thread-per-instance.
Reported ~1.5× purely from better core utilization. Our event queue is already
the fleet-optimal substrate (the cothread model multiplies fiber overhead
*per instance*). Parallelism is *across* independent machines, never *within*
one, so it's contract-safe by construction. `adopt later`, aligned with the
machine-fleet plan already in the repo. Cite:
https://github.com/Farama-Foundation/stable-retro

**Cached (block-linking) interpreter (shortlist #20).** Decode consecutive ops
once into cached blocks; the author reports 10–50% — but **vs a naïve
re-decode-every-instruction interpreter**, which we don't have. We already use
precomputed function-pointer dispatch, banking the decode-branch elimination the
cache targets; the *incremental* gain over our design is unquantified by any
source. Blocks also "cannot check the scheduler mid-block," which fights our
per-cycle StepClocks lockstep. `test-first` — benchmark a prototype against our
*actual* table-dispatch baseline, not a strawman. Fastmem and the StepClocks
fast path are likely better $/effort first. Cite:
https://emudev.org/2021/01/31/cached-interpreter.html

**Deliberately off the fleet path.** Threaded PPU/APU (Dolphin's dual-core
verdict: ~20% slower fake-completion, SyncGPU often slower than single-core —
keep PPU/APU on the CPU's deterministic tick); cothread scheduler (per-instance
fiber overhead); GPU-side PPU (DSHBA's blend-layer wall — GBA's top/bottom-pair
blending doesn't map onto standard GPU blend hardware, forcing a two-render
composite). The idle-loop heuristic detector is `skip` on determinism grounds:
our StepClocks quiescent collapse already banks the safe core of idle-skipping
generically; a tunable N-threshold or per-build DB would break reproducibility.
A small curated-address skip with a *fixed* constant in the override table is
`adopt later, only if profiled`.

---

## 8. Cartridge, save, RTC, peripherals

Save detection and RTC are strong (Flash tiers, EEPROM DMA-gate, tick-derived
S-3511A). The gaps are the override DB and the sensor peripherals.

**Per-game override database (shortlist #3).** A static table keyed by 4-char
game code that force-sets save type and RTC/rumble/gyro/tilt/solar flags,
overriding the ROM string-scan. Fixes the string-scan's known-broken minority:
Top Gun: Combat Zones (three conflicting save strings → anti-piracy lock), the
Classic NES Series SRAM-probe-but-EEPROM-backed bait-and-switch (Game Pak Error
otherwise), and correct RTC keying without content sniffing. Every mainstream
emulator converges on this — it's what makes save detection actually SOTA. A
source-generated `FrozenDictionary<uint, SaveProfile>` is a pure function of the
game code (perfectly reproducible), and the correct home for later peripheral
keying and any idle-loop DB. Seedable from mGBA's public MIT `overrides.c` — but
table values/enums must not embed external emulator proper nouns in identifiers.
`adopt now`. Cite:
https://github.com/mgba-emu/mgba/blob/master/src/gba/overrides.c ·
https://mgba.io/2014/12/28/classic-nes/

**GPIO sensors as replayable commands (shortlist #17).** Solar (Boktai), tilt
(Yoshi/WarioWare), gyro (WarioWare Twisted), rumble (Drill Dozer/WarioWare) all
ride the same 3-register GPIO block (`0x080000C4/C6/C8`) our RTC already uses —
the seam exists, only RTC rides it. Each sensor value must be a recorded/
replayable `CommandSnapshot` (virtualize, don't disable; never source from a real
accelerometer/clock), which is exactly our per-tick command model. `adopt later`,
keys off the override DB. Cite: https://shonumi.github.io/ ·
http://problemkaputt.de/gbatek-gba-cart-i-o-port-gpio.htm

**Folded cart items.** STOP-mode LCD/sound power-down — modeled identically to
HALT today (`AgbBus.Halt` comment); determinism-neutral, `adopt later`, low
priority unless a title depends on it. EEPROM save path is implemented but
untested against a real EEPROM game (none on hand) — the override DB + a real
title would close it. Exotic peripherals (e-Reader, Wireless Adapter/RFU,
Play-Yan, JOY-BUS/GameCube bridge, BattleChip Gate, Mobile Adapter GB) are all
research-heavy and small-audience — `skip`, revisit only on request; local
same-machine multi-instance link is the one thread that could matter, and it
folds into the rollback/Tier-C work, not into peripheral one-offs. GBE+ / "Edge
of Emulation" is the reference if peripheral emulation ever enters scope. Cite:
https://shonumi.github.io/

---

## 9. Determinism, savestate, rewind

This is the partition where our contract pays the biggest dividend — no-float/
no-RNG means a savestate is complete by construction — yet it's also our biggest
architectural outlier: the GB/GBC core has snapshot/restore/Fork; this one has
none.

**Flat whole-machine savestate (shortlist #5).** One contiguous, deterministic
byte image of all emulated state (IWRAM/EWRAM/VRAM/OAM/PRAM/save-RAM + CPU banked
regs/CPSR/SPSR + PPU/APU/DMA/timer/IRQ/scheduler/prefetch/cart state + master
`Now`), dumpable and restorable like mGBA's fixed-offset `GBAState`. The single
enabling primitive for rewind, runahead, rollback, and reinstating snapshot-
round-trip Post stages (GGPO reduces its entire contract to "save state, load
state, advance one frame"). Our no-float rule guarantees no hidden state to
serialize — the image is complete by construction; the only care item is the
scheduler event queue's `Callback` references, which must serialize as stable
enum/ID tags, not raw addresses (heap-address leakage is a named desync cause).
`Span<byte>`/`MemoryMarshal` bulk-copy the big RAM blocks; no reflection. Model
it on the GB/GBC core's existing snapshot/Fork API for consistency. Highest
leverage item in the perf/det partition. `adopt now`. Cite:
https://forums.mgba.io/showthread.php?tid=5624 · https://www.ggpo.net/ ·
https://docs.libretro.com/development/retroarch/netplay/

**Hash-based divergence detection + BIOS/ROM pre-flight gate (shortlist #2).**
The BIOS/ROM pre-flight hash gate is nearly free (S) and prevents a *known real
failure* — the documented "phantom cycle drift" session where the replacement
BIOS was accidentally used for parity work; Dolphin calls mismatched ROM/BIOS
hash "the top cause of divergences." Per-tick state hashing then upgrades our
current determinism Post stage (compare two machines' full observable state after
200 frames) into a cheap continuous detector that localizes the divergence *tick*
without shipping full state; the fine-diff step rides on savestate (#5). `adopt
now` for the gate + per-tick hash logging; `adopt later` for the A1-dependent
fine diff. Cite:
https://www.smashladder.com/guides/view/26pv/desync-troubleshooting-guide

**Rewind (shortlist #15).** Full base snapshot every ~120 frames; intermediate
frames as RLE/XOR deltas vs the nearest base in a fixed ring buffer; input
captured edge-triggered. binjgb hits ~1.39% ratio, ~712 B/frame, ~70 KiB/s
steady state, ~10× smaller than compressing independent snapshots. Deltas are
pure `Span<byte>` comparisons over the deterministic image; mGBA runs the diff on
a background thread, safe *only* because it never feeds back into emulated state
— keep any diff threading strictly read-only over a frozen snapshot. Pre-size and
reuse the ring's `byte[]`s; fuzz the wraparound (BizHawk's rewinder had
full-buffer crash bugs). `adopt later`, right after savestate. Cite:
https://binji.github.io/posts/binjgb-rewind/ ·
https://github.com/mgba-emu/mgba/blob/master/src/core/rewind.c

**Runahead, two-instance (shortlist #16).** Re-simulate N frames per displayed
frame to cut input latency (cost ≈ 1+k passes). The two-instance variant runs
the lookahead on a second independent core instead of save/load-thrashing one —
and is *faster* (Super Castlevania IV @5-frame runahead: 68→140 fps). A natural
fit for our fleet (independent machines already the model) and the better target
for us. `adopt later`. Cite: https://docs.libretro.com/guides/runahead/

**Rollback / lockstep cross-instance sync (shortlist #22).** Lockstep = fixed
input-delay so all instances apply the same input on the same frame (melonDS
delays 4); rollback = apply local input immediately, reload+resimulate on
misprediction. Both reduce to save/load/advance-one-frame — exactly the workload
our contract is built for. More relevant to us than netplay: replay validation
across fleet machines and a substrate for future cross-machine SIO/link
determinism (Tier-C Post is reserved, zero stages today). `test-first` — build
savestate + hash-divergence first, prove round-trip identity, then decide
lockstep vs rollback based on whether real link latency is ever in scope. mGBA's
approach (stream the SIO bus, lean on hardware determinism) is a lighter
alternative if link is the only use case. Watch the mundane traps the sources
flag (melonDS unsigned-underflow, save-while-polling races). Cite:
https://melonds.kuribo64.net/comments.php?id=179 · https://www.ggpo.net/

*Non-blocker worth stating: Waterbox forces software rendering to stay
snapshot-able. Our PPU is a CPU rasterizer producing a plain framebuffer in
emulated memory, so savestate has no GPU-state problem — don't import that
limitation as a phantom blocker.*

---

## 10. The emulator landscape

The field shares one vocabulary (endrift's **cycle-accurate** / **cycle-count-
accurate** / **HLE** taxonomy) and one credibility anchor (the AGS aging
cartridge + the community timing/ARMWrestler/FuzzARM suites), which is exactly
our co-sim posture. Where each project sits:

- **mGBA** (endrift) — deliberately *cycle-count* accurate for its GBA core, not
  cycle-accurate, by explicit design. The pragmatic reference; the "Holy Grail
  Bugs" and WAITCNT bit-15 writeups are the field's best evidence that GBATEK
  cannot be trusted uncritically and that independent emulators inherit identical
  doc-sourced bugs. Our co-sim oracle and the source of the FIFO (#4), Holy Grail
  (#6), override-DB (#3), and mix-formula (#18) techniques.
- **NanoBoyAdvance** (fleroviux) — targets *true* cycle accuracy; first emulator
  of any kind to pass AGS (predating even the MiSTer core). The specialist
  accuracy leader alongside SkyEmu, and the higher-value candidate if a third
  co-sim oracle is ever added for GBA-specific questions.
- **SkyEmu** (Saleh) — genuinely per-pixel PPU (mid-scanline effects) and
  per-sample audio; 2020/2020 timing with the official BIOS. The proof-of-cost
  reference for shortlist #21.
- **ares** — accuracy-purist multi-system project, cothread (`libco`) scheduler.
  **Our co-sim oracle**, and our timer-overflow→IRQ boundary matches it exactly.
- **VBA-M** — the anti-pattern baseline (680/1260 timing); its canonical warning
  is *accuracy debt* — ROM hacks authored against its bugs now depend on the
  inaccuracy.
- **DSHBA** (DenSinH) — the only GPU-side PPU attempt; documents the blend-layer
  wall worth knowing before any hardware-accelerated PPU path here.

**Conflicts / dubious claims flagged across the survey (not laundered):**

1. **"ares has no GBA support"** (a gatherer digest, sourced from Wikipedia/
   Grokipedia summaries) directly contradicts our live `ares-cosim.exe` +
   `--lockstep` oracle and the settled facts. **Stale/wrong — trust the
   implementation.** Separately, ares's GBA core is behind NBA/SkyEmu/mGBA on
   accuracy (interpreter-based, under-invested) — but that does not weaken its
   value as an *independent* oracle; the two statements are not in conflict.
2. **AGS "first/only to pass"** framing is stale within the digest set itself —
   NBA, MiSTer, Nintendo's official emulator, SkyEmu, and Hades all pass. We're
   in a small club, not alone; our 38/38 stands on its own (real BIOS).
3. **mGBA `Timing` score cross-comparison.** Our itemized 1460/2020 must not be
   compared against another table's "mGBA 0.9.3 Timing 1708/2020" — different
   emulators, possibly different suite versions. Treat suite totals as
   version-dependent; our own itemized scores are the trustworthy figure.
4. **Cached-interpreter 10–50%** and **EmuRust 1.5× / EnvPool 1M fps** are
   directional, not load-bearing: the first is vs a re-decoder we don't have; the
   others are C++/GPU-batched results, not C#/.NET targets. Don't cite as our
   expected numbers.
5. **DMA-latch per-channel** framing was consistent across the research digests
   (per-channel) but the Phantasy Star specifics are NGEMU forum lore — adopt the
   hardware-derived `dma/latch` ROM as the oracle, not the anecdote.
6. **Marketing self-claims** (SkyEmu/Hades "2nd vs 3rd to pass AGS," the
   mid-scanline game list) are self-reported and internally inconsistent — use
   the game list as *candidate* proving ROMs (verify each actually breaks on our
   renderer), not as established requirements.

---

## 11. Recommended next moves

Ordered by effort-adjusted value, consistent with the shortlist verdicts. This
list is the "what to build first"; the GB/GBC-native backlog and the Agb-costume
carry-forward stay in
[docs/ideal-gaming-brick-plan.md](ideal-gaming-brick-plan.md) §8 (KEY0 latch, AGB
OBJ quirk, palette/color-correction, post-boot DIV seed) — not duplicated here.

1. **Stand up the `nba-emu/hw-test` Tier-B battery (#1).** S–M, adopt-now,
   doctrine-aligned (evidence, not a gate). It is the enabling move that turns
   the entire test-first tier (Holy Grail, per-channel latch, I/O masks, OBJ
   budget, affine latch, ROM mirror, and the folded DMA/timer/HALTCNT audits)
   into pass/fail evidence. Nothing else in the middle of the shortlist is
   actionable without it.
2. **Add the BIOS/ROM pre-flight hash gate (#2).** S, adopt-now. Nearly free and
   prevents a known real failure (the phantom-cycle-drift session). Pair it with
   per-tick state-hash logging to prep the divergence localizer.
3. **Build the per-game override DB (#3).** S–M, adopt-now. Closes concrete
   compatibility holes (Top Gun, Classic NES), is determinism-perfect, and is the
   home for later RTC/sensor/idle-loop keying — a small investment that unlocks a
   category.
4. **Fix the FIFO ring/playing model + overflow-reset (#4).** M, adopt-now (probe
   ROM first). The highest-value APU item; fixes named audio crackle and is fully
   determinism-clean off the timer stream we already have.
5. **Implement flat whole-machine savestate (#5).** M–L, adopt-now. The keystone:
   it reinstates snapshot Post stages we currently work around and unblocks
   rewind, runahead, rollback, and the fine-grained hash diff. Model it on the
   GB/GBC core's Fork API; tag scheduler callbacks by stable ID.
6. **Investigate the DMA-lingering CPU open bus (#6).** M, test-first → adopt. The
   strongest commercial-compatibility correctness win; reproduce the mGBA
   HBlank-DMA ROM, confirm divergence, then bridge the DMA value into the next
   single CPU fetch. Promote the DMA latch to per-channel (#7) in the same pass.
7. **Grind the I/O read-mask table (#8) against mGBA `io-read`.** M, test-first.
   The biggest raw test-score gap (81/130); mechanical once the oracle is in.
8. **Prototype software fastmem (#9), measured against our current bus.** M,
   test-first. The best pure-perf lever, but benchmark before reaching for
   `unsafe` — the .NET JIT may already elide the bounds checks. Then take the
   fleet work-stealing pool (#14) and two-instance runahead (#16) as the
   many-machines arc, savestate-gated.

Everything below the fold — multiply carry-flag (#13), THUMB region open bus
(#19), exact mix levels (#18), GPIO sensors (#17), rewind (#15) — is well-scoped
`adopt later` that schedules naturally behind the eight above. The two XL items
(per-dot PPU #21, rollback #22) stay gated behind a co-sim-measured proven need;
do not start either speculatively.
