# Puck.AdvancedGamingBrick

Puck.AdvancedGamingBrick is the native ARM7TDMI machine costumed as AGB
hardware: CPU (ARM and Thumb instruction sets), PPU, APU, DMA, timers,
interrupts, cartridge, and link cable. It shares no CPU core with the SM83
compatibility machine in `Puck.HumbleGamingBrick` — only the master-clock and
link abstractions are shared where the hardware itself shares them. Snapshot,
fork, and queued off-thread hosting come from `Puck.GamingBricks`; this
project supplies only the AGB hardware itself.

## ✨ Key features

- *A complete machine from one DI scope:* `AdvancedGamingBrickMachine` binds
  the CPU, bus, PPU, APU, timers, DMA, interrupt controller, serial
  controller, and cartridge from one scope, so a machine never shares
  stateful peripherals with another and a snapshot can capture it completely.
- *Direct boot or BIOS boot:* a cartridge can start at
  `AdvancedGamingBrickMachine.CartridgeEntryPoint` (0x08000000) with the CPU's
  registers seeded to their post-BIOS state, or execute native firmware from
  reset. The screen engine defaults to bundled Puck firmware and cold startup;
  fast startup and external firmware are independent choices.
- *Cartridge detection with a documented override table:*
  `AgbCartridge` scans the ROM for save-type strings; `AgbGameOverrides`
  corrects the known-broken minority (anti-piracy decoy strings,
  EEPROM-backed carts that bait with an SRAM string) by the 4-character game
  code, and also keys GPIO sensor presence (rumble, solar, tilt) off the same
  code.
- *A deterministic, instruction-atomic link cable:* `AgbLinkSession` connects
  two to four machines and steps whichever is furthest behind its own
  cumulative target, one instruction or halted idle cycle at a time, ties to the lowest index — a
  state-free rule, so a linked run replays identically. `Suspend`/`Resume`
  give a credit-preserving reconnect that requires every console to be
  transfer-idle first.
- *Queued, backpressured hosting:* `AdvancedMachineHost` (the
  `IMachineEngine` adapter, engine id `advanced-gaming-brick`) forwards
  the neutral machine surface to `Puck.GamingBricks`'s `QueuedMachineWorker`.

## 📐 Hosting

```mermaid
flowchart LR
    Bytes["cartridge bytes + BIOS option"] --> Engine["AdvancedGamingBrickEngine.Create"]
    Engine --> Host["AdvancedMachineHost : QueuedMachineHost"]
    Host --> Worker["Puck.GamingBricks QueuedMachineWorker"]
    Worker --> Surface["IMachineRuntime / IQueuedMachineRuntime / IAudioMachine"]
```

`AdvancedGamingBrickEngine` (`Id = "advanced-gaming-brick"`) is the
`IMachineEngine` implementation a host resolves by id. Its options
string accepts `cold` (the default), `fast`, and an optional final `bios=<path>`.
Without a path, the runtime package supplies Puck firmware; `bios=puck` selects
it explicitly. An external path consumes the remaining text, allowing spaces.
`direct`, unknown tokens, conflicting modes, and zero-filled BIOS files are
rejected. Fast startup skips the presentation but retains the selected BIOS
for software interrupts and IRQ dispatch during cartridge execution.

`stub` explicitly selects a zero-filled image for BIOS-independent diagnostics
and polling cartridges. It implements no BIOS services or IRQ handler and
always selects fast startup. Callers constructing `AdvancedMachineHost`
directly supply `biosImage` and may select `bootMode`; an explicit zero-filled array has the same
limitations as `stub`. Nonzero replacement BIOS images are accepted, but
their service completeness is the caller's responsibility.

The [native firmware guide](Firmware/README.md) owns implementation coverage,
provenance and remaining compatibility limits. The image is MIT-licensed and
embedded in the runtime package; an application does not need Forge, LLVM or
a separate BIOS download. `AgbFirmware.GetImage()` returns a private copy.
`AgbBiosKind.Puck` verifies exact bundled bytes, not retail cycle parity.

Battery saves use a flushed temporary file beside the destination followed
by replacement, so a failed write preserves the previous save and remains
pending for retry. Restoring or rewinding requests a flush independently of
the snapshot's dirty flag: the disk does not rewind with emulated memory.
A forced flush persists the current save even when its emulated flag is clean.

## 🚀 Quick start

For a .NET 10 application with its own update loop, reference
`ByteTerrace.Puck.AdvancedGamingBrick` and construct the synchronous core.
NuGet supplies its managed dependencies; no Puck application, DI registration,
window, GPU backend, worker thread, or environment configuration is required.

```csharp
using Puck.AdvancedGamingBrick;
using Puck.Abstractions.Machines;

using var core = new AdvancedGamingBrickCore(
    cartridgeRom: File.ReadAllBytes(args[0])); // bundled firmware, native cold startup

core.ConfigureAudio(sampleRate: 48_000);
core.ApplyInput(input: new MachinePadState());
core.RunCycles(cycles: 280_896); // one nominal native frame at 16,777,216 Hz
uint[] pixels = core.Framebuffer.ToArray(); // 240 × 160, packed 0x00RRGGBB
short[] audio = new short[4096];
int sampleCount = core.DrainAudioSamples(destination: audio); // interleaved L/R
```

Supply `savePath` to opt into file-backed saves. With its default `null`,
saves stay in memory; `core.Instance.GetRequiredService<AgbCartridge>()`
exposes `SaveData` and `LoadSave` for a host's own persistence service.
The shared [core hosting contract](../Puck.GamingBricks/README.md#synchronous-core-hosting)
covers threading, buffer lifetime, cycle pacing, audio and snapshots.

Pass `bootMode: MachineBootMode.Fast` to skip startup. For an external BIOS,
use `AgbFirmware.CreateConfiguration(cartridgeRom, bios: image, bootMode: mode)`.
The existing explicit `AgbMachineConfiguration(bios, rom)` and two-image core
constructor retain their fast diagnostic default; the lower-level machine
factory still leaves explicit boot execution to its caller.

`AgbMachineConfiguration.Options` takes an immutable `AgbMachineOptions`.
`DisableRtc` and `DisablePrefetch` are diagnostic hardware overrides, both
off by default. `BusTrace` is an optional synchronous `Action<string>`;
route it to your own logging sink. Each machine gets its own settings and
forks retain them. Effective RTC and prefetch behavior participates in typed
snapshot identity; the observational trace sink does not. A shared trace sink
must handle concurrent calls if the host steps forks concurrently.

For Puck's queued screen-machine adapter:

```csharp
using Puck.Abstractions.Machines;
using Puck.AdvancedGamingBrick;

IMachineEngine engine = new AdvancedGamingBrickEngine();

IMachineRuntime machine = engine.Create(
    options: "bios=GBA_bios.rom",    // caller-supplied 16 KiB BIOS image
    contentBytes: cartridgeRom,      // the cartridge ROM image
    savePath: "save.sav",            // battery-save path, or null for in-memory only
    audioSampleRate: 48_000
);
```

`AgbMachineFactory.Create` is the lower-level path `AdvancedMachineHost`
itself builds on: it takes an `AgbMachineConfiguration` (BIOS + ROM bytes + options)
and an optional composition callback for pre-registering a decorating or
test-only subsystem — a tracing bus, a flat test bus — before the standard
`TryAddScoped` registrations defer to it. The factory returns an unbooted
machine; call `DirectBoot` for the seeded cartridge handoff or step it from
reset to execute the supplied BIOS. The synchronous core follows its configured
startup mode; its bundled-firmware convenience constructor defaults to cold boot.

## 📋 Core types

| Area | Types | Role |
|---|---|---|
| Machine | `AdvancedGamingBrickMachine`, `AgbMachineConfiguration`, `AgbMachineFactory`, `AdvancedGamingBrickServiceRegistration` | The composition root and per-machine startup configuration. |
| CPU | `Arm7Tdmi`, `Arm7Tdmi.Alu`, `Arm7Tdmi.Arm`, `Arm7Tdmi.Thumb`, `IArmCpu`, `ArmDisassembler`, `CpuMode`, `ShiftType` | The ARM7TDMI core, both instruction sets, and its disassembler. |
| Bus | `AgbBus`, `IAgbBus`, `BusAccessType`, `AgbScheduler` | The cycle-scheduled system bus every subsystem reads and writes through. |
| Video/audio | `AgbPpu`, `IAgbPpu`, `AgbApu`, `IAgbApu`, `ApuPulseChannel`, `ApuWaveChannel`, `ApuNoiseChannel` | The PPU and four-channel APU. |
| Timing/interrupts | `AgbTimerController`, `IAgbTimerController`, `AgbInterruptController`, `IAgbInterruptController`, `InterruptSource`, `AgbDmaController`, `IAgbDmaController` | Timers, the interrupt controller, and DMA. |
| Cartridge | `AgbCartridge`, `CartridgeBackup`, `AgbGameOverride`, `AgbGameOverrides` | ROM signature scanning and header game-code overrides for save/RTC/GPIO detection. |
| BIOS | `IBios`, `ReplacementBios`, `AgbBiosProfile`, `AgbFirmware` | The BIOS image contract, owned image storage, content-hash identification, and bundled firmware configuration. |
| Link | `AgbLinkCable`, `AgbLinkSession`, `AgbLinkResumeToken`, `IAgbLink`, `NullAgbLink`, `AgbSerialController`, `IAgbSerialController` | The deterministic, instruction-atomic multi-machine link cable. |
| Hosting | `AdvancedMachineHost`, `AdvancedGamingBrickEngine`, `AdvancedGamingBrickCore`, `AdvancedPad`, `AdvancedGamingBrickLookahead` | The `IMachineEngine` adapter over `Puck.GamingBricks`'s queued-host substrate. |

## 🧪 Verification

`Puck.AdvancedGamingBrick.Post` is the gate — there is no separate unit-test
project for this core:

```powershell
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release
```

Tier A covers CPU/bus smoke vectors, determinism, state round trip, fork
determinism, save round trip, queued-host backpressure, throughput, and
zero-alloc-per-frame with no external assets; Tier B adds conformance
CPU/save/misc suites, an ARM fuzz corpus, render hashes, and an accuracy
suite (`--roms`, `--games`); see the battery's own README
for tier C and every diagnostic switch. `Puck.GamingBricks.Tests` exercises
the shared serialization/fork/queued-host substrate this core builds on.

## Execution architecture and larger performance changes

`RunCycles` must finish on the same instruction as repeated `Step` calls
given the same cycle budget, including overshoot. Fetched instruction words
and delayed IRQ recognition remain in the CPU pipeline. Memory accesses can
trigger DMA, timers, or scheduled peripherals before an instruction ends.
Compiling existing instruction handlers still pays these costs; removing
opcode dispatch alone does not establish a performance improvement.

The built-in timer and interrupt controllers publish clock readiness into
one `AgbClockState` per bus. Ordinary clock charges check this derived status
and the scheduler's next event deadline. Register writes, pending latches,
IRQ transitions, and restore update readiness at their source; the bus keeps
the existing per-cycle path for unsettled state and custom controllers.
No charge crosses a scheduled event, including an event at its final cycle.
The readiness cache adds no snapshot fields.

A halted CPU step checks for a wake interrupt, then advances one idle cycle
and pending DMA if it remains asleep. It returns control so the next host
input or linked peer can produce the wake interrupt. `RunCycles` counts these
idle steps alongside instruction and exception steps; a sleeping CPU does
not wait indefinitely inside a call. STOP still uses the modeled restricted
wake sources while peripheral clocks continue advancing.

Text backgrounds without horizontal mosaic resolve one tile-map entry and
read one packed tile row for up to eight pixels. These bytes are used only
within the current scanline callback, so CPU and DMA writes before the next
line are visible without maintaining a persistent graphics cache. Horizontal
mosaic retains the scalar pixel sampler. The layer compositor and raster
event schedule are unchanged.

Further architectural opportunities have different benefits and boundaries:

| Direction | Intended benefit | Required boundary |
|---|---|---|
| Execute CPU blocks up to the next observable event | Reduce repeated execution and timing work | Account for scheduler events, timer overflow, IRQ synchronization, DMA, fetch/prefetch effects, and the caller's budget. Preserve already fetched words when code changes. |
| Retain decoded graphics across scanlines | Avoid repeated decoding of unchanged graphics | Invalidate affected data on CPU and DMA writes to VRAM, palettes, and OAM; preserve register-write timing and rebuild derived caches after restore. |
| Prepare immutable cartridge data once per shared image | Reduce machine construction and fleet startup cost | Share ROM identity and save/sensor classification while keeping saves, RTC, GPIO, and other mutable state per machine. Establish image ownership before caching metadata. |

Cartridge construction currently scans each ROM for library signatures, and
machine construction hashes it for snapshot identity. An immutable image
could perform that work once. It must identify the actual BIOS and cartridge
supplied by custom composition and apply runtime diagnostic overrides
separately from immutable metadata.

The Post battery's
[`cycle-budget-execution` stage and `--compare-execution` diagnostic](../Puck.AdvancedGamingBrick.Post/README.md#determinism-diagnostics)
compare complete state and audio across execution paths. They supplement
external conformance and frozen-build comparisons; comparing two paths through
the same hardware model cannot prove hardware accuracy. Future acceleration
should retain the interpreter and support runtimes without dynamic code.

## 📦 Packaging

`ByteTerrace.Puck.AdvancedGamingBrick` depends on `Puck.Abstractions` (the
`IMachineEngine`/`IMachineRuntime` contracts it implements),
`Puck.GamingBricks` (snapshot, fork, and queued-host substrate), and
`Puck.Maths` (fixed-point and hashing primitives). `Puck.World.Schema` and
everything layered above it depend on this package for the native AGB
machine.
