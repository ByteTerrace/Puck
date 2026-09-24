# Puck.Hosting.Tests

This suite exercises the fixed-step host and render-node contracts. It covers
hosting control, adversarial control cases, capability ownership, and the
local control and publication seams through `ControlTests`,
`ControlAdversarialTests`, and `HostingContractTests`. `CommandCompletionControlTests`
checks that control requests wait for applied and refused authority verdicts.
`ChildProcessTests` checks that a child writing more than a pipe buffer to both
output streams runs to exit through `ChildProcess.RunAsync`, with both streams
arriving whole. `RenderGraphSchedulerLawTests` holds the render-graph demand
scheduler to its demand, extent, refresh, cycle and budget rules, and
`RenderGraphHitWalkLawTests` follows a pick through a screen showing another
instance to the nested world's surface, stopping at the depth limit, an unread
image, or an instance with no camera.

## Running

```powershell
dotnet test tests/Puck.Hosting.Tests/Puck.Hosting.Tests.csproj -c Release
```

## Documentation

📚 [Puck.Hosting](../../src/Puck.Hosting/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
