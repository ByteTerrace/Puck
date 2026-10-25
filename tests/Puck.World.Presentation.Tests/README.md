# Puck.World.Presentation.Tests

These laws check a client and its frame producers over a live server or a
compiled world: state delivery to clients and sessions, federation routes that
carry a seat between authorities, portal input, projection anchors, levers, and
the scenes the shipped and canary worlds compose, including the counters
programs, the mesh canary oracle and the render envelope. The suite references
`Puck.World.Client`, the server, the console, the machine host, the transpiler
and both Gaming Brick cores, and not the composition root, the graphics backends
or the silo, so a backend change never selects it. Client laws that need no
server are in [`Puck.World.Client.Tests`](../Puck.World.Client.Tests/README.md).

Its fixtures come from [`tests/Shared/World`](../Shared/World/README.md), with the
client vocabulary hooks and the Gaming Brick machine catalog.

`AuthoredBindingAuthorityLawTests` installs the real command registry in an
isolated child test host. The server refuses a bindings-only author's overlay
naming the shipped unbindable `world.grant` destination without changing the
document; the same mutation naming a bindable command applies. The child owns
the process-wide registry, so it cannot change parallel laws' validation.

`WorldRenderEnvelopeLawTests` holds the world scene's CPU count to what the real
construction probe and static stamper emit, without a GPU, and the oracle's
refusals at the engine's instance and material ceilings.

The laws that boot the quarantined worlds (the flagship, the courtyard, the
parity world, the counters workloads, the canary and game fixtures, and the
saved-document corpus) are quarantined in
[`experimental/tests/Puck.World.Presentation.Tests`](../../experimental/tests/Puck.World.Presentation.Tests)
at the same relative paths, out of the build.

This suite holds scene probes and full composed worlds, so `puck affected` and
the gate treat its full run as heavy: one at a time on the machine.

## Verification

Run the suite's CPU laws from the repository root:

```powershell
dotnet test --project tests/Puck.World.Presentation.Tests/Puck.World.Presentation.Tests.csproj -c Release --filter-not-trait Category=Gpu
```

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
