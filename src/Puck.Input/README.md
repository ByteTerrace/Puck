# Puck.Input — controller input subsystem

Cross-platform game-controller input for Puck. All input flows through `Puck.Commands`
(per-device `InputDeviceId`, sticks/triggers/gyro via `CommandValue`). This document describes the
subsystem's architecture, current support, and remaining work.

> Project rule (see `CLAUDE.md`): build in the split `Puck.*` projects. The old `Puck`
> and `Puck.Avatars` projects are **inspiration only** — never reference them.

## Scope: input vocabulary + the keyboard/mouse seam

Beyond controllers, `Puck.Input` owns the engine's physical-input **vocabulary** (`InputSources` —
`Keyboard` / `Pointer` / `Gamepad` source names) and the **keyboard/mouse neutral seam**. The native windows
(`Puck.Platform`) decode raw OS keys/pointer motion into a provider-neutral `WindowInputEvent` (a `KeyCode`,
typed text, or a pointer delta/position, each carrying a `CommandPhase`); they name no controls.
`WindowInputMapper.ToInputSignal` then applies the `InputSources` vocabulary — exactly mirroring how
`GamepadInputSource` maps neutral gamepad state. `Puck.Commands` keeps only the modality-agnostic bridge
shapes (`InputSignal`, `InputModifiers`, `InputDeviceId`, `BindingCommandSource`, `CommandBinding`).

- **High-rate mouse (Win32).** Pointer motion comes from **Raw Input** (`WM_INPUT`) — un-accelerated,
  full-rate relative deltas — **summed pump-level per frame**, so a 1000–8000 Hz mouse collapses to one
  `pointer.move` the command registry records correctly (its polled value is last-wins; one signal per frame
  makes that exact). Absolute mode (RDP / VM / tablet) is detected via `RI_MOUSE_MOVE_ABSOLUTE` and converted
  from the previous sample rather than summed as garbage. Absolute position rides `pointer.position`. If raw
  registration fails, `WM_MOUSEMOVE` feeds the **same** accumulator (never both for one motion).
- **Press + release edges + pollable held state.** Keyboard keys and gamepad buttons emit both a press
  (`Started`) and a release (`Completed`). `CommandBinding.ActivateOn` (default: `Started`/`Active`, ignoring
  `Completed`) gates which edges run a **handler**, but the registry records every activation: a held digital
  input **persists its polled value** across frames (set on press, cleared on release), so a continuous
  consumer can `GetValue` "is it down" without the source re-asserting it each frame — and a held key never
  re-runs its press handler. This split is why `CommandSignal` carries a `Dispatch` flag (update the value vs.
  run the handler). On focus loss the held set is cleared (`CommandRegistry.ReleaseHeld`, wired in the launcher
  pump) so nothing sticks while undelivered releases are missed. X11 auto-repeat (a release+press pair at the
  same timestamp) is de-duped in `XcbNativeWindow`.

## Status

Feature support by controller family. Transport is USB-only across all three; Bluetooth is not implemented.

| Family            | Transport            | Input                          | Gyro / fused pose | Rumble                     | Triggers        | LED / indicator              |
|-------------------|----------------------|--------------------------------|------|----------------------------|-----------------|------------------------------|
| Nintendo Switch Pro | HID (USB)          | ✅ buttons/sticks/triggers     | ✅ + accel + fused orientation | ✅ HD (approx. encoding)   | digital ZL/ZR   | ✅ player LEDs (at init)     |
| Xbox Series       | XInput + GameInput   | ✅ incl. Guide                  | —    | ✅ 4-motor (incl. impulse) | analog          | — (no controllable LED)      |
| Sony DualSense (PS5) | HID (USB)         | ✅ buttons/sticks/triggers + touchpad-click/mute + 2-finger touch | ✅ calibrated + accel + fused orientation | ✅ dual motor              | analog + adaptive | ✅ RGB light bar + player LEDs |

## Architecture

`Puck.Input` is **platform-agnostic** — it owns the gamepad protocol logic and the abstractions; all OS-specific
transport (Windows HID, XInput, GameInput) lives in **`Puck.Platform`** and is injected. Two acquisition
transports feed one interface and one command seam:

```
HID stream  ─ GamepadDevice ─┐                                  ┌─ GamepadCoalescer ─┐
  (parser, own I/O loop)     ├─ IGamepadConnection ─ GamepadManager ─ Drain ─ GamepadInputSource ─ Puck.Commands
Xbox backend ─ XInputGamepadConnection ┘   (HID enumeration, hotplug, player slots, per-frame Drain)
  (IGamepadAcquisitionSource, owns its own poll thread)
```

- **`GamepadManager`** (platform-neutral) — owns HID enumeration + ~1.5s hotplug rescan against an injected
  `IHidDeviceSource`, player-slot assignment, pruning, and the per-frame `Drain`. It runs no OS-specific code.
  A transport it can't run itself (the Xbox backend) is supplied as an optional `IGamepadAcquisitionSource` that
  publishes connections through the manager's `IGamepadConnectionRegistry`.
- **HID path** — `GamepadDevice` hosts an `IGamepadParser` and runs its own async read/write I/O loop over an
  `IHidDevice` (`device → coalescer`, `output queue → device`). Rumble via `IRumbleParser`, LED via `ILedParser`.
  The concrete `IHidDevice`/`IHidDeviceSource` is the Windows `Win32HumanInterfaceDevice`/`Win32HidDeviceSource`
  in `Puck.Platform`; a Linux `hidraw` transport plugs in the same way.
- **Xbox backend** — `Win32XboxAcquisitionSource` (in `Puck.Platform`) owns the single 250 Hz XInput poll thread;
  each tick it calls `XInputGamepadConnection.Apply(state)` then `ServiceOutput()`. The connection correlates
  itself to a GameInput device and writes rumble to **both** GameInput (4 motors, wireless reach) and
  `XInputSetState` (overlay-captured pads). The manager holds no rumble state.
- **Input** — `GamepadCoalescer` bridges the high-rate I/O loop to the per-frame consumer
  (latest axes / OR-ed button **press** edges only / averaged gyro). `GamepadInputSource`
  (`ICommandSource`) drains it and emits provider-neutral `InputSignal`s.

### The platform seam (so `Puck.Input` has no Windows code)

`Puck.Input` defines the transport abstractions and `Puck.Platform` (which references `Puck.Input`) implements them:

| Abstraction (`Puck.Input`) | Windows implementation (`Puck.Platform`) |
|----------------------------|------------------------------------------|
| `IHidDevice` / `IHidDeviceSource` (`Hid/`) | `Win32HumanInterfaceDevice` / `Win32HidDeviceSource` (`Windows/Hid/`) |
| `IGamepadAcquisitionSource` + `IGamepadConnectionRegistry` (`Devices/`) | `Win32XboxAcquisitionSource` (`Windows/Gamepad/`) |

The composition root injects the two Windows implementations into `new GamepadManager(hidSource, acquisitionSource,
diagnostics)`. `[InternalsVisibleTo("Puck.Platform")]` lets the relocated `XInputGamepadConnection` reuse the
internal output plumbing (`GamepadOutput`, `GamepadOutputCommand`). CsWin32 + the `x64` pin live in `Puck.Platform`.

### Capabilities are derived, never declared by hand

Output capabilities come from the interfaces a parser implements
(`GamepadDevice.CapabilitiesFor`), so an advertised feature always has a real write path:
`IRumbleParser → Rumble`, `ILedParser → Led`, every HID parser → `RawEffect`. Input
capabilities (`GamepadInputCapabilities`: `Gyro`, `AnalogTriggers`) are reported per connection
and queryable via `GamepadManager.TryGetInputCapabilities`.

### Key contracts

- `IGamepadConnection` — `DeviceId`, `PlayerIndex`, `IsFaulted`, `Coalescer`, `Output`, `Key`,
  `Type`, `InputCapabilities`, `Start()`.
- `IGamepadParser` — `Type`, `InputCapabilities`, `InitializeAsync(playerIndex)`, `TryParse(report, out state)`.
- `IRumbleParser` — `SetRumbleAsync(low, high)`. `ILedParser` — `SetLedAsync(LedColor)`.
- `IGamepadOutput` (queue façade) — `Rumble`, `RumbleTriggers`, `SetLed`, `SendEffect`, gated by `Capabilities`.

## GameInput haptics (Xbox) — in `Puck.Platform`

The whole Xbox backend (`Win32XboxAcquisitionSource`, `XInputGamepadConnection`, `GameInputHaptics`, and the
XInput/GameInput interop) lives in `Puck.Platform/Windows/Gamepad/`; `Puck.Input` only sees it through
`IGamepadAcquisitionSource`. `XInputSetState` rumble is a silent no-op for Xbox pads over the Wireless Adapter /
Bluetooth and can't reach the trigger motors, so Xbox rumble goes through **GameInput** (`gameinput.dll`,
hand-authored flat-COM interop in `Windows/Gamepad/GameInput.cs`).

- `GameInputHaptics` enumerates real per-device handles via a `RegisterDeviceCallback`
  reverse-callback into a dictionary that **owns** the RCWs. **Never `ReleaseComObject` a device
  handle** — connections borrow them; releasing separates a handle still in use and crashes
  (`InvalidComObjectException`). Drop the dictionary reference and let the GC finalize.
- `Bind(targetButtons)` correlates an XInput slot to its physical device by matching the buttons
  it currently holds, reserving it so no other slot binds it. Returns a device only when **exactly
  one** unbound device matches (ambiguity is deferred — never a stable mis-bind). `Unbind` releases.
- `RumbleDevice` returns `false` (never throws) when the device has disconnected, so the caller
  drops the stale binding and re-correlates instead of tearing down the 250 Hz poll loop.
- The poll thread is the **sole owner** of `GameInputHaptics` (creates + disposes it in its own
  `finally`); ownership never crosses a thread.

## DualSense adaptive triggers (raw channel)

Adaptive triggers have no portable cross-family shape, so they ride the **raw effect channel**
(`IGamepadOutput.SendEffect` → `GamepadOutputKind.Raw` → direct HID write) rather than a typed
capability. `Output/DualSenseAdaptiveTrigger.cs` builds the complete USB output report (id `0x02`):

- Asserts **only** the trigger-FFB bits in `valid_flag0` (`0x04` right, `0x08` left). These are disjoint
  from the vibration bits (`0x01`/`0x02`) and the light-bar flag (`valid_flag1` `0x04`), so the firmware
  applies only the trigger sections — a trigger write and a rumble/LED write never clobber each other,
  even though all three multiplex into one report.
- Trigger blocks: mode byte + params at `report[11]` (right) and `report[22]` (left), the same layout the
  player-LED (`[44]`) / light-bar (`[45..47]`) offsets in `DualSenseController` come from.
- **Official zone-packed effects** (recommended, range-validated): `Feedback(position 0..9, strength 0..8)`
  (`0x21`), `Weapon(start 2..7, end >start..8, strength 0..8)` (`0x25`), `Vibration(position 0..9, amplitude
  0..8, frequency)` (`0x26`). Zones index the pull in 10 steps; the 3-bit-per-zone strength array is bit-packed
  into `[+1..+6]` (Vibration's frequency rides `[+9]`).
- **Legacy "simple" modes** (raw `0..255` params, unvalidated firmware leftovers): `0x00` off; `0x01`
  `Simple_Feedback` = `Resistance(position, force)` → `[+1]=position, [+2]=force`; `0x02` `Simple_Weapon` =
  `Section(start, end, strength)` → `[+1]=start, [+2]=end, [+3]=strength`. The `strength` byte (`[+3]`) is
  **mandatory** for `0x02`; without it the band exerts no force.

The effect persists in the controller until replaced (e.g. by `Off`). USB-only, like rumble/LED — the report is
sized for the `0x02` USB report, not BT's `0x31`. The demo arms resistance on D-pad Up and clears it on D-pad
Down; pull L2/R2 to feel it.

## IMU fusion (absolute orientation)

Both IMU pads expose a fused absolute orientation (`GamepadState.Orientation`, a unit quaternion;
`InputSources.Gamepad.Orientation`, a `CommandValue.Orientation`). `ImuFusion` is a complementary
filter: it integrates the gyro and corrects the accumulated drift against the accelerometer's gravity
vector. `ImuOrientationTracker` is the shared per-device state (wall-clock `dt`, gyro-bias learning,
the orientation), so each parser only supplies gyro (rad/s) + accel (g) in a canonical **right-handed**
frame (X=right, Y=up, Z=back) — the parser maps its own sensor axes in.

Notes on the per-device sensor handling:

- **Sensor frames differ per device.** The DualSense IMU frame is **left-handed** (X=right, Y=up, Z=forward),
  so the Z axis is negated: the transform `diag(1, 1, -1)` is a handedness **reflection** (det −1) that converts
  it to the right-handed fusion frame. The Switch Pro frame is right-handed but rotated → `(x,y,z) → (-y, z, -x)`,
  a proper rotation (det +1). The same per-device transform applies to both that device's gyro and accel.
- **The DualSense gyro needs its factory calibration** (feature report `0x05`, read in `InitializeAsync`). The
  per-device sensitivity is **~64×** the bare `1024 LSB/°/s` resolution figure, so using `1024` directly reads
  ~64× too weak and yaw (which has no gravity reference) is effectively dead. The uncalibrated fallback therefore
  uses `64 × (π/180) / 1024`, so a pad whose `0x05` read fails still reports usable angular velocity. The read is
  retried (~360 ms) because a device may not answer the feature report immediately after connecting. The Switch's
  nominal `0.070 °/s` scale is adequate without per-device calibration.
- **Gravity-anchored pitch/roll vs. gyro-only yaw**: a wrong gyro scale or a stuck bias estimator shows up *only*
  as a dead yaw, because the accelerometer keeps pitch/roll correct regardless.
- The complementary-filter correction term is `cross(measured, estimated)` (Mahony); reversing the operands would
  make the 180° flip the filter's stable point.

`gamepad.orientation` rides the accelerometer emit gate (a device with an accel is the one running the
filter). The demo draws a per-controller pitch/yaw/roll needle gauge from it (see bindings below).

## Deferred / next steps

- **Bluetooth** (all three). DualSense BT = input report `0x31` / output `0x31` (block shifted +1,
  trailing CRC32 with the `0xA2` prefix, ≥78-byte buffers); needs feature report `0x05` to enter
  full mode. **BT input must validate, and BT output must append, the trailing CRC32 — neither is
  implemented or checked today** (BT is not merely "the same report shifted"). The first BT RGB write must
  also clear the boot LED animation once (`valid_flag2` `BIT(1)` / `lightbar_setup`), or it masks the
  programmed color (not needed on USB). A BT Switch Pro matches PID `0x2009` but would
  wrongly run the USB `0x80` handshake — needs transport detection. Read/write buffers are already sized from
  `HIDP_CAPS` (`Input/OutputReportByteLength`), the prerequisite for this.
- **Linux / Steam Deck hidraw transport** — parsers are OS-agnostic; only the HID transport is
  Windows-specific today (nothing in the parsers has been exercised on a non-Windows backend yet).
- Per-device factory calibration: Switch stick (SPI flash) and Switch IMU (SPI) still use nominal scales;
  DualSense gyro is factory-calibrated (report `0x05`) but its accel/sticks use nominal scales. The Switch
  HD-rumble amplitude/frequency encoding is a perceptible linear approximation rather than a full perceptual LUT.
- XInput rumble is not explicitly rate-limited (it is already coalesced to one write per 250 Hz poll tick); the
  Switch and DualSense HID paths coalesce to a ≥30 ms cadence and the Switch low band is clamped to the LRA-safe
  `0x72` ceiling.

## Build / run / debug

```sh
dotnet build Puck.slnx
dotnet run --project src/Puck.Demo/Puck.Demo.csproj -- --exit-after-seconds 30
```

`Puck.Input` is platform-agnostic and **AnyCPU**; the `x64` pin and CsWin32 now live in `Puck.Platform` (its
SetupAPI structs can't be AnyCPU). Diagnostics go to **stderr** as `[gamepad]` / `[gameinput]` lines (device
discovery, handshake, streaming, correlation, errors). The demo's gamepad bindings:

| Source button       | Verb                      | Effect                                                            |
|---------------------|---------------------------|------------------------------------------------------------------|
| South (A/Cross/B)   | `gamepad-a`               | logs a press                                                     |
| East (B/Circle/A)   | `gamepad-rumble`          | dual-motor rumble on the pressing pad                           |
| West (X/Square/Y)   | `gamepad-trigger-rumble`  | impulse-trigger rumble (Xbox) or dual-motor fallback (others)   |
| North (Y/Triangle/X)| `gamepad-led`             | sets the light bar cyan (DualSense); `[unsupported]` elsewhere  |
| D-pad Up            | `gamepad-trigger-effect`  | arms DualSense adaptive-trigger resistance (raw effect); pull L2/R2 to feel it |
| D-pad Down          | `gamepad-trigger-effect-off` | clears DualSense adaptive-trigger resistance                  |
| Start               | `gamepad-start`           | logs a press                                                    |
| Touchpad click      | `gamepad-touchpad`        | logs a press (DualSense)                                        |
| Mute button         | `gamepad-mute`            | logs a press (DualSense)                                        |
| Touchpad finger 1/2 | `gamepad-touch0`/`gamepad-touch1` | logs each finger's normalized 0..1 position (DualSense multitouch) |
| Left stick          | `gamepad-move`            | logs the stick vector                                           |
| Gyro                | `gamepad-gyro`            | logs angular velocity (Switch / DualSense)                      |
| Touchpad finger 1   | `cursor-touch`            | absolute per-controller cursor (color matched to the LED)       |
| Left stick / accel  | `cursor-nudge-stick` / `cursor-tilt` | nudge the cursor (relative) / marble-maze tilt          |
| Orientation         | `gamepad-orientation`     | drives the per-controller pitch/yaw/roll needle gauge           |

The cursor overlay (colored per-controller cursors + the orientation needle gauges) renders on the
**Vulkan same-device producer** — run the demo with `--produce vulkan` to see it. It's a demo-owned
overlay pass (`Puck.Demo`), so no cursor/gauge concept leaks into the reusable SDF engine.

## File map

**`Puck.Input` (platform-agnostic):**

| File | Role |
|------|------|
| `GamepadManager.cs` | HID enumeration, hotplug rescan, player slots, per-frame drain, lifetime; drives an injected HID source + optional acquisition source |
| `GamepadInputSource.cs` | `ICommandSource` — drains the manager, emits `InputSignal`s (press edges, axes, gyro) |
| `InputSources.cs` | the physical-control name vocabulary (`Keyboard` / `Pointer` / `Gamepad`) — the single home for source names |
| `KeyCode.cs` / `WindowInputEvent.cs` | the neutral keyboard/mouse seam the native windows emit (pre-vocabulary key/text/pointer events, each with a phase) |
| `WindowInputMapper.cs` | maps a neutral `WindowInputEvent` → `InputSignal` via `InputSources` (the keyboard/mouse mirror of `GamepadInputSource`) |
| `Hid/IHidDevice.cs` / `IHidDeviceSource.cs` / `HidDeviceInfo.cs` | the HID transport abstraction the parsers + manager consume |
| `Devices/IGamepadAcquisitionSource.cs` / `IGamepadConnectionRegistry.cs` | the seam a non-HID backend (the Xbox poll loop) uses to publish connections |
| `Devices/IGamepadConnection.cs` | the uniform connection surface both transports implement |
| `Devices/GamepadDevice.cs` | HID connection: hosts a parser, owns its read/write I/O loop over `IHidDevice`, services rumble/LED |
| `Devices/IGamepadParser.cs` / `IRumbleParser.cs` / `ILedParser.cs` | per-family parse + rumble + LED contracts |
| `Devices/NintendoSwitchController.cs` | Switch Pro: UART handshake, IMU enable, `0x30` parse, `0x10` rumble (throttled + LRA-clamped), player LEDs |
| `Devices/DualSenseController.cs` | DualSense: `0x01` parse (sticks/triggers/buttons/gyro), `0x02` rumble + light bar + player LEDs |
| `Devices/GamepadCoalescer.cs` / `GamepadDrain.cs` | high-rate I/O → per-frame bridge (latest axes / press edges / mean gyro) |
| `Devices/GamepadState.cs` / `GamepadButtons.cs` / `GamepadTouchPoint.cs` / `GamepadType.cs` / `GamepadInputCapabilities.cs` | normalized input model |
| `Devices/ImuFusion.cs` / `Devices/ImuOrientationTracker.cs` | complementary gyro+accel orientation filter + shared per-device state (dt, bias learning) |
| `Output/*` | `IGamepadOutput` queue façade, `GamepadOutputCapabilities`, `RumbleEffect`/`TriggerRumbleEffect`/`LedColor` |
| `Output/DualSenseAdaptiveTrigger.cs` | builds DualSense adaptive-trigger `0x02` reports — official `Feedback`/`Weapon`/`Vibration` + legacy `Resistance`/`Section` |

**`Puck.Platform` (Windows implementations of the above):**

| File | Role |
|------|------|
| `Windows/Hid/Win32HumanInterfaceDevice.cs` | Windows HID transport (CsWin32: SetupDi enumerate, CreateFile, HidP caps, overlapped async R/W) implementing `IHidDevice` |
| `Windows/Hid/Win32HidDeviceSource.cs` | `IHidDeviceSource` — enumerates/opens HID interfaces (empty off-Windows) |
| `Windows/Gamepad/Win32XboxAcquisitionSource.cs` | `IGamepadAcquisitionSource` — the 250 Hz XInput poll thread, owns `GameInputHaptics` |
| `Windows/Gamepad/XInputGamepadConnection.cs` | XInput connection: poll-driven, owns GameInput correlation + dual-path rumble |
| `Windows/Gamepad/GameInput.cs` / `GameInputHaptics.cs` | GameInput flat-COM interop + Xbox rumble (device enumeration, correlation) |
| `Windows/Gamepad/XInput.cs` | XInput interop (`xinput1_4.dll`, ordinal `#100` `GetStateEx` for Guide, `timeBeginPeriod`) |
