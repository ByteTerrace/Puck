using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>Who travels through a <see cref="WorldPlacementPortal"/> — mirrors
/// <c>Puck.World.WorldInstanceHost.TransferScope</c>.</summary>
public enum WorldPortalTravel {
    /// <summary>The traveling seat's whole active local-seat party — mirrors <c>WorldInstanceHost.TransferScope.Party</c>.</summary>
    Party,

    /// <summary>One seat only — mirrors <c>WorldInstanceHost.TransferScope.Body</c>.</summary>
    Body,
}
/// <summary>What the source does after a destination refuses a transfer because the authored border capacity is
/// full. Both cases refuse the current attempt immediately; <see cref="Retry"/> permits the client to try again on
/// a later source tick, while <see cref="Refuse"/> makes that refusal terminal. Neither case creates a queue.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldTransferFullPolicy>))]
public enum WorldTransferFullPolicy : byte {
    /// <summary>Refuse this attempt and let the client retry on a later source tick.</summary>
    Retry,

    /// <summary>Refuse this attempt without scheduling another one.</summary>
    Refuse,
}
/// <summary>Where a traveler lands at a <see cref="WorldPlacementPortal"/>'s destination — the positional-continuity
/// decision a portal facet authors on top of which destination it names.</summary>
public enum WorldPortalArrival {
    /// <summary>The destination instance's ordinary seat spawn point — today's behavior, unchanged for every facet
    /// authored before this member existed. <see cref="WorldPlacementPortal.Counterpart"/> is refused (by name) when
    /// paired with this.</summary>
    Spawn,

    /// <summary>The pose relative to this facet's own placement frame, mapped through the map between this frame and
    /// <see cref="WorldPlacementPortal.Counterpart"/>'s frame in the destination document — the seamless
    /// border-crossing half of a portal pair. Requires <see cref="WorldPlacementPortal.Counterpart"/> (refused by
    /// name when absent). The map's scale is the counterpart face's size over this face's
    /// (<see cref="WorldFrameIsometry.Scale"/>): a door paired with one a quarter its size sets a traveller down a
    /// quarter its size, moving a quarter as fast, and the window through the door shows the far side at that scale.
    /// A scaled pair stands in one world, which authors <c>bodies.scale</c> to name the register holding each body's size; its two faces
    /// are one rectangle at two sizes a power of two apart, so a round trip returns a traveller to exactly its
    /// size.</summary>
    Mapped,
}
/// <summary>
/// A <see cref="WorldPlacementFace"/>'s portal facet — the authored decision that a face is a door: which
/// <see cref="WorldDestination"/> row it leads to, and under what travel scope. Absent (the default) means the face
/// is not a door — nothing here fires anything by itself; turning the decision into a diegetic step-into trigger is
/// <c>WorldInstanceHost</c>'s job (its per-tick portal scan, <c>ScanInstancePortals</c>, then
/// <c>ResolveAndEnqueueCoalescedTransfers</c>), never this facet's. Durability, scope, and process-local instance
/// selection live on the named <see cref="WorldDestination"/> row this facet points at, not here — a facet composes
/// one destination selection with a travel scope, never re-authors how that destination is minted. Extensible deliberately (an optional-member
/// record, the same widen-without-moving-existing-members shape <see cref="WorldLatticeMedium"/>'s own remarks
/// describe): a future fact-gate field (an authored predicate a traveler must satisfy to pass) adds cleanly as a new
/// trailing member.
/// </summary>
/// <param name="Destination">The <see cref="WorldDestination.Name"/> of the <c>destinations</c> row naming the
/// selected destination. Must resolve to an existing row — an undeclared name refuses by name (see
/// <see cref="WorldDefinitionValidator"/>). No boot-time file-existence check on the destination's own referenced
/// document — resolving the document is a future consumer's job; resolving the name, against this document's own
/// <c>destinations</c> section, is this facet's own.</param>
/// <param name="Travel">Who travels through (see <see cref="WorldPortalTravel"/>). Absent resolves against the
/// world's own <c>portals.portalDefaults.travel</c> (see <see cref="WorldPortalsSection"/>), or
/// <see cref="WorldPortalTravel.Body"/> when the world declares no <c>portals</c> section at all — see
/// <see cref="WorldDefinitionValidator"/>'s resolution order, echoed by <c>world.portals</c>. Omitted from the wire
/// when null.</param>
/// <param name="Arrival">Where a traveler lands at the destination (see <see cref="WorldPortalArrival"/>). Default
/// <see cref="WorldPortalArrival.Spawn"/> — unauthored worlds and every facet authored before this member existed
/// are unchanged. Optional and trailing (the same widen-without-moving-existing-members shape <paramref name="Travel"/>
/// itself already follows).</param>
/// <param name="Counterpart">The destination document's border face this facet's frame maps onto under
/// <see cref="WorldPortalArrival.Mapped"/> — <c>"&lt;placementId&gt;/&lt;face&gt;"</c>, the placement id and
/// declared face name whose own placement transform (position + yaw) is the arrival anchor. Required exactly when
/// <paramref name="Arrival"/> is <see cref="WorldPortalArrival.Mapped"/> (refused by name otherwise, in either
/// direction — see <see cref="WorldDefinitionValidator"/>). No boot-time cross-document existence check: the named
/// document is not resolved at boot (same reason <paramref name="Destination"/> is not), so an absent placement/face
/// refuses at transfer time instead, against the destination's own delivered definition (see
/// <see cref="WorldPortalCounterpart"/>). Omitted from the wire when null.</param>
/// <param name="Capacity">The maximum number of live bodies and outstanding reservations admitted through this
/// border at once, or <see langword="null"/> to use the destination population's remaining capacity. A full border
/// refuses the current attempt immediately; it never queues.</param>
/// <param name="Open">The state condition (<see cref="WorldPlacementResponseCondition.StateCondition"/>, the condition a
/// placement's <c>respond</c> reads) under which the door is open, read each tick the authority scans its doors;
/// <see langword="null"/> for a door always open. A closed door fires nothing: a body that walks into it meets the
/// frame as though no door hung there, and a body standing in it when it closes stays on the side it is on.</param>
/// <param name="Gate">Who may pass: a condition on a row keyed by body index, read at the traveller's own index
/// (<see cref="WorldPortalGate.Admits"/>); <see langword="null"/> admits every traveller the destination admits. A body
/// the gate refuses walks through the frame without crossing. The destination's admission still judges every body the
/// gate lets in.</param>
public sealed record WorldPlacementPortal(
    [property: DocumentText("a destination name")] string Destination,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPortalTravel? Travel = null,
    WorldPortalArrival Arrival = WorldPortalArrival.Spawn,
    [property: DocumentText("a counterpart in the destination world")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Counterpart = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Capacity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementResponseCondition? Open = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPortalGate? Gate = null
);
/// <summary>A door's gate (<see cref="WorldPlacementPortal.Gate"/>): a row keyed by body index whose cell at a
/// traveller's index decides whether that traveller passes. The author's rules write the row: a door bound to its
/// opener writes the opener's cell, a party door writes each member's, so who may pass is a rule of play, never an
/// engine choice.</summary>
/// <param name="State">The keyed numeric row read, one cell per body index (key <c>"0"</c>, <c>"1"</c>, …).</param>
/// <param name="Comparison">The comparison the traveller's cell must satisfy.</param>
/// <param name="Value">The literal the cell is compared against; a cell the row does not hold reads as no value and
/// refuses.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPortalGate(
    [property: DocumentName(WorldNameKind.State, WorldNameRole.Names)] string State,
    [property: JsonConverter(typeof(ExpressionComparisonJsonConverter))] ExpressionOp Comparison,
    float Value
) {
    /// <summary>Returns whether the gate admits a traveller: its row's cell at the traveller's body index satisfies the
    /// comparison.</summary>
    /// <param name="definition">The document to read.</param>
    /// <param name="bodyIndex">The traveller's body index at this authority.</param>
    /// <param name="tick">The tick the cell is read as of.</param>
    /// <param name="engineTick">The engine tick the cell is read as of.</param>
    /// <param name="arena">The live store.</param>
    /// <returns><see langword="true"/> when the cell holds a value that satisfies the comparison.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public bool Admits(WorldDefinition definition, int bodyIndex, ulong tick, ulong engineTick, StateArena? arena = null) => new WorldPlacementResponseCondition.StateCondition(
        Comparison: Comparison,
        Key: bodyIndex.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
        State: State,
        Value: Value
    ).Holds(arena: arena, definition: definition, engineTick: engineTick, tick: tick);
}
/// <summary>The destinations a face leads to and shows, one at a time (<see cref="WorldPlacementFace.Routes"/>): a list of
/// routes and the state cell that chooses among them, so a door is pointed somewhere else by a rule's write rather
/// than by an edit of the document. The face's own <see cref="WorldPlacementFace.Portal"/> and session
/// <see cref="WorldPlacementFace.Source"/> carry everything a route shares (travel, arrival, gate, projection, attention,
/// ripple); a route replaces only where they lead.</summary>
/// <remarks>
/// <para>The authority's door scan reads the cell once a tick, between ticks, and records the route it
/// read beside the door's closure (<c>Puck.World.Server.WorldPortalClosures</c>). The crossing the scan mints, the
/// session the face's window observes through (<c>WorldInstanceHost.SettleScreenSessions</c>) and so the picture every
/// presentation draws all follow that one record, so the glass never shows one world while the door leads to another.
/// A presentation never reads the cell: it draws the session the authority holds for the face's screen.</para>
/// <para>Before the first scan records a route, the face shows and leads where its own portal and source say. A cell
/// that holds no whole number naming a route (absent, negative or past the last route) closes the door as an unmet
/// <see cref="WorldPlacementPortal.Open"/> does: it fires nothing and holds no session.</para>
/// <para>Everyone sees the route the world's cell holds. A per-viewer route (a player's own screen pointing a door for
/// that player alone) is a second, viewer-keyed cell read by the presentation for its own viewer's session, beside this
/// one; nothing here assumes it is absent.</para>
/// </remarks>
/// <param name="State">The numeric state row whose cell chooses the route (never <see cref="CellKind.Text"/>).</param>
/// <param name="Entries">The routes, chosen by their 0-based place in this list. At least one, at most
/// <see cref="MaxEntries"/>; each names a different destination.</param>
/// <param name="Key">The cell inside <paramref name="State"/>, or <see langword="null"/> for its slot cell.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFaceRoutes(
    [property: DocumentName(WorldNameKind.State, WorldNameRole.Names)] string State,
    IReadOnlyList<WorldFaceRoute> Entries,
    [property: DocumentName(WorldNameKind.State, WorldNameRole.Key)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null
) {
    /// <summary>The route the scan records for a door whose cell names none: the door stands closed.</summary>
    public const int Closed = -1;
    /// <summary>The most routes one face carries: a representation bound, sized for a world's whole library.</summary>
    public const int MaxEntries = 1024;

    /// <summary>Reads the route the cell chooses: its whole value when it names one of <see cref="Entries"/>, else
    /// <see cref="Closed"/>.</summary>
    /// <param name="definition">The document to read.</param>
    /// <param name="tick">The tick to read the cell as of.</param>
    /// <param name="engineTick">The engine tick to read the cell as of.</param>
    /// <param name="arena">The live store.</param>
    /// <returns>The 0-based route, or <see cref="Closed"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public int Pick(WorldDefinition definition, ulong tick, ulong engineTick, StateArena? arena = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return (WorldStateReader.TryReadWhole(arena: arena, definition: definition, engineTick: engineTick, key: Key, rowName: State, tick: tick, whole: out var whole) &&
            (whole >= 0L) && (whole < Entries.Count))
            ? ((int)whole)
            : Closed;
    }
}
/// <summary>One of a face's <see cref="WorldFaceRoutes"/>: where the face leads and what its window shows while the
/// route is chosen.</summary>
/// <param name="Destination">The <see cref="WorldDestination.Name"/> of the <c>destinations</c> row the face's portal
/// and its session source lead to and observe on this route.</param>
/// <param name="Counterpart">The destination document's border face a mapped arrival lands out of on this route
/// (<see cref="WorldPlacementPortal.Counterpart"/>'s spelling), or <see langword="null"/> for the face's own portal's.</param>
/// <param name="Fallback">The colour the window shows past the deepest nesting on this route, as <c>#RRGGBB</c>, or
/// <see langword="null"/> for the face's own session's.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFaceRoute(
    [property: DocumentText("a destination name")] string Destination,
    [property: DocumentText("a counterpart in the destination world")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Counterpart = null,
    [property: DocumentText("a fallback color")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Fallback = null
);
/// <summary>A routed face as it stands on each of its routes: the one derivation every reader of a route takes, so the
/// door the scan crosses through, the session the authority opens and the counterpart a presentation maps a window
/// through are the same face.</summary>
public static class WorldFaceRouting {
    // Each routed face's faces on its routes, built once per face row: a scan asks every tick, and a row never changes
    // once built.
    private static readonly Puck.Abstractions.CodeDerivedTable<WorldPlacementFace, WorldPlacementFace[]> Faces = new();

    /// <summary>Returns the face as it stands on route <paramref name="route"/>: its portal leading to the route's
    /// destination through the route's counterpart, and its session source observing that destination with the
    /// route's fallback. A face with no routes, or a route outside them, answers the face itself.</summary>
    /// <param name="face">The authored face row.</param>
    /// <param name="route">The 0-based route.</param>
    /// <returns>The face on that route.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="face"/> is <see langword="null"/>.</exception>
    public static WorldPlacementFace On(WorldPlacementFace face, int route) {
        ArgumentNullException.ThrowIfNull(argument: face);

        if ((face.Routes is not { } routes) || (route < 0) || (route >= routes.Entries.Count)) {
            return face;
        }

        return Faces.GetValue(key: face, derive: Derive)[route];
    }
    /// <summary>Returns the face as it stands on the route that leads to <paramref name="destination"/>, or the face
    /// itself when none does: how a reader holding the session the authority opened finds the route it shows.</summary>
    /// <param name="face">The authored face row.</param>
    /// <param name="destination">The destination name the face's session observes.</param>
    /// <returns>The face on that route.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="face"/> is <see langword="null"/>.</exception>
    public static WorldPlacementFace To(WorldPlacementFace face, string destination) {
        ArgumentNullException.ThrowIfNull(argument: face);

        if (face.Routes is not { } routes) {
            return face;
        }

        for (var index = 0; (index < routes.Entries.Count); index++) {
            if (string.Equals(a: routes.Entries[index].Destination, b: destination, comparisonType: StringComparison.Ordinal)) {
                return On(face: face, route: index);
            }
        }

        return face;
    }
    /// <summary>Returns whether a session is the one the face shows on its own or on one of its routes: what a
    /// presentation accepts the authority's session for the face's screen against.</summary>
    /// <param name="face">The authored face row.</param>
    /// <param name="session">The session the authority holds for the face's screen.</param>
    /// <returns><see langword="true"/> when <paramref name="session"/> is one the face shows.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static bool Shows(WorldPlacementFace face, WorldScreenSource.Session session) {
        ArgumentNullException.ThrowIfNull(argument: face);
        ArgumentNullException.ThrowIfNull(argument: session);

        return (Equals(objA: face.Source, objB: session) || Equals(objA: To(face: face, destination: session.Destination).Source, objB: session));
    }

    private static WorldPlacementFace[] Derive(WorldPlacementFace face) {
        var entries = face.Routes!.Entries;
        var faces = new WorldPlacementFace[entries.Count];

        for (var index = 0; (index < entries.Count); index++) {
            var entry = entries[index];

            faces[index] = face with {
                Portal = ((face.Portal is { } portal)
                    ? portal with { Destination = entry.Destination, Counterpart = (entry.Counterpart ?? portal.Counterpart) }
                    : null),
                Source = ((face.Source is WorldScreenSource.Session session)
                    ? session with { Destination = entry.Destination, Fallback = (entry.Fallback ?? session.Fallback) }
                    : face.Source),
            };
        }

        return faces;
    }
}
/// <summary>Parses and resolves a <see cref="WorldPlacementPortal.Counterpart"/> string — the one spelling shared by
/// <see cref="WorldDefinitionValidator"/>'s boot-time format check and <c>Puck.World.WorldInstanceHost</c>'s
/// transfer-time existence check against the destination's own delivered definition, so the two can never disagree
/// on what "malformed" or "missing" means.</summary>
public static class WorldPortalCounterpart {
    // Each authored counterpart string's halves, split once: a window's frustum fit parses its portal's counterpart
    // every frame, and the string is the document's own instance.
    private static readonly Puck.Abstractions.CodeDerivedTable<string, Halves> Parsed = new();

    private sealed record Halves(string PlacementId, string Face);

    /// <summary>Splits a counterpart string into its placement id and face name, at the first <c>/</c>.</summary>
    /// <param name="counterpart">The authored counterpart string.</param>
    /// <param name="placementId">The placement id half, when parsing succeeds.</param>
    /// <param name="face">The face name half, when parsing succeeds.</param>
    /// <returns><see langword="true"/> when <paramref name="counterpart"/> is non-empty, contains exactly one
    /// meaningful <c>/</c> split, and both halves are non-empty/non-whitespace.</returns>
    public static bool TryParse(string? counterpart, out string placementId, out string face) {
        placementId = string.Empty;
        face = string.Empty;

        if (string.IsNullOrWhiteSpace(value: counterpart)) {
            return false;
        }
        if (Parsed.TryGetValue(key: counterpart, value: out var halves)) {
            placementId = halves.PlacementId;
            face = halves.Face;

            return true;
        }

        var separator = counterpart.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: '/'
        );

        if (separator <= 0) {
            return false;
        }

        var candidatePlacementId = counterpart[..separator];
        var candidateFace = counterpart[(separator + 1)..];

        if (
            string.IsNullOrWhiteSpace(value: candidatePlacementId) ||
            string.IsNullOrWhiteSpace(value: candidateFace)
        ) {
            return false;
        }

        placementId = candidatePlacementId;
        face = candidateFace;
        Parsed.AddOrUpdate(key: counterpart, value: new Halves(Face: candidateFace, PlacementId: candidatePlacementId));

        return true;
    }
    /// <summary>Resolves a counterpart string against a (destination-side, at transfer time) definition — the
    /// placement + face whose transform is the arrival anchor.</summary>
    /// <param name="definition">The definition to resolve against (the destination's own delivered document).</param>
    /// <param name="counterpart">The authored counterpart string.</param>
    /// <param name="placement">The resolved placement, when resolution succeeds.</param>
    /// <param name="face">The resolved face row, when resolution succeeds.</param>
    /// <param name="reason">The named refusal reason, quoting exactly what was written, when resolution fails.</param>
    /// <returns><see langword="true"/> when <paramref name="counterpart"/> names a real placement/face pair in
    /// <paramref name="definition"/>.</returns>
    public static bool TryResolve(WorldDefinition definition, string? counterpart, out WorldPlacement? placement, out WorldPlacementFace? face, out string reason) {
        placement = null;
        face = null;

        if (!TryParse(
            counterpart: counterpart,
            face: out var faceName,
            placementId: out var placementId
        )) {
            reason = $"counterpart '{counterpart}' is malformed — expected '<placementId>/<face>'";

            return false;
        }

        var resolvedPlacement = WorldDefinitionRows.FindPlacement(
            placements: definition.Placements,
            id: placementId
        );

        if (resolvedPlacement is null) {
            reason = $"counterpart '{counterpart}' names no placement '{placementId}'";

            return false;
        }

        var resolvedFace = WorldDefinitionRows.FindPlacementFace(
            face: faceName,
            placement: resolvedPlacement
        );

        if (resolvedFace is null) {
            reason = $"counterpart '{counterpart}' names no face '{faceName}' on placement '{placementId}'";

            return false;
        }

        placement = resolvedPlacement;
        face = resolvedFace;
        reason = string.Empty;

        return true;
    }
}
/// <summary>The world-scope transfer defaults a portal facet resolves against.</summary>
/// <param name="Travel">The default travel scope (see <see cref="WorldPortalTravel"/>).</param>
/// <param name="HoldSeconds">How long a destination reservation is binding, converted from source simulation ticks
/// through the exact 50400 engine-tick bridge.</param>
/// <param name="Full">Whether the client may retry after an immediate full-border refusal.</param>
/// <param name="PartyAllOrNothing">Whether a party transfer reserves and commits as one cohort. When false, each
/// member is a separate atomic body transfer.</param>
public sealed record WorldPortalDefaults(
    WorldPortalTravel Travel,
    double HoldSeconds = 2.0,
    WorldTransferFullPolicy Full = WorldTransferFullPolicy.Retry,
    bool PartyAllOrNothing = true
);
/// <summary>
/// The <c>portals</c> section — the world-scope defaults a <see cref="WorldPlacementPortal"/> facet resolves
/// against when it does not author its own fact. Optional, for the same reason
/// <see cref="WorldReference"/>'s own <c>references</c> section is: a world with no portals authors neither this
/// section nor any portal facet, and a required section would refuse every existing document at boot for declaring
/// nothing. Slotted immediately after <c>references</c> in <see cref="WorldDefinition"/>'s declaration order — the
/// two sections are the world-topology cluster a portal composes from (which world, and how travel through it
/// defaults).
/// </summary>
/// <param name="PortalDefaults">The travel default every portal facet in this document falls back to when it
/// authors no <see cref="WorldPlacementPortal.Travel"/> of its own.</param>
public sealed record WorldPortalsSection(WorldPortalDefaults PortalDefaults);
