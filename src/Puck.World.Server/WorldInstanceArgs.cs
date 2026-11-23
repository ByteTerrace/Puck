using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>Resolves a verb's trailing <c>instance:&lt;name&gt;</c> token (<see cref="WorldInstanceToken.Prefix"/>) to
/// the running instance it names, for the console verbs that read the instance itself; they run in the control phase,
/// inside the boundary window.</summary>
public static class WorldInstanceArgs {
    /// <summary>Parses and resolves a trailing instance token to its running instance, layering the "no instance
    /// named '…'" refusal over <see cref="WorldInstanceToken.TryParseName"/> — the form every instance-addressed read-back
    /// that needs the instance object itself (not just its name) shares.</summary>
    /// <param name="token">The token, prefix included.</param>
    /// <param name="verb">The verb name the refusal text is scoped to.</param>
    /// <param name="instances">The host to resolve the name against.</param>
    /// <param name="instance">The resolved instance on success.</param>
    /// <param name="error">The refusal, on failure.</param>
    /// <returns>Whether the token resolved to a running instance.</returns>
    public static bool TryResolveInstance(ReadOnlySpan<char> token, string verb, WorldInstanceHost instances, out WorldInstance? instance, out CommandResult? error) {
        if (!WorldInstanceToken.TryParseName(
            error: out error,
            name: out var name,
            token: token,
            verb: verb
        )) {
            instance = null;

            return false;
        }

        if (
            !instances.TryGet(
            instance: out var resolved,
            name: name
        ) ||
            (resolved is null)
        ) {
            instance = null;
            error = CommandResult.Error(output: $"[{verb}: no instance named '{name}' — see world.instance.status]");

            return false;
        }

        instance = resolved;
        error = null;

        return true;
    }
}
