# Puck world agent bridge

This project gives an autonomous participant a small, typed way to perceive and
act in a Puck world. A **principal** is the identity attached to an action, and
a **grant** is the world's rule saying what that identity may observe or drive.
The bridge never turns either one into a prompt convention: every read and write
crosses Puck's existing protocol with the principal attached, and the server
checks the live grants.

The bridge does not depend on a model provider or an agent framework. That
separation lets a Microsoft Agent Framework harness, a scripted bot, or a later
MCP adapter share one authority boundary instead of growing separate game APIs.

## The boundary

`WorldAgentBridge` controls one 0-based body index as one `WorldPrincipal`. Its
constructor takes an `IPrincipalServerLink`, which is the explicit-principal
form of Puck's in-process server link, an `IWorldAgentDispatcher`, and a
function that returns the current `WorldChannelTable`.

An agent-capable host explicitly calls `AddPuckWorldAgentBridge` to register
`WorldAgentMailbox` as both that dispatcher and an `ISnapshotInputCapture`.
The base `Puck.World` executable never references this project or performs that
registration. Harness and model work stays on worker threads; the launcher
drains only short link and live-definition operations during its existing
per-frame capture pass on the single pump thread. The mailbox is bounded and
refuses overflow rather than silently dropping actions or letting an agent
create unbounded retained work.

The channel function is evaluated for every affordance read and action. This
matters after `world.reload` or another definition swap: channel names and their
numeric positions come from the live document, so an agent must not keep an old
table and press the wrong channel.

The bridge accepts only principals that may arrive through a real ingress:
seats, Console, addons, and peers. A group, document, or the world's own program
can receive or explain grants, but it cannot impersonate an actor through this
API.

## Observations and actions

`ObserveAsync` exposes six body-scoped readings: pose, channels, action state,
targets, contacts, and properties. The returned text is composed by the
authoritative server. `GetAffordancesAsync` combines Observe and Drive grant
checks with the live channel vocabulary so an agent can plan from what actually
exists.

The mutating surface stays deliberately small:

- `MoveAsync` submits a positive-duration motion segment through the world's declared
  movement roles.
- `PressAsync` resolves an exact authored channel name and submits a timed or
  single-step press.
- `StopAsync` clears the body's movement tape and held channels.

Every numeric input must be finite, and every duration must fit the protocol's
single-precision seconds field. The server still owns range folding, grant
ceilings, liveness, and the final decision.

An action returns a `WorldAgentActionReceipt`. A nonzero correlation id proves
that the local link minted a coordinate for the envelope; it does not prove that
the server authorized or applied the action. The participant should observe the
body afterward before claiming a result.

## Why this is not command text

`Puck.Commands` remains the shared vocabulary behind human input, bindings, and
Console scripts. It is a useful discovery and operator surface, but free-form
command strings would make an agent parse human output and could accidentally
borrow Console identity. The bridge instead constructs the same typed
`WorldCommand` and `WorldQuery` values those handlers ultimately use.

The Console belongs above this project as a control plane for tasks such as
starting, pausing, inspecting, and stopping hosted agents. It should not become
the path an agent uses to move a body.

MCP (Model Context Protocol, a standard tool interface for AI applications) can
be added later as an adapter over `WorldAgentBridge`. Such an adapter should
translate MCP calls into these methods and preserve a scoped principal; it must
not talk around the bridge or create a second authorization system.

## Creating a bridge

```csharp
var bridge = new WorldAgentBridge(
    link: loopbackTransport,
    dispatcher: worldAgentMailbox,
    principal: WorldPrincipal.Peer(index: bodyIndex, generation: generation),
    bodyIndex: bodyIndex,
    channels: () => WorldChannelTable.Compile(server.Definition.Channels));
```

An opt-in host calls `services.AddPuckWorldAgentBridge()` after registering its
ordinary `LoopbackTransport`, then resolves that transport through
`IPrincipalServerLink` and the mailbox through `IWorldAgentDispatcher`. The
example shows the ownership shape, not a requirement to recompile on every
call. A composition root may cache each immutable table and replace the
reference when the world definition changes.

## Verifying changes

Run the focused laws, then the architecture gate:

```powershell
dotnet test tests/Puck.World.Agents.Tests/Puck.World.Agents.Tests.csproj -c Release
dotnet src/Puck.Cli/publish/Puck.Cli.dll architecture
```

The focused suite checks principal stamping, coordinate translation, live
channel refresh, typed action construction, honest receipts, bounded mailbox
dispatch/cancellation/shutdown, and the Harness tool policy.
