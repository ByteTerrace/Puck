using Puck.Commands;

namespace Puck.World.Protocol;

/// <summary>
/// THE seam between the ingress layer's <see cref="CommandPrincipal"/> and the world's own
/// <see cref="WorldPrincipal"/>. One file, both directions, exhaustive switches — never a cast between the two
/// discriminants, because their values are pinned independently and a cast would silently make one enum's ordinals
/// the other's meaning.
/// </summary>
/// <remarks>
/// This is also the only place in <c>Puck.World</c> that MINTS a principal from something other than a read. A
/// command handler asks <see cref="ToWorld"/> for the identity its <see cref="CommandContext"/> already carries;
/// a handler that constructs <see cref="WorldPrincipal.Console"/> or <see cref="WorldPrincipal.Seat"/> instead is
/// asserting an identity rather than carrying the one its ingress door stamped.
/// </remarks>
public static class WorldPrincipalMapping {
    /// <summary>Returns the world identity for a stamped ingress principal.</summary>
    /// <param name="principal">The principal an ingress door stamped.</param>
    /// <returns>The equivalent <see cref="WorldPrincipal"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="principal"/> carries no stamped kind — a
    /// dispatch reached a handler without passing a door, which is a defect rather than an anonymous caller.</exception>
    public static WorldPrincipal ToWorld(CommandPrincipal principal) {
        return principal.Kind switch {
            CommandPrincipalKind.Console => WorldPrincipal.Console,
            CommandPrincipalKind.Seat => WorldPrincipal.Seat(slot: principal.Index),
            CommandPrincipalKind.Addon => WorldPrincipal.Addon(name: (principal.Name ?? string.Empty)),
            CommandPrincipalKind.Peer => WorldPrincipal.Peer(index: principal.Index, generation: principal.Generation),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(principal), actualValue: principal.Kind, message: "A command reached its handler carrying no stamped principal; every ingress door must stamp one."),
        };
    }

    /// <summary>Returns the ingress principal for a world identity — the direction the roster answers
    /// <see cref="ICommandPrincipalResolver.PrincipalOf"/> through.</summary>
    /// <param name="principal">The world identity.</param>
    /// <returns>The equivalent <see cref="CommandPrincipal"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="principal"/> carries an unknown kind.</exception>
    public static CommandPrincipal ToCommand(WorldPrincipal principal) {
        return principal.Kind switch {
            PrincipalKind.Console => CommandPrincipal.Console,
            PrincipalKind.Seat => CommandPrincipal.Seat(slot: principal.Index),
            PrincipalKind.Addon => CommandPrincipal.Addon(name: (principal.Name ?? string.Empty)),
            PrincipalKind.Peer => CommandPrincipal.Peer(index: principal.Index, generation: principal.Generation),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(principal), actualValue: principal.Kind, message: "Unknown world principal kind."),
        };
    }

    /// <summary>Returns the world identity acting through a dispatched command — the ONE call a handler makes to attribute
    /// its action.</summary>
    /// <param name="context">The dispatch context.</param>
    /// <returns>The acting <see cref="WorldPrincipal"/>.</returns>
    public static WorldPrincipal ActingPrincipal(this in CommandContext context) => ToWorld(principal: context.Principal);
}
