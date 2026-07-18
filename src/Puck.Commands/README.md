# Puck.Commands

The engine-wide **command system**: a single, modality-aware surface for driving the
engine. Keyboard, mouse, gamepad, console text, AI, replay, and network input all become
the same thing — a typed, named **command** carrying a per-frame **value** — so consumers
never need to know where an activation came from.

```text
namespace Puck.Commands
target     net10.0
deps       System.CommandLine 2.0.9
```

A command can be driven two ways over one shared identity:

- **Discretely** — a handler runs on each activation (a key press, a typed line).
- **Continuously** — the current frame's value is polled (an analog stick, a mouse delta).

For an authoritative simulation, use the fixed-step path: `InputRouter` captures every modality into ordered,
per-slot `CommandSnapshot`s; Launcher applies one snapshot and calls one `IFixedStepSimulation.Step` for each exact
host-owned tick. `CommandContext.Slot` is the simulation identity. `DeviceId` is only a live, local annotation (for
rumble or device assignment) and is deliberately absent from recordings.

```csharp
services.AddFixedStepSimulation<GameSimulation>(bindings);

sealed class GameSimulation : IFixedStepSimulation {
    public void Step(in FixedStepContext tick, in CommandSnapshot commands) {
        // Advance authoritative state exactly once. Launcher already applied commands.
    }
}
```

This registration is the easy path: Launcher owns the accumulator, input capture windows, held folding, console
injection, snapshot application, catch-up, focus-loss release, and interpolation residual. A consumer does not build a
second loop or recover seconds from floating point. The frame-oriented
`BeginFrame` / `Collect` path below remains available to presentation-only or
non-simulated consumers.

---

## Mental model

```
 producers                  registry                      consumers
 ─────────                  ────────                      ─────────
 ICommandSource ─Collect()→ CommandRegistry ─Push()→ handler runs (discrete)
   (per frame, pull)            │  state[name] = value ─→ GetValue() (continuous)
                                │
 Submit("line")  ────────────→ ┘  (text path: parse + run, never map-gated)
```

Every frame:

1. `BeginFrame()` clears the previous frame's values.
2. `Collect()` pulls every registered `ICommandSource`, which **pushes**
   `CommandSignal`s into the registry.
3. A pushed signal is **gated by command maps** — only commands in an active map run.
4. Consumers either receive a handler call or poll `GetValue(name, kind)`.

The **text path** (`Submit`) is separate and deliberate: it parses a console line and runs
the matching handler with **no map gating**.

---

## Core types

| Type | Role |
|------|------|
| `CommandRegistry` | The hub. Aggregates modules, owns sources, dispatches, gates by map, holds per-frame state. Implements `ICommandSink`. |
| `CommandDefinition` | Named, typed, invokable command — the shared identity behind every way it can be driven. |
| `ICommandModule` | Unit of composition: contributes a set of `CommandDefinition`s. |
| `CommandContext` | Per-invocation state handed to a handler (value, phase, logical slot, local device, parse result, text, registry). |
| `CommandResult` | What a handler returns for the transcript (output text + optional clear). |
| `CommandValue` | The per-frame value, tagged with its `CommandValueKind`, packed into a `Vector4`. |
| `CommandValueKind` | Shape of the value: `Digital`, `Axis1D`, `Axis2D`, `Axis3D`, `Orientation`. |
| `CommandPhase` | Transition the activation represents: `Started`, `Active`, `Completed`, `Canceled`. |
| `CommandSignal` | One activation pushed by a source into a sink (named by command). |
| `ICommandSource` / `ICommandSink` | Producer (`Collect`) / receiver (`Push`) contracts. |
| `CommandMaps` | Well-known map names; `CommandMaps.Global` is always active. |
| `InputSignal` | A raw input keyed by a physical source id, *before* binding. |
| `CommandBinding` | Binds an input source id to a command (constant or pass-through value). |
| `BindingCommandSource` | Rewrites `InputSignal`s into `CommandSignal`s via a binding table. |
| `TextCommandSource` | Feeds queued command lines through the registry's text path. |
| `InputRouter` | Captures timestamped physical signals and pre-resolved injections, then emits ordered per-tick, per-slot snapshots. |
| `CommandSnapshot` / `CommandLane` / `CommandEntry` | Canonical deterministic input for one fixed tick; recordable and replayable without local device identities. |

---

## Values

`CommandValue` packs every shape into one `Vector4` — small, copy-cheap, never allocates.
The **kind is a property of the value, not the producer**, so one command can be fed as a
digital action one frame and a continuous axis the next.

| Kind | Components used | Typical use |
|------|-----------------|-------------|
| `Digital` | `X` (0/1) | press/release actions (`jump`, `exit`) |
| `Axis1D` | `X` | scalar, conventionally −1…1 |
| `Axis2D` | `X, Y` | movement, or a raw look delta |
| `Axis3D` | `X, Y, Z` | motion sensors (gyro, accel) |
| `Orientation` | `X, Y, Z, W` | fused absolute orientation (unit quaternion) |

```csharp
var move = CommandValue.Axis(value: new Vector2(x: 1f, y: 0f)); // Axis2D
bool held = move.IsActive;        // any non-zero component
Vector2 v = move.AsAxis2D;        // read it back in its kind
```

---

## Maps (modality)

A **command map** is a named group that can be toggled together. Only commands whose
`Map` is active accept *pushed* (source-driven) signals — this is how you model gameplay
vs. menu vs. console modes without consumers caring.

```csharp
registry.ActivateMap(map: "Gameplay");
registry.DeactivateMap(map: "Gameplay");   // Global can never be deactivated
bool on = registry.IsMapActive(map: "Gameplay");
```

`CommandMaps.Global` is always active and is the default `Map` for a `CommandDefinition`.

---

## Defining commands

Implement `ICommandModule` and return `CommandDefinition`s. Use `CommandDefinition.Verb`
for a bare verb, or the full constructor to attach a `System.CommandLine` `Command` (with
options/arguments) plus a `ValueSelector` that maps the parsed line to a `CommandValue`.

```csharp
using Puck.Commands;

public sealed class GameplayModule : ICommandModule {
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            name: "jump",
            description: "Makes the avatar jump.",
            valueKind: CommandValueKind.Digital,
            handler: context => {
                // context.Value, context.Phase, context.Parse, context.Registry
                return CommandResult.None;        // continuous/effectful: no transcript output
            },
            map: "Gameplay"
        );
    }
}
```

A handler returns `CommandResult.None` when its effect is observed by polling the value;
return `new CommandResult("...")` to write to the transcript, or `CommandResult.Cleared()`
to request a transcript clear.

---

## Wiring it up

```csharp
using Puck.Commands;

// 1. Aggregate modules.
var registry = new CommandRegistry(modules: [new GameplayModule()]);

// 2. Register sources. A binding table turns physical inputs into commands.
var bindings = new Dictionary<string, IReadOnlyList<CommandBinding>> {
    ["Keyboard.Space"] = [new CommandBinding(Command: "jump")],
};
var inputs = new BindingCommandSource(bindings: bindings);
registry.AddSource(source: inputs);

// Optional: pipe a scripted/console stream through the text path.
registry.AddSource(source: new TextCommandSource(
    registry: registry,
    onResult: (line, result) => Console.WriteLine(value: result.Output)
));

// 3. Per frame:
registry.BeginFrame();
inputs.Enqueue(input: new InputSignal(            // producer feeds raw input
    Source: "Keyboard.Space",
    DeviceId: default,
    Value: CommandValue.Digital(active: true),
    Phase: CommandPhase.Started
));
registry.Collect();                                // pull all sources -> push -> gate -> run

// 4. Continuous consumers poll:
var jump = registry.GetValue(name: "jump", kind: CommandValueKind.Digital);
if (jump.AsDigital) { /* ... */ }

// Console entry point (not map-gated):
CommandResult help = registry.Submit(line: "help");
```

### Bindings: constant vs. pass-through

In a `CommandBinding`, leave `Value` `null` to **pass the input's own value through** (a
mouse delta driving `look`, typed text driving `console.insert`); set it to send a
**constant** instead (an arrow key driving a fixed `move` axis). One physical input may bind
to several commands across different maps — map gating keeps whichever is active, so
`BindingCommandSource` stays modality-agnostic.

---

## Notes for agents

- **One identity, many drivers.** A `CommandDefinition` is resolved both when a console
  line is parsed and when a source dispatches a signal for its `Name`. Don't model the same
  action twice.
- **Push is gated, `Submit` is not.** Source-driven activation respects command maps;
  the text path is the deliberate, always-available console seam.
- **Frame discipline.** Call `BeginFrame()` then `Collect()` once per frame. Values are
  transient — they live for one frame and are cleared.
- **Unknown / inactive is silent.** A signal naming an unknown command, or one whose map
  is inactive, is ignored without error.
- **`help` is built in.** The registry auto-registers a `help` command listing every
  command and description.
- **Thread-safety.** Only `TextCommandSource`'s queue is thread-safe (a background reader
  may enqueue while the frame thread collects); the registry itself is single-threaded.
- See the [generated API reference](../../docs/api) for full member docs.
