using Puck.Commands;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>The silo's <see cref="IWorldConsoleAuthority"/>: the row bound to the invocation's own tagged session,
/// resolved by <see cref="CommandContext.Slot"/> — the slot <see cref="SiloConsoleRouting.Register"/> stamped on
/// that row's session at activation.</summary>
internal sealed class SiloConsoleAuthority(WorldSiloHost host, SiloConsoleRouting routing) : IWorldConsoleAuthority {
    /// <inheritdoc/>
    public bool TryResolve(CommandContext context, out WorldInstance instance, out string refusal) {
        if (!routing.TryResolveWorldId(
            slot: context.Slot,
            worldId: out var worldId
        )) {
            instance = null!;
            refusal = "this invocation carries no row — address it with '@<key> ' or 'silo.use <key>' first";

            return false;
        }

        if (!host.Instances.TryGet(
            instance: out instance!,
            name: worldId
        )) {
            refusal = $"'{worldId}' is no longer admitted";

            return false;
        }

        refusal = string.Empty;

        return true;
    }
}
