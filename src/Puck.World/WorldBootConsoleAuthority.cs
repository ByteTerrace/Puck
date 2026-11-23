using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The desktop's <see cref="IWorldConsoleAuthority"/>: a console line addresses the boot row, or, through a trailing
/// <c>instance:&lt;name&gt;</c> token a verb honours (<see cref="WorldConsoleAuthorityExtensions.TryResolveAddressed"/>),
/// any other row running in this process, read under that row's own grants.
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
    public bool TryResolveNamed(CommandContext context, ReadOnlySpan<char> token, string verb, out WorldInstance instance, out CommandResult error) {
        if (WorldInstanceArgs.TryResolveInstance(error: out var refusal, instance: out var named, instances: instances, token: token, verb: verb)) {
            instance = named!;
            error = default;

            return true;
        }

        instance = null!;
        error = refusal!.Value;

        return false;
    }
}
