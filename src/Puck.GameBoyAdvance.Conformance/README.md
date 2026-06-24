# Puck.GameBoyAdvance — conformance, accuracy & handoff

This is the handoff doc for the GBA core's accuracy work: how to run the suites, how the
cycle-exact **mGBA co-simulator** oracle is built and used, the timing model, and the one
open functionality gap. Pick the work up from here on any machine.

The core itself lives in `src/Puck.GameBoyAdvance` (all six subsystems built: ARM7TDMI,
bus/memory-map, IRQ/timers/DMA, replacement BIOS, full PPU, full APU). This project drives it.

---

## 1. Current status

- **CPU/bus/PPU/APU/DMA/timers**: complete. Golden Sun renders correctly in colour.
- **AGS aging cartridge: 37 / 38** subtests pass. The prefetch cell is fixed (§6.B; prefetch timer
  reads `0x18` on / `0x33` off, all 24 wait-state + cart-RAM reads hardware-exact). Implementing SIO
  unblocked the SIO interrupt cell (now passes) plus three further cells (#33–36) that previously
  never ran behind the stall; the lone failure is **#37** (a multiplayer-SIO cell that genuinely
  needs a link partner — expected to fail on a single console).
- **jsmolka** `arm`/`thumb`/`memory` + **FuzzARM** `ARM_Any`/`THUMB_Any`: pass.
- **40 smoke tests** + **4 render-hash floors** (Golden Sun, AGS menu, jsmolka hello/stripes): green.
- **Saves**: SRAM, **Flash 64K/128K** (full command/bank/erase state machine + chip-ID),
  **EEPROM** (full serial read/write protocol, 6- and 14-bit auto-detected bus widths, DMA-gated so
  it never shadows the 0x0D ROM mirror), and the **Seiko S-3511A RTC over GPIO**.
- **Hardware-parity pass** (this round): APU per-channel L/R panning + master volume + Direct Sound
  L/R + **SOUNDBIAS** + **2-bank 64-sample wave** + PSG register read-back; **SIO/serial** (no-cable
  defaults, start-bit auto-clear + IRQ); **HALT/STOP + POSTFLG/HALTCNT**; **BIOS open-bus read
  protection** + general open bus; **OBJ (sprite) mosaic**. See §8.
- **Open functionality gap**: **Pokémon Emerald boots to a white screen**, but the cause is now
  pinned down (and is *not* the old cycle-drift theory — after the §5 prefetch fix Emerald tracks
  mGBA to within ~13 cycles for 250k instructions). The state-diff co-sim (§4) finds a **functional**
  register divergence on the boot path; see §6.A. This is the highest-value next task.

Everything below is green and regression-free as of this writing.

---

## 2. External assets (must be supplied per machine)

None of these are committed. Re-provide them on a new machine:

| Asset | Used for | Pointer |
|-------|----------|---------|
| Cult-of-GBA replacement BIOS (`bios.bin`, 16 KiB, MIT) | `PUCK_GBA_BIOS` | github.com/Cult-of-GBA/BIOS |
| jsmolka `gba-tests` | `--roms` | github.com/jsmolka/gba-tests |
| `FuzzARM` (`ARM_Any.gba`, `THUMB_Any.gba`) | auto-found beside `--roms` dir | github.com/DenSinH/FuzzARM |
| AGS dump **`AGB_CHECKER_TCHK10.gba`** (md5 `9f74b2ad1d33e08e8a570ffe4564cbc3`) | `--ags` | user-supplied |
| Commercial ROMs (Golden Sun, Pokémon Emerald) | render-hash floors, game testing | user-supplied |
| mGBA source + a C toolchain (CMake + MSVC/clang) | the co-sim oracle (§4) | github.com/mgba-emu/mgba |
| AGSTests decompilation | AGS subtest spec / flag meanings | github.com/DenSinH/AGSTests |

The harness reads paths from env (`PUCK_GBA_BIOS`, `PUCK_GBA_GAMES`) or CLI flags; it skips
cleanly when an asset is absent.

> **AGS note:** the AGS subtest result-flags are read headlessly by patching `TCHK10` in memory
> with the DenSinH "output results" patch (3 Thumb instrs at file offset `0xB20`, so each test
> writes its flags to address `0x04`) and capturing them via `TracingGbaBus`. `RunAgs` does this
> automatically. Only the **TCHK10** dump matches the patch offsets — the v7.1 AGS and TCHK30
> dumps do **not**.

---

## 3. Running the harness

```sh
# Smoke + ROM suites + render-hash floors
PUCK_GBA_BIOS=/path/bios.bin PUCK_GBA_GAMES=/path/roms \
  dotnet run --project src/Puck.GameBoyAdvance.Conformance -c Release -- --roms /path/gba-tests
```

CLI modes (each takes a ROM path):

| Flag | Purpose |
|------|---------|
| `--roms <dir>` | smoke + jsmolka + FuzzARM + render-hash floors |
| `--ags <rom>` | run the AGS aging cartridge headlessly, print per-subtest pass/fail |
| `--render <rom> <out.png> [steps]` | boot and dump a framebuffer PNG |
| `--render-hash <rom> <steps>` | print the deterministic FNV-1a frame hash (for capturing a floor) |
| `--trace-cycles <rom> <steps>` | per-instruction `(PC, cumulative-cycles, delta)` — diff vs the co-sim |
| `--pctrace <rom> <steps>` | per-instruction `PC + Thumb-flag + cycles`; **boots through the BIOS** (matches the co-sim's full-BIOS boot for divergence diffing) |
| `--statetrace <rom> <steps>` | full per-instruction state `PC CPSR r0..r14 cycles`; diff the registers vs `cosim … --statetrace` to find the first **functional** divergence (immune to PC offset / line slips) |
| `--probe <rom> <steps>` | dump GPRs, DISPCNT/DISPSTAT/VCOUNT, IE/IF/IME/WAITCNT, VRAM/palette occupancy — diagnose a blank-screen boot |
| `--trace-crash <rom>` | report the first branch into unmapped memory |

Render-hash floors are deterministic; re-capture with `--render-hash` and update the
`expected:` constants in `Program.cs` whenever an *intended* timing/PPU change shifts a frame
(confirm the frame is still visually correct first).

---

## 4. The mGBA cycle co-simulator (the oracle)

The accuracy frontier is driven by diffing our per-instruction cycle counts against mGBA, the
cycle-exact reference. The harness for this lives outside the repo (built from mGBA source).

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
`D:\Source\ByteTerrace\gba-cosim\cosim.c`. Usage: `cosim <rom> <steps> [bios] [--probe|--pctrace]`.

Build it from an MSVC dev shell (note the `/MD` and the extra Win32 link libs, both required):

```sh
cl /O2 /MD <defs> /I <mgba-src>/include cosim.c <mgba-build>/mgba.lib \
   ws2_32.lib advapi32.lib shell32.lib user32.lib ole32.lib shlwapi.lib /Fe:cosim.exe
```

- **`<defs>` must match the lib's build flags exactly** or `struct mCore`/`struct GBA` layouts
  differ and you get an instant access-violation. Pull them from `mgba-build/build.ninja`; for the
  §4 config they are `-DBUILD_STATIC -DENABLE_DIRECTORIES -DENABLE_VFS -DENABLE_VFS_FD
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

For an isolated timing question, hand-assemble a tiny ROM that sets WAITCNT, enables timer 0
(÷1), runs the construct under test, then stores the timer value to EWRAM `0x02000000`. Run it
on both cores and compare that value. Existing examples (regenerate as needed): an IWRAM-resident
LDR-from-ROM loop, a ROM-resident prefetch-off loop, and a ROM-resident branch loop. This is how
the wait-state, timer, and prefetch models below were each validated to per-cycle parity.

---

## 5. The cycle-timing model (validated against mGBA)

All in `src/Puck.GameBoyAdvance/GbaBus.cs` and `GbaTimerController.cs`. Timing "emerges from the
bus accessors": every fetch/transfer/idle advances the machine through `Tick`.

- **Game-pak access** (`RomCycles`): every 16-bit halfword transfer costs its wait-state **plus a
  one-cycle bus base** — a sequential halfword is `S+1`, a non-sequential one `N+1`. A 32-bit
  access is just two such transfers, so a word is `2S+2` sequential / `N+S+2` non-sequential. This
  base cycle applies to **code fetches (prefetch off) and data alike** — both 16- and 32-bit. SRAM
  data adds `+1` likewise. (The 16-bit base was historically dropped, which under-counted every
  Thumb ROM fetch by 1; caught by the §4 co-sim — a Thumb ROM self-branch loop is `2S+1N = 2·3+5 =
  11` cycles, not 8 — and fixed.)
- **Timer start-up delay**: a timer ignores its first **2 cycles** after the enable bit is
  written (`GbaTimerController.StartDelay`). Real hardware behaviour; required for the AGS
  wait-state and prescaler tests and for any timer-measured loop.
- **Game-pak prefetch buffer** (`CodeFetchCycles`, WAITCNT bit 14): the buffer only helps when
  it has genuinely **run ahead** of the CPU, which it can only do during cycles the CPU isn't
  fetching from ROM (internal cycles, non-ROM data accesses — see `StepPrefetch`).
  - A fetch whose opcode the buffer already holds costs 1 cycle per halfword.
  - A **branch target** (address discontinuity) pays the full non-sequential access and restarts
    the run-ahead. Restart is keyed on *address discontinuity*, **not** the N/S access type — a
    data access marks the next fetch non-sequential, but a contiguous opcode fetch after it is
    still served from the buffer.
  - A **cold contiguous** fetch (a tight ROM loop that never frees the bus) pays the full
    sequential access — so a tight ROM loop sees no prefetch speed-up, matching hardware.

These are verified per-cycle against mGBA via §4. Don't "tune" them to a single test number;
re-validate any change with the micro-ROM method and re-run the full suite + AGS.

---

## 6. Open items

### A. Pokémon Emerald boot — the main functionality task

Emerald white-screens (stuck at PC `0x082E6DD0`, DISPCNT=0). The **old cycle-drift theory is
stale**: after the §5 prefetch fix, Emerald's cumulative cycles track mGBA to within **~13 cycles
over 250k instructions** (re-measured with the fixed co-sim — see §4). The real cause is a
**functional register divergence** on the boot path, found by the §4 **state-diff co-sim**:

- SOUNDBIAS (0x04000088) read as 0 instead of its `0x0200` power-on default — the BIOS sound init
  derailed on it. **Fixed** (APU now models SOUNDBIAS); divergence moved ~67k instructions later.
- The *next* divergence is at ROM PC `~0x6A6` (Thumb): register **r0** differs after a load
  (`0x84000200` → ours vs mGBA), right after a ~2570-cycle SWI/HALT wait. Not yet root-caused.

**Method (this is the breakthrough):** dump full per-instruction state from both cores
(`--statetrace` here, `cosim … --statetrace`) and diff the *architectural registers* (immune to the
PC pipeline offset and to single-instruction count slips that defeat a PC-only diff). The first
register that differs is the bug — `/tmp/statediff2.awk`-style two-pointer resync diff finds it.
Continue from the `~0x6A6` r0 divergence: disassemble the instruction, find which input (an I/O
read, a memory value) we compute wrong, fix, re-diff.

### B. AGS `prefetch_buffer` subtest (the 35th cell) — FIXED & CONFIRMED ✅

The prefetch-**disabled** half read `0x2A` (42); hardware wants `0x33` (51) — our prefetch-off
ROM code-fetch was ~1 cycle/fetch light. **Cause found & fixed:** 16-bit game-pak accesses omitted
the per-transfer +1 bus base cycle that the 32-bit path already carried, so every Thumb ROM fetch
(and 16-bit ROM data read) under-counted by 1 (see §5). Verified per-instruction against the §4
co-sim (sequential fetch 2→3, self-branch loop 8→11, load+branch loop 22-total — all match mGBA,
prefetch on and off) **and confirmed on the real `TCHK10` aging cartridge: AGS is now 35/35**, with
prefetch reads `0x18` on / `0x33` off. Zero regression on the full suite.

### C. EEPROM saves — IMPLEMENTED ✅

Full serial protocol in `GbaCartridge` (`ReadEeprom`/`WriteEeprom`): read (`11`) and write (`10`)
commands, 6-bit (512 B) and 14-bit (8 KiB) bus widths auto-detected from the first command's length,
64-bit blocks shifted MSB-first, 68-bit read replies (4 dummy + 64 data). The bus routes 0x0D…
to it **only for DMA accesses** (`m_dmaActive`), matching hardware — EEPROM is DMA-driven, and this
keeps test carts that merely embed the `EEPROM_V` string (the AGS aging cartridge has `EEPROM_V122`!)
from having their 0x0D ROM mirror hijacked. Untested against a real EEPROM game (none on hand).

### D. Other hardware-parity items now implemented (this round)

- **APU mix**: SOUNDCNT_L per-channel L/R panning + master volume, Direct Sound L/R enables,
  SOUNDBIAS, 2-bank 64-sample wave mode, PSG register read-back. `GbaApu` / `Apu*Channel`.
- **SIO/serial**: SIODATA/SIOMULTI/SIOCNT/RCNT with no-cable defaults (0xFFFF), start-bit
  auto-clear + serial IRQ. `GbaBus.WriteSioControl`.
- **HALT/STOP + POSTFLG/HALTCNT**: `GbaBus.Halt`/`RunUntilInterrupt`, CPU suspends until IE&IF.
- **BIOS open-bus read protection**: `GbaBus.ReadBios` — BIOS readable only by code in it.
- **OBJ (sprite) mosaic**: `GbaPpu.RenderSprites`.

Deferred (genuine accuracy frontier, not stubs): dot-accurate mid-scanline PPU register effects;
exact APU mix levels (structure is correct, absolute levels unverified without an audio reference).

---

## 7. Files

- `src/Puck.GameBoyAdvance/GbaBus.cs` — memory map, wait-states, **prefetch buffer**, cycle model.
- `src/Puck.GameBoyAdvance/GbaTimerController.cs` — timers + the 2-cycle start delay.
- `src/Puck.GameBoyAdvance/GbaCartridge.cs` — Flash state machine, RTC/GPIO, save detection.
- `RomRunner.cs` — all the CLI modes (`RunAgs`, `Render`, `TraceCycles`, `PcTrace`, `Probe`, …).
- `TracingGbaBus.cs` — pass-through `IGbaBus` decorator (watches stores/reads; the AGS result
  capture and timer-read diagnostics use it).
- `SmokeTests.cs`, `Program.cs` — smoke vectors and the CLI dispatch + render-hash floors.
