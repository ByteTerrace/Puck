using System.Numerics;
using Puck.Abstractions.Machines;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The ENGAGE route — where a player's resolved intent GOES, distinct from the <see cref="IntentSource"/> axis (what
/// fills it). It is a VIEW over the server's ONE capability table (<see cref="WorldGrants"/>): a player's engagement is
/// an exclusive-per-body <see cref="WorldCapability.Control"/> route grant over a screen subject — no parallel route
/// table (§0.1 dedup). While a player is engaged its per-tick <see cref="WorldBody.EngagedIntent"/> is translated to a
/// neutral <see cref="MachinePadState"/> and delivered to that screen's machine (the binder pulls <see cref="MergedPad"/>
/// each host tick), and the avatar stands idle. The translation is generic (an intent → a standard-controller image);
/// each machine's engine maps that image to its own buttons.
/// </summary>
/// <remarks>Single-threaded, like the roster/table: the <c>player.engage</c>/<c>disengage</c> verbs mutate the grants
/// on the command pump and the binder reads them during produce, both on the launcher's window-pump thread, so no lock
/// guards this state. A display index (1..4 local seats, 5..128 population entries) maps to a <see cref="WorldPrincipal"/>
/// (<see cref="PrincipalKind.Seat"/> / <see cref="PrincipalKind.Peer"/>) and is re-resolved to a live
/// <see cref="WorldBody"/> each read; a seat that leaves, an entry that deactivates, or a newly-created body at the same
/// index whose <see cref="WorldBody.Engaged"/> latch is clear is PRUNED (its route grant cleared) rather than inheriting
/// a stale route — the per-body <see cref="WorldBody.Engaged"/> latch is the liveness gate, exactly as before.
/// LOOPBACK-ONLY: the grant reads/writes and the <see cref="WorldBody.SetEngaged"/>/<see cref="WorldBody.EngagedIntent"/>
/// touches here are in-process; a socket transport replaces them with engage/disengage commands over
/// <see cref="Protocol.IServerLink"/> and a per-tick engaged-intent lane on the snapshot.</remarks>
internal sealed class WorldEngagement {
    private readonly PlayerRoster m_roster;
    private readonly WorldServer m_server;
    private readonly WorldGrants m_grants;
    // Reused scratch for the per-frame MergedPad/PlayersOn collect+prune, so the hot path allocates nothing after warmup.
    private readonly List<WorldPrincipal> m_holderScratch = new();
    private readonly List<WorldPrincipal> m_staleScratch = new();

    /// <summary>Initializes the engagement view over the roster (seat liveness) and server (bodies 1..128 and the ONE
    /// grant table the routes live in).</summary>
    /// <param name="roster">The participant roster.</param>
    /// <param name="server">The authoritative server (its <see cref="WorldServer.Grants"/> hold the routes).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldEngagement(PlayerRoster roster, WorldServer server) {
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: server);

        m_roster = roster;
        m_server = server;
        m_grants = server.Grants;
    }

    /// <summary>Engages a player (by 1-based display index) on a screen: checks the principal holds
    /// <see cref="WorldCapability.Control"/> over the screen (the default permissive Control/all satisfies it), latches
    /// the body's <see cref="WorldBody.SetEngaged"/> route so its intent diverts to the screen's machine, drops a seat's
    /// held device state (nothing leaks into the machine), and records the route as the principal's exclusive Control
    /// screen grant (re-pointing an already-engaged player just replaces the route). Policy (engageable, proximity, a
    /// machine present) is enforced by the caller against the declared screen data. Returns <see langword="false"/> when
    /// the principal lacks Control over the screen.</summary>
    /// <param name="index">The 1-based player display index (1..4 seat, 5..128 population entry).</param>
    /// <param name="body">The resolved body the caller validated.</param>
    /// <param name="screenIndex">The engine screen-surface index to engage.</param>
    /// <returns>Whether the engagement was permitted and recorded.</returns>
    public bool Engage(int index, WorldBody body, int screenIndex) {
        var principal = PrincipalFor(index: index);

        if (!m_grants.Allows(principal: principal, capability: WorldCapability.Control, subject: GrantSubject.Screen(index: screenIndex))) {
            return false;
        }

        body.SetEngaged(engaged: true);
        SeatFor(index: index)?.ReleaseAllHeld();
        m_grants.SetControlRoute(principal: principal, screenIndex: screenIndex);

        return true;
    }

    /// <summary>Disengages a player: clears its route latch and drops a seat's held device state (so nothing leaks
    /// across the boundary and the avatar does not burst into motion), then clears its Control route grant. A no-op that
    /// returns <see langword="false"/> when the player was not engaged.</summary>
    /// <param name="index">The 1-based player display index.</param>
    /// <param name="body">The resolved body.</param>
    /// <returns>Whether the player had been engaged.</returns>
    public bool Disengage(int index, WorldBody body) {
        body.SetEngaged(engaged: false);
        SeatFor(index: index)?.ReleaseAllHeld();

        return m_grants.ClearControlRoute(principal: PrincipalFor(index: index));
    }

    /// <summary>Disengages EVERY player routed to <paramref name="screenIndex"/> — the clean teardown a screen removal
    /// runs before the binder disposes the slot: each engaged body's latch is cleared (its avatar resumes normal intent
    /// rather than being held idle against an invisible machine), a seat's held device state is dropped, and the Control
    /// route grant is cleared. Iterates a scratch copy of the holders, so it never mutates a live grant enumeration.</summary>
    /// <param name="screenIndex">The engine screen-surface index being removed.</param>
    public void DisengageScreen(int screenIndex) {
        m_grants.CollectRouteHolders(screenIndex: screenIndex, into: m_holderScratch);

        foreach (var principal in m_holderScratch) {
            var index = DisplayFor(principal: principal);

            if ((index > 0) && (Resolve(index: index) is { } body)) {
                body.SetEngaged(engaged: false);
                SeatFor(index: index)?.ReleaseAllHeld();
            }

            _ = m_grants.ClearControlRoute(principal: principal);
        }

        m_holderScratch.Clear();
    }

    // The seat controller behind a display index, or null for a population entry (which has no device state to drop).
    private SeatController? SeatFor(int index) {
        return ((index <= PlayerRoster.MaxSlots)
            ? m_roster.Seat(slot: PlayerRoster.SlotFromDisplay(number: index))
            : null);
    }

    /// <summary>The engine screen index a live player is engaged on, or <see langword="null"/> when it is not engaged.
    /// A route whose display index no longer resolves to an engaged player is stale and its grant is cleared.</summary>
    /// <param name="index">The 1-based player display index.</param>
    public int? EngagedScreen(int index) {
        var principal = PrincipalFor(index: index);

        if (m_grants.ControlRoute(principal: principal) is not { } screen) {
            return null;
        }

        if (TryResolveEngaged(index: index, player: out _)) {
            return screen;
        }

        _ = m_grants.ClearControlRoute(principal: principal);

        return null;
    }

    /// <summary>The 1-based display indices of every player currently engaged on <paramref name="screenIndex"/>, in
    /// ascending order — the players whose intents OR-merge onto that screen's machine. Prunes any route that no longer
    /// resolves to an engaged body.</summary>
    /// <param name="screenIndex">The engine screen index.</param>
    /// <returns>The engaged players' display indices.</returns>
    public IReadOnlyList<int> PlayersOn(int screenIndex) {
        m_grants.CollectRouteHolders(screenIndex: screenIndex, into: m_holderScratch);

        var players = new List<int>(capacity: m_holderScratch.Count);

        m_staleScratch.Clear();

        foreach (var principal in m_holderScratch) {
            var index = DisplayFor(principal: principal);

            if ((index > 0) && TryResolveEngaged(index: index, player: out _)) {
                players.Add(item: index);
            } else {
                m_staleScratch.Add(item: principal);
            }
        }

        PruneStale();
        players.Sort();

        return players;
    }

    /// <summary>The controller image for a screen's machine THIS frame — the merge of every engaged player's translated
    /// intent (the multiplayer-cabinet shape: buttons OR, stick axes sum and clamp). Re-resolves each engaged player and
    /// PRUNES any that no longer resolve (a seat that left, an entry that deactivated), so a stale route never lingers.
    /// <see cref="MachinePadState.Neutral"/> when no player is engaged on the screen. Allocation-free after warmup.</summary>
    /// <param name="screenIndex">The engine screen index.</param>
    /// <returns>The merged pad image for this tick's machine step.</returns>
    public MachinePadState MergedPad(int screenIndex) {
        m_grants.CollectRouteHolders(screenIndex: screenIndex, into: m_holderScratch);

        var pad = MachinePadState.Neutral;

        m_staleScratch.Clear();

        foreach (var principal in m_holderScratch) {
            var index = DisplayFor(principal: principal);

            if ((index > 0) && TryResolveEngaged(index: index, player: out var player)) {
                pad = MachinePadState.Merge(first: in pad, second: Translate(intent: player.EngagedIntent));
            } else {
                m_staleScratch.Add(item: principal);
            }
        }

        PruneStale();

        return pad;
    }

    // Clear the Control route of every principal collected as stale this pass (its body no longer engaged/live). Runs
    // over the scratch copy, so it never mutates a live enumeration.
    private void PruneStale() {
        foreach (var principal in m_staleScratch) {
            _ = m_grants.ClearControlRoute(principal: principal);
        }

        m_staleScratch.Clear();
    }

    /// <summary>Translates a resolved <see cref="PlayerIntent"/> to a neutral standard-controller image — the pure,
    /// generic mapping the world applies ONCE, with no per-machine smarts (each machine's engine maps this image to its
    /// own buttons). The movement channels ride the left stick VERBATIM (no quantization here — a digital d-pad's
    /// threshold is the machine's own concern): <see cref="PlayerIntent.MoveStrafe"/> → left stick <c>X</c>,
    /// <see cref="PlayerIntent.MoveForward"/> → left stick <c>Y</c>. The <see cref="ActionLanes.Primary"/> channel →
    /// <see cref="MachineButtons.South"/>. The yaw/6DOF channels have no standard-pad channel yet, so they are not
    /// mapped; new action lanes map to more buttons here (the growth point, not per-button smarts).</summary>
    /// <param name="intent">The resolved intent to translate.</param>
    /// <returns>The controller image the intent presses.</returns>
    public static MachinePadState Translate(PlayerIntent intent) {
        var buttons = (((intent.Actions & ActionLanes.Primary) != ActionLanes.None) ? MachineButtons.South : MachineButtons.None);

        return new MachinePadState(
            Buttons: buttons,
            LeftStick: new Vector2(x: (float)(double)intent.MoveStrafe, y: (float)(double)intent.MoveForward),
            RightStick: Vector2.Zero,
            LeftTrigger: 0f,
            RightTrigger: 0f
        );
    }

    // The principal that owns a 1-based display index: 1..4 the local seats, 5..128 the population entries (their
    // reserved Peer identity). Both carry Index = display-1, so DisplayFor is the exact inverse.
    private static WorldPrincipal PrincipalFor(int index) {
        return ((index <= PlayerRoster.MaxSlots)
            ? WorldPrincipal.Seat(slot: PlayerRoster.SlotFromDisplay(number: index))
            : WorldPrincipal.Peer(index: (index - 1)));
    }

    // The 1-based display index a Seat/Peer principal maps back to (Index + 1), or -1 for a principal that never holds a
    // screen route (Console/Addon) — a defensive skip.
    private static int DisplayFor(WorldPrincipal principal) {
        return ((principal.Kind is PrincipalKind.Seat or PrincipalKind.Peer) ? (principal.Index + 1) : -1);
    }

    // Resolve a 1-based display index to its live body: 1..4 the seats (gated on roster membership), 5..128 the
    // population entries. Null for an unjoined seat or an inactive entry — the caller prunes it.
    private WorldBody? Resolve(int index) {
        if (index <= PlayerRoster.MaxSlots) {
            var slot = PlayerRoster.SlotFromDisplay(number: index);

            return (m_roster.IsJoined(slot: slot) ? m_server.Body(index: slot) : null);
        }

        return m_server.Body(index: (index - 1));
    }

    // A display index is only a live route while the body it resolves to owns the matching engagement latch. Population
    // reactivation deliberately mints a fresh, disengaged WorldBody at the same index; the old index-only route must not
    // transfer to that new lifetime.
    private bool TryResolveEngaged(int index, out WorldBody player) {
        if (Resolve(index: index) is { Engaged: true } resolved) {
            player = resolved;

            return true;
        }

        player = null!;

        return false;
    }
}
