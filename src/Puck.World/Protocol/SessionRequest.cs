namespace Puck.World.Protocol;

/// <summary>A session/identity request a client submits to the authoritative server — the closed set the roster/population
/// management verbs translate into (join/leave/profile → seat allocation, population/behavior → the simulated census).
/// The server validates it and answers with a <see cref="SessionReply"/>. Every request carries its acting
/// <see cref="Principal"/> on the base (uniform with <see cref="WorldCommand"/> and <see cref="WorldMutation"/>); seat
/// management stays UNGATED (couch-local); the field is carried for future network validation, not yet checked.</summary>
/// <param name="Principal">The acting identity the request is attributed to.</param>
internal abstract record SessionRequest(WorldPrincipal Principal) {
    /// <summary>Joins a player: a named <see cref="ProfileName"/> joins directly active on it, a null one joins pending
    /// (a profile is chosen, then confirmed). A <see cref="Slot"/> of -1 takes the next free slot. The
    /// <paramref name="ProtocolVersion"/> is checked against <see cref="WorldProtocol.Version"/> — a mismatch is rejected
    /// with a reason.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Slot">The 0-based slot to join, or -1 for the next free slot.</param>
    /// <param name="ProfileName">The profile to seat on (active), or <see langword="null"/> to join pending.</param>
    /// <param name="ProtocolVersion">The client's <see cref="WorldProtocol.Version"/>.</param>
    internal sealed record Join(WorldPrincipal Principal, int Slot, string? ProfileName, int ProtocolVersion) : SessionRequest(Principal);

    /// <summary>Removes a scripted or device player, unmapping its devices and freeing its profile (slot 0 never leaves).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Slot">The 0-based slot to free.</param>
    internal sealed record Leave(WorldPrincipal Principal, int Slot) : SessionRequest(Principal);

    /// <summary>Sets a specific profile on a slot's participant and makes it active (a live switch or a choose-and-confirm).</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Slot">The 0-based slot.</param>
    /// <param name="ProfileName">The profile to seat on.</param>
    internal sealed record SetProfile(WorldPrincipal Principal, int Slot, string ProfileName) : SessionRequest(Principal);

    /// <summary>Sets the active simulated-peer census (the <c>world.population &lt;n&gt;</c> count). Newly activated
    /// peers take the stored peer-source default; existing peers keep their per-entity source.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Count">The requested active simulated count.</param>
    internal sealed record SetPopulation(WorldPrincipal Principal, int Count) : SessionRequest(Principal);

    /// <summary>Sets the peer intent-source default AND sweeps every peer (4..127) to it — last-writer-wins; a
    /// per-entity source does not survive the global flip.</summary>
    /// <param name="Principal">The acting identity.</param>
    /// <param name="Source">The intent source to store and sweep.</param>
    internal sealed record SetPeerSource(WorldPrincipal Principal, IntentSource Source) : SessionRequest(Principal);

    /// <summary>Durably edits ONE section of a player profile in the server-owned <c>puck.world.player.v1</c> document:
    /// the server deserializes <paramref name="Payload"/> per <paramref name="Section"/>, applies it to the profile,
    /// bumps the document revision, persists it, and acknowledges (a bindings payload additionally passes
    /// <c>BindingProfile.Compile</c> before it applies; a compile/parse failure is <see cref="SessionReply.Accepted"/>
    /// false with a reason). The client applies the acknowledged version — the durable half of a rebind that
    /// <c>profile.save</c> drives. Gated on <see cref="WorldCapability.Edit"/> (permissive local default: every seat and
    /// the console may edit every profile).</summary>
    /// <param name="Principal">The acting identity, checked against the Edit capability.</param>
    /// <param name="ProfileId">The <c>WorldPlayerProfile.Id</c> to edit.</param>
    /// <param name="Section">Which section the payload targets.</param>
    /// <param name="Payload">The section value as JSON text (the section's wire shape).</param>
    internal sealed record SetPlayerSection(WorldPrincipal Principal, string ProfileId, WorldPlayerSection Section, string Payload) : SessionRequest(Principal);
}

/// <summary>The player-profile sections a <see cref="SessionRequest.SetPlayerSection"/> targets — the message grain
/// (storage stays per-profile). A genre world arrives as different DATA in the preferences bag, never a new
/// section kind here.</summary>
internal enum WorldPlayerSection : byte {
    /// <summary>The display identity (name + color) — a <c>WorldPlayerIdentity</c> payload.</summary>
    Identity,

    /// <summary>The locomotion preferences (speeds + look-invert) — a <c>WorldPlayerMotion</c> payload.</summary>
    Motion,

    /// <summary>The binding profile — a <c>BindingProfileDocument</c> payload (or the literal <c>null</c> to inherit the
    /// engine default).</summary>
    Bindings,

    /// <summary>The open preferences bag — a JSON object folded into the profile's extension data.</summary>
    Preferences,
}

/// <summary>The server's answer to a <see cref="SessionRequest"/>: whether it was accepted, the 1-based display index it
/// assigned or acted on (or -1 when none), the roster echo the client prints verbatim, and a rejection reason (empty
/// when accepted — the seam the protocol-version handshake reports a mismatch through).</summary>
/// <param name="Accepted">Whether the request was accepted.</param>
/// <param name="AssignedIndex">The 1-based display index assigned/acted on, or -1 when none.</param>
/// <param name="RosterEcho">The roster read-back string, printed verbatim (may be empty).</param>
/// <param name="Reason">The rejection reason, or the empty string when <paramref name="Accepted"/> is <see langword="true"/>.</param>
internal readonly record struct SessionReply(bool Accepted, int AssignedIndex, string RosterEcho, string Reason);

/// <summary>A read-back request a client sends the server (<c>player.where</c>, <c>world.players</c>, the pose portion of
/// <c>screen.state</c>): the server composes the answer string authoritatively so the client prints a byte-identical
/// echo.</summary>
internal abstract record WorldQuery {
    /// <summary>The full 6DOF pose read-back for one entity (<c>player.where</c>).</summary>
    /// <param name="Index">The 1-based player display index.</param>
    internal sealed record PlayerWhere(int Index) : WorldQuery;

    /// <summary>The roster glance across every local seat (<c>world.players</c>).</summary>
    internal sealed record WorldPlayers : WorldQuery;

    /// <summary>The pose portion of a screen's state read-back (<c>screen.state</c>).</summary>
    /// <param name="ScreenIndex">The engine screen index.</param>
    internal sealed record ScreenState(int ScreenIndex) : WorldQuery;

    /// <summary>The whole server-owned player document (<c>puck.world.player.v1</c>) as JSON text — the read-back an
    /// editor/agent pulls before editing a profile section. The reply's <see cref="QueryAnswer.Text"/> is the serialized
    /// document.</summary>
    internal sealed record PlayerDocument : WorldQuery;
}

/// <summary>The server's composed answer to a <see cref="WorldQuery"/> — the read-back string the client prints verbatim
/// (a byte-identical echo of the authoritative pose/roster state).</summary>
/// <param name="Text">The answer string.</param>
internal readonly record struct QueryAnswer(string Text);
