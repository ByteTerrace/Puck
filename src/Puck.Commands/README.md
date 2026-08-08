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

A command carries a typed **value** (an analog stick, a mouse delta) alongside its
**activation**: a handler runs on each activation (a key press, a typed line), receiving
that activation's value on `CommandContext.Value`.

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
second loop or recover seconds from floating point. It is also the ONLY path for bound input: a composition root
without an `InputRouter` has no way to dispatch a binding at all, deliberately.

---

## Mental model

```text
 producers                 mixer                     registry                consumers
 ---------                 -----                     --------                ---------
 InputSignal --Capture()-> InputRouter --snapshot--> CommandRegistry ------> handler runs
 (device / player.signal)  stamps the lane's         ApplySnapshot           (map-gated)
                           principal

 Submit("line") ---------> Simulation-routed lines fold in through the console
                           injection sink, stamped Console -----------------> handler runs
                           (text path: parse + run, never map-gated)
```

There are exactly **three ingress doors**, and each stamps the acting `CommandPrincipal`
itself:

1. **The mixer.** `InputRouter.SnapshotForTick` folds captured signals through the slot's
   bindings and stamps each lane with `ICommandPrincipalResolver.PrincipalOf(slot)` — the
   host's answer to *who is acting through this slot*, never a seat synthesized from the
   slot number.
2. **Text.** `Submit` runs the handler as `CommandPrincipal.Console`; a
   `CommandRouting.Simulation` line folds into the snapshot stream through a
   `CommandInjectionSink` that was **constructed** bound to Console.
3. **The addon pump** (Puck.Scripting.Simulation), host-bound at mount and outside the
   recorded text surface by design.

There is no fourth door, and it takes **two** closures to say so — the second is the one
that is easy to miss:

- **Dispatch needs a `CommandContext`**, which only the registry and the mixer can build,
  and `CommandRegistry.Definitions` hands out `CommandMetadata` rather than an invocable
  handler.
- **`ApplySnapshot` needs a `CommandSnapshot`**, which only the mixer can build. That is a
  separate closure because the entries it applies are not merely carried through: an entry's
  `Principal` becomes the handler's verbatim, and an entry's `Text` is re-parsed and executed
  with no map gate. `CommandEntry`, `CommandLane`, and `CommandSnapshot` are therefore
  **internal to construct** — public to read, `internal init` to write. Without that, any
  assembly holding the registry could hand-build one entry and dispatch an authority verb with
  arguments of its choosing under an identity of its choosing, having entered by no door at
  all; closing `CommandContext` alone does not reach that path.

---

## Core types

| Type | Role |
|------|------|
| `CommandRegistry` | The hub. Aggregates modules, dispatches snapshots and text lines, gates by map. |
| `CommandDefinition` | Named, typed, invokable command — the shared identity behind every way it can be driven. |
| `ICommandModule` | Unit of composition: contributes a set of `CommandDefinition`s. |
| `CommandContext` | Per-invocation state handed to a handler (value, phase, logical slot, stamped principal, local device, parse result, text, registry). **Internal to construct.** |
| `CommandPrincipal` / `CommandPrincipalKind` | The acting identity a dispatch carries, stamped at its door: `Console`, `Seat`, `Addon`, `Peer`. |
| `ICommandPrincipalResolver` | The host's answer to *who is acting through slot N* — what the mixer stamps from. |
| `CommandBindability` | Whether a binding document may name a command. Required at every registration; `Unspecified` is refused by name. |
| `CommandMetadata` | The public read-only face of a registration (name, value kind, routing, bindability) — what `Definitions` returns. |
| `CommandResult` | What a handler returns for the transcript (output text + optional clear). |
| `CommandValue` | The per-frame value, tagged with its `CommandValueKind`, packed into a `Vector4`. |
| `CommandValueKind` | Shape of the value: `Digital`, `Axis1D`, `Axis2D`, `Axis3D`, `Orientation`. |
| `CommandPhase` | Transition the activation represents: `Started`, `Active`, `Completed`, `Canceled`. |
| `CommandMaps` | Well-known map names; `CommandMaps.Global` is always active. |
| `InputSignal` | A raw input keyed by a physical source id, *before* binding. |
| `CommandBinding` | Binds an input source id to a command (constant or pass-through value). |
| `CommandInjectionSink` | One pre-resolved-command door, bound to its principal and lane at construction. |
| `TextCommandSource` / `CommandShell` | Queue and per-frame pump for command lines through the registry's text path. |
| `InputRouter` | Captures timestamped physical signals and pre-resolved injections, then emits ordered per-tick, per-slot snapshots. |
| `CommandSnapshot` / `CommandLane` / `CommandEntry` | Canonical deterministic input for one fixed tick, built and applied within it — ephemeral, never itself persisted, with local device identities excluded from its deterministic content. |

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
`Map` is active dispatch from a snapshot — this is how you model gameplay vs. menu vs.
console modes without consumers caring.

```csharp
registry.ActivateMap(map: "Gameplay");
registry.DeactivateMap(map: "Gameplay");   // Global can never be deactivated
bool on = registry.IsMapActive(map: "Gameplay");
```

`CommandMaps.Global` is always active and is the default `Map` for a `CommandDefinition`.

---

## Defining commands

Implement `ICommandModule` and return `CommandDefinition`s. Use `CommandDefinition.Verb`
for a bare verb, or `CommandDefinition.WithWireArgs` for an argument-bearing one. Every
registration declares its `bindability` — there is no default, and a registration that
declared none fails at registry construction, by name.

```csharp
using Puck.Commands;

public sealed class GameplayModule : ICommandModule {
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            name: "jump",
            description: "Makes the avatar jump.",
            valueKind: CommandValueKind.Digital,
            handler: context => {
                // context.Value, context.Phase, context.Principal, context.Parse, context.Registry
                return CommandResult.None;        // effectful: no transcript output
            },
            bindability: CommandBindability.Bindable,
            map: "Gameplay"
        );
    }
}
```

A handler returns `CommandResult.None` when it has no transcript output; return
`new CommandResult("...")` to write to the transcript, or `CommandResult.Cleared()` to
request a transcript clear.

---

## Wiring it up

```csharp
using Puck.Commands;

// 1. Aggregate modules.
var registry = new CommandRegistry(modules: [new GameplayModule()]);

// 2. The mixer. `bindings` resolves a slot's source-to-command table; `principals` is the
//    host's roster, answering who acts through each slot.
var router = new InputRouter(registry: registry, bindings: bindings, principalResolver: principals);

// 3. Route Simulation-class text lines into the deterministic stream through the console door.
registry.RouteSimulationTo(sink: router.ConsoleTextSink);

// 4. Per frame: producers capture raw input; the host pulls one snapshot per fixed tick.
router.Capture(signal: new InputSignal(
    Source: "Keyboard.Space",
    DeviceId: default,
    Value: CommandValue.Digital(active: true),
    Phase: CommandPhase.Started
));

var snapshot = router.SnapshotForTick(tick: tick, windowEndTick: windowEnd);

registry.ApplySnapshot(snapshot: in snapshot);

// Console entry point (not map-gated), dispatched as CommandPrincipal.Console:
CommandResult help = registry.Submit(line: "help");
```

### Bindings: constant vs. pass-through

In a `CommandBinding`, leave `Value` `null` to **pass the input's own value through** (a
mouse delta driving `look`, typed text driving `console.insert`); set it to send a
**constant** instead (an arrow key driving a fixed `move` axis). One physical input may bind
to several commands across different maps — map gating keeps whichever is active, so the
binding table stays modality-agnostic.

A binding may only name a command whose `Bindability` is `Bindable`. `BindingVocabularyCheck`
refuses a page naming an unbindable destination, loudly, wherever a document enters: an
authority verb reached from a page would be an escalation the grant table never sees, because
the page rather than the principal chose the destination.

---

## Notes for agents

- **One identity, many drivers.** A `CommandDefinition` is resolved both when a console
  line is parsed and when a source dispatches a signal for its `Name`. Don't model the same
  action twice.
- **Names and aliases are claimed, not shared.** The registry's constructor throws if two
  modules register the same command name or alias, or if either collides with a built-in
  (`help`, `wire.ack`, `wire.errors`) — a collision is a composition-root bug, never a
  silent last-writer-wins. A non-`Global` map is deliberately shared: it is how several
  modules express one modality spanning them, exactly as `CommandMaps`' own doc says
  ("gameplay, console, or menu"), so there is no analogous guard over map names.
- **Snapshot dispatch is gated, `Submit` is not.** Bound activation respects command maps;
  the text path is the deliberate, always-available console seam.
- **Handlers READ their principal, never construct one.** `context.Principal` is what the
  door stamped. A handler that mints an identity is asserting one rather than carrying it.
- **Unknown / inactive is silent.** A signal naming an unknown command, or one whose map
  is inactive, is ignored without error.
- **`help` is built in.** The registry auto-registers a `help` command listing every
  command and description.
- **Thread-safety.** Only `TextCommandSource`'s queue is thread-safe (a background reader
  may enqueue while the frame thread collects); the registry itself is single-threaded.
- See the [generated API reference](../../docs/api) for full member docs.
