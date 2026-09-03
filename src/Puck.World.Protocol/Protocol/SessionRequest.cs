using Puck.Commands;

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
    /// <summary>Records a device's preferred profile against the owned-world identity catalog — a local-machine
    /// remembered controller/profile association, not a slot state change (see <c>Server.WorldOwnedWorlds.
    /// RememberPreferredController</c>).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Device">The device the preference is recorded against.</param>
    /// <param name="IdentityName">The identity to remember as the device's preference.</param>
    public sealed record RememberPreferredController(WorldPrincipal Principal, InputDeviceId Device, string IdentityName) : SessionRequest(Principal);

}
/// <summary>The server's answer to a <see cref="SessionRequest"/>: whether it was accepted, the 1-based display index it
/// assigned or acted on (or -1 when none), the roster echo the client prints verbatim, and a rejection reason (empty
/// when accepted — the seam the protocol-version handshake reports a mismatch through).</summary>
/// <param name="Accepted">Whether the request was accepted.</param>
/// <param name="AssignedIndex">The 1-based display index assigned/acted on, or -1 when none.</param>
/// <param name="RosterEcho">The roster read-back string, printed verbatim (may be empty).</param>
/// <param name="Reason">The rejection reason, or the empty string when <paramref name="Accepted"/> is <see langword="true"/>.</param>
public readonly record struct SessionReply(bool Accepted, int AssignedIndex, string RosterEcho, string Reason);
/// <summary>A read-back request a client sends the server (<c>body.where</c>, <c>world.players</c>, the pose portion of
/// <c>screen.state</c>): the server composes the answer string authoritatively so the client prints a byte-identical
/// echo.</summary>
public abstract record WorldQuery {
    /// <summary>Returns only the state observations admitted for the submission stamp; no caller-selected recipient.</summary>
    public sealed record StateObservations(string? Row = null) : WorldQuery;
    /// <summary>Returns the capability subject a submitted query must hold <see cref="WorldCapability.Observe"/>
    /// over. Body/screen read-backs narrow to their concrete target; world-wide read-backs require <c>all</c>. Kept
    /// with the closed query union so a transport/server does not need access to its intentionally internal leaves.</summary>
    /// <returns>The query's observation subject.</returns>
    public GrantSubject ObservationSubject() => this switch {
        StateObservations { Row: { } row } => GrantSubject.State(row),
        PlayerWhere where => GrantSubject.Body(index: where.Index),
        PlayerChannels channels => GrantSubject.Body(index: channels.Index),
        PlayerState state => GrantSubject.Body(index: state.Index),
        PlayerTargets targets => GrantSubject.Body(index: targets.Index),
        Contacts contacts => GrantSubject.Body(index: (contacts.Index - 1)),
        ScreenState screen => GrantSubject.Screen(index: screen.ScreenIndex),
        Properties { BodyIndex: int bodyIndex } => GrantSubject.Body(index: bodyIndex),
        GrantAllows allows => allows.Subject,
        MusicState state => GrantSubject.Body(index: (state.Index - 1)),
        JudgeState state => GrantSubject.Body(index: (state.Index - 1)),
        InstrumentState state => GrantSubject.Body(index: (state.Index - 1)),
        _ => GrantSubject.All,
    };

    /// <summary>The full 6DOF pose read-back for one entity (<c>body.where</c>).</summary>
    /// <param name="Index">The 0-based body index.</param>
    public sealed record PlayerWhere(int Index) : WorldQuery;
    /// <summary>The channel decision read-back for one entity (<c>body.channels</c>) — per declared channel, the
    /// folded value, the owning seat's base, the later held overlay and composed result, every contributor tagged by
    /// principal, and the pool ceiling/clamp state (see <c>Server.WorldServer.Answer</c>).</summary>
    /// <param name="Index">The 0-based body index.</param>
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
    /// <param name="Index">The 0-based body index.</param>
    public sealed record PlayerState(int Index) : WorldQuery;
    /// <summary>Every authored world rule: its mode, its gate's own predicates, and its effects.</summary>
    public sealed record Rules : WorldQuery;
    /// <summary>Every authored target register and the latest designation refusal for one entity.</summary>
    /// <param name="Index">The 0-based body index.</param>
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
    /// <summary>Whether <paramref name="Principal"/> holds <paramref name="Capability"/> over <paramref name="Subject"/>
    /// — the query form of <c>Server.WorldGrants.Allows</c>, for a caller (e.g. <c>Client.PlayerRoster</c>) that holds
    /// no live server reference. The answer's <see cref="QueryAnswer.Payload"/> carries the <see cref="GrantVerdict"/>.
    /// </summary>
    /// <param name="Principal">The principal to check.</param>
    /// <param name="Capability">The capability to check.</param>
    /// <param name="Subject">The subject to check.</param>
    public sealed record GrantAllows(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject) : WorldQuery;
    /// <summary>Mints a <see cref="WorldHandle"/> for <paramref name="Index"/> against <paramref name="Principal"/>'s
    /// <paramref name="Capability"/> handle table — the query form of <c>Server.WorldHandleTable.TryMint</c>. The
    /// answer's <see cref="QueryAnswer.Payload"/> carries the minted <see cref="WorldHandle"/>, or <see langword="null"/>
    /// when the index names no live slot.</summary>
    /// <param name="Principal">The handle table's principal (must be outside the trust boundary).</param>
    /// <param name="Capability">The handle table's capability.</param>
    /// <param name="Index">The 0-based slot index to mint.</param>
    public sealed record GrantHandleMint(WorldPrincipal Principal, WorldCapability Capability, int Index) : WorldQuery;
    /// <summary>Resolves a previously minted <see cref="WorldHandle"/> against its own handle table — the query form of
    /// <c>Server.WorldHandleTable.TryResolve</c>. The answer's <see cref="QueryAnswer.Payload"/> carries the resolved
    /// <see cref="GrantSubject"/>, or <see langword="null"/> when the handle no longer resolves.</summary>
    /// <param name="Handle">The handle to resolve.</param>
    public sealed record GrantHandleResolve(WorldHandle Handle) : WorldQuery;
    /// <summary>The world's compiled channel shape table (bipolar/unipolar/binary per declared channel), the same
    /// table every entity's held channels are read against. The answer's <see cref="QueryAnswer.Payload"/> carries the
    /// <see cref="WorldChannelTable"/>.</summary>
    public sealed record PopulationChannels : WorldQuery;
    /// <summary>The owned-world identity catalog, projected — name, appearance, and claimed motion rates, never the
    /// owned document itself (the same projection a federation seam carries). The answer's
    /// <see cref="QueryAnswer.Payload"/> carries an <see cref="IReadOnlyList{T}"/> of <see cref="WorldIdentityProjection"/>.
    /// </summary>
    public sealed record ProfileCatalog : WorldQuery;
    /// <summary>Finds a catalog identity by name (case-insensitive). The answer's <see cref="QueryAnswer.Payload"/>
    /// carries the matching <see cref="WorldIdentityProjection"/>, or <see langword="null"/> when none matches.
    /// </summary>
    /// <param name="Name">The identity name to look up.</param>
    public sealed record FindProfile(string Name) : WorldQuery;
    /// <summary>The device's remembered preferred identity on this machine, if any. The answer's
    /// <see cref="QueryAnswer.Payload"/> carries the preferred <see cref="WorldIdentityProjection"/>, or
    /// <see langword="null"/> when the device has no remembered preference.</summary>
    /// <param name="Device">The device to look up.</param>
    public sealed record PreferredControllerProfile(InputDeviceId Device) : WorldQuery;
    /// <summary>The live music clock/director state (<c>music.state</c>) — the current segment, any pending
    /// transition, and the tick/from/to of the most recent committed transition, if any. World-wide, not
    /// per-entity; <see cref="Index"/> names only the observing seat, the same subject its own
    /// <see cref="ObservationSubject"/> checks Observe against.</summary>
    /// <param name="Index">The 1-based observing player display index.</param>
    public sealed record MusicState(int Index) : WorldQuery;
    /// <summary>The declared judge window sets (<c>judge.state</c>) — a structural echo of every
    /// <c>puck.judge.v1</c> row's name and windows. World-wide, not per-entity; <see cref="Index"/> names only the
    /// observing seat, the same subject its own <see cref="ObservationSubject"/> checks Observe against.</summary>
    /// <param name="Index">The 1-based observing player display index.</param>
    public sealed record JudgeState(int Index) : WorldQuery;
    /// <summary>Which diegetic instrument screen (if any) the observing seat is engaged with, whether the booted
    /// machine there carries the instrument-clock capability, and its authored tempo (<c>instrument.state</c>).
    /// World-wide, not per-entity; <see cref="Index"/> names only the observing seat, the same subject its own
    /// <see cref="ObservationSubject"/> checks Observe against.</summary>
    /// <param name="Index">The 1-based observing player display index.</param>
    public sealed record InstrumentState(int Index) : WorldQuery;
}
/// <summary>The server's composed answer to a <see cref="WorldQuery"/> — the read-back string the client prints verbatim
/// (a byte-identical echo of the authoritative pose/roster state), plus the verdict that says whether the answer is a
/// read-back or a refusal.</summary>
/// <param name="Text">The answer string.</param>
/// <param name="Refused">Whether the query named a MISSING or INACTIVE subject, so the answer is a refusal rather than
/// state. A rendering module maps this onto <c>CommandResult.IsError</c>, which is what makes the miss reach
/// <c>wire.errors</c> on every transport — not just the loopback, where the client-side liveness guard catches it
/// first.</param>
/// <param name="Payload">A typed value a programmatic (non-console) caller reads instead of parsing
/// <paramref name="Text"/> — the shape is named on each <see cref="WorldQuery"/> leaf that populates one.
/// <see langword="null"/> for every console read-back query, which carries its whole answer in
/// <paramref name="Text"/>.</param>
public readonly record struct QueryAnswer(string Text, bool Refused = false, object? Payload = null);
