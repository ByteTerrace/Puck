# CPU Pipeline, Prefetch, and Waitstates

The ARM7TDMI runs a 3-stage fetch/decode/execute pipeline over a 16-bit
cartridge bus, so almost every accuracy question here is a *timing* question:
how many sequential (S), non-sequential (N), and internal (I) cycles an
instruction is billed, how the Game Pak prefetch unit races ahead when the CPU
isn't on the bus, and what garbage value an invalid read returns. Puck's core
already ships the hard parts — the prefetch FIFO with idle-only fill, per-mirror
WAITCNT tables, and multiply early-termination — verified on the mGBA `Timing`
suite and bit-exact against ares per co-sim. The open items are two known-hard
reverse-engineering details and a cluster of cheap audits.

Provenance: `digest-1` (gatherer), `review-a` (deep review), with credit facts
from `digest-0` (implementation sweep).

---

### The 8-halfword Game Pak prefetch FIFO

- **Source:** GBATEK, *GBA GamePak Prefetch*,
  https://problemkaputt.de/gbatek-gba-gamepak-prefetch.htm ; mGBA blog, *Cycle
  Counting, Memory Stalls, Prefetch and Other Pitfalls*,
  https://mgba.io/2015/06/27/cycle-counting-prefetch/ ; ares release notes,
  https://github.com/ares-emulator/ares/releases.
- **Finding:** an 8-entry, 16-bit (halfword) FIFO that holds *only* opcodes
  fetched from Game Pak ROM. It fills only when the cartridge bus is otherwise
  idle — during any I cycle, or during an N/S cycle that doesn't touch ROM
  (barrel-shift-by-register, multiply, load/store address-generation). A hit
  costs 0 additional wait states; a branch or any PC-changing instruction
  invalidates and refills it from the new PC (ares' changelog calls out
  "resetting the prefetch buffer on ROM accesses from the CPU"). Endrift's
  calibration is worth quoting: mGBA deliberately does *not* attempt true
  cycle-accurate prefetch, and warns "it is a common misconception that mGBA is
  now or will become cycle accurate" — fixing it would need "a major rewrite."
- **Determinism fit:** pure integer cycle accounting. Deterministic by
  construction.
- **Puck status: already at SOTA.** `AgbBus` models the full 8-halfword FIFO
  (`m_prefetchSlots`, `PrefetchStep`/`Sync`/`Read`/`Reset`), fills only while the
  CPU isn't on the ROM bus, and — critically — a ROM-bus miss advances the clock
  *without* filling the buffer. An earlier over-fill bug (buffer filled on a
  miss) caused false speed-ups in tight ROM loops and was found and fixed; that
  is the exact false-speedup trap the mGBA blog warns about. Branch invalidation
  is `PrefetchReset`. This is the mGBA-blog-hard feature done properly. Disable
  via `PUCK_NO_PREFETCH=1` for A/B testing.
- **See also:** [dma-timers-interrupts-open-bus.md](dma-timers-interrupts-open-bus.md)
  (DMA forces the following fetch non-sequential), the prefetch-disable anomaly
  below.

### S / N / I instruction cycle attribution

- **Source:** GBATEK, *ARM CPU Instruction Cycle Times*,
  https://problemkaputt.de/gbatek-arm-cpu-instruction-cycle-times.htm.
- **Finding:** the canonical cycle table — ALU 1S (+1S+1N if Rd=R15, +1I if the
  shift amount is register-sourced); LDR 1S+1N+1I (+1S+1N if loaded into R15);
  STR 2N; LDM nS+1N+1I (+1S+1N if R15 in the list); STM (n-1)S+2N; B/BL 2S+1N;
  THUMB BL 3S+1N; SWI/exception entry 2S+1N; a condition-failed instruction still
  costs 1S. The load-bearing rule for prefetch: an I cycle doesn't hold the
  address bus, so the *following* bus access can't continue a sequential run and
  is billed 1N.
- **Determinism fit:** integer — compatible.
- **Puck status: already at SOTA.** The explicit 3-stage pipeline with lazy
  refill charges the refill cost to the consuming instruction; R15-visible-as-PC
  offset falls out of pipeline-slot bookkeeping rather than a hardcoded constant.
- **See also:** the prefetch FIFO above, multiply timing below.

### Multiply timing (early termination) and the Booth carry-flag frontier

- **Source:** GBATEK, *ARM Opcodes — Multiply and Multiply-Accumulate*,
  http://problemkaputt.de/gbatek-arm-opcodes-multiply-and-multiply-accumulate-mul-mla.htm ;
  bmchtech, *Solving the Mystery of ARM7TDMI Multiply Carry Flag*,
  https://bmchtech.github.io/post/multiply/.
- **Finding:** MUL costs `1S + m·I`; the multiplier processes Rs in 8-bit
  Booth-recoded chunks and terminates early once the remaining upper bits are
  uniform (all-0 unsigned, all-0/all-1 signed): bits[31:8] uniform → m=1, else
  [31:16] → m=2, else [31:24] → m=3, else m=4. The subtle trap: the ARM TRM calls
  the C flag after MUL/MLA "meaningless," but real silicon sets it
  *deterministically* from the barrel shifter's carry-out on the *final* Booth
  iteration — i.e. a function of how many early-termination iterations ran. An
  emulator that treats C as undefined diverges from hardware on long multiplies.
- **Determinism fit:** pure integer bit derivation — compatible.
- **Puck status: timing SOTA, carry-flag value is the named open frontier.** The
  per-cycle byte-scan implements the `m` rule with the documented
  signed-vs-unsigned asymmetry (signed long multiplies get the all-ones
  sign-extension shortcut, unsigned don't), verified +92 on mGBA `Timing`. The
  *carry-flag value* on Booth-carry cases is still wrong — this is the mGBA
  `MulLong` 52/72 gap named in the Post README. **Verdict (review-a A2): adopt
  later** (S–M, `Arm7Tdmi.Alu.cs`) — a bounded ≈+20-score win, but a known-hard
  RE detail that needs careful recoding against a known-accurate core; co-sim vs
  ares confirms bit-exactness. Not rushed.
- **See also:** [test-roms-and-evidence.md](test-roms-and-evidence.md) (mGBA
  `MulLong`/`Carry` suites).

### WAITCNT and per-mirror waitstates

- **Source:** GBATEK, *GBA System Control* / *GamePak Prefetch*,
  https://problemkaputt.de/gbatek-gba-system-control.htm.
- **Finding:** WAITCNT (`0x4000204`) sets SRAM wait; WS0/WS1/WS2 first(N)/second(S)
  access times independently; the prefetch-enable bit (14); and a read-only Game
  Pak type bit (15) — the same bit GBATEK once mislabeled writable, a bug every
  major emulator inherited (see [emulator-landscape.md](emulator-landscape.md)).
  The same physical ROM is mirrored at `0x08000000`/`0x0A000000`/`0x0C000000`,
  each independently configurable, so a game can access identical bytes through a
  different mirror for a different N/S profile — this must be modeled
  per-address-range, not globally. Actual access time is `1 + waitstates` clocks;
  F-Zero and Super Mario Advance reconfigure to 3,1 with prefetch on.
- **Determinism fit:** integer waitstate table recomputed on WAITCNT write —
  compatible.
- **Puck status: already at SOTA.** `UpdateWaitControl` decodes N/S per ROM
  mirror plus SRAM wait and the prefetch bit from hardware tables; `RomCycles`
  computes N+S+2 / 2S+2 on the 16-bit-bus model, and a sequential access landing
  on a 128 KiB page boundary re-charges as non-sequential (`RomBurstAccess`).
- **See also:** the fast-EWRAM register below.

### The undocumented fast-EWRAM waitstate register (`0x4000800`)

- **Source:** GBATEK, *GBA System Control*,
  https://problemkaputt.de/gbatek-gba-system-control.htm ; mGBA issue #1276,
  https://github.com/mgba-emu/mgba/issues/1276.
- **Finding:** a 32-bit R/W register mirrored every 64 KB across I/O space,
  undocumented (reverse-engineered). Bits 24–27 select EWRAM (256 K external
  WRAM) waitstates: default `0Dh` → 2 waitstates; `0Eh` → 1 waitstate works on
  GBA/SP but **hangs a GBA Micro** (a model-specific lockup). Bit 0 disables
  EWRAM; bit 5 gates the 256 K region vs the 32 K mirror. mGBA still tracks this
  as an open gap.
- **Determinism fit:** integer waitstate table — compatible. (The Micro lockup is
  a specific-SKU hardware detail; we would model the *timing*, not the lockup.)
- **Puck status: not implemented — EWRAM timing appears fixed.** **Verdict
  (review-a A8): adopt later** (M, `AgbBus.cs`, mirror the WAITCNT
  `UpdateWaitControl` pattern). Even mGBA hasn't shipped it; low compatibility
  value. Do it when chasing the last mGBA `Timing`/`Memory` points, not before.
- **See also:** WAITCNT above.

### The prefetch-disable timing anomaly (WAITCNT bit 14 = 0)

- **Source:** GBATEK, *GBA GamePak Prefetch*,
  https://problemkaputt.de/gbatek-gba-gamepak-prefetch.htm ; jsmolka gba-tests,
  https://github.com/jsmolka/gba-tests.
- **Finding:** with prefetch disabled, ROM opcodes that would take only internal
  cycles (MUL, register-specified shift — ops that don't change PC) get billed a
  non-sequential (1N) fetch instead of the sequential (1S) they'd get with the
  buffer pre-staging them. A genuine silicon anomaly, not an emulator artifact —
  it is the same underlying reason I cycles force the next access non-sequential.
- **Determinism fit:** integer cycle accounting — compatible.
- **Puck status: partial / likely not special-cased.** The core decodes the
  prefetch-enable bit and models the FIFO, but nothing signals "an internal-only
  ROM op with prefetch off should re-bill its next fetch N." **Verdict (review-a
  A1): test-first** (S–M, `AgbBus.RomCycles`/`PrefetchStep` + an ALU/shift-path
  signal) — pull the jsmolka waitstate-with-prefetch-off ROMs first; only patch
  if we fail. Commercial games run with prefetch *on*, so this is low
  compatibility value but real test-score value.
- **See also:** [test-roms-and-evidence.md](test-roms-and-evidence.md).

### ARM open bus (`[PC+8]`)

- **Source:** GBATEK, *Unpredictable Things*, https://problemkaputt.de/gbatek.htm.
- **Finding:** in ARM mode an invalid/unmapped read returns the word two
  instructions ahead — `[PC+8]`, the pipeline's own prefetch value (ARM's 3-stage
  pipeline is always fetching PC+8 relative to the executing instruction).
- **Determinism fit:** integer latch — compatible.
- **Puck status: partial (coarse).** Open bus is `m_openBus = last ReadRegion
  result` plus I/O-register `OpenBusHalf` shifting; the coarse "recently
  prefetched opcode" idea is present. See the THUMB entry for what's missing.
- **See also:** THUMB region open bus below,
  [dma-timers-interrupts-open-bus.md](dma-timers-interrupts-open-bus.md) (DMA has
  its own open-bus path).

### Region- and alignment-dependent THUMB open bus

- **Source:** GBATEK, *Unpredictable Things*, https://problemkaputt.de/gbatek.htm ;
  NGEMU, *GBA Open Bus*, https://www.ngemu.com/threads/gba-open-bus.170809/
  (forum lore — flagged).
- **Finding:** in THUMB the echoed value is two 16-bit halves whose source
  offsets differ by region: Main RAM / Palette / VRAM / ROM → LSW=MSW=`[$+4]`;
  BIOS / OAM → `[$+4]`/`[$+6]` (aligned) or `[$+2]`/`[$+4]` (unaligned); 32 K
  on-chip WRAM → `[$+4]`/`OldHI` (aligned) or `OldLO`/`[$+4]` (unaligned), where
  `OldHI`/`OldLO` are the *previous* fetch's halves — WRAM open bus literally has
  one-instruction memory because of how that bus is wired. The single easiest
  open-bus corner to under-model. Historical canary: Mega Man Battle Network 4
  Blue Moon.
- **Determinism fit:** integer latches + pipeline PC bookkeeping — compatible.
- **Puck status: mostly not implemented.** The coarse `m_openBus` does *not*
  compose the per-region LSW/MSW halves or the WRAM `OldHI`/`OldLO` latch. A
  meaningful chunk of our mGBA `I/O` 81/130 deficit likely lives here. **Verdict
  (review-a A9): adopt later** (M, `AgbBus.cs` `ReadRegion` + `Arm7Tdmi` pipeline
  PC visibility) — incremental against jsmolka `memory`/`io-read` + mGBA `I/O`.
- **See also:** [dma-timers-interrupts-open-bus.md](dma-timers-interrupts-open-bus.md)
  (I/O-register read masks, a *separate* axis from open bus).

### Structuring an interpreter to get timing right cheaply

- **Source:** mGBA blog, *Cycle Counting…*,
  https://mgba.io/2015/06/27/cycle-counting-prefetch/ ; NanoBoyAdvance,
  https://github.com/nba-emu/NanoBoyAdvance ; ares release notes.
- **Finding:** two archetypes. mGBA's "cycle-count accuracy" bills each operation
  the correct *total* cycles but executes units atomically rather than truly
  interleaved — cheap and mostly right. NanoBoyAdvance's look-ahead prefetch
  classification evaluates the *upcoming* fetch's sequentiality proactively (the
  S/N attribution of instruction N+1's fetch depends on what N did to the bus) and
  applies a one-cycle "last ROM prefetch cycle" penalty for code racing a still-
  filling queue. The general pattern: track a little persistent bus state across
  instruction boundaries (prefetch FIFO contents, prior-cycle sequentiality,
  per-WAITCNT-region N/S tables recomputed only on write) and classify each
  fetch against that state machine, keeping the hot path a table lookup.
- **Determinism fit:** N/A (design guidance).
- **Puck status: surveyed, not deep-reviewed (as technique); our own model is
  ares-side.** The core runs the ares-architecture per-cycle model with the
  `StepClocks` quiescent-span collapse — true cycle accuracy where it matters,
  with a fleet-friendly event queue instead of ares's cothreads. See
  [performance-techniques.md](performance-techniques.md). **Calibration
  (review-a C1):** NanoBoyAdvance issue #37 ("prefetch of next instruction")
  returned 404 on fetch; its exact one-cycle penalty is inferred from the issue
  title + release notes and must not be encoded as ground truth — co-sim vs
  ares/mGBA is the arbiter. digest-1 §6's "general interpreter pattern" is the
  gatherer's own synthesis, flagged as inference, fine as guidance not spec.
- **See also:** [performance-techniques.md](performance-techniques.md),
  [emulator-landscape.md](emulator-landscape.md).

---

**Already at SOTA in this partition (credit, per review-a §B):** the 8-halfword
prefetch FIFO with idle-only fill and branch-invalidation reset; per-mirror
WAITCNT N/S tables with 128 KiB-boundary sequential re-charge; the multiply
early-termination byte-scan with the signed/unsigned asymmetry (only the carry
*value* remains); open-bus-from-prefetch for BIOS data reads (`ReadBios` returns
the latched last-BIOS opcode outside BIOS execution — the behavior most cores get
wrong); and the ares-architecture per-cycle model, co-sim-confirmed bit-exact vs
ares with AGS 38/38 behind it. The open items are two known-hard RE details (mul
carry, THUMB region open bus) and cheap audits that ride the hw-test corpus.
