# Puck.Hosting

Puck.Hosting is the shared host substrate between deterministic simulation and
presentation. A **host** owns the outer loop: it measures time, advances the
simulation in fixed steps, routes services and exclusive capabilities through a
tree of render nodes, and publishes completed surfaces without letting GPU or
capture work become simulation state.

Puck.Hosting targets .NET 10; `dotnet pack` produces `ByteTerrace.Puck.Hosting`.
It depends on `Puck.Abstractions` for presentation, machine, capture, and GPU
contracts, and on [`Puck.Commands`](../Puck.Commands/README.md) for fixed-step
input snapshots.
The [project map](../../docs/project-map.md) shows where it sits in the wider
repository; the [generated API reference](../../docs/api) owns complete member
signatures, parameters, return values, and exceptions.

## ✨ Key features

- *One recursive render contract:* `IRenderNode` produces a `Surface`, may host
  children, and receives device-loss notifications without changing simulation
  state.
- *Deterministic fixed-step context:* `EngineTicks` provides an integer time
  base that divides common update rates exactly. `FrameContext` keeps
  authoritative simulation ticks separate from presentation-only wall time and
  interpolation.
- *Scoped host services:* inherited capabilities flow to descendants, while
  held capabilities such as terminal control and input focus form explicit,
  revocable ownership chains.
- *The terminal's console tape:* `ConsoleTape` records the console exchange
  (submitted lines, result echoes, panel visibility) into a bounded scrollback
  ring and publishes immutable `ConsoleTapeFrame` snapshots through
  `ConsoleTapeStore`; `ConsoleLineEditor` owns the prompt row's caret-addressed
  buffer and command history. Renderers read `IConsoleTapeSource`; the window
  host bridges keystrokes in (`ConsoleInputSink` in `Puck.Launcher`).
- *Safe parallel stepping:* `ISteppableRenderNode` separates serial shared-state
  preparation from parallel private-state execution; GPU work stays on the
  render thread.
- *Presentation observability:* frame capture, latest-value publication, emitted
  light, and frame-timing samples remain outside the simulation trajectory.

## 📐 The host boundary

The fixed-step simulation is authoritative. Rendering, timing diagnostics,
capture, and GPU upload observe or present its state; they do not decide what
the state becomes.

```mermaid
graph LR
    Input(["⌨️ Captured input"]) --> Commands["📋 CommandSnapshot"]
    Clock["⏱️ EngineTicks fixed-step clock"] --> Pump["🔁 Host pump"]
    Commands --> Pump
    Pump --> Simulation["🌍 Deterministic simulation"]
    Pump --> Context["🧭 FrameContext"]
    Context --> Tree["🌳 IRenderNode tree"]
    Simulation --> Tree
    Tree --> Surface["🖼️ Root Surface"]
    Surface --> Present["🖥️ Swapchain / capture"]
    Context -. "presentation timing only" .-> Timing["📊 FrameTimingHub"]
```

## 🚀 Quick start: a render node

An `IRenderNode` can return CPU pixels or a GPU image-view handle. This minimal
node produces a one-pixel CPU surface and has no device-owned resources to
release:

```csharp
using Puck.Abstractions.Presentation;
using Puck.Hosting;

sealed class StatusPixelNode : IRenderNode {
    private readonly byte[] pixels = [0x20, 0x80, 0xE0, 0xFF];

    public NodeDescriptor Descriptor { get; } = new(
        Name: "status-pixel",
        SurfaceId: SurfaceId.New());

    public Surface ProduceFrame(in FrameContext context) => Surface.CpuPixels(
        pixels: pixels,
        width: 1,
        height: 1,
        format: SurfaceFormat.R8G8B8A8Unorm);

    public void Dispose() { }
}
```

The outer host constructs one `FrameContext` per rendered frame. Its
`ElapsedTicks` advances only by completed fixed steps; its `AccumulatorTicks`
holds the fractional remainder used for interpolation:

```csharp
var context = new FrameContext(
    Host: HostContext.Empty,
    ElapsedTicks: elapsedTicks,
    DeltaTicks: stepsThisFrame * stepTicks,
    FrameDeltaTicks: wallTicks,
    AccumulatorTicks: accumulatorTicks,
    StepTicks: stepTicks,
    TargetWidth: width,
    TargetHeight: height);

Surface surface = root.ProduceFrame(context: in context);
```

`RenderTicks` is `ElapsedTicks + AccumulatorTicks`, and
`InterpolationAlpha` is the remainder divided by `StepTicks`. They are useful
for smooth presentation, but neither value authorizes another simulation step.

## 🌳 Host contexts and capability ownership

`IHostContext` exposes two deliberately different policies:

| Policy | Lookup | Propagation | Typical use |
|---|---|---|---|
| Inherited capability | `TryResolveCapability<T>` | Flows through descendant contexts | Shared services, registries, factories |
| Held capability | `HoldsCapability<T>` | Belongs to one holder; a child receives it only through a grant | Terminal control, input focus, exclusive authority |

`HostContext` stores both kinds. `ChainedHostContext` tries its primary then its
fallback context for inherited capabilities, but checks only the primary for
held capabilities. That rule prevents an exclusive authority from leaking
through a convenient service fallback.

`HeldCapabilityGrants` creates a delegation chain. Revoking an ancestor grant
invalidates every descendant lease, including a descendant grant described as
irrevocable from that descendant's point of view:

```csharp
var childGrants = new HeldCapabilityGrants();
ICapabilityTakeBack? takeBack = childGrants.Grant<ITerminalControl>(
    grantor: parentContext);

var childContext = new HostContext(
    capabilities: new Dictionary<Type, object>(),
    heldGrants: childGrants);

// Later: remove this delegation and every subgrant rooted in it.
takeBack?.Revoke();
```

Two standard held capabilities keep unrelated authority separate:

- `ITerminalControl` is the baton for requesting application exit.
- `IInputFocus` claims and releases individual input devices, or all devices,
  for the current holder.

Composition code can collect `HostCapabilityContribution` values before it
builds the root context. Each contribution states its runtime type, instance,
and whether the capability is held.

## ⏱️ Clocks and fixed-step timing

`EngineTicks.PerSecond` is 50,400. Common simulation rates—including 24, 25,
30, 48, 50, 60, 72, 90, 120, 144, and 240 updates per second—divide that base
without a fractional tick. `EngineTicks.PerRate(rate)` returns the exact step
size and rejects a rate that does not divide the base.

Hosting uses several clocks because they answer different questions:

| Type | Question answered | Rule |
|---|---|---|
| `TickClock` | How much wall time elapsed since the previous host sample? | Converts `Stopwatch` time to engine ticks and carries conversion remainder |
| `InputClock` | When did an input arrive? | Process-wide monotonic capture clock shared by input backends |
| `OsTimeCorrelator` | Where does a native 32-bit millisecond event stamp belong on the input timeline? | Handles wraparound and clamps the result to the observed engine-time window |
| `FrameContext` | What fixed-step instant is being presented? | Integer ticks are authoritative; seconds and interpolation are derived at the presentation seam |

`IFixedStepSimulation.RatePerSecond` must divide `EngineTicks.PerSecond`
exactly. For each completed step, the launcher constructs a
`FixedStepContext`, builds and applies one `CommandSnapshot`, and then calls
`Step`. A render frame may contain zero, one, or several fixed steps.

The fields most often confused in `FrameContext` have distinct meanings:

| Field | Meaning |
|---|---|
| `ElapsedTicks` | Simulation time after all completed steps |
| `DeltaTicks` | Whole fixed-step advancement performed for this rendered frame |
| `FrameDeltaTicks` | Clamped wall interval for presentation and diagnostics only |
| `AccumulatorTicks` | Unconsumed engine ticks, always less than one normal step |
| `StepTicks` | Fixed update period |
| `RenderTicks` | Interpolated presentation instant: elapsed plus accumulator |

`FrameTimingSample` records CPU wall-clock phase buckets, garbage-collection
overlays, and a remainder that makes the buckets tile the whole observed frame.
`FrameTimingHub` publishes the latest sample with a version number and invokes
its `Published` event synchronously on the render thread. Observers must keep
event handlers small and non-blocking. None of this timing data belongs in
simulation decisions.

## 🖼️ Render lifecycle and publication

Every `IRenderNode` has a stable `NodeDescriptor`, produces one `Surface`, and
is disposable. Hosting nodes that own children forward `OnDeviceLost` through
the tree. Nodes that own device resources release stale handles there and
rebuild them on a later frame; device loss must not advance or reset simulation.

For hosts that parallelize CPU stepping, `ISteppableRenderNode` divides the
work into three phases:

1. `PrepareStep(in FrameContext)` runs serially and may drain shared input or
   timelines. It reports whether the node has work.
2. `ExecuteStep()` may run in parallel, but touches only the node's private
   state.
3. `ProduceFrame(in FrameContext)` remains on the render thread and performs
   GPU work.

`FrameCaptureController` owns an optional capture session, including its
engine-time cadence, frame indexing, budget, and capture-only fault isolation.
The launcher hands it the exact root surface and matching `FrameContext`
immediately before presentation. CPU-backed surfaces need no conversion; GPU
surfaces use the active presenter's optional readback capability. Capture
timestamps use authoritative `ElapsedTicks`, while cadence follows continuous
`RenderTicks` so it does not assume a fixed host presentation rate.

`PublishBuffer<T>` is the smaller handoff for immutable latest-state values. A
single writer swaps a holder reference and readers snapshot the newest value.
It is not a FIFO and retains no history, so use it only when skipping obsolete
intermediate publications is correct.

## 🧱 Core types

| Area | Types | Purpose |
|---|---|---|
| Render tree | `IRenderNode`, `ISteppableRenderNode`, `NodeDescriptor`, `SurfaceId` | Recursive surface production and lifecycle |
| Fixed-step time | `EngineTicks`, `TickClock`, `FrameContext`, `FixedStepContext`, `IFixedStepSimulation` | Integer simulation time and presentation context |
| Input time | `InputClock`, `OsTimeCorrelator` | Monotonic capture timestamps and native event correlation |
| Host scope | `IHostContext`, `HostContext`, `ChainedHostContext`, `HostCapabilityContribution` | Inherited services and exclusive held capabilities |
| Delegation | `HeldCapabilityGrants`, `ICapabilityTakeBack`, `IHeldCapabilityLeaseSource` | Revocable held-capability chains |
| Standard authority | `ITerminalControl`, `IInputFocus` | Exit ownership and device focus |
| Observation | `FrameCaptureController`, `PublishBuffer<T>`, `FrameTimingHub`, `FrameTimingSample` | Capture sessions, latest-value handoff, and timing telemetry |

The machine-neutral queued-host substrate (`QueuedMachineWorker`,
`IQueuedMachineCore`, `MachineTimeTravel<TInput>`, `QueuedHostContractProbe`)
lives in [`Puck.GamingBricks`](../Puck.GamingBricks/README.md), which depends
on this project for `EngineTicks`.

## ✅ Verification

The focused Hosting tests cover capture cadence and fault containment,
capability-revocation cascades, and time-correlation validation:

```powershell
dotnet test tests/Puck.Hosting.Tests/Puck.Hosting.Tests.csproj
```
