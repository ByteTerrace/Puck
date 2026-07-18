namespace Puck.Commands;

/// <summary>
/// The console verbs' shared availability-guard idiom — every stateful command module wraps its editing handlers so
/// a verb typed before its target exists (the composition root not yet resolved, or the target's own mode not yet
/// entered) reports a friendly narration instead of throwing. Two independent gates compose:
/// <list type="bullet">
/// <item><b>Host gate</b> (always checked): the target getter returns <see langword="null"/> when the composition
/// point isn't ready — the host's root isn't the active root, or (for a nested target) the parent scene hasn't
/// resolved yet. Every module needs this one.</item>
/// <item><b>Active gate</b> (only when the target actually models an on/off mode): once the target resolves, an
/// optional second predicate reports whether the target's OWN mode is entered. Targets with no such concept (the
/// composition stays live for the whole run once the root resolves) pass <see langword="null"/> and only the host
/// gate applies — this mirrors each target's REAL state machine rather than forcing a uniform two-tier check where
/// only one tier exists.</item>
/// </list>
/// The message shape is picked from the clearest of the pre-unification wordings: "[[verb]: unavailable — the
/// host is not the active root]" for the host gate, "[[verb]: enter [mode] mode first (console: [mode])]" for
/// the active gate.
/// </summary>
public static class CommandAvailability {
    /// <summary>Wraps a no-argument, target-editing handler with the host gate (and, when <paramref name="isActive"/>
    /// is given, the active gate too).</summary>
    /// <typeparam name="T">The target type (a scene, or a small host adapter).</typeparam>
    /// <param name="getTarget">Resolves the live target, or <see langword="null"/> when the host isn't ready.</param>
    /// <param name="unavailableMessage">The narration when <paramref name="getTarget"/> returns <see langword="null"/>.</param>
    /// <param name="handler">The target-editing logic.</param>
    /// <param name="isActive">Optional: whether the target's own mode is entered. Omit when the target has no
    /// separate on/off mode.</param>
    /// <param name="inactiveMessage">The narration when <paramref name="isActive"/> is given and returns
    /// <see langword="false"/>. Required whenever <paramref name="isActive"/> is given.</param>
    public static Func<CommandContext, CommandResult> WithTarget<T>(
        Func<T?> getTarget,
        string unavailableMessage,
        Func<T, string> handler,
        Func<T, bool>? isActive = null,
        string? inactiveMessage = null
    ) where T : class {
        return _ => {
            if (getTarget() is not { } target) {
                return new CommandResult(unavailableMessage);
            }

            if ((isActive is not null) && !isActive(target)) {
                return new CommandResult(inactiveMessage!);
            }

            return new CommandResult(handler(arg: target));
        };
    }

    /// <summary>The argument-taking counterpart of <see cref="WithTarget{T}(Func{T},string,Func{T,string},Func{T,bool},string)"/>.</summary>
    public static Func<CommandContext, string[], CommandResult> WithTargetArgs<T>(
        Func<T?> getTarget,
        string unavailableMessage,
        Func<T, string[], string> handler,
        Func<T, bool>? isActive = null,
        string? inactiveMessage = null
    ) where T : class {
        return (_, args) => {
            if (getTarget() is not { } target) {
                return new CommandResult(unavailableMessage);
            }

            if ((isActive is not null) && !isActive(target)) {
                return new CommandResult(inactiveMessage!);
            }

            return new CommandResult(handler(arg1: target, arg2: args));
        };
    }
}
