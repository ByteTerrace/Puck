# Machine emulation

Puck provides deterministic emulation libraries for three hardware families.
`Puck.HumbleGamingBrick` models the Game Boy family around the SM83 processor,
and `Puck.AdvancedGamingBrick` models the native Game Boy Advance around an
ARM7TDMI processor. Both handheld cores expose synchronous embedding and queued
hosting. `Puck.HumbleGamingDeck` models the NTSC NES/Famicom console and offers
the same embedding and queued hosting.

This documentation separates host integration from hardware detail. Start with
[Machine hosting runtime](shared/machine-hosting.md) when you need to load content,
advance a machine, consume frames or audio, or capture state. Read the
[Humble Gaming Brick](hgb/README.md), [Advanced Gaming Brick](agb/README.md),
and [Humble Gaming Deck](hgd/README.md)
entries for the corresponding hardware cores and their detailed topic pages.

## Supported machine families

The Humble core covers DMG, MGB, SGB/SGB2, CGB, and AGB/AGS compatibility
revisions through capability checks on `ConsoleModel`. Its topic pages cover
the SM83 CPU, PPU, APU, cartridge mappers, and conformance evidence.

The Advanced core covers the GBA CPU, PPU, APU, DMA, timers, interrupts,
cartridge storage, and peripherals. Its topic pages cover pipeline and
wait-state behavior, scanline rendering, direct sound, bus arbitration,
saves, replay, performance, and evidence.

The Deck core covers the NTSC console: the RP2A03G CPU with its DMA unit and
APU, the RP2C02G PPU, standard controllers, NROM, header-based cartridge loading,
and mid-instruction snapshots and forks. Its topic pages cover the CPU and
master clock, picture processing, audio, input and DMA, hosting, and the
verification battery.

The shared layer supplies machine hosting, integer tick-to-cycle pacing,
backpressure, snapshots, and link sessions. It does not define a core's
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
