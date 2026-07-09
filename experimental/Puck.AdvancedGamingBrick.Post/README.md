# Puck.AdvancedGamingBrick.Post — the GBA machine's power-on self-test (and accuracy handoff)

This project is two things in one:

1. **The POST** — the Game Boy Advance machine's *power-on self-test*, an ordered battery of
   self-checking stages that is the primary way the machine is validated. It runs anywhere, exits
   `0`/`1`/`2`, and writes a report. This mirrors `Puck.HumbleGamingBrick.Post` (the GB/GBC POST).
2. **The accuracy handoff** — the mGBA/ARES co-simulator tooling and single-ROM inspectors that
   drive the accuracy frontier, reachable as diagnostic CLI modes. Pick that work up from §5 onward
   on any machine.

The core itself lives in `experimental/Puck.AdvancedGamingBrick` (all six subsystems built: ARM7TDMI,
bus/memory-map, IRQ/timers/DMA, replacement BIOS, full PPU, full APU). This project drives it.

---

## 1. The POST battery

Run it with no arguments; it runs the whole battery and exits with the folded verdict:

```sh
dotnet run --project experimental/Puck.AdvancedGamingBrick.Post -c Release
```

**Exit code** — `0` all stages passed or skipped · `1` a check failed (a correctness divergence) ·
`2` a stage could not run (an exception / missing prerequisite). The report is echoed to the console
and written to `artifacts/gba-post/post-report.txt`.

**Tiers** (run in order; `--tier A|B|C` selects one, `--filter <substr>` selects by stage name):

| Tier | What | Needs |
|------|------|-------|
| **A** | Core self-tests — run anywhere on hand-assembled vectors + a synthetic cartridge | nothing |
| **B** | Reference-ROM behavioural checks | the corpus / a real BIOS (skips when absent) |
| **C** | Cross-machine link determinism (two-console SIO) | reserved — no stages yet (null link) |

**Stages:**

| Tier | Stage | Verdict basis |
|------|-------|---------------|
| A | `smoke` | 41 hand-assembled CPU/PPU/APU/IRQ/DMA/DI vectors (the BIOS-IRQ one skips without a real BIOS) |
| A | `determinism` | two independently-built machines are register- **and** framebuffer-identical after 200 frames |
| A | `save-round-trip` | the cartridge `.sav` export/import round-trip + dirty-flag + wrong-size rejection |
| A | `throughput` | measurement — fps / ×realtime / Mcycle-per-second (never fails) |
| B | `jsmolka-cpu` / `-save` / `-misc` | jsmolka gba-tests `r12` verdict register (arm/thumb/memory, save×4, nes) |
| B | `fuzzarm` | FuzzARM randomized coverage — no EWRAM failure marker |
| B | `render-hash` | deterministic FNV-1a framebuffer floors (ppu demos + commercial games) |
| B | `mgba-suite` | measurement — mGBA test-suite score (accuracy frontier, not a gate) |
| B | `ags` | measurement — AGS aging-cartridge cell count (one cell needs a link partner; not a gate) |

**Knobs & environment** (a CLI flag always wins over its environment variable):

| Knob | Env var | Purpose |
|------|---------|---------|
| `--artifacts <dir>` | — | where the report is written (default `artifacts/gba-post`) |
| `--tier A\|B\|C` | — | run only one tier |
| `--filter <substr>` | — | run only stages whose name contains the substring |
| `--roms <dir>` | `PUCK_GBA_TESTROMS` | the jsmolka gba-tests corpus root (FuzzARM is found in a sibling `FuzzARM/` dir) |
| `--games <dir>` | `PUCK_GBA_GAMES` | commercial ROMs for the render-hash floors |
| — | `PUCK_GBA_BIOS` | a 16 KiB BIOS image (use the REAL BIOS — see §3); without it the BIOS-dependent checks skip |
| — | `PUCK_GBA_MGBA_SUITE` | the mGBA test-suite ROM (`suite.gba`) |
| — | `PUCK_GBA_AGS` | the AGS aging-cartridge dump (only **TCHK10** matches the result patch) |

**Determinism note:** the GBA core exposes no whole-machine snapshot/restore/fork, so — unlike the
GB/GBC POST — there are no `snapshot-round-trip` / `fork-determinism` stages; `determinism` compares
the observable state (register file + framebuffer) of two machines instead. Adding a save-state layer
to the core would let those stages be reinstated.

---

## 2. Current status

- **All six subsystems complete** (ARM7TDMI, bus/memory-map, IRQ/timers/DMA, PPU, APU, SIO/serial)
  on the ares-architecture per-cycle model (§6). Golden Sun renders correctly in colour.
- **AGS aging cartridge: 38 / 38** with the real BIOS. (Historical handoffs cite 35/35 or 37/38 —
  those predate the real-BIOS switch and the SIO completion work.)
- **Pokémon Emerald boots** through the real BIOS into the Game Freak intro (DISPCNT=0x9802). The
  root causes were functional, not timing: KEYINPUT had to be **read-only** (a boot-time I/O clear
  stored 0 = "all buttons pressed" = the A+B+Start+Select soft-reset combo, looping the boot), and
  the SIO Normal-32 **master transfer had to complete** across the external→internal clock switch
  (the wireless-adapter probe holds the start bit through the switch).
- **mGBA suite** (measurement stage): Memory 1519/1552 · I/O 81/130 · Timing 1460/2020 ·
  Timer count-up 630/936 · Timer IRQ 54/90 · Shifter 140/140 ✓ · Carry 93/93 ✓ · MulLong 52/72 ·
  BIOS math 615/615 ✓ · DMA 1244/1244 ✓ · SIO R/W 85/90 · SIO timing 0/8 · Misc 3/12. See §7.E for
  the frontier analysis.
- **jsmolka** `arm`/`thumb`/`memory`/`save`×4/`nes` + **FuzzARM**: pass. **41 smoke vectors** +
  **render-hash floors** (jsmolka ppu demos, Golden Sun, AGS menu): green. Full POST battery 11/11.
- **Saves**: SRAM, **Flash 64K/128K** (full command/bank/erase state machine + chip-ID),
  **EEPROM** (full serial read/write protocol, 6- and 14-bit auto-detected bus widths, DMA-gated so
  it never shadows the 0x0D ROM mirror), and the **Seiko S-3511A RTC over GPIO**.
- **Hardware-parity pass**: APU per-channel L/R panning + master volume + Direct Sound L/R +
  **SOUNDBIAS** + **2-bank 64-sample wave** + PSG register read-back; **SIO/serial** (no-cable
  defaults, mode-gated register access, level-triggered start re-evaluation); **HALT/STOP +
  POSTFLG/HALTCNT**; **BIOS open-bus read protection** + general open bus; **OBJ (sprite) mosaic**;
  PPU read masks (unused register bits drive 0).

---

## 3. External assets (must be supplied per machine)

None of these are committed. Re-provide them on a new machine:

| Asset | Used for | Pointer |
|-------|----------|---------|
| **Real GBA BIOS** (16 KiB; local copy `D:\Source\ByteTerrace\Temp\GBA_bios.rom`) | `PUCK_GBA_BIOS` — required for AGS 38/38, Emerald, and any cycle-parity work | user-supplied |
| Cult-of-GBA replacement BIOS (`bios.bin`, 16 KiB, MIT) | built-in fallback (`ReplacementBios`); fine for Tier A, NOT for cycle parity — a wrong-BIOS run once burned a whole session on phantom "cycle drift" | github.com/Cult-of-GBA/BIOS |
| jsmolka `gba-tests` | `--roms` / `PUCK_GBA_TESTROMS` | github.com/jsmolka/gba-tests |
| `FuzzARM` (`ARM_Any.gba`, `THUMB_Any.gba`) | auto-found in a sibling `FuzzARM/` beside `--roms` | github.com/DenSinH/FuzzARM |
| mGBA test suite (`suite.gba`) | `PUCK_GBA_MGBA_SUITE` / `--mgba-suite` | github.com/mgba-emu/suite |
| AGS dump **`AGB_CHECKER_TCHK10.gba`** (md5 `9f74b2ad1d33e08e8a570ffe4564cbc3`) | `PUCK_GBA_AGS` / `--ags` | user-supplied |
| Commercial ROMs (Golden Sun, Pokémon Emerald) | `PUCK_GBA_GAMES` — render-hash floors, game testing | user-supplied |
| mGBA source + a C toolchain (CMake + MSVC/clang) | the co-sim oracle (§5) | github.com/mgba-emu/mgba |
| AGSTests decompilation | AGS subtest spec / flag meanings | github.com/DenSinH/AGSTests |

The harness reads paths from env or CLI flags; it skips cleanly when an asset is absent.

> **AGS note:** the AGS subtest result-flags are read headlessly by patching `TCHK10` in memory
> with the DenSinH "output results" patch (3 Thumb instrs at file offset `0xB20`, so each test
> writes its flags to address `0x04`) and capturing them via `TracingGbaBus`. `RunAgs` does this
> automatically. Only the **TCHK10** dump matches the patch offsets — the v7.1 AGS and TCHK30
> dumps do **not**.

---

## 4. Diagnostics (co-sim & single-ROM inspectors)

The accuracy-frontier tooling is retained as **diagnostic CLI modes**. When one of these flags is
present it runs that single mode and returns *instead of* the battery (`Diagnostics.TryRun`), so the
battery stays the default. Each takes a ROM path unless noted.

| Flag | Purpose |
|------|---------|
| `--oracle` | run the self-authored cycle-oracle probe battery: measured-vs-documented per probe. Two rows are self-checking gates (Direct Sound FIFO ring/playing model; per-channel DMA read latch); the DMA/timer/IRQ/halt cycle rows are honest our-harness measurements against the documented corpus targets (divergence recorded, not chased). The interrupt/halt probes need the retail BIOS (`PUCK_GBA_BIOS`) and skip without it. |
| `--save-test` | the save round-trip, standalone (self-contained; no ROM) |
| `--gen-rom <kind> <out.gba>` | hand-assemble a timer/IRQ micro-ROM (`timer-irq` \| `timer-irq-iwram` \| `cascade-irq` \| `ime-delay`) |
| `--ags <rom>` | run the AGS aging cartridge headlessly, print per-subtest pass/fail + timing diagnostics |
| `--mgba-suite <rom>` | run the menu-driven mGBA test suite headlessly, print the per-suite/per-subtest score |
| `--render <rom> <out.png> [steps]` | boot and dump a framebuffer PNG |
| `--render-hash <rom> <steps>` | print the deterministic FNV-1a frame hash (for capturing a floor) |
| `--trace-cycles <rom> <steps>` | per-instruction `(PC, cumulative-cycles, delta)` — diff vs the co-sim |
| `--pctrace <rom> <steps>` | per-instruction `PC + Thumb-flag + cycles`; **boots through the BIOS** (matches the co-sim's full-BIOS boot) |
| `--statetrace <rom> <steps>` | full per-instruction state `PC CPSR r0..r14 cycles`; diff registers vs `cosim … --statetrace` to find the first **functional** divergence |
| `--lockstep <rom> <steps> [direct]` | step Puck against `ares-cosim` in lockstep; halt at the first functional divergence, characterise cycle drift |
| `--iodump <rom> <steps>` | dump every I/O register halfword, to diff against `ares-cosim`'s `iodump` |
| `--probe <rom> <steps>` | dump GPRs, DISPCNT/DISPSTAT/VCOUNT, IE/IF/IME/WAITCNT, VRAM/palette occupancy — diagnose a blank-screen boot |
| `--emerald-trace <rom> <loHex> <hiHex> <count> [skip]` | trace instructions once the PC enters `[lo,hi)`, with the SIO/timer/IRQ registers the link probe reads |
| `--trace-crash <rom>` | report the first branch into unmapped memory |

Render-hash floors are deterministic; re-capture with `--render-hash` and update the `ExpectedHash`
constants in `PostStages.cs` whenever an *intended* timing/PPU change shifts a frame (confirm the
frame is still visually correct first).

**BIOS pre-flight guard.** The cycle-parity / co-sim diagnostics (`--lockstep`, `--statetrace`,
`--trace-cycles`) identify the loaded BIOS by SHA-1 and **refuse to run on a non-retail BIOS** — the
documented "phantom cycle drift" trap, where a session was once burned diffing cycles against the
replacement BIOS. Supply the retail image via `PUCK_GBA_BIOS`, or pass `--allow-replacement-bios` to
downgrade the refusal to a warning and proceed. The classification is also surfaced through
`AdvancedGamingBrickMachine.BiosIdentity` to the demo's `agb.status`.

---

## 5. The mGBA cycle co-simulator (the oracle)

The accuracy frontier is driven by diffing our per-instruction state/cycle streams against
reference emulators. TWO live oracles exist, sharing one trace format: the mGBA co-sim below
(`D:\Source\ByteTerrace\Temp\gba-cosim\cosim.exe`) and the ares co-sim (`ares-cosim.exe` in the same
dir, driven directly by `--lockstep`; `statediff.py` aligns any two statetrace streams). Remember
that **no emulator is hardware truth** — mGBA and ares both disagree with the mGBA suite's
hardware-derived expectations in places (e.g. the IRQ-recognition depth, §7.E) — so test-ROM
verdicts outrank any single reference. The harness lives outside the repo (built from mGBA source).

### Build it (once per machine)

mGBA core only — no frontend, no deps:

```sh
cmake -S mgba-src -B mgba-build -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release \
  -DLIBMGBA_ONLY=ON -DM_CORE_GB=OFF -DM_CORE_GBA=ON -DBUILD_STATIC=ON -DBUILD_SHARED=OFF \
  -DUSE_FFMPEG=OFF -DUSE_ZLIB=OFF -DUSE_PNG=OFF -DUSE_LIBZIP=OFF -DUSE_MINIZIP=OFF \
  -DUSE_SQLITE3=OFF -DUSE_ELF=OFF -DUSE_LUA=OFF -DUSE_JSON_C=OFF -DUSE_FREETYPE=OFF \
  -DUSE_LZMA=OFF -DUSE_DISCORD_RPC=OFF -DUSE_EPOXY=OFF -DUSE_EDITLINE=OFF -DUSE_PTHREADS=OFF
cmake --build mgba-build         # -> mgba.lib (a.k.a. libmgba)
```

`-DLIBMGBA_ONLY=ON` is the key flag — without it the Windows build hard-errors on the missing
epoxy/OpenGL module.

### The harness (`cosim.c`)

A small C program links `mgba.lib` and single-steps the GBA core, mirroring our `--pctrace`
output so the two streams diff line-for-line. A working copy is checked out at
`D:\Source\ByteTerrace\Temp\gba-cosim\cosim.c`. Usage: `cosim <rom> <steps> [bios]
[--probe|--pctrace|--statetrace]` (`--statetrace` = full `PC CPSR r0..r14 cycles` per instruction —
the register-diff stream the §7.A method depends on).

Build it from an MSVC dev shell (note the `/MD` and the extra Win32 link libs, both required):

```sh
cl /O2 /MD <defs> /I <mgba-src>/include cosim.c <mgba-build>/mgba.lib \
   ws2_32.lib advapi32.lib shell32.lib user32.lib ole32.lib shlwapi.lib /Fe:cosim.exe
```

- **`<defs>` must match the lib's build flags exactly** or `struct mCore`/`struct GBA` layouts
  differ and you get an instant access-violation. Pull them from `mgba-build/build.ninja`; for the
  §5 config they are `-DBUILD_STATIC -DENABLE_DIRECTORIES -DENABLE_VFS -DENABLE_VFS_FD
  -DHAVE_SETLOCALE -DHAVE_STRDUP -DM_CORE_GBA -DNOMINMAX -DWIN32_LEAN_AND_MEAN`. (`ENABLE_VFS`+
  `ENABLE_DIRECTORIES` add a `dirs` field to `struct mCore` — omit them and every offset shifts.)
- **Sequence that actually works** (each step below was a separate crash until added):
  `GBACoreCreate` → `core->init` → **`mCoreConfigInit(&core->config, "gba")`** (reset reads config
  tables — they must exist) → **`core->setVideoBuffer`** with a `baseVideoSize`-sized buffer (the
  software renderer derefs NULL otherwise) → set `core->opts.skipBios` → **`mCoreConfigSetValue(
  &core->config,"idleOptimization","ignore")` + `core->loadConfig`** → `loadROM`/`loadBIOS` →
  `reset` → `core->step`. Install a silent `mLogger` (`mLogSetDefaultLogger`) or mGBA floods stdout.
- Boots through the BIOS when one is supplied (`skipBios=0`), matching `--pctrace`. Pass **no bios**
  to direct-boot to `0x08000000` (matches our `--trace-cycles`), best for ROM-resident micro-ROMs.
- `--pctrace` prints `gprs[15] + Thumb-flag + cycles`. Our PC is a constant **+4** vs mGBA's (a
  pipeline-representation offset); subtract it and the streams align. Use the raw architectural PC.
- **CAUTION — cumulative cycles are NOT monotonic.** `mTimingGlobalTime` rebases to 0 on every
  frame reroot, so the absolute column resets repeatedly. **Compare per-instruction *deltas*, not
  the absolute count.** mGBA also attributes the *next* instruction's fetch to the *current* one,
  so single-instruction deltas are shifted by one vs ours — **compare closed-loop totals** (a tight
  loop's cycles/iteration) for an apples-to-apples number.
- `idleOptimization=ignore` is mandatory: without it mGBA fast-forwards busy-wait loops and the
  cycle clock freezes.

### Diffing workflow

1. Capture both: `cosim <rom> <N> <bios> --pctrace > mgba.txt` and
   `--pctrace <rom> <N> > ours.txt`.
2. Both boot through the BIOS, so they align after a 1-line reset-pipeline offset.
3. Walk the two PC streams; the first **sustained** divergence is the bug. For timing bugs,
   compare the cumulative-cycle column to quantify and localize the drift.

### Micro-ROM method

For an isolated timing question, hand-assemble a tiny ROM (`--gen-rom`) that sets WAITCNT, enables
timer 0 (÷1), runs the construct under test, then stores the timer value to EWRAM `0x02000000`. Run
it on both cores and compare that value. Existing examples (regenerate as needed): an IWRAM-resident
LDR-from-ROM loop, a ROM-resident prefetch-off loop, and a ROM-resident branch loop. This is how
the wait-state, timer, and prefetch models below were each validated to per-cycle parity.

---

## 6. The cycle-timing model (ares architecture; validated against mGBA, ares, AGS and the suites)

All in `experimental/Puck.AdvancedGamingBrick/GbaBus.cs` + `GbaTimerController.cs` +
`GbaInterruptController.cs` + `GbaCartridge.cs`. Timing "emerges from the bus accessors": every
fetch/transfer/idle computes its wait-state and funnels through **`GbaBus.StepClocks(n)`** — a
per-cycle loop that steps the IRQ synchronizer and the timer state machine every cycle and fires
scheduler events at their exact cycle (with a provably-identical fast path over quiescent spans).

- **Game-pak access** (`RomCycles`): every 16-bit halfword transfer costs its wait-state **plus a
  one-cycle bus base** — a sequential halfword is `S+1`, a non-sequential one `N+1`; a 32-bit
  access is two such transfers. SRAM data adds `+1` likewise.
- **Timers** (`RunCycle`/`StepLatch`/`ReloadLatch`): a per-cycle state machine — the prescaler is a
  mask of the global clock's low bits, cascades ripple synchronously in the same cycle, and writes
  commit through a 1-cycle latch (+ an enable-reload latch). The hardware start-up delay *emerges*
  from the latch pipeline; there is no tuned delay constant.
- **IRQ recognition**: IE/IF/IME are double-buffered ([1] = written, [0] = committed; shifted once
  per cycle into the `Synchronizer` line), and the CPU adds a 2-stage decode→execute recognition
  pipeline sampled at fetch. DMA stalls freeze the shift.
- **Game-pak prefetch buffer** (`Prefetch*`, WAITCNT bit 14): the 8-halfword FIFO runs ahead only
  while the CPU isn't on the ROM bus. A held opcode costs 1 cycle/halfword; a branch target pays
  full non-sequential and restarts the run-ahead (keyed on *address discontinuity*, not the N/S
  access type); a ROM-bus miss advances the clock but does **not** fill the buffer; a tight ROM
  loop sees no speed-up, matching hardware.
- **ROM burst** (`GbaCartridge.ReadRomBurst`): the cartridge's half-word page counter — a non-seq
  access latches the page, sequential accesses auto-increment it, and the burst ends at the last
  half-word of a 128 KiB page. Applied to DMA ROM reads (the fixed/decrement-source open-bus
  quirk); a sequential access landing on a page start re-charges as non-sequential.
- **Multiplies**: early-termination per multiplier byte — but unsigned long multiplies
  (UMULL/UMLAL) get **no all-ones shortcut**; only the signed variants treat all-ones as sign
  extension (worth +92 on the mGBA Timing suite).

Don't "tune" any of these to a single test number; re-validate a change with the micro-ROM method
(§5) and re-run the full POST + AGS + the mGBA suite.

---

## 7. Open items

### A. Pokémon Emerald boot — RESOLVED ✅

Emerald boots through the real BIOS into the Game Freak intro. The chain of root causes, in the
order they were peeled (each found with the §5 state-diff method — dump `--statetrace` from both
cores and diff the *architectural registers*, immune to PC-offset and count slips):

- **SOUNDBIAS power-on default** (`0x0200`) — the BIOS sound init derailed on a 0 read. Fixed.
- **SIO Normal-32 master completion** — the wireless-adapter probe (`AgbRFU_checkID`) starts a
  transfer while external-clocked, then switches to internal master *with the start bit held*; the
  edge-triggered start never re-evaluated, so no Serial IRQ ever fired. Fixed: re-evaluate on every
  SIOCNT write while the start bit is set.
- **KEYINPUT must be read-only** — the final root cause of the boot loop: a boot-time I/O clear
  wrote 0 into KEYINPUT and the bus stored it, so reads returned "all ten buttons pressed" — which
  is the A+B+Start+Select soft-reset combo, resetting the game every frame forever. Fixed
  (`case 0x130: return;`). The pokeemerald decompilation (built byte-matching with agbcc; its
  `pokeemerald.map` resolves any traced address to a source symbol) was the tool that cracked this.

### B. AGS `prefetch_buffer` subtest (the 35th cell) — FIXED & CONFIRMED ✅

The prefetch-**disabled** half read `0x2A` (42); hardware wants `0x33` (51) — our prefetch-off
ROM code-fetch was ~1 cycle/fetch light. **Cause found & fixed:** 16-bit game-pak accesses omitted
the per-transfer +1 bus base cycle that the 32-bit path already carried, so every Thumb ROM fetch
(and 16-bit ROM data read) under-counted by 1 (see §6). Verified per-instruction against the §5
co-sim (sequential fetch 2→3, self-branch loop 8→11, load+branch loop 22-total — all match mGBA,
prefetch on and off) **and confirmed on the real `TCHK10` aging cartridge**, with prefetch reads
`0x18` on / `0x33` off. Zero regression on the full suite. (The "35/35" count in old handoffs was
the pre-SIO harness; the full cartridge is now **38/38**.)

### C. EEPROM saves — IMPLEMENTED ✅

Full serial protocol in `GbaCartridge` (`ReadEeprom`/`WriteEeprom`): read (`11`) and write (`10`)
commands, 6-bit (512 B) and 14-bit (8 KiB) bus widths auto-detected from the first command's length,
64-bit blocks shifted MSB-first, 68-bit read replies (4 dummy + 64 data). The bus routes 0x0D…
to it **only for DMA accesses** (`m_dmaActive`), matching hardware — EEPROM is DMA-driven, and this
keeps test carts that merely embed the `EEPROM_V` string (the AGS aging cartridge has `EEPROM_V122`!)
from having their 0x0D ROM mirror hijacked. Untested against a real EEPROM game (none on hand).

### D. Other hardware-parity items now implemented

- **APU mix**: SOUNDCNT_L per-channel L/R panning + master volume, Direct Sound L/R enables,
  SOUNDBIAS, 2-bank 64-sample wave mode, PSG register read-back. `GbaApu` / `Apu*Channel`.
- **SIO/serial**: SIODATA/SIOMULTI/SIOCNT/RCNT with no-cable defaults (0xFFFF), start-bit
  auto-clear + serial IRQ. `GbaBus.WriteSioControl`.
- **HALT/STOP + POSTFLG/HALTCNT**: `GbaBus.Halt`/`RunUntilInterrupt`, CPU suspends until IE&IF.
- **BIOS open-bus read protection**: `GbaBus.ReadBios` — BIOS readable only by code in it.
- **OBJ (sprite) mosaic**: `GbaPpu.RenderSprites`.

### E. The accuracy frontier (next)

- **Timer count-up 630/936 + Timer IRQ 54/90 + the Timing tail (1460/2020) share ONE root**: the
  timer-overflow→IRQ-recognition depth is ~1-2 cycles short of hardware — and **ares matches us
  exactly here** (proven by `--lockstep`), so pushing further means deviating from every on-disk
  emulator toward hardware truth. Evidence: Timer count-up `0b,0x0005` loop-sum `Got 2 vs 3` (the
  measured loop exits one iteration early); the Timing `N nops` boundary shifts uniformly. Plan:
  build NanoBoyAdvance (`D:\Source\ByteTerrace\Temp\nanoboyadvance`, v1.8.3 source, unbuilt) as a third
  reference for 2-of-3 voting, then pin the depth with `--gen-rom` micro-ROMs.
- **SIO timing 0/8** — the transfer-completion timing model (cycles-per-transfer vs clock select).
- **Multiply long 52/72** — the Booth-carry *values* (C flag after MULL); the timing half is done.
- **I/O read 81/130** — mostly open-bus-from-prefetch on write-only/unmapped register reads.
- **Misc edge cases 3/12**; **Memory 1519/1552**; **SIO R/W 85/90**.

Deferred (genuine accuracy frontier, not stubs): dot-accurate mid-scanline PPU register effects;
exact APU mix levels (structure is correct, absolute levels unverified without an audio reference).

---

## 8. Files

**The POST battery**

- `Program.cs` — parse knobs, dispatch diagnostics (`Diagnostics.TryRun`), else run the battery.
- `PostStages.cs` — the ordered stage registry (the render-hash floor `ExpectedHash` constants live here).
- `IPostStage` / `PostTier` / `PostVerdict` / `PostStageOutcome` / `PostStageResult` — the stage contract & result types.
- `PostContext.cs` — the shared per-run context (artifacts dir, corpus/games roots, BIOS image).
- `PostBattery.cs` — runs the stages, isolates failures, builds the report.
- `PostReport.cs` — folds the exit code, renders + writes `post-report.txt`.
- `PostMachine.cs` — builds an isolated, direct-booted machine and advances it by frames.
- `SyntheticRom.cs` — the self-contained Tier-A cartridge (a deterministic backdrop-walking loop).
- `RomCase.cs` / `RomCatalog.cs` — reference-ROM discovery under the corpus.
- `Stages/*.cs` — one class per stage (Tier A: smoke/determinism/save-round-trip/throughput; Tier B: jsmolka/fuzzarm/render-hash/mgba-suite/ags).
- `*Probe.cs` — the verdict readers (`MachineProbe`, `JsmolkaProbe`, `FuzzArmProbe`, `RenderHashProbe`, `SaveRoundTripProbe`).
- `SmokeTests.cs` — the 41 hand-assembled CPU/PPU/APU/IRQ/DMA/DI vectors (drives `SmokeStage`).

**Diagnostics & shared test infrastructure**

- `Diagnostics.cs` — all diagnostic CLI modes + the mGBA-suite / AGS runners (shared with their stages).
- `MicroRoms.cs` — the `--gen-rom` timer/IRQ micro-ROMs + a minimal ARM assembler.
- `FlatTestBus.cs` — a flat little-endian bus for isolating the CPU on hand-assembled vectors.
- `TracingGbaBus.cs` — a pass-through `IGbaBus` decorator (AGS result capture, probe diagnostics).
- `MgbaDebugBus.cs` — an `IGbaBus` decorator emulating mGBA's debug-log register + input injection.

**The core (reference only — lives in `experimental/Puck.AdvancedGamingBrick`)**

- `GbaBus.cs` — memory map, wait-states, **prefetch buffer**, the `StepClocks` per-cycle engine.
- `GbaTimerController.cs` — the per-cycle timer latch state machine (`RunCycle`).
- `GbaInterruptController.cs` — the double-buffered IE/IF/IME synchronizer.
- `GbaCartridge.cs` — Flash state machine, RTC/GPIO, save detection, EEPROM, ROM burst counter.
