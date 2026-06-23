# Puck.GameBoyAdvance — conformance, accuracy & handoff

This is the handoff doc for the GBA core's accuracy work: how to run the suites, how the
cycle-exact **mGBA co-simulator** oracle is built and used, the timing model, and the one
open functionality gap. Pick the work up from here on any machine.

The core itself lives in `src/Puck.GameBoyAdvance` (all six subsystems built: ARM7TDMI,
bus/memory-map, IRQ/timers/DMA, replacement BIOS, full PPU, full APU). This project drives it.

---

## 1. Current status

- **CPU/bus/PPU/APU/DMA/timers**: complete. Golden Sun renders correctly in colour.
- **AGS aging cartridge: 34 / 35** subtests pass (the one open cell is documented in §6).
- **jsmolka** `arm`/`thumb`/`memory` + **FuzzARM** `ARM_Any`/`THUMB_Any`: pass.
- **40 smoke tests** + **4 render-hash floors** (Golden Sun, AGS menu, jsmolka hello/stripes): green.
- **Saves**: SRAM, **Flash 64K/128K** (full command/bank/erase state machine + chip-ID), and
  the **Seiko S-3511A RTC over GPIO** are implemented. EEPROM is still a linear stub.
- **Open functionality gap**: **Pokémon Emerald boots to a white screen** — it hangs in a
  VCOUNT-poll wait-loop because our cycle timing drifts ~4200 cycles behind real hardware over
  boot. This is the highest-value next task; see §5–6.

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
output so the two streams diff line-for-line. Usage: `cosim <rom> <steps> [bios] [--probe|--pctrace]`.

- Boots through the BIOS when one is supplied (`skipBios=0`), matching our `--pctrace`.
- `--pctrace` prints `gprs[15] + Thumb-flag + cumulative-cycles`. **Use the raw architectural
  PC, not a normalized "exec" address** — normalization is unreliable across mode switches and
  branches and produces phantom ±4 divergences.
- `--probe` prints `PC / DISPCNT / VCOUNT / VRAM-occupancy` after N steps.
- Install a silent `mLogger` (`mLogSetDefaultLogger`) or mGBA floods stdout.
- API surface used: `GBACoreCreate` → `init` → `mCoreConfigSetIntValue(skipBios,…)` →
  `loadBIOS`/`loadROM` → `reset` → `core->step`; read `((struct ARMCore*)core->cpu)->gprs[15]`,
  cumulative cycles via `mTimingGlobalTime(&((struct GBA*)core->board)->timing)`,
  memory via `core->busRead16/32`.

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

- **Game-pak word access** (`RomCycles`, 32-bit): `N+S+2` non-sequential / `2S+2` sequential —
  the two 16-bit halves plus the inter-halfword merge and a game-pak first-access cycle. SRAM
  data adds `+1` likewise.
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

Emerald white-screens. It is **not** a save/BIOS problem (Flash 128K + RTC are detected and
wired; mGBA boots Emerald with the *same* replacement BIOS via the co-sim). The cause is a
**cycle-timing drift**:

- Emerald spins a Thumb wait-loop polling **VCOUNT** for value `0xE0` (224) — regs in the loop:
  `r1=0x04000006` (VCOUNT addr), `r5=0xE0`.
- Our core runs **~4200 cycles behind** mGBA by the time the loop should exit, so our VCOUNT is
  several scanlines out of phase and the `==224` sample never lands; the loop never exits.
- The drift accumulates across boot. The §5 prefetch-model fix removed a large tight-loop error
  (and fixed the AGS prefetch-ON cell + general ARM ROM-loop accuracy) but did **not** close the
  Emerald drift — so there is at least one more under-counted construct on Emerald's boot path.

**Next step:** use `--pctrace`/cosim with the cumulative-cycle column to find *where* in boot
the drift accumulates (diff per-instruction cycle deltas, not just the final loop), isolate the
mischarged construct with a micro-ROM, fix the model in `GbaBus`/CPU, re-validate. Prime suspects
to check first: IRQ exception-entry cycle cost, and Thumb branch/load cycle edges in ROM with
prefetch on.

### B. AGS `prefetch_buffer` subtest (the 35th cell)

The prefetch-**disabled** half reads `0x2A` (42); hardware wants `0x33` (51) — our prefetch-off
ROM code-fetch is ~1 cycle/fetch light. Lower priority than A; the same per-instruction
cycle-diff method applies.

### C. EEPROM saves

Still a linear stub in `GbaCartridge`. Implement the serial protocol (read/write address +
64/512-byte modes) when a game that needs it is tested. Flash + RTC are the model to follow.

---

## 7. Files

- `src/Puck.GameBoyAdvance/GbaBus.cs` — memory map, wait-states, **prefetch buffer**, cycle model.
- `src/Puck.GameBoyAdvance/GbaTimerController.cs` — timers + the 2-cycle start delay.
- `src/Puck.GameBoyAdvance/GbaCartridge.cs` — Flash state machine, RTC/GPIO, save detection.
- `RomRunner.cs` — all the CLI modes (`RunAgs`, `Render`, `TraceCycles`, `PcTrace`, `Probe`, …).
- `TracingGbaBus.cs` — pass-through `IGbaBus` decorator (watches stores/reads; the AGS result
  capture and timer-read diagnostics use it).
- `SmokeTests.cs`, `Program.cs` — smoke vectors and the CLI dispatch + render-hash floors.
