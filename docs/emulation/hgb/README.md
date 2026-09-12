# Humble Gaming Brick

`Puck.HumbleGamingBrick` is Puck's shared SM83-based machine for the Game Boy
family. One core models the DMG, MGB, SGB/SGB2, CGB, and GBA compatibility
revisions; named capability checks express hardware differences without
forking the CPU implementation.

## Hardware coverage

The core includes the SM83 CPU, scanline PPU, audio channels, timers, DMA,
cartridge mappers, battery-backed storage, and serial, infrared, and printer
links. `ConsoleModel` selects the revision, and `ConsoleModelExtensions`
answers hardware questions such as color support, double speed, fetch-row
latching, infrared behavior, and wave-RAM boot seeding.

The detailed pages are organized by hardware area:

- [SM83 CPU and timing](sm83-cpu-and-timing.md)
- [PPU scanlines and pixel FIFO](ppu-scanlines-and-fifo.md)
- [APU and sound channels](apu-and-sound-channels.md)
- [MBC mappers and banking](mbc-mappers-and-banking.md)
- [Post harness and conformance](post-and-conformance.md)

## Embedding and hosting

For a synchronous application, the project exposes `HumbleGamingBrickCore`.
For a queued engine integration, `MachineHost` adapts the core to the shared
machine runtime. The [Humble project README](../../../src/Puck.HumbleGamingBrick/README.md)
contains the API examples and firmware details; [shared hosting](../shared/machine-hosting.md)
covers worker boundaries, pacing, buffers, audio, and snapshots.

The core's machine state is deterministic when driven by its integer cycle
clock and explicit inputs. Presentation output is consumed after execution and
does not define emulated state. Link sessions and cross-machine state belong to
the [shared infrastructure](../shared/README.md).
