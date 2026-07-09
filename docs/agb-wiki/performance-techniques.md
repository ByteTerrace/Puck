# Performance Techniques

The core is already at the interpreter SOTA — function-pointer dispatch tables,
the `StepClocks` quiescent-span collapse, and an event-queue scheduler that is
*specifically* the correct architecture at fleet scale. So the perf arc is about
the many-instance fleet, not single-instance speed, and the standing rule is that
nothing here may cost determinism: the fastest deterministic-safe answer to
threaded rendering, a JIT, and a heuristic idle-loop detector is "don't."

Provenance: `digest-5` (perf gatherer), `digest-6` (architecture gatherer),
`review-c` §A/§C (deep review), with credit facts from `digest-0`.

---

### Function-pointer dispatch tables

- **Source:** emudev.org, *Writing a Cached Interpreter*,
  https://emudev.org/2021/01/31/cached-interpreter.html ; NanoBoyAdvance
  (DeepWiki), https://deepwiki.com/nba-emu/NanoBoyAdvance ; *C# Function Pointers
  for High-Performance Scenarios*,
  https://medium.com/@KeyurRamoliya/c-function-pointers-for-high-performance-scenarios-387c5e91bde8.
- **Finding:** decode the relevant opcode bits into a flattened function-pointer
  handler table at compile time (ARM: bits 27-20 + 7-4 → 4096 entries; Thumb: top
  byte → 256), eliminating runtime decode branching. In C#, `delegate*<T>`
  unmanaged function pointers compile to `calli` with no delegate allocation / GC
  pressure — but can only target `static` methods, so handler state must go
  through an explicit CPU-state ref parameter.
- **Determinism fit:** N/A — deterministic.
- **Puck status: already at SOTA.** The core uses precomputed
  `delegate*<Arm7Tdmi, uint, void>[]` static dispatch tables (ARM 4096 / Thumb
  256), zero virtual dispatch, zero delegate-invoke — exactly the `calli`/zero-alloc
  form and the repo devirtualization doctrine. **Verdict (review-c B2): already at
  SOTA** — no incremental work.
- **See also:** the cached interpreter below.

### Cached (block-linking) interpreter

- **Source:** emudev.org, *Writing a Cached Interpreter*,
  https://emudev.org/2021/01/31/cached-interpreter.html ; *Common Dynarec
  Optimizations*, https://emudev.org/2021/02/01/Dynarec.html.
- **Finding:** decode consecutive ops once into cached decoded-handler blocks and
  re-execute on hits; non-writable ROM/BIOS blocks cache permanently, writable
  IWRAM is invalidated via a per-256-byte-page touch map. The author reports
  10–20% across most games, up to 50% on ROM-heavy titles, and explicitly chose it
  *over* JIT to preserve fine scheduler/IRQ-line granularity — a block "cannot
  check the scheduler mid-block."
- **Determinism fit:** compatible if invalidation is exact and the cache is a pure
  derived accelerator that never changes observable timing; blocks must still yield
  at the same cycle boundaries as our per-cycle `StepClocks` lockstep, which caps
  the speedup.
- **Puck status: partial — most of the win already captured.** Our static
  function-pointer dispatch already eliminates the decode-branching the cache
  targets; what we lack is block-*linking* (skipping re-fetch + per-instruction
  dispatch re-resolution). **Verdict (review-c A7 / survey #20): test-first**
  (L–XL — cache structure + IWRAM invalidation + preserving per-cycle timing
  simultaneously is the hard part) — benchmark a prototype against our *actual*
  table-dispatch baseline, not a strawman re-decoder. Fastmem and the StepClocks
  fast path are likely better $/effort first.
- **Calibration (review-c D2): the "10–50%" is vs a naïve re-decode-every-instruction
  interpreter, which we don't have.** Do not cite it as our expected speedup — the
  *incremental* gain over our table dispatch is unquantified by any source. (The
  same author rejected JIT for GBA-class timing precision, reinforcing our
  per-cycle lockstep choice.)
- **See also:** dispatch tables above, the JIT rejection below.

### JIT / dynarec — off the path

- **Source:** emudev.org, *Common Dynarec Optimizations*,
  https://emudev.org/2021/02/01/Dynarec.html ; SkyEmu README,
  https://github.com/skylersaleh/SkyEmu/blob/dev/README.md.
- **Finding:** no mainstream GBA emulator ships a production dynarec by default —
  mGBA, NanoBoyAdvance, SkyEmu, and ares all run interpreters or cached
  interpreters — precisely because a JIT batching instructions per block cannot
  check the scheduler mid-block without reintroducing per-instruction overhead.
  Generalizable dynarec tricks worth knowing conceptually: indirect-branch flat
  lookup tables, static/self-modifying block linking, page-protection vs
  bitmap vs per-block-checksum invalidation.
- **Determinism fit:** the per-block batching tension is the same one a
  deterministic interpreter faces — the JIT's determinism/accuracy cost generalizes
  directly to our no-float constraint.
- **Puck status: not pursued — deliberately.** **Verdict: skip** — our per-cycle
  lockstep is the opposite philosophy; the field's own accuracy leaders reject JIT
  for this exact reason.
- **See also:** the cached interpreter above.

### Scheduler design: event queue vs cothreads

- **Source:** mgba `src/core/timing.c`,
  https://github.com/mgba-emu/mgba/blob/master/src/core/timing.c ; mGBA blog,
  *Emulation Accuracy, Speed, and Optimization*,
  https://mgba.io/2017/04/30/emulation-accuracy/ ; higan/ares cothread discussion.
- **Finding:** mGBA's `mTiming` is a sorted singly-linked event list (O(n) insert,
  cheap because few events pend: PPU H/V-blank, 4 timers, DMA, serial) with a dual
  `root`/`reroot` list to handle events that reschedule during their own callback.
  The higan/ares alternative runs each device as a cooperative thread (cothread/
  libco) yielding at cycle boundaries — clean per-component code at
  context-switch cost. Critically, cothread-per-component *multiplies* OS/fiber
  overhead **per instance**, which argues for the event-queue model at fleet scale.
- **Determinism fit:** the event-queue model is deterministic; cothread scheduling
  ordering is a determinism risk if not carefully pinned.
- **Puck status: already at SOTA — the right choice for a fleet.** `AgbScheduler`
  is a structural twin of `mTiming` (sorted singly-linked `Event{When, Callback}`).
  We are on the fleet-optimal side of this axis by design. **Verdict (review-c B1):
  already at SOTA.**
- **Calibration (review-c D5/D7):** mGBA's list is O(n) insert — fine at our event
  counts, revisit vs a heap/timer-wheel only if a future design pends many
  simultaneous events. The cothread-vs-event-queue conclusion rests on secondary
  synthesis (byuu's canonical scheduler article was unreachable in the research
  pass); it aligns with first principles (per-instance fiber overhead) but the
  primary source is unverified.
- **See also:** the fleet pool below.

### Deterministic idle-skipping vs a heuristic idle-loop DB

- **Source:** mGBA 0.2.0 / 0.4.0 release notes, https://mgba.io/2015/04/03/mgba-0.2.0/ ,
  https://mgba.io/2016/02/02/mgba-0.4.0/ ; GBAtemp, *GBA idle loop patches*,
  https://gbatemp.net/threads/game-boy-advance-idle-loop-patches-i-e-speedhacks.396278/.
- **Finding:** mGBA ships "Remove Known" (a curated per-game idle-loop address DB),
  "Detect and Remove" (a runtime heuristic), and "Don't Remove" — framed as opt-in
  speed hacks, explicitly *not* on the accuracy-first path (a "loop" that looks
  idle can have an IRQ-driven exit or a timed poll).
- **Determinism fit:** a *generic* clock-collapse is safe (pure function of
  committed state); a *heuristic* DB is determinism-risky — a tunable N-threshold
  or per-build config breaks reproducibility.
- **Puck status: partial — and our version is arguably better.** The `StepClocks`
  quiescent-span collapse jumps straight to the next scheduled event when nothing
  can change IRQ/timer/pipeline state ("provably identical" to per-cycle stepping)
  — it banks the *safe core* of idle-skipping generically. **Verdict (review-c A11
  / survey): skip the heuristic detector; adopt later only if profiled** a small
  curated-address skip with a *fixed* constant in the override DB, never
  runtime-tunable. Don't trade determinism for a marginal speed hack the StepClocks
  fast path already covers.
- **See also:** the scheduler above,
  [cartridge-saves-rtc-peripherals.md](cartridge-saves-rtc-peripherals.md) (the
  override DB as the fixed-constant home).

### Software fastmem (region pointer table)

- **Source:** wheremyfoodat, *Software Fastmem*,
  https://wheremyfoodat.github.io/software-fastmem/ ; yuzu fastmem,
  https://yuzu-mirror.github.io/entry/yuzu-fastmem/ ; Dolphin PR #13768,
  https://github.com/dolphin-emu/dolphin/pull/13768.
- **Finding:** split the address space into pages with parallel read/write pointer
  arrays; RAM/BIOS/ROM map to a direct host pointer (`*(uint*)(ptr+off)`, no
  branch); I/O/unmapped route to a callback. GBA's coarse map (≤16 top-level
  regions, a switch on bits 27–24) means no full page table is needed. The
  *software* variant is the right fit for managed .NET — the hardware-MMU variant's
  ~3-instruction path needs SIGSEGV trapping, which would inject OS-timing
  nondeterminism and is impractical in .NET.
- **Determinism fit:** fully deterministic — no OS page-fault timing dependence.
- **Puck status: partial — a region switch exists, likely no branchless fast
  path.** The `ReadRegion`/`ChargeData` bus dispatches by region, but likely still
  goes through method calls + timing bookkeeping per access. **Verdict (review-c
  A10 / survey #9): test-first, leaning adopt** (M — back regions with
  pinned/native buffers + `unsafe` or `MemoryMarshal`) — the best pure-perf
  $/effort lever for single-instance *and* fleet. **But measure first:** the .NET
  JIT often elides `Span` bounds checks, so the delta to raw pointers may be small;
  don't reach for `unsafe` without a benchmark (see the `dotnet10-performance`
  skill). Keep timing/prefetch/PRAM-contention accounting on the slow path — only
  side-effect-free RAM/ROM reads take the fast path.
- **Calibration (review-c D6):** the "3-instruction fastmem" figure is for
  hardware-MMU trapping (Dolphin/yuzu) — not achievable *or desirable* in .NET;
  our target's cost is a branch, not 3 instructions.
- **See also:** the fleet pool below.

### In-process fleet with a shared work-stealing pool

- **Source:** Farama stable-retro, https://github.com/Farama-Foundation/stable-retro ;
  CuLE, https://www.researchgate.net/publication/334603144 ; EmuRust / EnvPool
  (both low-confidence — see calibration).
- **Finding:** advance N independent deterministic machines from a shared
  work-stealing pool, each single-threaded internally, zero IPC — vs OS-process-
  per-instance (stable-retro) or thread-per-instance. EmuRust reports ~1.5× over
  one-thread-per-instance purely from better core utilization; the generalizable
  lesson is that process-per-instance leaves large speedups on the table, and (per
  CuLE) branch-heavy CPU-bound work — not I/O — is the fleet bottleneck.
- **Determinism fit:** fully compatible — parallelism is *across* independent
  machines, never *within* one; no state crosses machine boundaries, so
  `Parallel.For`/work-stealing over instances is safe by construction.
- **Puck status: not yet built — the core is the right substrate.** The core is a
  sealed, allocation-light per-instance state machine; the fleet-scheduling layer
  is not yet built. **Verdict (review-c A9 / survey #14): adopt later** (M,
  engine-level orchestration not core changes) — align with the machine-fleet plan
  already in the repo; our event queue is already the fleet-optimal substrate.
- **Calibration (review-c D1):** EmuRust's "1.5×" is search-synthesized with no
  stable primary URL — directional, not load-bearing. EnvPool's "1M fps" is a C++
  vectorized result, and CuLE's 40–190M frames/hr is GPU-batched Atari — neither is
  a C#/.NET target.
- **See also:** [determinism-savestate-replay.md](determinism-savestate-replay.md)
  (two-instance runahead rides this).

### Deliberately off the fleet path: threaded PPU/APU

- **Source:** Dolphin Progress Report May/June 2022,
  https://dolphin-emu.org/blog/2022/07/07/dolphin-progress-report-may-and-june-2022/ ;
  DSHBA, https://github.com/DenSinH/DSHBA.
- **Finding:** Dolphin's Dual Core (CPU + GPU-command threads) cannot be made
  deterministic without erasing the speedup — "fake-completion" works for ~half the
  library and is ~20% slower, "SyncGPU" is often *slower than single-core*;
  Dolphin's own verdict is "fixing Dual Core just isn't on the table." GPU-side PPU
  (DSHBA) hits a blend-layer wall — GBA's top/bottom-pair blending doesn't map onto
  standard GPU blend hardware, forcing a two-render composite.
- **Determinism fit:** threaded simulation breaks the single-core determinism story
  — a strong argument for keeping PPU/APU on the CPU's deterministic tick.
- **Puck status: correctly kept off.** **Verdict (review-c §C): skip** threaded
  PPU/APU, the cothread scheduler (per-instance fiber overhead), and GPU-side PPU.
  Our PPU is a CPU rasterizer producing a plain framebuffer in emulated memory, so
  the savestate has no GPU-state problem either (see
  [determinism-savestate-replay.md](determinism-savestate-replay.md)).
- **See also:** the scheduler above, the fleet pool above.

---

**Fleet-throughput priority ordering (the partition headline, review-c §C):**
(1) software fastmem (test-first — hottest per-access path); (2) the in-process
work-stealing pool (~1.5× from utilization, our event queue is the right
substrate); (3) savestate (unlocks two-instance runahead, rewind, rollback); (4)
hash-divergence + BIOS pre-flight gate (cheap fleet-wide verification, landed this
arc); (5) cached interpreter (test-first only — may not beat our table dispatch).

**Already at SOTA in this partition (credit, per review-c §B):** function-pointer
dispatch tables; the event-queue scheduler (fleet-optimal by design); the
`StepClocks` quiescent-span collapse (lazy catch-up done deterministically, and
the safe form of idle-skipping); no-float/no-RNG discipline (what makes savestate
complete-by-construction); dual live co-simulation vs mGBA + ares; AGS 38/38 with
real BIOS; and per-cycle lockstep beyond mGBA's cycle-*count* tier while keeping
the event-queue perf — a genuinely strong architectural position.
