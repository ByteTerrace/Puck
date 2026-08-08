using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// Publishes each seat's admitted context-family states (<see cref="WorldContextFamilies"/>) AND its resolved
/// perception anchor (<see cref="WorldPerceptionAnchor"/>) — the one derivation feed, shared by the post-step sync
/// (<see cref="WorldSimulation"/>, every tick, so a state change flips the derived group and swaps the anchor the
/// same tick it applied) and the post-build wiring (once at boot, so a pre-first-tick read-back reports the boot
/// census truthfully instead of the resolvers' cold defaults). Roster is the client roster's own lifecycle tuple
/// made one value; engagement AND the anchor are both READS over the server grant table's single-valued Control
/// route for the seat's acting principal — the same in-process loopback discipline as <c>CheckEngage</c>, never a
/// parallel latch, and the anchor rides this SAME read rather than opening a second one (one source of truth).
/// <see cref="WorldSeatBindings.SetContextState"/> short-circuits on an unchanged state, so an ordinary tick costs
/// two string compares, one route lookup, and one array write per seat.
/// </summary>
internal static class WorldSeatContextSync {
    /// <summary>Publishes both admitted families' current states, and the resolved perception anchor, for every
    /// local seat.</summary>
    /// <param name="seatBindings">The per-seat binding resolver the states publish into.</param>
    /// <param name="roster">The client roster (the roster family's machine, and the seat→acting-principal map).</param>
    /// <param name="grants">The server grant table the engagement family and the anchor read the Control route from.</param>
    /// <param name="anchor">The per-seat perception anchor the resolved possession target publishes into.</param>
    public static void Publish(WorldSeatBindings seatBindings, PlayerRoster roster, IWorldGrantsView grants, WorldPerceptionAnchor anchor) {
        for (var slot = 0; (slot < WorldSeatBindings.SeatCount); slot++) {
            var principal = roster.PrincipalOf(slot: slot);
            var route = grants.ControlRoute(principal: principal);

            seatBindings.SetContextState(slot: slot, family: WorldContextFamilies.Roster, state: RosterStateOf(roster: roster, slot: slot));
            seatBindings.SetContextState(
                slot: slot,
                family: WorldContextFamilies.Engagement,
                state: ((route is not null) ? WorldContextFamilies.EngagementEngaged : WorldContextFamilies.EngagementNone)
            );

            // Possession means possession: a route targeting a BODY with capture ON swaps the seat's entire
            // perceived world onto that body, in this one place. A mirror route (capture off) keeps driving a
            // machine AND walking the seat's own avatar, so it stays perceiving from that avatar; a screen route
            // (classic engage) never swaps either. Anything else — no route, a screen route, or a mirrored body
            // route — perceives from the seat's own bound body (the slot index).
            anchor.Publish(
                slot: slot,
                bodyIndex: (((route is { Kind: GrantSubjectKind.Body } bodyTarget) && grants.RouteCapture(principal: principal))
                    ? bodyTarget.Value
                    : slot)
            );
        }
    }

    // The roster family's single value — the joined/claimed/pending booleans the roster already publishes, made one
    // state (claimed before pending/active: an exclusive claim overrides the participant's own lifecycle for gestures).
    private static string RosterStateOf(PlayerRoster roster, int slot) {
        if (!roster.IsJoined(slot: slot)) {
            return WorldContextFamilies.RosterUnjoined;
        }

        if (roster.IsClaimed(slot: slot)) {
            return WorldContextFamilies.RosterClaimed;
        }

        return (roster.IsPending(slot: slot) ? WorldContextFamilies.RosterPending : WorldContextFamilies.RosterActive);
    }
}
