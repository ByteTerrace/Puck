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
    /// <summary>Returns the view this invocation reads the resolved row's state through: the document as its acting
    /// principal may read it. Every console read-back of state values reaches the row through this one member.</summary>
    /// <param name="context">The invocation's context — carries the acting session's identity.</param>
    /// <param name="instance">The row <see cref="TryResolve"/> resolved.</param>
    /// <returns>The acting principal's view; the operator's reads the live document whole.</returns>
    WorldStateReadView ReadView(CommandContext context, WorldInstance instance) => WorldStateReadView.Of(
        reader: context.Principal,
        server: instance.Server
    );
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
    /// <summary>Resolves this invocation's row and hands back the view its acting principal reads that row's state
    /// through — the shape every read-back of state values opens with, in place of the server itself.</summary>
    /// <param name="authority">The authority to resolve against.</param>
    /// <param name="context">The invocation's context.</param>
    /// <param name="verb">The calling verb's name, for the refusal echo.</param>
    /// <param name="view">The acting principal's view, on success.</param>
    /// <param name="error">The inline refusal echo, on failure.</param>
    /// <returns><see langword="true"/> when a row was resolved.</returns>
    public static bool TryResolveReadView(this IWorldConsoleAuthority authority, CommandContext context, string verb, out WorldStateReadView view, out CommandResult error) {
        if (!authority.TryResolve(
            context: context,
            instance: out var instance,
            refusal: out var refusal
        )) {
            view = null!;
            error = CommandResult.Error(output: $"[{verb}: refused ({refusal})]");

            return false;
        }

        view = authority.ReadView(
            context: context,
            instance: instance
        );
        error = default;

        return true;
    }
    /// <summary>Validates that no arguments were passed, then resolves the invocation's row and hands back its
    /// <see cref="WorldServer"/> directly — the shape zero-argument query and inspection commands open with.</summary>
    /// <param name="authority">The authority to resolve against.</param>
    /// <param name="context">The invocation's context.</param>
    /// <param name="args">The invocation's wire arguments.</param>
    /// <param name="verb">The calling verb's name, for the refusal echo.</param>
    /// <param name="server">The resolved row's server, on success.</param>
    /// <param name="error">The inline refusal echo, on failure.</param>
    /// <returns><see langword="true"/> when no arguments were supplied and a row was resolved.</returns>
    public static bool TryResolveServerWithoutArguments(this IWorldConsoleAuthority authority, CommandContext context, in WireArgs args, string verb, out WorldServer server, out CommandResult error) {
        if (CommandResult.RequireNoArguments(
            args: args,
            verb: verb
        ) is { } refusal) {
            server = null!;
            error = refusal;

            return false;
        }

        return authority.TryResolveServer(
            context: context,
            error: out error,
            server: out server,
            verb: verb
        );
    }
    /// <summary>Creates an unbindable wire command that validates zero arguments, resolves the server, and outputs the result of <paramref name="describe"/>.</summary>
    public static CommandDefinition CreateServerQueryCommand(
        this IWorldConsoleAuthority authority,
        string name,
        string description,
        Func<WorldServer, string> describe
    ) =>
        CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            description: description,
            handler: (context, args) => {
                if (!authority.TryResolveServerWithoutArguments(
                    args: in args,
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: name
                )) {
                    return error;
                }

                return new CommandResult(Output: describe(server));
            },
            name: name
        );
    /// <summary>Creates an unbindable wire command that validates at most one argument (<paramref name="paramName"/>), resolves the server, and outputs the result of <paramref name="describe"/>.</summary>
    public static CommandDefinition CreateOptionalFilterCommand(
        this IWorldConsoleAuthority authority,
        string name,
        string description,
        string paramName,
        Func<string?, WorldServer, string> describe
    ) =>
        CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            description: description,
            handler: (context, args) => {
                if (args.Count > 1) {
                    return CommandResult.Error(output: $"[{name}: expected [{paramName}]]");
                }

                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: name
                )) {
                    return error;
                }

                return new CommandResult(Output: describe(
                    ((args.Count == 1)
                        ? args[0].ToString()
                        : null),
                    server
                ));
            },
            name: name
        );
}
