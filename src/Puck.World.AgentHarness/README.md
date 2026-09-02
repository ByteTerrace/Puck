# Microsoft Agent Harness for Puck

This project composes Microsoft's Agent Framework Harness around the
provider-neutral `WorldAgentBridge`. The Harness supplies model/tool loops,
conversation sessions, planning, and human approval handling. Puck supplies the
world, deterministic simulation, identity, and authorization.

An `IChatClient` is Microsoft's common C# interface for a model provider. The
caller injects one, so Puck does not select an AI vendor, read an API key, or
turn provider settings into world state. The integration pins
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

Setting `RequireActionApproval` to `false` permits unattended tool invocation,
but it does not widen Puck grants. This is suitable only when the host has
already established its own consent policy.

## Hosting and Console control

The library intentionally stops at composition. The base `Puck.World`
application is agent-blind: it references neither agent project and registers
no agent services. A separate agent-capable composition root must call
`AddPuckWorldAgentBridge`, decide which provider creates the `IChatClient`, how
credentials are stored, which skills are trusted, and which principal and body
are assigned. Those are extension and deployment decisions rather than durable
world-document fields.

When the application hosts persistent agents, Console commands should manage
their lifecycle and surface pending approvals and status. Body actions should
continue through the bridge's typed tools. An MCP server, if added, should be a
separate adapter over the same bridge so desktop Harness agents and external
clients receive identical grants and observations.

## Verifying changes

```powershell
dotnet test tests/Puck.World.Agents.Tests/Puck.World.Agents.Tests.csproj -c Release
dotnet build src/Puck.World.AgentHarness/Puck.World.AgentHarness.csproj -c Release
```

The focused test uses a recording `IChatClient` to inspect the real options sent
through Harness. It proves that reads are ordinary functions, mutations are
`ApprovalRequiredAIFunction` values, and hosted web search is absent.
