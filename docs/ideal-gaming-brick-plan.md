# GamingBrick architecture and accuracy contract

The GamingBrick family models GB, GBC, and GBA hardware as deterministic
machines. `Puck.HumbleGamingBrick` runs SM83 cartridges in DMG, CGB, and AGB
compatibility modes. `Puck.AdvancedGamingBrick` runs GBA-native ARM7TDMI
cartridges. The cores share determinism, snapshot, and link-cable principles;
they do not share CPU or bus implementations.

## Design invariants

1. **Deterministic state.** Emulated state is a pure function of machine
   configuration, integer ticks, and input. Wall-clock time, unrecorded random
   input, and floating-point simulation state are prohibited.
2. **One SM83 core.** `ConsoleModel` selects DMG, CGB, or AGB compatibility
   capabilities. A GB cartridge does not move between separate emulators when
   the device costume changes.
3. **Full-instant snapshots.** A snapshot represents an arbitrary mid-frame
   instant, including CPU, bus, PPU, APU, timer, serial, DMA, cartridge, and
   scheduler state.
4. **Explicit ordering.** Equal-timestamp component ordering is part of the
   machine contract. Timer processing precedes serial processing.
5. **Observable verification.** Puck's POST batteries gate deterministic
   behavior. External emulators and ROM suites provide evidence and diagnostics,
   not an implementation template.

## Clock domains

The common integer time base is 2^24 ticks per second:

| Machine mode | Effective clock |
|---|---:|
| DMG and CGB single speed | 2^22 Hz |
| CGB double speed | 2^23 Hz |
| GBA native | 2^24 Hz |

`Tick` uses fixed-point sub-cycle precision in the Humble core. The GBA core
counts native 16.777216 MHz CPU cycles; one frame is 280,896 cycles. Host frame
cadence is converted to an exact integer budget with a carried remainder.

## SM83 timing contract

### Timer and serial

- TIMA increments on the falling edge of the TAC-selected DIV signal while
  enabled. DIV and TAC writes can therefore cause an increment.
- TIMA overflow observes the four-T-cycle reload delay and the documented TIMA
  and TMA write precedence during that interval.
- Serial shifts on falling edges of DIV counter bit 8 at normal speed and bit 3
  in CGB fast mode. An SC write arms the transfer without resetting or
  re-phasing DIV.
- `SerialLinkSession` advances a pair as one deterministic unit. `Suspend`
  returns the credit needed to snapshot, restore, and reconnect without losing
  or duplicating elapsed work.
- `ISerialPeer` generalizes the cable's far end beyond a second console:
  `GamePrinterDevice` and the CGB infrared line (`InfraredPort`, RP/0xFF56 and
  the HuC1/HuC3 IR cart windows, propagated through `IrLinkSession`) are both
  deterministic peers on the same seam.

### PPU timing

Mode 0 begins when the 160th visible pixel leaves the FIFO. CPU-visible STAT
mode and memory-access locks trail that internal transition by the calibrated
values in `PpuTimingParameters`. Treat those values as a coupled model: changing
one requires rerunning the PPU evidence corpus and link-game reproducers.

The current schedule includes:

- polled STAT and VRAM-read unlock at internal mode-0 edge + 4 dots;
- mode-0 interrupt and VRAM/OAM write unlock at + 5 dots, with the CGB
  single-speed interrupt adjustment;
- OAM-read unlock at + 6 dots;
- the OAM STAT pulse one dot after the LY write;
- model-specific LCD-enable entry timing and object-fetch stalls.

The remaining GB PPU accuracy work is the mealybug `m3_*` sub-dot register
signature set. Use `--stat-trace` and `--render` to diagnose it; do not tune
against a single ROM in isolation.

### Audio

The channel digital outputs, including PCM12 and PCM34, are deterministic
integer state. The frame sequencer is DIV-driven. Host resampling and speaker
mixing may use presentation-only transforms, but no feedback from presentation
may enter emulated state.

### Cartridge support

The Humble core provides ROM-only, MBC1, MBC2, MBC3, MBC5, MMM01, HuC1, HuC3,
MBC7, and Pocket Camera implementations. Add a mapper when a supported content
path requires it, with mapper state included in snapshots and battery-save
round trips.

Peripheral feedback and sensor latches are deterministic snapshot state, not
presentation state. MBC5 rumble variants (0x1C-0x1E) and the AGB GPIO rumble
pin latch a motor bit; MBC7 latches its tilt reading behind an `ITiltSensor`
DI seam (the `ICameraSensor` precedent) and the AGB equivalent is address-
mapped and keyed per game code; Boktai's solar sensor is a GPIO-pin counter
keyed by game code, with light level entering as recorded per-segment input
(`MachinePadState.LightLevel`). Both hosts expose the motor line through
`IFeedbackMachine`. Cartridges with a live sensor input (tilt, solar, camera)
are excluded from time-travel recording eligibility.

## GBA-native contract

The Advanced core models ARM7TDMI execution, pipeline-visible open bus, memory
waitstates and prefetch, DMA, timers, interrupts, PPU, PSG and Direct Sound,
cartridge backups, SIO, and JOY-bus registers. `AgbMachineFactory` owns machine
construction. `AgbMachineInstance.Fork` rebuilds from the configuration recipe
and restores a full `AgbMachineSnapshot`.

`AgbLinkCable` and `AgbLinkSession` connect multiple serial controllers. Normal
and multiplayer transfers expose partner presence and player identity through
the hardware register fields. A lone machine retains the hardware-appropriate
idle-line behavior. `Suspend` returns an `AgbLinkResumeToken` carrying each
console's instruction-overshoot credit past its cumulative link target, so
severing, snapshotting, restoring, and reconnecting a linked set discards no
work — the same guarantee `SerialLinkSession` gives the Humble core.

Prescaler and cascade timers are event-scheduled in steady state: a running
timer's next overflow is closed-form from its anchor clock and reload value,
scheduled as a timing event rather than stepped every master cycle. Per-cycle
stepping survives only inside the ≤2-cycle control/reload latch and IRQ-delay
window, so a Direct-Sound title with a timer permanently enabled no longer
defeats the bus's single-add fast path.

Accuracy work should preserve these boundaries:

- BIOS identity is explicit. Diagnostics that require retail behavior reject a
  replacement or unknown BIOS unless the caller deliberately overrides the
  check.
- Game-specific save and peripheral exceptions belong in
  `AgbGameOverrides`, not in instruction or bus special cases.
- RTC and sensor values enter as deterministic, recordable inputs.
- Reference-emulator cycle counts are compared per instruction. mGBA rebases
  its cumulative counter at frame boundaries and exposes a pipeline PC four
  bytes behind Puck's visible representation.

## Snapshot identity

Both cores share one state-serialization substrate — `Puck.Snapshots`
(`StateWriter`/`StateReader`, `SnapshotSection`, the FNV-1a fingerprint, and
the composed `SnapshotImage`) — behind one `ISnapshotable` component contract.
Snapshots carry a machine identity and an ordered section table. Restore must
reject a snapshot created for an incompatible configuration or cartridge.
`ContentEquals` compares deterministic content, while diagnostic section tables
localize a mismatch without making internal layout a public compatibility
promise. Identity records and component discovery order stay per-core by
design (`MachineIdentity` vs `AgbMachineIdentity`); only the plumbing is
shared. Current format versions: Humble `MachineIdentity.CurrentVersion` = 4,
Advanced `AgbMachineIdentity.CurrentVersion` = 6. The AGB snapshot excludes
the presentation-side APU sample rate — it is host configuration, not
emulated state.

Snapshot format changes require all of the following:

- update the writer and reader together;
- preserve every live latch and pending scheduler event;
- run the state/snapshot round-trip and fork-determinism stages;
- use the hash-divergence diagnostic when the serialized byte count or section
  boundaries change unexpectedly.

## Link verification

The Humble Tier-C battery covers synthetic byte exchange for all SM83 costume
pairings, suspend/restore/reconnect transparency, a commercial link-game replay,
a complete Pokémon Gold trade when the required ROM is supplied, GB Printer
byte-identical print-buffer round trips, and CGB infrared exchange
(`IrLinkSession`, its own credit-preserving `IrLinkResumeToken`). The
Advanced Tier-C battery covers deterministic multiplayer exchange, a
commercial link-game replay, and link-churn (`AgbLinkSession.Suspend`'s
credit-preserving `AgbLinkResumeToken`: sever, snapshot, restore, and
reconnect a linked set without discarding overshoot credit).

The cross-core SM83-to-ARM cable remains a separate integration problem. Do not
conflate it with the proven same-core costume pairings or with either core's
native link session.

## Performance rules

Measure before changing a hot path and consult the `dotnet10-performance`
skill. The per-cycle path must avoid allocations, DI resolution, floating-point
state, and unnecessary dispatch. `ComponentClock` stores concrete components
and preserves timer-before-serial order. An `AllocationStage` in both `.Post`
batteries asserts zero allocation across a warmed-up run of emulated frames, so
a regression surfaces as a red battery rather than a demo GC spike. Fork is
pooled restore-into-a-parked-instance rather than a fresh DI container build on
both cores. Fleet-level measurements and accepted optimization seams live in
[machine-fleet-plan.md](machine-fleet-plan.md).

## Verification

Run the battery for the core changed:

```powershell
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release
```

Tier A is self-contained. Reference-ROM and commercial-game stages skip when
their configured assets are absent. See each Post README for asset variables and
diagnostic commands.
