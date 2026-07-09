# Test ROMs and Evidence

Reference suites are **evidence, never CI gates** — they run as Post Tier-B
batteries that skip cleanly when their assets are absent. This page is the
evidence catalog: the test-ROM→finding map (the corpus's most directly adoptable
artifact), the suites that separate accurate cores from approximate ones, and our
own `--oracle` cycle-probe battery — including the measured-vs-documented
divergence rows, recorded here as honest open questions for a future session to
pick up.

Provenance: `digest-4` §6 (the test-ROM map), `digest-1` §5, `digest-2` §7,
`digest-8` §8, with post-wave facts from the implementation (`OracleProbes.cs`).

---

## The test-ROM → finding map

The single most directly adoptable artifact in the corpus: each hardware finding
paired with the ROM that pins it as a hard numeric oracle rather than prose. From
`nba-emu/hw-test` (https://github.com/nba-emu/hw-test , Codeberg mirror
https://codeberg.org/nba-emu/hw-test) unless noted.

| Finding | Proving test ROM | Expected value |
|---|---|---|
| DMA 2-cycle start delay | `dma/start-delay` | `TM0CNT_L == 20` |
| DMA forces non-sequential CPU access | `dma/force-nseq-access` | `88` (EWRAM and ROM cases) |
| Per-channel DMA read latch + open-bus fallback | `dma/latch` | `0xC0DE`/`0xDEAD` alignment cases |
| DMA-lingering value on CPU open bus | mGBA "Infinite Loop That Wasn't" custom HBlank-DMA ROM (reproducible, not upstreamed) | game exits the loop |
| Timer reload write doesn't affect live counter; the "arrived too late" boundary cycle | `timer/reload` | `0xDEAE` / `_32_7` → `0xFFF9` |
| Timer start/stop latency window | `timer/start-stop` | 3 → frozen `8` |
| IRQ dispatch latency by handler region | `irq/irq-delay` | `92` IWRAM / `112` EWRAM / `120` ROM (needs real BIOS) |
| HALTCNT exit timing (direct / CpuSet / CpuSet+DMA / pre-pending-IRQ) | `haltcnt` | `12` / `4155` / `4154` / `125`/`249` |
| 128 KB EWRAM mirror / bus-boundary behavior | `bus/128kb-boundary` | — |
| VRAM background-fetch open bus | ares #1113 `sbb_reg` custom ROM | matches hardware capture |
| OBJ per-line sprite-cycle budget + dropout | sprite-overcommit ROM + render-hash (candidate) | trailing sprites drop |
| Affine reference-point mid-frame write | Tonc `affbg.gba` | per-line matrix correct |
| Vertical sprite mosaic block length | Tonc `mos_demo.gba` (mGBA #1008 regression ROM) | block edges correct |
| FIFO ring size / double-DMA-impossible / overflow-resets | mGBA #1847 hardware timing probe ROM | 7 ring + 1 playing |
| Broad memory/IO/timing/DMA corpus with per-subtest granularity | `mgba-emu/suite`, `jsmolka/gba-tests` | itemized pass counts |
| Pure-CPU ALU/pipeline sanity (no SWI/DMA/timer/IRQ) | ARMWrestler, `DenSinH/FuzzARM` | full pass |

The reviewers' recurring verdict: **adopting the `nba-emu/hw-test` corpus as a
Tier-B battery (survey #1) is the single highest leverage-per-effort move** — it
turns a dozen "partial/unknown" rows across the CPU/DMA/timer partitions into
pass/fail evidence. Nothing in the middle of the shortlist is actionable without
it. It landed this arc as the `--oracle` probe battery (below).

---

## The discriminating suites

### AGS Aging Cartridge

- **Source:** TCRF, https://tcrf.net/AGS_Aging_Cartridge ; DenSinH/AGSTests,
  https://github.com/DenSinH/AGSTests.
- **Finding:** a genuine Nintendo factory test cartridge (five known revisions;
  v10.0 leaked via the official Switch "Nintendo Classics" emulator), exercising
  timing-sensitive memory/waitstate/prefetch edge cases. The community's toughest
  bar and the credibility anchor NanoBoyAdvance and SkyEmu built their reputations
  on. DSHBA's own README admits it passes "except those requiring very accurate
  timings" — a useful map of which categories are hardest.
- **Puck status:** the core passes **AGS 38/38 with a real BIOS** (a Post Tier-B
  stage). Stands on its own regardless of the "first/only to pass" framing, which
  is stale (NBA, MiSTer, Nintendo's official emulator, and GameBeanAdvance all
  pass — a small club, not alone).

### The mGBA test suite

- **Source:** https://github.com/mgba-emu/suite ; forum index,
  https://forums.mgba.io/showthread.php?tid=18.
- **Finding:** categories per `src/main.c`: memory, io-read, timing, timers,
  timer-IRQ, shifter, carry, multiply-long, bios-math, dma, sio-read, sio-timing,
  misc-edge, video. The gap between accurate and approximate cores concentrates in
  Timing / DMA / IO — mGBA (cycle-*count*) still trails SkyEmu/NanoBoyAdvance on
  those cycle-level edges.
- **Puck status:** run as a Post Tier-B stage; our itemized scores (from the
  digest-0 sweep) — Memory 1519/1552, I/O 81/130, Timing 1460/2020, Timer count-up
  630/936, Timer IRQ 54/90, Shifter 140/140, Carry 93/93, MulLong 52/72, BIOS math
  615/615, DMA 1244/1244, SIO R/W 85/90, SIO timing 0/8, Misc 3/12. The 81/130 I/O
  gap ([dma-timers-interrupts-open-bus.md](dma-timers-interrupts-open-bus.md), I/O
  read masks) is the biggest raw test-score lever; MulLong 52/72 is the Booth
  carry-flag frontier ([cpu-pipeline-prefetch-waitstates.md](cpu-pipeline-prefetch-waitstates.md)).
- **Calibration (reviews A/B/C):** suite totals are **non-comparable across
  emulators and across suite versions** — our itemized 1460/2020 Timing must *not*
  be cross-compared against a table's "mGBA 0.9.3 Timing 1708/2020" (different
  emulator, possibly different suite version). Our own itemized scores are the
  trustworthy figure; external comparison tables (e.g. SkyEmu's Accuracy.md) are
  useful for *direction*, not absolute targets. Community per-category pass-count
  folklore ("90/90 timer-irq", "625/625 bios-math") is emulation-wiki-grade,
  claims-at-time-of-writing.

### jsmolka/gba-tests, ARMWrestler, FuzzARM

- **Source:** https://github.com/jsmolka/gba-tests ,
  https://github.com/DenSinH/FuzzARM.
- **Finding:** jsmolka covers arm/thumb/ppu/memory/bios/save/unsafe with
  per-subtest first-failure reporting, including the ROM-waitstate-with/without-
  prefetch timing tests. ARMWrestler is a hand-written ALU/load-store correctness
  ROM (functional, no timing — good "stage-0" smoke). FuzzARM is a randomized
  ~10,000-case ARM/THUMB generator with no timing requirements, for early bring-up.
- **Puck status:** jsmolka + FuzzARM run as Post Tier-B stages and pass. ARMWrestler
  is **skip** (redundant with FuzzARM + jsmolka + our own 41-vector hand-assembled
  smoke suite — review-a A17).

---

## Our `--oracle` cycle-probe battery (landed this arc)

The `nba-emu/hw-test` adoption (survey #1) shipped as a self-authored `--oracle`
probe battery (`OracleProbes.cs`) that reports **measured-vs-documented per
probe**. Two rows are **self-checking gates**; the DMA/timer/IRQ/halt cycle rows
are **honest our-harness measurements** against the documented corpus targets.

**The critical framing (carried from the code's own comment):** our probe harness
is *not* the external corpus's harness — a probe drives our core with our own
micro-ROM shape and reads a timer value the way *we* set it up, not the way the
`nba-emu/hw-test` ROM does. So a documented corpus number and our measurement can
**legitimately differ** without either being wrong; the divergence is a
probe-shape difference until proven otherwise. **These are recorded as open
questions, not chased as core bugs.**

### Self-checking gates (pass/fail)

- **`directsound-fifo`** — drives `AgbApu` directly and asserts the hardware-measured
  Direct Sound FIFO model: 7-word ring size, the double-DMA-impossible invariant,
  overflow-resets-to-empty. (Values derived from the model, not a number to tune
  to.) Backs survey #4/#5; see [apu-and-direct-sound.md](apu-and-direct-sound.md).
- **`dma/latch-per-channel`** — channel 1 runs a drivable transfer, channel 0 then
  reads its undrivable-source latch; passes iff ch0 returns `0x00000000` (its own
  latch), *not* ch1's `0xAABBCCDD`. Proves the read latch is per-channel — which it
  always was; see [dma-timers-interrupts-open-bus.md](dma-timers-interrupts-open-bus.md).

### Measurement rows — documented corpus number vs our probe's measurement

Recorded as open questions. In each, the probe shape differs from the external
ROM's; a divergence is **not a proven core bug** until a future session either
reconciles the probe shape with the corpus ROM or confirms a real discrepancy via
co-sim.

| Probe | Documented (corpus) | Our probe's measurement | Reading |
|---|---|---|---|
| `dma/start-delay` | `20` | `133` | large divergence — probe measures start delay **+ burst cycles** through our bus, a different quantity than the corpus ROM's isolated capture; reconcile the ROM shape before treating as a bug |
| `dma/force-nseq` | `88` | `115` | divergence — our micro-ROM's fetch stream around the DMA differs from the corpus ROM's `mov r0,r0` sled; probe-shape difference |
| `irq/dispatch (ROM handler)` | `120` (region band `92`/`112`/`120`) | `~90` | **inside/near the documented band** — our IRQ dispatch lands ~90; the region-dependent handler-fetch cost is folded into general waitstate accounting; least-divergent row |
| `haltcnt/exit (direct)` | `12` | `0` | divergence — our direct-HALTCNT wake predicate resolves immediately in our probe's timing frame; verify against the `haltcnt` ROM's TM0/TM1 stopwatch setup |
| `timer/start-stop` | `3` then frozen `8` | (probe row) | latch-discipline check — do not retune if it matches ares |
| `timer/reload-race` | `0xDEAE` / `0xFFF9` boundary | (probe row) | reload-vs-live-counter boundary check |

**How a future session should pick these up (honestly):** treat the divergence as
a hypothesis about *probe shape*, not a defect. Either (a) rebuild the probe ROM to
match the `nba-emu/hw-test` source ROM's exact instruction sequence and timer-read
placement so the numbers become comparable, or (b) if the shapes already match,
escalate to a co-sim (`--lockstep` vs ares/mGBA) — and remember the standing rule:
**a residual gap that already matches ares is a shared hardware-truth frontier,
not our bug** (the timer-overflow→IRQ-recognition depth is the known example — do
not retune what ares also has). The `irq/dispatch ~90-in-band` row and the two
self-checking gates are the trustworthy signals today; the four large divergences
(`dma/start-delay`, `dma/force-nseq`, `haltcnt`) are probe-shape reconciliation
work, not confirmed accuracy defects.

---

## The dual co-simulation harness (Puck-side)

- **Source:** implementation (`Diagnostics.cs`, `--lockstep`/`--statetrace`/
  `--trace-cycles`); `statediff.py`.
- **Finding:** two live oracles share one trace format — an mGBA-core-linked C
  harness (`cosim.exe`) and `ares-cosim.exe`, driven via `--lockstep`;
  `statediff.py` aligns statetrace streams. The README stresses "no emulator is
  hardware truth" — the mGBA suite's hardware-derived expectations sometimes
  diverge from both mGBA and ares themselves.
- **Puck status: already SOTA-elite.** This is the field's recommended posture
  (anchor on multiple independent oracles because independently-written emulators
  inherit identical GBATEK-sourced bugs). The BIOS pre-flight gate now refuses a
  non-retail BIOS on these diagnostics without `--allow-replacement-bios` (landed
  this arc — [determinism-savestate-replay.md](determinism-savestate-replay.md)).
- **See also:** [emulator-landscape.md](emulator-landscape.md).

---

**Bottom line.** The core sits on the NanoBoyAdvance/ares side of the accuracy
line, with AGS 38/38 and the `--oracle` battery + dual co-sim as its checkable
credibility anchors. The evidence gaps are: standing up the remaining
`nba-emu/hw-test` rows as literal proving ROMs (so the `--oracle` measurements
become comparable), and the honest reconciliation of the four large probe-shape
divergences above — none of which is a confirmed core bug today.
