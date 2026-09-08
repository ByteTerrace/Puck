using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// Publishes each seat's roster/engagement context states and its resolved
/// perception anchor (<see cref="WorldPerceptionAnchor"/>) — the one derivation feed, shared by the post-step sync
/// (<see cref="WorldSimulation"/>, every tick, so a state change flips the derived group and swaps the anchor the
/// same tick it applied) and the post-build wiring (once at boot, so a pre-first-tick read-back reports the boot
/// census truthfully instead of the resolvers' cold defaults). Roster is the client roster's own lifecycle tuple
/// made one value; engagement and the anchor are both reads over the server grant table's control-application set
/// for the seat's acting principal — the same in-process loopback discipline as <c>CheckEngage</c>, never a
/// parallel latch, and the anchor rides this same read rather than opening a second one (one source of truth).
/// State-backed control contexts are published separately by <see cref="WorldSeatBindings.SyncSeat"/> from the
/// seat's routed definition. <see cref="WorldSeatBindings.SetContextState"/> short-circuits on an unchanged value.
/// </summary>
internal static class WorldSeatContextSync {
    // The roster family's single value — the joined/claimed/pending booleans the roster already publishes, made one
    // state (claimed before pending/active: an exclusive claim overrides the participant's own lifecycle for gestures).
    private static string RosterStateOf(PlayerRoster roster, int slot) {
        if (!roster.IsJoined(slot: slot)) {
            return WorldContextFamilies.RosterUnjoined;
        }

        if (roster.IsClaimed(slot: slot)) {
            return WorldContextFamilies.RosterClaimed;
        }

        return (roster.IsPending(slot: slot)
            ? WorldContextFamilies.RosterPending
            : WorldContextFamilies.RosterActive
        );
    }

    /// <summary>Publishes roster, engagement, and layout state, and the resolved perception anchor, for every
    /// local seat.</summary>
    /// <param name="seatBindings">The per-seat binding resolver the states publish into.</param>
    /// <param name="roster">The client roster (the roster family's machine, and the seat→acting-principal map).</param>
    /// <param name="grants">The server grant table the engagement family and the anchor read the Control route from.</param>
    /// <param name="anchor">The per-seat perception anchor the resolved possession target publishes into.</param>
    /// <param name="activeLayout">The window composer's active layout selection (window-wide, so every seat
    /// publishes the same value) — an authored layout name, or <see cref="WorldViewComposer"/>'s <c>builtin</c>.</param>
    public static void Publish(WorldSeatBindings seatBindings, PlayerRoster roster, IWorldGrantsView grants, WorldPerceptionAnchor anchor, string activeLayout) {
        for (var slot = 0; (slot < WorldSeatBindings.SeatCount); slot++) {
            var principal = roster.PrincipalOf(slot: slot);
            var applications = grants.Applications(principal: principal);
            var own = GrantSubject.Body(index: principal.Index);
            var holdsOwn = false;
            var possessed = -1;
            var composed = false;

            foreach (var application in applications) {
                if (application.Target == own) {
                    holdsOwn = true;

                    continue;
                }

                composed = true;

                if (
                    (application.Target.Kind == GrantSubjectKind.Body) &&
                    (possessed < 0)
                ) {
                    possessed = application.Target.Value;
                }
            }

            seatBindings.SetContextState(
                slot: slot,
                family: WorldContextFamilies.Roster,
                state: RosterStateOf(
                    roster: roster,
                    slot: slot
                )
            );
            seatBindings.SetContextState(
                family: WorldContextFamilies.Engagement,
                slot: slot,
                state: (composed
                ? WorldContextFamilies.EngagementEngaged
                : WorldContextFamilies.EngagementNone)
            );
            seatBindings.SetContextState(
                family: WorldContextFamilies.Layout,
                slot: slot,
                state: activeLayout
            );

            // Possession means possession: a set naming a BODY while OMITTING its own-body application swaps the
            // seat's entire perceived world onto that body, in this one place. A set that retains its own-body
            // application keeps walking the seat's avatar, so it stays perceiving from that avatar; a screen
            // application never swaps either.
            anchor.Publish(
                bodyIndex: ((!holdsOwn && (possessed >= 0))
                ? possessed
                : slot),
                slot: slot
            );
        }
    }
}
