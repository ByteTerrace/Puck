# Puck.Input

Puck.Input turns controller reports and window keyboard or text events into one
provider-neutral input stream. It owns controller protocols, hotplug and player
slots, per-frame coalescing, sub-frame timestamps, motion fusion, haptics, and
the physical-control vocabulary. `Puck.Commands` owns the next step: binding
those physical controls to named commands and producing a fixed-step snapshot.

The project contains no operating-system transport. `Puck.Platform.Windows`
supplies HID and Xbox acquisition through injected interfaces; another
platform package can implement the same interfaces without changing a parser.
Absolute cursor position remains presentation-side browsing state; relative
mouse motion, wheel motion, and mouse buttons are ordinary bindable sources.

This README is the human entry point. The
[generated API reference](../../docs/api) owns complete member signatures,
parameters, return values, and exceptions.

## ✨ Key features

- *One normalized controller model:* Switch Pro, Xbox, DualSense, and both
  Steam Controller families produce the same `GamepadState` shape.
- *One destructive drain per frame:* `InputArbiter` drains the manager once and
  lets snapshot capture and registered lanes read that result safely.
- *Sub-frame edge timing:* the acquisition thread stamps reports and remembers
  each button's first press time between frame drains.
- *Hotplug without platform coupling:* the manager reconciles injected HID and
  non-HID acquisition sources, assigns the lowest free player slot, and prunes
  faulted connections.
- *Motion as captured data:* device-specific gyro and accelerometer frames feed
  a clock-free complementary filter; the resulting quaternion enters the input
  snapshot instead of being recomputed during replay.
- *Typed output capabilities:* rumble, trigger rumble, LEDs, and DualSense
  adaptive triggers are exposed only when the parser has a matching write path.
- *Presentation lighting:* the LampArray legend colors physical keyboard keys
  from the host's current bindings without entering simulation state.

## 📐 How device input reaches a command

Two acquisition transports feed one manager. HID devices own an asynchronous
read/write loop; the Windows Xbox backend polls separately and publishes the
same `IGamepadConnection` surface. The manager's `Drain` is destructive, so
`InputArbiter` performs it at most once for each host frame key.

```mermaid
flowchart LR
    subgraph Platform ["Puck.Platform.Windows — injected OS transports"]
        HidSource["HID source"]
        XboxSource["Xbox acquisition source"]
        Window["Native window events"]
    end

    subgraph Input ["Puck.Input"]
        Device["GamepadDevice + parser"]
        Connection["Xbox connection"]
        Coalescer["GamepadCoalescer"]
        Manager["GamepadManager"]
        Arbiter["InputArbiter"]
        Capture["GamepadCaptureSource"]
        Mapper["WindowInputMapper"]
    end

    subgraph Commands ["Puck.Commands"]
        Router["InputRouter"]
        Snapshot["CommandSnapshot"]
    end

    HidSource --> Device --> Coalescer
    XboxSource --> Connection --> Coalescer
    Coalescer --> Manager -->|"one drain per frame"| Arbiter --> Capture --> Router
    Window -->|"keyboard + text"| Mapper --> Router
    Router --> Snapshot
```

`GamepadSnapshotInputCapture` is the standard host-loop contribution. It asks
the arbiter to drain the current frame, then passes the drained devices to
`GamepadCaptureSource`, which appends timestamped `InputSignal`s to an
`InputRouter`. `Puck.World` composes this path and focus-gates it through
`IInputFocus.IsActiveFor`.

Registered arbiter lanes provide alternate views without draining again:

| Lane mode | Resolution |
|---|---|
| `Multicast` | One representative state with buttons OR-ed across connected pads. |
| `PerPlayer` | The device currently assigned to one player seat. |
| `Owned` | A device explicitly assigned through `SetLaneDevice`; neutral until assigned. |
| `Suppressed` | Always neutral. Any other lane can also be muted temporarily. |

## 🎮 Device support

| Family | Transport | Input | Motion | Output |
|---|---|---|---|---|
| **Switch Pro** | HID over USB | buttons, sticks, digital triggers | gyro, accelerometer, fused orientation | approximate HD rumble; player LEDs at initialization |
| **Xbox** | XInput + GameInput | buttons including Guide, sticks, analog triggers | — | four-motor rumble including impulse triggers |
| **DualSense** | HID over USB or Bluetooth | buttons, sticks, analog triggers, two touches | calibrated gyro, accelerometer, fused orientation | rumble, RGB and player LEDs, adaptive triggers |
| **Steam Controller** | HID over USB or classic receiver | buttons, analog trigger stages, stick, two trackpads | nominal gyro and accelerometer scales, fused orientation | coarse dual-pad haptic pulses |
| **Steam Controller Triton** | HID through the 2026 receiver | buttons, four paddles, analog triggers, two sticks, two trackpads | nominal gyro and accelerometer scales, fused orientation | dual-motor rumble |

Support means that a parser and acquisition path exist. Hardware evidence and
known limitations are recorded below.

## 🚀 Quick start

Build the platform-neutral project, then run the live Windows host:

```sh
dotnet build src/Puck.Input/Puck.Input.csproj -c Release
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 30
```

While the host runs, `world.devices` and `world.players` show the connected set
and seat assignments. Controller diagnostics use `[gamepad]` or `[gameinput]`
lines on stderr. Bindings are data in `Puck.World`; they are not hard-coded in
this project.

## The gamepad pipeline

### Per-device I/O and coalescing

Each HID connection initializes its parser, waits asynchronously for reports,
stamps every successful parse with an arrival tick and sequence number, and
folds the state into `GamepadCoalescer`. The same loop serializes queued output
writes for that handle. Streaming reads use a 16-millisecond bounded wait so
scheduled effects, rumble expiry, and receiver-silence detection keep moving
when input pauses. Each output queue retains at most 64 commands, and the loop
services at most 32 before returning to input; a full queue rejects the request
instead of growing without bound or starving reports.

Between frame drains the coalescer keeps the latest continuous values, ORs all
button press and release edges, records the first press time for each button,
and averages gyro samples. A short tap therefore survives even if it begins and
ends between two rendered frames.

### Connection lifecycle

`GamepadManager` rescans HID interfaces about every 1.5 seconds. A supported
interface is opened, assigned the lowest free player slot, and started. A
faulted connection is removed at the next drain or rescan, and disposal occurs
outside the manager lock.

Classic and Triton Steam receivers expose several input collections, most of
which can be empty. Those collections start dormant: they hold no player slot,
are excluded from public connection queries, and do not run the ordinary
five-second first-report watchdog. The first parsed state activates a slot.
The classic receiver reports a disconnect event and can return to dormancy.
Triton disconnect framing is not decoded, so the device loop also parks any
active receiver whose parsed state stream is silent for one second. Parking
clears coalesced state, pending output, scheduled effects, rumble, motion-fusion
history, and the player slot before that receiver carries another controller.

### Motion fusion

The motion-capable parsers map their sensor frames to a common right-handed
frame before calling `ImuOrientationTracker`. DualSense uses its report counter
for `dt`; Switch integrates three fixed five-millisecond samples per report;
Triton derives `dt` from its microsecond sensor timestamp, while the classic
Steam Controller uses a nominal four milliseconds per report. The tracker
integrates angular velocity, estimates stationary gyro bias, and corrects pitch
and roll toward accelerometer gravity.

DualSense reads factory gyro calibration from feature report `0x05`. Switch
reads factory or user stick calibration from SPI flash and falls back to its
nominal scale if a read is missing or implausible. Switch IMU calibration and
both Steam families currently use nominal scales.

## ⌨️ Keyboard, mouse commands, text, and pointer browsing

`InputSources` is the single vocabulary for keyboard, mouse, and gamepad control
names. `InputSourceVocabulary` resolves those names CASE-INSENSITIVELY, in the
reflection-derived declared tables and in every parametric family alike
(keyboard letters and digits, function and numpad keys, numbered mouse buttons,
`probe.<name>`), because `Puck.Commands`' `BindingProfile` compiles a page's
sources into a case-insensitive table and dispatches through the same one: a
case-sensitive catalog beside a case-insensitive compiler would refuse working
rows as unknown controls. A source id's case is authored-document noise, never
identity, and case-insensitivity widens nothing, since an id no member and no
family declares stays unknown in every casing. `WindowInputMapper` converts
neutral window events from `Puck.Platform` into `InputSignal`s, mirroring the
gamepad capture path. Left and right Control,
Shift, Alt, and Super remain distinct ordinary controls; number-row and numpad
digits remain distinct too, including when programmable mouse buttons emit them.

Absolute cursor position, hover, capture, and visibility remain presentation
state in the window-observer path. Relative motion (`mouse.motion`), two-axis
wheel notches (`mouse.wheel`), and numbered buttons (`mouse.button1` and upward)
also project into the command path. Button numbering has no five-button model
ceiling; a backend preserves every stable ordinal it can report. Windows' generic
mouse messages expose the conventional five, while programmable devices such as
the Naga commonly expose their larger grid as number-row or numpad keyboard
controls; both families are bindable here. Mouse motion and wheel samples are
transient impulses, never persistent analog holds.

`HeldDigitalInputState` retains keyboard and mouse-button first-down order and
emits per-frame digital `Active` samples after the original press frame. That
lets binding reloads and per-player modality changes cancel first, then recover
channels and modifier pages through the current profile without synthesizing an
edge verb.

Bindings, chord pages, toggles, sequences, interactive rebinding, and the
binding document format belong to [Puck.Commands](../Puck.Commands/README.md).
This project contributes only `InputSourceLabels`, which describes a physical
source using the connected family's labels—for example, the south face button
is B on Switch, A on Xbox or Steam, and X on DualSense.

## 🔊 Output and haptics

`IGamepadOutput` accepts typed output commands and a raw-report escape hatch.
The connection advertises capabilities from the interfaces its parser actually
implements. Switch and DualSense throttle equal-or-weaker rumble updates to a
minimum 30-millisecond cadence while allowing starts, stops, and intensity
increases through immediately.

Rumble duration is finite by contract: a zero-millisecond request stops every
motor immediately. Backends clamp finite intensities to `0..1` and map non-finite
values to rest before encoding a report.

Xbox output writes through both GameInput and XInput. GameInput reaches wireless
devices and the impulse-trigger motors; the XInput write remains useful when an
overlay has captured the GameInput endpoint.

DualSense uses one persistent output image for rumble, LEDs, and both adaptive
triggers. USB writes use report `0x02`; Bluetooth writes use report `0x31` with
its logical 78-byte framing and trailing CRC even when Windows advertises a
larger output buffer. A later rumble or LED update therefore preserves the
current trigger effects on either transport.

## 💡 Dynamic lighting

The bind legend is presentation-only. `Puck.Abstractions` defines the neutral
LampArray contract, `Puck.Platform` implements the Windows HID LampArray
transport, and this project composes colors from the host's current binding
state:

```text
lamp geometry + palette + live bindings
    -> LightLegendComposer
    -> LightLegendDriver
    -> dirty lamp writes at no more than 30 Hz
```

`LightLegendDriver` takes host control on its first tick and restores autonomous
mode when disposed. If the process is killed before disposal, a lamp array can
remain frozen on its last host-controlled frame until the operating system,
vendor software, or a replug reclaims it.

The transport and composer have been exercised on a Logitech G915 LampArray
(115 lamps, 33-millisecond minimum update), a G502 mouse, and a POWERPLAY pad.

## ⚠️ Limits and hardware evidence

- **DualSense USB and Bluetooth are hardware-verified.** Input, motion, rumble,
  LEDs, and adaptive triggers share the current output path. Bluetooth input
  CRC is left to the link layer; output supplies the required seeded CRC.
- **Switch Bluetooth is not supported.** The current initializer performs the
  USB handshake and has not implemented the Bluetooth-specific vibration setup.
- **Xbox Bluetooth is expected to flow through XInput and GameInput but has not
  been recorded as hardware-verified here.**
- **Only the Windows HID transport exists.** The parsers are platform-neutral,
  but no Linux `hidraw` implementation has exercised them.
- **Classic Steam Controller support is protocol-derived and not verified on
  physical classic hardware.** Lizard-mode control, raw IMU, state parsing,
  receiver events, and haptic framing are implemented.
- **Triton has hardware evidence for feature writes, rumble, steady-state IMU,
  and roughly 265 Hz input.** Its pairing/status report is not decoded; a
  one-second parsed-stream silence watchdog releases a slot after power-off.
  IMU axes and scales remain nominal, and decoded pressure and
  capacitive-touch fields have no `GamepadState` carrier.
- **Steam lizard mode and LampArray host control restore only during clean
  disposal.** An abnormal process exit can leave the device in the last mode
  until another driver or a replug resets it.
- **Switch HD-rumble uses a perceptible linear approximation rather than the
  complete perceptual frequency/amplitude table.**

For receiver diagnostics, match Steam VID `0x28DE` and the supported PID on
vendor input usage `0xFF00/0x01`. Classic control messages use their classic
feature framing. Triton feature messages use report id 1 and
`[type][length][payload]`; report id 0 is rejected with Win32 error 87.

## 📋 Core types

| Type | Role |
|---|---|
| `GamepadManager` | Enumerates, hotplugs, slots, queries, drains, and owns connection lifetimes. |
| `InputArbiter` | Performs the single destructive drain and resolves registered lanes. |
| `GamepadSnapshotInputCapture` | Connects the arbiter's frame drain to the snapshot input pump. |
| `GamepadCaptureSource` | Converts drained normalized states into timestamped `InputSignal`s. |
| `GamepadDevice` | Hosts one HID parser and owns that handle's asynchronous I/O loop. |
| `IGamepadParser` | Defines initialization and report parsing for a controller family. |
| `GamepadCoalescer` / `GamepadDrain` | Bridge high-rate acquisition to a loss-aware per-frame view. |
| `GamepadState` / `GamepadButtons` | Define the normalized controller state and button vocabulary. |
| `ImuOrientationTracker` | Holds complementary-filter orientation and gyro-bias state. |
| `InputSources` / `WindowInputMapper` | Own physical source names and neutral keyboard/mouse/text mapping. |
| `HeldDigitalInputState` | Retains edge-reported held controls in press order and produces safe per-frame reassertions. |
| `IGamepadOutput` / `TriggerEffectSpec` | Expose capability-gated controller output. |
| `LightLegendComposer` / `LightLegendDriver` | Compose and deliver presentation-side LampArray legends. |

The concrete controller parsers are `NintendoSwitchController`,
`DualSenseController`, `SteamController`, and `SteamControllerTriton`.
Windows implementations of `IHidDeviceSource`, `IHidDevice`, and the Xbox
acquisition source live in `src/Puck.Platform.Windows`.

## 🧪 Testing

Run the dedicated contract and regression suite, then build the production
projects in Release:

```sh
dotnet test tests/Puck.Input.Tests/Puck.Input.Tests.csproj -c Release
dotnet build src/Puck.Input/Puck.Input.csproj -c Release
dotnet build src/Puck.Platform/Puck.Platform.csproj -c Release
```

`Puck.Input.Tests` covers normalization and source contracts, parser report
bounds and output buffer floors, bounded output, rumble stop semantics,
scheduled effects during input silence, receiver parking and state reset,
disposal ordering, touch release, lane lifecycle and deterministic player
ordering, and empty LampArray behavior. These tests use transport fakes; live
hardware verification still uses `Puck.World`, its `world.devices` and
`world.players` verbs, stderr diagnostics, and physical output observation.
