using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The desktop's <see cref="IWorldConsoleAuthority"/>: every moved console module addresses the boot row
/// (<see cref="WorldInstanceHost.Boot"/>), since a desktop process runs exactly one console bound to one row; a
/// read-back that takes a trailing <c>instance:&lt;name&gt;</c> token reads the running instance it names.
/// </summary>
internal sealed class WorldBootConsoleAuthority(WorldInstanceHost instances) : IWorldConsoleAuthority {
    /// <inheritdoc/>
    public bool TryResolve(CommandContext context, out WorldInstance instance, out string refusal) {
        if (instances.Boot is { } boot) {
            instance = boot;
            refusal = string.Empty;

            return true;
        }

        instance = null!;
        refusal = "no boot instance";

        return false;
    }
    /// <inheritdoc/>
    public bool TryResolveNamed(CommandContext context, string name, out WorldInstance instance, out string refusal) {
        if (instances.TryGet(instance: out var named, name: name) && (named is not null)) {
            instance = named;
            refusal = string.Empty;

            return true;
        }

        instance = null!;
        refusal = $"no instance named '{name}' — see world.instance.status";

        return false;
    }
}
