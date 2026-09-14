# Machine emulation

Puck's Gaming Bricks provide deterministic emulation libraries for two related
families of handheld hardware. `Puck.HumbleGamingBrick` models the Game Boy
family around the SM83 processor, while `Puck.AdvancedGamingBrick` models the
native Game Boy Advance around an ARM7TDMI processor. Both expose synchronous
cores for direct embedding and adapters for queued hosting.

This documentation separates host integration from hardware detail. Start with
[Machine hosting runtime](shared/machine-hosting.md) when you need to load content,
advance a machine, consume frames or audio, or capture state. Read the
[Humble Gaming Brick](hgb/README.md) and [Advanced Gaming Brick](agb/README.md)
entries for the corresponding hardware cores and their detailed topic pages.

## Supported machine families

The Humble core covers DMG, MGB, SGB/SGB2, CGB, and AGB/AGS compatibility
revisions through capability checks on `ConsoleModel`. Its topic pages cover
the SM83 CPU, PPU, APU, cartridge mappers, and conformance evidence.

The Advanced core covers the GBA CPU, PPU, APU, DMA, timers, interrupts,
cartridge storage, and peripherals. Its topic pages cover pipeline and
wait-state behavior, scanline rendering, direct sound, bus arbitration,
saves, replay, performance, and evidence.

The shared layer supplies machine hosting, integer tick-to-cycle pacing,
backpressure, snapshots, and link sessions. It does not define either core's
hardware behavior. [Shared emulation infrastructure](shared/README.md) routes to those
contracts.

## Determinism and evidence

Machine state advances from integer clocks and explicit inputs. Wall-clock time,
unrecorded randomness, and presentation resampling do not feed back into the
emulated state. Snapshots and replay include the live machine state needed to
reproduce execution; RTC and sensor values cross the recordable input surface.

Repository Post stages and golden replays provide the project verification
story. External test ROMs, co-simulators, and hardware measurements are useful
evidence for ambiguous behavior and are documented with the relevant core.
