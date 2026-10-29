using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// Decides which edit outcomes reach the overlay toast. A toast narrates what a player's own act caused: a seat,
/// a peer or a session viewer. An operator's act (the console, a control attachment, a sweep or schedule prelude,
/// every one stamped <see cref="PrincipalKind.Console"/>), the world's own program and a scoped replay's recorded input
/// report through the console mirror, the log and the cue lane instead. An addon's applied edit is its guest's
/// ordinary output, so only its refusals toast.
/// </summary>
public static class WorldToastPolicy {
    /// <summary>Determines whether an edit outcome raises a toast.</summary>
    /// <param name="echo">The outcome the server echoed.</param>
    /// <returns><see langword="true"/> when the toast should publish.</returns>
    public static bool Narrates(in WorldEditEcho echo) {
        if (echo.ConnectionId == SubmissionEnvelope.ScopedReplayConnectionId) {
            return echo.Rejected;
        }

        return echo.ActingPrincipal.Kind switch {
            PrincipalKind.Console or PrincipalKind.World => false,
            PrincipalKind.Addon => echo.Rejected,
            _ => true,
        };
    }
}
