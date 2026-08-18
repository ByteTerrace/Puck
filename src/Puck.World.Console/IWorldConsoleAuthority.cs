using Puck.Commands;

namespace Puck.World.Server;

/// <summary>
/// Resolves the <see cref="WorldInstance"/> a console invocation addresses. The desktop's implementation
/// (<c>Puck.World</c>) always answers the boot row; a hosted implementation answers the row the session's own tag
/// bound it to. Every moved command module reaches its target row through this seam instead of an injected
/// <see cref="WorldServer"/> singleton, so the same module runs unchanged whether one row exists or many.
/// </summary>
public interface IWorldConsoleAuthority {
    /// <summary>Resolves the row this invocation addresses.</summary>
    /// <param name="context">The invocation's context — carries the acting session's identity.</param>
    /// <param name="instance">The resolved row, on success.</param>
    /// <param name="refusal">The refusal reason, on failure.</param>
    /// <returns><see langword="true"/> when a row was resolved.</returns>
    bool TryResolve(CommandContext context, out WorldInstance instance, out string refusal);
}
/// <summary>Shared resolve-and-echo helper every moved command module's handler opens with.</summary>
public static class WorldConsoleAuthorityExtensions {
    /// <summary>Resolves this invocation's row and hands back its <see cref="WorldServer"/> directly — the shape
    /// every moved handler that reads or mutates through the server (rather than through <c>IServerLink</c>) needs.</summary>
    /// <param name="authority">The authority to resolve against.</param>
    /// <param name="context">The invocation's context.</param>
    /// <param name="verb">The calling verb's name, for the refusal echo.</param>
    /// <param name="server">The resolved row's server, on success.</param>
    /// <param name="error">The inline refusal echo, on failure.</param>
    /// <returns><see langword="true"/> when a row was resolved.</returns>
    public static bool TryResolveServer(this IWorldConsoleAuthority authority, CommandContext context, string verb, out WorldServer server, out CommandResult error) {
        if (!authority.TryResolve(
            context: context,
            instance: out var instance,
            refusal: out var refusal
        )) {
            server = null!;
            error = CommandResult.Error(output: $"[{verb}: refused ({refusal})]");

            return false;
        }

        server = instance.Server;
        error = default;

        return true;
    }
}
