# DMA, Timers, Interrupts, and Open Bus

This is where cycle-level edge cases concentrate — the residual "hard 10%" that
separates accurate cores from approximate ones. Puck's DMA/timer/IRQ *shape* is
already accurate-core tier (priority preemption, video-capture windows,
undrivable-source open bus, latched timers, the two-stage synchronizer), and the
mGBA `DMA` suite is 1244/1244. The open items are the CPU-side lingering DMA
value, the I/O read-mask grind, and a cluster of cheap `test-first` audits that
ride the hw-test corpus. One correction: the DMA read latch was **always**
per-channel — the survey's "single shared latch" premise was stale at gather time.

Provenance: `digest-4` (gatherer), `review-a` (deep review), with credit and
post-wave facts from `digest-0` and the implementation.

---

### DMA-lingering CPU open bus — the "Holy Grail"

- **Source:** mGBA blog, *The Infinite Loop That Wasn't*,
  https://mgba.io/2020/01/25/infinite-loop-holy-grail/ ; NGEMU, *GBA Open Bus*,
  https://www.ngemu.com/threads/gba-open-bus.170809/.
- **Finding:** a just-completed DMA's last bus value lingers on the external bus
  for exactly one instruction; if the next CPU fetch is an invalid-memory read, it
  observes the DMA value, not the normal prefetch open-bus value. Games hang in
  tight loops without it — Pokémon Emerald, Sonic Pinball Party, Hello Kitty
  Collection. mGBA's fix compares expected-fetch-PC vs actual on an invalid load
  and substitutes the DMA-lingering value when they match; validated against an
  HBlank-DMA hardware ROM.
- **Determinism fit:** integer latch + PC comparison — compatible.
- **Puck status: not implemented — the two latches are disjoint.** The core keeps
  a DMA-side `m_dataLatch` and a *separate* CPU `m_openBus`, but nothing bridges
  "last DMA bus value → next single CPU fetch." **Verdict (review-a A3 / survey
  #6): test-first → adopt** (M, `AgbBus` open-bus read path +
  `AgbDmaController.RunDmaLoop` completion; a "DMA just finished, value V, valid
  for the next fetch only" one-shot) — the strongest commercial-compatibility win
  in this partition. We boot Emerald into the intro today, so we may not currently
  hit the hang path, but any game gating logic on this value is at risk. Reproduce
  the mGBA HBlank-DMA ROM, confirm divergence, then implement — promoting the DMA
  latch usage in the same pass.
- **See also:** the per-channel latch below,
  [cpu-pipeline-prefetch-waitstates.md](cpu-pipeline-prefetch-waitstates.md)
  (CPU open bus).

### Per-channel DMA read latch (the survey's stale premise)

- **Source:** nba-emu/hw-test `dma/latch`, https://github.com/nba-emu/hw-test ;
  GBATEK, *DMA Transfers*, http://problemkaputt.de/gbatek-gba-dma-transfers.htm.
- **Finding:** each DMA channel (0–3) has its *own* 32-bit read latch. A source
  `< 0x02000000` (BIOS/unused) doesn't update the latch — the stale value is
  emitted; a 16-bit DMA write duplicates the halfword into both latch halves;
  destination alignment picks LSW vs MSW; a read from unmapped I/O falls back to
  the prefetch-echo open bus. A different channel's leftover latch leaking into a
  subsequent channel's illegal read is the (NGEMU-lore) Phantasy Star Collection
  interlaced-scanline case.
- **Determinism fit:** integer — compatible.
- **Puck status: already per-channel — the survey premise was stale.** The read
  latch was already a per-channel `uint[4]`, keyed by the active channel; there
  was no shared-latch bug. **The survey's row #7 "single shared latch" premise was
  wrong.** The `--oracle` `dma/latch-per-channel` gate now proves the isolation:
  channel 0's undrivable-source read returns `0x00000000` (its own latch), *not*
  channel 1's `0xAABBCCDD`, confirming the latches are separate. **Verdict
  (review-a A4 / survey #7): implemented (this arc)** as the intent-making commit +
  self-checking gate; no behavioral bug to fix.
- **Calibration (review-a C3):** the per-channel framing was consistent across the
  research digests, but the Phantasy Star specifics are NGEMU forum lore — the
  hardware-derived `dma/latch` ROM is the oracle, not the anecdote.
- **See also:** the Holy Grail entry above,
  [test-roms-and-evidence.md](test-roms-and-evidence.md).

### DMA 2-cycle start delay

- **Source:** nba-emu/hw-test `dma/start-delay`, https://github.com/nba-emu/hw-test ;
  GBATEK, http://problemkaputt.de/gbatek-gba-dma-transfers.htm.
- **Finding:** after the enable bit flips 0→1, the DMA waits 2 clock cycles (2I
  internal setup) before touching the bus. `dma/start-delay` pins the total
  enable-write→first-read cost to a captured `TM0CNT_L == 20`.
- **Determinism fit:** integer — compatible.
- **Puck status: partial — right shape, exact number recorded as a probe row.**
  DMA runs on the CPU's *next bus access* (correct shape), but the exact 2-cycle
  setup / `==20` total was unverified. **Verdict (review-a A6 / survey folded):
  test-first** — adopt the probe; only patch if we diverge. Our `--oracle`
  `dma/start-delay` measurement diverges from the documented 20 (probe-shape
  difference, not a proven bug — recorded as an open question on
  [test-roms-and-evidence.md](test-roms-and-evidence.md)).
- **See also:** forced-nseq below.

### DMA forces the following CPU fetch non-sequential

- **Source:** nba-emu/hw-test `dma/force-nseq-access`,
  https://github.com/nba-emu/hw-test.
- **Finding:** DMA breaking the CPU's S-cycle prefetch stream costs the next CPU
  fetch an extra N-cycle regardless of which side is slow memory;
  `dma/force-nseq-access` expects `88` for both EWRAM-only and ROM-source cases.
- **Determinism fit:** integer — compatible.
- **Puck status: partial.** `m_activeChannel` tracks whose turn charges S vs N and
  `BeginDmaStall`/`EndDmaStall` frame the burst; whether the *first CPU fetch
  after* the burst is forced N is not stated. **Verdict (review-a A7 / survey
  folded): test-first** (S, reset the sequential-tracking flag on DMA end). Our
  `--oracle` `dma/force-nseq` measurement diverges from the documented 88 (probe
  shape — see [test-roms-and-evidence.md](test-roms-and-evidence.md)).
- **See also:** start delay above.

### DMA source/dest/count snapshotting at start

- **Source:** GBATEK, http://problemkaputt.de/gbatek-gba-dma-transfers.htm.
- **Finding:** SAD/DAD/CNT_L "do NOT change during or after the transfer" —
  hardware loads internal source/dest/counter pointers once at DMA start; live
  writes to the visible registers during an active transfer are cosmetic. An
  emulator that live-reads the visible registers to drive its loop diverges when a
  game pokes them mid-burst.
- **Determinism fit:** N/A (structural) — compatible.
- **Puck status: unknown — audit, probably already right.** `RunDmaLoop` scans per
  word, but the digest doesn't state whether it reads visible registers or
  internal cursors; ares-parity suggests it's already snapshotted. **Verdict
  (review-a A5): test-first / audit** (S if a fix is needed) — read `RunDmaLoop`
  once; if cursors are internal, mark SOTA. Exercised indirectly by `dma/latch`
  and Emerald.
- **See also:** the per-channel latch above.

### Video-capture DMA3

- **Source:** GBATEK, http://problemkaputt.de/gbatek-gba-dma-transfers.htm.
- **Finding:** DMA3's Video Capture mode (repeat bit set, units-per-scanline in
  the word count) starts at `VCOUNT=2`, repeats every scanline, and auto-stops at
  `VCOUNT=162` — a scanline-counter-driven repeat/stop, distinct from HBlank-DMA
  repeat.
- **Determinism fit:** integer — compatible.
- **Puck status: already at SOTA.** `OnVideoCapture`/`OnVideoCaptureEnd` are active
  on HBlank of scanlines 2–161, matching the VCOUNT=2 start / VCOUNT=162 stop.
- **See also:** priority preemption (credit list below).

### Timer reload write-race + start/stop latency window

- **Source:** nba-emu/hw-test `timer/reload` + `timer/start-stop`,
  https://github.com/nba-emu/hw-test ; GBATEK, *Timers*,
  https://problemkaputt.de/gbatek-gba-timers.htm.
- **Finding:** `timer/reload` proves a write to the (write-only) reload register
  never disturbs the live counter (`_16_0`/`_32_0`/`_32_6` → `0xDEAE`), and pins
  the exact cycle where a `TM*CNT` word-write's control half stops being
  observable in-window (`_32_7` → the undisturbed baseline `0xFFF9`).
  `timer/start-stop` pins the post-stop-write coast (counter reads 3, then
  continues to a frozen 8 rather than stopping instantaneously) — a real
  start/stop latency window. Count-up (cascade) ignores the prescaler and cannot
  run on timer 0; reads are live-sampled while running, freeze-latched (not
  undefined) while stopped.
- **Determinism fit:** integer — compatible.
- **Puck status: partial — right architecture, exact edges recorded as probes.**
  The latch discipline is strong: control/reload committed one cycle later via
  `StepLatch`, a fresh 0→1 enable arms `m_pending` applied the following cycle so
  the startup delay is emergent, not a tuned constant; the prescaler is a real
  per-cycle state machine (`s_mask` low-bit mask), not a lazy `(now-last)>>prescale`;
  cascade ripples timer0→1→2 in one cycle. The exact `_32_7` boundary and the
  start-stop coast are recorded as `--oracle` `timer/reload-race` and
  `timer/start-stop` rows. **Verdict (review-a A14 / survey folded): test-first** —
  adopt as cycle-indexed oracles; **do not retune anything that already matches
  ares** (our known ~1–2-cycle overflow→recognition shortfall matches ares exactly
  — a shared hardware-truth frontier, not our bug).
- **See also:** [test-roms-and-evidence.md](test-roms-and-evidence.md),
  [apu-and-direct-sound.md](apu-and-direct-sound.md) (timer→FIFO interaction).

### IRQ dispatch latency by handler region

- **Source:** nba-emu/hw-test `irq/irq-delay`, https://github.com/nba-emu/hw-test ;
  GBATEK, *Interrupt Control*,
  https://problemkaputt.de/gbatek-gba-interrupt-control.htm.
- **Finding:** the *same* IRQ dispatch costs 92 (IWRAM) / 112 (EWRAM) / 120 (ROM)
  cycles purely by where the handler lives — a combined oracle for BIOS-dispatch +
  first-fetch waitstate. Requires a *real* BIOS to be exact. The BIOS IRQ
  trampoline (save R0–R3/R12/R14, jump through the `0x03007FFC` user-handler
  pointer, return via `BX LR`) must reproduce the exact shape or handler-timing
  tests diverge.
- **Determinism fit:** integer — compatible.
- **Puck status: partial.** The halt-wake +2 penalty and the two-stage IE/IF/IME
  synchronizer are modeled; the region-dependent handler-fetch cost is likely
  folded into general waitstate accounting but unverified as a three-way oracle.
  **Verdict (review-a A12 / survey folded): test-first** — adopt `irq/irq-delay`
  with a real BIOS. Our `--oracle` `irq/dispatch (ROM handler)` measurement lands
  ~90, inside the documented 92/112/120 band (recorded as a probe row — see
  [test-roms-and-evidence.md](test-roms-and-evidence.md)).
- **See also:** HALTCNT below.

### HALTCNT exit-timing variants

- **Source:** nba-emu/hw-test `haltcnt`, https://github.com/nba-emu/hw-test ;
  GBATEK, *BIOS Halt Functions*,
  http://problemkaputt.de/gbatek-bios-halt-functions.htm.
- **Finding:** five sub-tests race TM0 vs a TM1 IRQ across halt-entry methods —
  direct `REG_HALTCNT=0` expects 12; via BIOS `CpuSet` expects 4155; `CpuSet`+DMA
  expects 4154; IWRAM/ROM re-entry with an already-pending, already-IF-acked IRQ
  expects 125/249. Proves halt-exit fires on `(IE AND IF) != 0` even when the
  interrupt was asserted before HALTCNT was written, with the I-bit and IME
  don't-care for exit. The GBA analogue of the GB halt-bug family.
- **Determinism fit:** integer — compatible.
- **Puck status: partial.** `RunUntilInterrupt` wakes on any enabled interrupt
  with the +2 penalty; STOP is (deliberately) modeled as HALT. The
  pre-pending-IRQ and CpuSet-mediated cases aren't called out and the exact numbers
  are unverified. **Verdict (review-a A13 / survey folded): test-first** — adopt
  `haltcnt` (some sub-tests need real-BIOS CpuSet). Our `--oracle`
  `haltcnt/exit (direct)` measurement diverges from the documented 12 (probe shape
  — see [test-roms-and-evidence.md](test-roms-and-evidence.md)).
- **See also:** IRQ dispatch latency above,
  [determinism-savestate-replay.md](determinism-savestate-replay.md) (STOP mode).

### The IF-acknowledge / IME-write race

- **Source:** GBATEK, *Interrupt Control*,
  https://problemkaputt.de/gbatek-gba-interrupt-control.htm.
- **Finding:** GBATEK cautions that an interrupt can fire while a command clearing
  IME or an IE flag is executing, and recommends clearing IME *before* touching
  IE; IF uses write-1-to-clear.
- **Determinism fit:** integer — compatible.
- **Puck status: already at SOTA.** The two-stage IE/IF/IME synchronizer (writes
  land in stage `[1]`, reads return committed stage `[0]`, the shift is frozen
  during a DMA stall) produces register-visibility delay and IRQ-recognition
  latency as emergent pipeline behavior, structurally handling this race.
- **See also:** HALTCNT above.

### I/O-register read masks (distinct from open bus)

- **Source:** GBATEK, https://problemkaputt.de/gbatek.htm ; mGBA suite,
  https://github.com/mgba-emu/suite.
- **Finding:** reads of mapped-but-unused I/O addresses in `0x04000000–0x040003FE`
  return fixed/zero/partial bit patterns per register — *not* the prefetch echo. A
  separate accuracy axis from generic open bus; exactly what the mGBA `io-read`
  category pins down.
- **Determinism fit:** integer masks — compatible.
- **Puck status: partial — the biggest raw test-score gap.** PPU register
  read-masking is done well (BG0/1CNT bit 13, WININ/WINOUT, BLDCNT/BLDALPHA driven
  to 0), but the general non-PPU I/O map is where mGBA `I/O` 81/130 lives (the
  gatherer attributes the gap to applying open bus where a per-register mask is
  correct). **Verdict (review-a A10 / survey #8): test-first** (M breadth, not
  depth — a per-register mask table in the I/O read switch) — grind, not
  cleverness; use mGBA `io-read` as the itemized oracle. Highest test-score
  leverage in this partition (49-point gap), even if per-game compatibility impact
  is modest.
- **See also:** [cpu-pipeline-prefetch-waitstates.md](cpu-pipeline-prefetch-waitstates.md)
  (THUMB region open bus, the *other* half of the I/O deficit).

### SRAM 8-bit-bus width mirroring

- **Source:** GBATEK, https://problemkaputt.de/gbatek.htm ; gbadev.net/gbadoc/memory.html.
- **Finding:** Game Pak SRAM is CPU-only over an 8-bit bus — it cannot be a
  16/32-bit DMA target, and wide CPU reads/writes re-sample the same byte lines
  (the byte value replicated across the width).
- **Determinism fit:** N/A — compatible.
- **Puck status: partial / unknown.** SRAM is 32 KiB "direct byte-addressed"; the
  byte-replication on wide access and the DMA-to-SRAM block aren't confirmed.
  **Verdict (review-a A11): test-first / audit** (S) — jsmolka `save` + our
  `save-round-trip` Post stage cover most of this; confirm the width-mirror and
  DMA-block, patch if missing. (Also confirm DMA3-only-to-ROM and the
  SRAM-DMA-block as part of the same audit; DRQ/DREQ-driven cartridge DMA is
  **skip** — no commercial cart uses it, review-a A18.)
- **See also:** [cartridge-saves-rtc-peripherals.md](cartridge-saves-rtc-peripherals.md).

---

**Already at SOTA in this partition (credit, per review-a §B):** the DMA
undrivable-source open bus (`<0x02000000` leaves the prior latch; 16-bit read
mirrors into both halves; destination low bit selects the half — correct, and
per-channel); video-capture DMA3 on HBlank of scanlines 2–161 (VCOUNT=2/162);
DMA priority preemption 0→3 per-word with `m_activeChannel` charging S vs N; the
timer prescaler as a real per-cycle state machine with same-cycle cascade ripple
and emergent startup delay; and the two-stage IE/IF/IME synchronizer. The open
items are the DMA-lingering CPU open bus, the I/O read-mask grind, and cheap
`test-first` audits that ride the hw-test corpus.

**Calibration (review-a C4/C6):** the mGBA `Timing` score must not be
cross-compared across emulators or suite versions — our itemized 1460/2020 is not
on the same scale as a table's "mGBA 0.9.3 Timing 1708/2020." The AGS
"first/only to pass" framing is stale even within the digest set (NBA, MiSTer,
Nintendo's official emulator, and GameBeanAdvance all pass) — our 38/38 stands on
its own (real BIOS), we're in a small club, not alone.
