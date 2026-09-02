# Puck.World.Console — the control plane that follows the authority

This project owns `IWorldConsoleAuthority` (resolves the `WorldInstance` a
console invocation addresses) and the server-only command modules moved out
of [`Puck.World`](../Puck.World/README.md): `world.grant`/`.revoke`/`.grants`/
`.why` (`WorldGrantCommandModule`), `world.contributions`
(`WorldContributionCommandModule` — the contribution-slot read-back; slots are
authored and filled through `world.row.set placements`, so it carries no
mutating verb), `world.dynamics` (`WorldDynamicsCommandModule` — the
`dynamics` section's read-back: every row's authored triple, the derived
fixed-point constants, and its live reference count), `world.curves`
(`WorldCurveCommandModule` — the `curves` section's read-back: every row's
authored shape, its compiled segment count and total arc length, and its live
reference count), `world.group.*`/`world.ownership.*`/
`world.groups` (`WorldGroupCommandModule`), `world.population.spawn`/
`world.looks` (`WorldLookCommandModule`), `market.*`/`world.market`
(`WorldMarketCommandModule`), `world.peers`/`world.projection`
(`WorldNetworkCommandModule`), `world.row.*`/`world.kits`/`world.assign`
(`WorldRowCommandModule`), `world.state.*`/`world.generate`/`world.state`
(`WorldStateCommandModule`), `world.update` (`WorldUpdateCommandModule`), and
`world.wait` (`WorldWaitCommandModule`, alongside the tick-barrier gate it
arms, `WorldConsoleWaitGate`, and `IWorldWaitGateResolver` — the row's own
gate, since a host running several rows has one gate per row and a singleton
would always arm whichever row it was constructed against). `WorldCommandArguments` (the free-text-tail
reconstruction every JSON/prose-tailed verb shares) lives in
[`Puck.World.Server`](../Puck.World.Server/README.md) instead, since modules
that stayed in `Puck.World` need it too.

Project references: `Puck.World.Server`, `Puck.World.Protocol`,
`Puck.World.Schema`, `Puck.Commands`, `Puck.Launcher` (the
`ITextCommandHoldGate` seam `WorldConsoleWaitGate` implements). `Puck.Networking`
is reached only through `Puck.World.Server`; nothing here names it directly.

## `IWorldConsoleAuthority`

```csharp
public interface IWorldConsoleAuthority {
    bool TryResolve(CommandContext context, out WorldInstance instance, out string refusal);
}
```

Every moved module resolves its target row through this seam instead of an
injected `WorldServer` singleton, via the `TryResolveServer` extension that
hands back the resolved row's `WorldServer` directly and formats a refusal
echo on failure. `Puck.World`'s own implementation
(`WorldBootConsoleAuthority`, internal to that project) always answers the
boot row — none of the moved verbs carry a trailing `instance:<name>` token
the way `player.*`/`world.instance.*` do, so that answer is exact rather than
a placeholder.

## The move predicate

A module moves here when every type its constructor and handlers touch is
reachable from this project's own reference set. A module whose ctor or
handlers touch `WorldClient`, `PlayerRoster`, the seat surface (e.g.
`WorldSeatAuthorityRouter`), views, HUD, screens, audio, or recording
stays in `Puck.World` instead — those verbs need a live player-facing session
this project never carries. `Puck.World.Addons` is likewise out of reach (not
in this project's reference set), so `WorldAddonCommandModule` (the
`world.addons` cost-surface read-back — mounting/unmounting/reloading/
enabling/disabling an addon rides `world.row.set addons`/`.remove` instead,
this project's own door) stays in `Puck.World` too.

Inventory control: `puck declarations src/Puck.World.Console --kind class --name CommandModule`.
