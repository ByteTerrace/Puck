# Advanced Gaming Brick

`Puck.AdvancedGamingBrick` is Puck's native Game Boy Advance machine. It
models the ARM7TDMI CPU, memory bus, PPU, audio, DMA, timers, interrupts,
cartridge storage, and link hardware. The core uses an integer 2²⁴ Hz clock;
one nominal frame is 280,896 CPU cycles.

## Hardware topics

Use the topic pages for the implementation and evidence behind each subsystem:

- [CPU pipeline, prefetch, and wait states](cpu-pipeline-prefetch-waitstates.md)
- [PPU rendering models](ppu-rendering-models.md)
- [APU and Direct Sound](apu-and-direct-sound.md)
- [DMA, timers, interrupts, and open bus](dma-timers-interrupts-open-bus.md)
- [Cartridge, saves, RTC, and peripherals](cartridge-saves-rtc-peripherals.md)
- [Determinism, snapshots, and replay](determinism-savestate-replay.md)
- [Performance techniques](performance-techniques.md)
- [Emulator landscape](emulator-landscape.md)
- [GBA test ROMs and evidence](test-roms-and-evidence.md)
- [GBA capabilities and gaps](capabilities-and-gaps.md)

The status vocabulary and evidence rules live in [GBA capabilities and gaps](capabilities-and-gaps.md)
and the evidence pages. A comparison implementation or test ROM supplies
evidence for a behavior; it does not by itself establish hardware truth.

## Embedding and hosting

Applications can use `AdvancedGamingBrickCore` synchronously or resolve the
queued `AdvancedMachineHost` through the shared machine engine surface. The
[AGB project README](../../../src/Puck.AdvancedGamingBrick/README.md) documents
construction, firmware, saves, options, and API examples. [Machine hosting runtime](../shared/machine-hosting.md)
covers worker lifetime, cycle pacing, buffers, audio, and snapshots.

The Humble core's AGB compatibility mode is documented in the [Humble Gaming
Brick entry](../hgb/README.md). Shared machine hosting and link sessions are
documented in [Shared emulation infrastructure](../shared/README.md).
