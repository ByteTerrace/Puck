# Puck.World.AgentHarness

This project composes Microsoft's Agent Framework Harness around the
provider-neutral `WorldAgentBridge`. The Harness supplies model/tool loops,
conversation sessions, planning, and human approval handling. Puck supplies the
world, deterministic simulation, identity, and authorization.

An `IChatClient` is Microsoft's common C# interface for a model provider. The
library takes one from its caller; the hosted participant below takes one from an
installed `ChatClientProvider`. Puck never reads an API key or turns provider
settings into world state. The integration pins
`Microsoft.Agents.AI.Harness` 1.20.0 because that package is evolving quickly
and an unreviewed package update should not silently change the agent runtime.

## Safe default composition

`WorldAgentHarness.Create` publishes five model tools:

| Tool | Purpose | Approval by default |
|---|---|---:|
| `puck_get_affordances` | Read the body's current grants and channel vocabulary. | No |
| `puck_observe_body` | Read pose, channels, state, targets, contacts, or properties. | No |
| `puck_move` | Submit a bounded motion segment. | Yes |
| `puck_press_channel` | Submit a named channel press. | Yes |
| `puck_stop` | Clear motion and held channels. | Yes |

Harness approval records human consent to invoke a mutating tool. It does not
grant world authority. The server independently checks the bridge principal's
live grants, so an approved call can still be refused.

The default composition disables web search, file memory, file access, ambient
skill discovery, background agents, and Harness's plan/execute mode provider.
The todo provider remains available for multi-step objectives and can be
disabled with `EnablePlanning`. OpenTelemetry is enabled by default and can be
disabled by the host. No shell tool is registered.

## User-defined skills

The host may import trusted user-defined skills by setting
`WorldAgentHarnessOptions.SkillsSource`. This accepts any Agent Framework
`AgentSkillsSource`, including `AgentFileSkillsSource` for directories of
`SKILL.md` packages. Puck does not search the process working directory: a null
source keeps the provider disabled, so skill import is always explicit and
belongs entirely to the optional agent host.

```csharp
using var skills = new AgentFileSkillsSource(
    skillPaths: [engineSkillsDirectory, userSkillsDirectory]);

HarnessAgent agent = WorldAgentHarness.Create(
    chatClient: chatClient,
    bridge: bridge,
    options: new WorldAgentHarnessOptions {
        SkillsSource = skills,
    });
```

A skill source is a trust boundary: its instructions and resources enter model
context. A file source with a script runner can also execute skill scripts, so
the host should expose that only through a separate, explicit trust policy.

Microsoft currently labels the Harness compaction options as evaluation-only.
This project does not suppress that warning or expose those options; sessions
retain their ordinary Harness history without taking a source-level dependency
on an unstable API.

## Running a session

```csharp
HarnessAgent agent = WorldAgentHarness.Create(
    chatClient: chatClient,
    bridge: bridge,
    options: new WorldAgentHarnessOptions {
        Name = "harbor-guide",
        Instructions = "Help visitors reach the observatory without blocking the path.",
    });

AgentSession session = await agent.CreateSessionAsync(cancellationToken);
AgentResponse response = await agent.RunAsync(
    "Find the observatory and wait beside its entrance.",
    session,
    cancellationToken: cancellationToken);
```

Keep the `AgentSession` for later turns. If a response contains an approval
request, the host should present it to the operator and return the approval or
denial through Agent Framework's normal response flow. The official
[Harness guide](https://learn.microsoft.com/en-us/agent-framework/get-started/harness)
and [terminal sample](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/Harness/Harness_Shared_Console/HarnessConsole.cs)
show that interaction loop.

`WorldAgentHarnessOptions.Actions` chooses how the three mutating tools reach
the model. `RequireApproval`, the default, wraps each in an approval request.
`Unattended` offers them without one, which suits only a host that has already
established its own consent policy. `None` omits them, so the model can only
observe. No mode widens Puck grants.

## Running as a world participant

The extension contributes the `agent.harness` participant type. A host runs one
when its [extension configuration](../Puck.World.Server/ExtensionConfiguration.md#run-an-agent-participant)
lists it: a local World through `--extensions-config-file`, a silo through a
row's `extensions` file. Both hosts compose it the same way. The base
`Puck.World` and silo reference neither agent project; they discover this one
installed beside them.

The participant's `settings` are:

| Setting | Meaning | Default |
|---|---|---|
| `objective` | What the participant works toward; its first turn's message. Required. | |
| `provider` | The installed `ChatClientProvider` to use. | the one installed |
| `providerSettings` | The provider's own settings object. | `{}` |
| `instructions` | Role instructions added to the Harness's own. | none |
| `approval` | `refuse` or `allow` (see below). | `refuse` |
| `turnSeconds` | Seconds between the end of one turn and the start of the next. | `10` |
| `maximumTurns` | Stop after this many turns. | run until stopped |
| `maximumIterationsPerRequest` | The Harness's model and tool iteration limit per turn. | `12` |
| `planning`, `telemetry` | Enable the todo provider and OpenTelemetry. | `true` |

The host builds a `WorldAgentBridge` over the participant's principal and body
and a Harness over the selected provider's client. Each turn sends the objective,
then a short continuation, and waits `turnSeconds` on the host clock before the
next. The model's reads and actions queue on a bounded `WorldAgentMailbox` that
the host drains only at closed simulation boundaries, so a participant never
touches the world from its own thread. Stopping the host, or retiring a silo row,
cancels the turn, refuses queued work, and disposes the model client.

There is no operator in the loop, so the configured `approval` is the consent.
`refuse` offers the model no mutating tools, and the participant only observes.
`allow` offers them without a Harness approval step. Either way Puck's grants
decide whether an action is authorized.

A provider is selected by name, or, when `provider` is absent, as the one
installed; none, several, or an unknown name is refused by name, as is an
unknown setting, a blank objective, or a console principal. Providers
authenticate with identity. [Puck.World.AgentHarness.Azure](../Puck.World.AgentHarness.Azure/README.md)
contributes `azure.openai`. Another provider is an extension that calls
`AddChatClient` with its own name.

`world.extensions` echoes each participant as one status line: type, principal,
body, provider, state, completed turns, and the last failure.

## Verifying changes

```powershell
dotnet test tests/Puck.World.Agents.Tests/Puck.World.Agents.Tests.csproj -c Release
dotnet test tests/Puck.World.Tests/Puck.World.Tests.csproj -c Release --filter "FullyQualifiedName~WorldSiloExtensionLawTests|FullyQualifiedName~ExtensionModelLawTests"
```

The focused tests drive the harness and the participant with scripted
`IChatClient`s. They show that reads are ordinary functions and mutations are
`ApprovalRequiredAIFunction` values by default, that the loop observes its body
only when the host pumps it, that `allow` offers the actions and submits one and
`refuse` offers none, that disposal stops the loop between turns, and that bad configuration is
refused by name. The World tests show a local World and a silo composing the same
participant from one configuration, and an installed harness selecting an
installed provider across load contexts. No test reaches a live model.
## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
