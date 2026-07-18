# GBA emulation techniques

This reference describes hardware behavior, implementation choices, evidence,
and open accuracy questions for `Puck.AdvancedGamingBrick`. The native GBA core
uses an integer 2^24 Hz clock, where one tick is one CPU cycle and one frame is
280,896 cycles.

Use [verdict-index.md](verdict-index.md) for a compact capability and gap table.
Use [../agb-sota-survey.md](../agb-sota-survey.md) for prioritized engineering
work. Topic pages contain the hardware reasoning and reproducible evidence.

## Reading guide

- [cpu-pipeline-prefetch-waitstates.md](cpu-pipeline-prefetch-waitstates.md)
  covers pipeline state, prefetch, WAITCNT, multiplication timing, and open bus.
- [ppu-rendering-models.md](ppu-rendering-models.md) covers scanline and per-dot
  models, OBJ budgets, affine state, windows, blending, and access conflicts.
- [apu-and-direct-sound.md](apu-and-direct-sound.md) covers PSG, Direct Sound
  FIFO behavior, mixing, and presentation resampling.
- [dma-timers-interrupts-open-bus.md](dma-timers-interrupts-open-bus.md) covers
  DMA arbitration, timers, IRQ latency, I/O masks, and SRAM bus width.
- [cartridge-saves-rtc-peripherals.md](cartridge-saves-rtc-peripherals.md) covers
  backup detection, EEPROM, Flash, RTC, GPIO sensors, and cartridge mirroring.
- [performance-techniques.md](performance-techniques.md) covers interpreter,
  scheduler, fast-memory, and fleet techniques.
- [determinism-savestate-replay.md](determinism-savestate-replay.md) covers
  snapshots, fork, rewind, runahead, divergence localization, and link sessions.
- [emulator-landscape.md](emulator-landscape.md) explains which external
  implementations are useful for each kind of comparison.
- [test-roms-and-evidence.md](test-roms-and-evidence.md) maps suites and probes to
  the behaviors they measure.

## Status vocabulary

| Status | Meaning |
|---|---|
| implemented | Present in the core and covered by repository verification or reproducible evidence. |
| evidence needed | A focused ROM or hardware result must establish a divergence before code changes. |
| candidate | Well-scoped work that follows higher-value accuracy items. |
| unsupported | Deliberately outside the current product or determinism contract. |

Effort labels are estimates: S is focused, M spans several connected behaviors,
L is subsystem-scale, and XL changes the execution architecture.

## Evidence rules

- External ROM suites and co-simulators are evidence, not repository gates.
- Record BIOS identity, boot mode, ROM identity, stop condition, and command for
  every parity result.
- Normalize mGBA cycle counters at frame boundaries and align the pipeline PC
  representation before comparing instruction traces.
- Prefer a self-checking micro-ROM and observable memory result to an unbounded
  trace.
- A discrepancy between two emulators is not hardware truth. Use independent
  implementations or hardware measurements when documentation is ambiguous.
- Keep external emulator names in citations and prose, not in core identifiers.

## Determinism constraints

Emulated state contains no wall-clock, unrecorded random, or floating-point
inputs. RTC and sensor values cross a recordable input seam. Whole-machine
snapshots include every live latch and scheduler event. Presentation resampling
may use floating point only when it cannot feed back into emulation.

The SM83 AGB compatibility costume is documented separately in
[../ideal-gaming-brick-plan.md](../ideal-gaming-brick-plan.md).
