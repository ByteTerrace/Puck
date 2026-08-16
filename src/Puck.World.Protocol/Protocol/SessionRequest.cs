namespace Puck.World.Protocol;

/// <summary>A session/identity request a client submits to the authoritative server — the closed set the roster/population
/// management verbs translate into (join/leave/profile → seat allocation, population/behavior → the simulated census).
/// The server validates it and answers with a <see cref="SessionReply"/>. Every request carries its acting
/// <see cref="Principal"/> on the base (uniform with <see cref="WorldCommand"/> and <see cref="WorldMutation"/>).
/// <see cref="Join"/>/<see cref="Leave"/>/<see cref="SetIdentity"/> are gated on <see cref="WorldCapability.Drive"/> over
/// the targeted slot's <see cref="GrantSubject.Body"/> (the same grant every seat is seeded with over its own body);
/// <see cref="SetPopulation"/>/<see cref="SetPeerSource"/> are gated on <see cref="WorldCapability.Mutate"/> over
/// <see cref="WorldSection.Population"/> — a principal seeded nothing (an addon) can drive none of them.</summary>
/// <param name="Principal">The acting identity the request is attributed to.</param>
public abstract record SessionRequest(WorldPrincipal Principal) {
    /// <summary>Joins a player: a named <see cref="IdentityName"/> joins directly active on it, a null one joins pending
    /// (a profile is chosen, then confirmed). A <see cref="Slot"/> of -1 takes the next free slot. The
    /// <paramref name="WireProtocolKey"/> is checked against <see cref="WorldProtocol.WireProtocolKey"/> — a mismatch is rejected
    /// with a reason.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Slot">The 0-based slot to join, or -1 for the next free slot.</param>
    /// <param name="IdentityName">The owned-world identity to seat on, or <see langword="null"/> to join pending.</param>
    /// <param name="WireProtocolKey">The client's <see cref="WorldProtocol.WireProtocolKey"/> echo.</param>
    public sealed record Join(WorldPrincipal Principal, int Slot, string? IdentityName, ulong WireProtocolKey) : SessionRequest(Principal);
    /// <summary>Removes a scripted or device player, unmapping its devices and freeing its profile (slot 0 never leaves).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Slot">The 0-based slot to free.</param>
    public sealed record Leave(WorldPrincipal Principal, int Slot) : SessionRequest(Principal);
    /// <summary>Sets a specific owned-world identity on a slot's participant.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Slot">The 0-based slot.</param>
    /// <param name="IdentityName">The identity to seat on.</param>
    public sealed record SetIdentity(WorldPrincipal Principal, int Slot, string IdentityName) : SessionRequest(Principal);
    /// <summary>Sets the active simulated-peer census (the <c>world.population &lt;n&gt;</c> count). Newly activated
    /// peers take the stored peer-source default; existing peers keep their per-entity source.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Count">The requested active simulated count.</param>
    public sealed record SetPopulation(WorldPrincipal Principal, int Count) : SessionRequest(Principal);
    /// <summary>Sets the peer intent-source default AND sweeps every peer (4..127) to it — last-writer-wins; a
    /// per-entity source does not survive the global flip.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Source">The intent source to store and sweep.</param>
    public sealed record SetPeerSource(WorldPrincipal Principal, IntentSource Source) : SessionRequest(Principal);

}
/// <summary>The server's answer to a <see cref="SessionRequest"/>: whether it was accepted, the 1-based display index it
/// assigned or acted on (or -1 when none), the roster echo the client prints verbatim, and a rejection reason (empty
/// when accepted — the seam the protocol-version handshake reports a mismatch through).</summary>
/// <param name="Accepted">Whether the request was accepted.</param>
/// <param name="AssignedIndex">The 1-based display index assigned/acted on, or -1 when none.</param>
/// <param name="RosterEcho">The roster read-back string, printed verbatim (may be empty).</param>
/// <param name="Reason">The rejection reason, or the empty string when <paramref name="Accepted"/> is <see langword="true"/>.</param>
public readonly record struct SessionReply(bool Accepted, int AssignedIndex, string RosterEcho, string Reason);
/// <summary>A read-back request a client sends the server (<c>player.where</c>, <c>world.players</c>, the pose portion of
/// <c>screen.state</c>): the server composes the answer string authoritatively so the client prints a byte-identical
/// echo.</summary>
public abstract record WorldQuery {
    /// <summary>Returns the capability subject a submitted query must hold <see cref="WorldCapability.Observe"/>
    /// over. Body/screen read-backs narrow to their concrete target; world-wide read-backs require <c>all</c>. Kept
    /// with the closed query union so a transport/server does not need access to its intentionally internal leaves.</summary>
    /// <returns>The query's observation subject.</returns>
    public GrantSubject ObservationSubject() => this switch {
        PlayerWhere where => GrantSubject.Body(index: (where.Index - 1)),
        PlayerChannels channels => GrantSubject.Body(index: (channels.Index - 1)),
        PlayerState state => GrantSubject.Body(index: (state.Index - 1)),
        PlayerTargets targets => GrantSubject.Body(index: (targets.Index - 1)),
        Contacts contacts => GrantSubject.Body(index: (contacts.Index - 1)),
        ScreenState screen => GrantSubject.Screen(index: screen.ScreenIndex),
        Properties { BodyIndex: int bodyIndex } => GrantSubject.Body(index: bodyIndex),
        _ => GrantSubject.All,
    };

    /// <summary>The full 6DOF pose read-back for one entity (<c>player.where</c>).</summary>
    /// <param name="Index">The 1-based player display index.</param>
    public sealed record PlayerWhere(int Index) : WorldQuery;
    /// <summary>The channel decision read-back for one entity (<c>player.channels</c>) — per declared channel, the
    /// folded value, the owning seat's base, the later held overlay and composed result, every contributor tagged by
    /// principal, and the pool ceiling/clamp state (see <c>Server.WorldServer.Answer</c>).</summary>
    /// <param name="Index">The 1-based player display index.</param>
    public sealed record PlayerChannels(int Index) : WorldQuery;

    /// <summary>The roster glance across every local seat (<c>world.players</c>).</summary>
    internal sealed record WorldPlayers : WorldQuery;
    /// <summary>The pose portion of a screen's state read-back (<c>screen.state</c>).</summary>
    /// <param name="ScreenIndex">The engine screen index.</param>
    internal sealed record ScreenState(int ScreenIndex) : WorldQuery;

    /// <summary>The active participants' authored, measured, and applied input holds plus the participant setting the
    /// equalized maximum.</summary>
    public sealed record InputHolds : WorldQuery;
    /// <summary>The named action-state register file for one entity.</summary>
    /// <param name="Index">The 1-based player display index.</param>
    public sealed record PlayerState(int Index) : WorldQuery;
    /// <summary>Every authored world rule: its mode, its gate's own predicates, and its effects.</summary>
    public sealed record Rules : WorldQuery;
    /// <summary>Every authored target register and the latest designation refusal for one entity.</summary>
    /// <param name="Index">The 1-based player display index.</param>
    public sealed record PlayerTargets(int Index) : WorldQuery;
    /// <summary>The grounded/contact witnesses for one generation-routed entity.</summary>
    /// <param name="Index">The authority-local, 1-based entity display index.</param>
    public sealed record Contacts(int Index) : WorldQuery;
    /// <summary>The declared property registry, or — with <see cref="BodyIndex"/> — one carrier's live property set.
    /// </summary>
    /// <param name="BodyIndex">The 0-based entity index to read a carrier's tags for, or <see langword="null"/> for
    /// the whole registry.</param>
    public sealed record Properties(int? BodyIndex = null) : WorldQuery;
    /// <summary>Every compiled interaction: its co-occurrence gate's own predicates, its effects, and its latch (held
    /// = fired/holding at the last evaluation) — the SAME line shape <see cref="Rules"/> gives a compiled rule.
    /// </summary>
    public sealed record Interactions : WorldQuery;
}
/// <summary>The server's composed answer to a <see cref="WorldQuery"/> — the read-back string the client prints verbatim
/// (a byte-identical echo of the authoritative pose/roster state), plus the verdict that says whether the answer is a
/// read-back or a refusal.</summary>
/// <param name="Text">The answer string.</param>
/// <param name="Refused">Whether the query named a MISSING or INACTIVE subject, so the answer is a refusal rather than
/// state. A rendering module maps this onto <c>CommandResult.IsError</c>, which is what makes the miss reach
/// <c>wire.errors</c> on every transport — not just the loopback, where the client-side liveness guard catches it
/// first.</param>
public readonly record struct QueryAnswer(string Text, bool Refused = false);
