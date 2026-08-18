using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The desktop's <see cref="IWorldConsoleAuthority"/>: every moved console module addresses the boot row. None of
/// the moved verbs carry a trailing <c>instance:&lt;name&gt;</c> token the way the <c>player.*</c> and <c>world.instance.start</c> family
/// do (see <see cref="PlayerCommandModule"/>), so resolving unconditionally to <see cref="WorldInstanceHost.Boot"/>
/// is exact, not a placeholder — a desktop process runs exactly one console, bound to exactly one row.
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
}
