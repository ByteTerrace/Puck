# Puck.World.Console

Puck.World.Console provides the command modules that operate on authoritative
world state. It resolves the target world for each invocation and uses the
server's ordinary mutation and authority paths.

`world.budget.rules` reads the installed compilation's shared cost report. Its
existing heuristic lines remain labelled as work units; a separate reference
line names the model, evidence digest, unresolved or known cycle bound, and
certification status. Incomplete calibration never turns work units into cycles.

## Command modules

This project owns `IWorldConsoleAuthority` (resolves the `WorldInstance` a
console invocation addresses) and the server-only command modules
[`Puck.World`](../Puck.World/README.md) composes: `world.grant`/`.revoke`/`.grants`/
`.why` (`WorldGrantCommandModule`), `world.contributions`
(`WorldContributionCommandModule`—the contribution-slot read-back; slots are
authored and filled through `world.row.set placements`, so it carries no
mutating verb), `creation.sculpts`/`creation.sculpt`
(`WorldSculptCommandModule`—lists/applies a `Puck.World.Authoring.Sculpting.ICreationSculpt`'s
patch against the live document by resubmitting each touched row through
`world.row.set`/`.remove`, never a parallel apply path), `world.dynamics` (`WorldDynamicsCommandModule`—the
`dynamics` section's read-back: every row's authored triple, the derived
fixed-point constants, and its live reference count), `world.curves`
(`WorldCurveCommandModule`—the `curves` section's read-back: every row's
authored shape, its compiled segment count and total arc length, and its live
reference count), `world.group.*`/`world.ownership.*`/
`world.groups` (`WorldGroupCommandModule`—`world.group.form`/`.join`/`.leave`/
`.kick` work the live runtime roster, added by `form` and wiped on the next
whole-document rebuild, while the kind catalog itself is authored through
`world.row.set`/`.remove groups.kinds`; `world.ownership.offer`/`.accept`/
`.reclaim` work the escrow/transfer lane over an already-declared
`WorldOwnership` row; `world.groups` is the read-back for all of it—kinds,
live group rows, and ownership bindings. Every mutating verb routes
`Simulation` and returns `None`, since the server prints the loud
`[world.mutation: … applied/rejected]` line), `world.population.spawn`/
`world.looks` (`WorldLookCommandModule`), `world.peers`/`world.projection`
(`WorldNetworkCommandModule`), `world.row.*`/`world.kits`/`world.assign`
(`WorldRowCommandModule`), `world.state.*`/`world.generate`/`world.state`
(`WorldStateCommandModule`), `world.tabletop` (`WorldTabletopCommandModule`—
the tabletop primitive's read-back: every placement carrying a `board` facet,
its anchored frame, its occupancy row's live cells, and any bound
`turn`/`verdict`/`move`/`plan` rows; read-only, since a board's rows are
authored/mutated through the same ordinary state doors any other row uses),
`world.update` (`WorldUpdateCommandModule`),
`world.wait` (`WorldWaitCommandModule`, alongside the tick-barrier gate it
arms, `WorldConsoleWaitGate`, and `IWorldWaitGateResolver`—the row's own
gate, since a host running several rows has one gate per row and a singleton
would always arm whichever row it was constructed against), and `replay.*`
(`WorldReplayCommandModule.cs`, `WorldReplayCommandModule.Drive.cs`,
`WorldReplayCommandModule.Inspect.cs`—record/stop/cancel/
drive/fork/verify/inspect/list/status; a client-local control surface over the
tape, none of it touching a live player-facing session). The tape mechanism
itself (`WorldReplayTape`, `WorldReplaySnapshot`, `WorldReplayInspector`,
`WorldReplayEntryDescriber`) stays in
[`Puck.World.Server`](../Puck.World.Server/README.md#deterministic-replay-worldreplaytapecs-worldreplaytapedrivecs-worldreplaysnapshotcs)—
`WorldReplaySnapshot` reads `WorldReplayInspector.DescribeRate`, a Server-internal
coupling this project cannot see through, so only the verb surface lives here; the
module reaches the tape, the inspector, and `WorldInstanceHost` by their
already-public surface. `WorldCommandArguments` (the free-text-tail
reconstruction every JSON/prose-tailed verb shares) lives in
[`Puck.World.Server`](../Puck.World.Server/README.md) instead, since modules
in `Puck.World` need it too.

`gpu.faults` (`GpuFaultsCommandModule`) is the operator's arming of the host's
`GpuCreationFaults`, which each GPU backend passes its device services through.
`AddGpuCreationFaults` registers both in the two GPU presentation shapes; the
verb's forms are in the [World guide](../Puck.World/README.md#shader-pipelines).

`WorldCaptureScheduler` is the second tick-published hook here beside
`WorldConsoleWaitGate`. It arms the `captures` section's rows at their
completed ticks. Its only contact with rendering is the
`ICaptureRequestTarget` the composition root passes in. It writes every armed
capture to the capture directory's `puck.parity.manifest.v1` manifest as one
entry: the frame that showed its tick, or a
named `refusal` with its `detail` (the vocabulary is in the
[parity README](../../tests/Puck.Parity/README.md)). While a capture waits for
its frame, `AwaitsFrame` holds, and the host composes that frame before its
next step. The offscreen host goes further and steps no tick past the armed one
until the capture is served or refused (`HoldsClock`). The hold counts from
readiness: time held while the engine is not ready (`IWorldEngineReadiness`,
its pipeline set not yet installed or no frame produced from it) is spent from
`BuildHoldBudgetSeconds` (180) per run, and time held once it is ready from
`HoldBudgetSeconds` (60). Past either, the capture is refused as `unserved`,
naming the engine's pipeline build and its progress when the build spent the
budget. `Drain` decides whatever is still
owed a frame as the run ends, before the host disposes the render chain. The
scheduler counts `world.captures.held` and `world.captures.ticks-while-armed`
under its `world.captures` work source. It lives here rather than in `Puck.World.Client` because it reads
`WorldServer` (the capture state hash and the `SolidField` inside-check), a
reference Client is denied.

`WorldConsoleNarrationSink` (`WorldConsoleNarrationSink.cs`) is the
`IWorldNarrationSink` implementation every composition root binds so a
headless script or canary reads each narration on stdout or stderr as one
`ConsoleRecord` (a multi-line narration's further lines indented), the same
framing command results take—it lives here rather than in `Puck.World.Server`
because `build/Architecture.props` denies that project a reference to
`System.Console`.

Project references: `Puck.World.Server`, `Puck.World.Protocol`,
`Puck.World.Schema`, `Puck.Commands`, and `Puck.Launcher`. `Puck.Hosting` and
`Puck.Networking` are reached only transitively, through
`Puck.Launcher`/`Puck.World.Server`; no `ProjectReference` here names either
directly.

`world.wait` holds only its issuing `TextCommandSession`. A row's
`WorldConsoleWaitGate` supplies the host-work clock; sessions keep independent
release deadlines. The release is exact: the host drains the console before
every step, so the session's next line runs after the releasing tick and
before the one after it
([commands reference](../../docs/reference/commands.md#who-can-dispatch-a-command)). Other text sessions remain responsive. Pausing or stopping
the row releases its armed waits. Direct registry calls without an originating
text session are refused. A clock reset invalidates all earlier deadlines,
including an expired wait the command pump has not yet observed.

## `IWorldConsoleAuthority`

```csharp
public interface IWorldConsoleAuthority {
    bool TryResolve(CommandContext context, out WorldInstance instance, out string refusal);
}
```

Every module here resolves its target row through this seam instead of an
injected `WorldServer` singleton, via the `TryResolveServer` extension that
hands back the resolved row's `WorldServer` directly and formats a refusal
echo on failure. `Puck.World`'s own implementation
(`WorldBootConsoleAuthority`, internal to that project) always answers the
boot row—none of these verbs carry a trailing `instance:<name>` token
the way `player.*`/`world.instance.*` do, so that answer is exact rather than
a placeholder.

## Which modules live here

A module lives here when every type its constructor and handlers touch is
reachable from this project's own reference set. A module whose ctor or
handlers touch `WorldClient`, `PlayerRoster`, the seat surface (e.g.
`WorldSeatAuthorityRouter`), views, HUD, screens, audio, or recording
stays in `Puck.World` instead—those verbs need a live player-facing session
this project never carries. `Puck.World.Addons` is likewise out of reach (not
in this project's reference set), so `WorldAddonCommandModule` (the
`world.addons` cost-surface read-back—mounting/unmounting/reloading/
enabling/disabling an addon rides `world.row.set addons`/`.remove` instead,
this project's own door) stays in `Puck.World` too.

Inventory control: `puck declarations src/Puck.World.Console --kind class --name CommandModule`.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
