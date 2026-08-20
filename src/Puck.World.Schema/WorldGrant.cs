using System.Globalization;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World.Protocol;

/// <summary>The coarse capability verbs a <see cref="WorldGrant"/> confers — the closed set the server checks a
/// submission's <see cref="WorldPrincipal"/> against at each write boundary. A genre world arrives as different data
/// (new subjects, new sections), never a new capability.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldCapability>))]
public enum WorldCapability : byte {
    /// <summary>The right to drive a body — submit its per-tick intents and authority commands. Checked at the intent
    /// drain and <c>ApplyCommand</c>.</summary>
    Drive,

    /// <summary>The right to observe a subject — read it rather than change it. Enforced on the addon read path: a
    /// guest's pose query resolves an Observe handle and is checked against this capability over the concrete body it
    /// designates (<c>Server.WorldAddonRuntime</c>'s read point), so an <c>observe body:&lt;n&gt;</c> row is real,
    /// checkable authority. Submitted <c>WorldQuery</c> envelopes are likewise checked over the addressed body/screen
    /// (or <c>all</c> for world-wide read-backs) before <c>WorldServer.Answer</c> composes them; its direct public call
    /// remains the trusted in-process read-back surface.</summary>
    Observe,

    /// <summary>The right to control a screen/machine surface — the engagement route (a player's intent diverts to the
    /// screen's machine). Checked on the engage path.</summary>
    Control,

    /// <summary>The right to mutate a world-document section — apply a <c>WorldMutation</c> targeting it.
    /// Checked at mutation apply (and, over every section, at a whole-document swap or journal undo). A
    /// <see cref="GrantSubject.Section"/> subject admits every row the section carries; the row-scoped
    /// <see cref="GrantSubjectKind.Creation"/>/<see cref="GrantSubjectKind.Placement"/> subjects admit one row apiece
    /// and are checked as an alternative to the section hold, never beneath it.</summary>
    Mutate,

    /// <summary>The right to edit a concrete <c>state</c> row — slot-shaped or table-shaped alike (a slot is a table
    /// with one key). Checked against the <see cref="GrantSubject.State"/> subject of the row being touched,
    /// narrowing which named row a principal may touch beneath the coarse <see cref="Mutate"/>/<c>section:state</c>
    /// hold the mutation's own section-authority check already requires — the same subject whether the write is
    /// whole-row (<c>WorldMutation.UpsertStateRow</c>/<c>WorldMutation.RemoveStateRow</c>) or per-cell
    /// (<c>WorldMutation.UpsertStateCell</c>/<c>WorldMutation.RemoveStateCell</c>) — there is no
    /// separate <c>table:&lt;name&gt;</c> subject narrowing per-cell writes independently of the whole row's own
    /// hold. The <see cref="GrantSubject.All"/> wildcard covers every row, so the domain-seeded <c>Edit/all</c> every
    /// seat and Console already holds reaches every state row until someone narrows it.
    /// <para>A concrete <c>state:&lt;name&gt;</c> row may additionally carry a <see cref="WorldGrant.KindMask"/> —
    /// the verb-scoped narrowing that separates bumping a row (the per-cell pair) from redefining it (the whole-row
    /// pair). An unmasked row keeps full reach over its subject, so a mask is opt-in narrowing beneath an already
    /// deny-by-default capability, never a new gate a seeded grant has to pass.</para></summary>
    Edit,
}
/// <summary>The world-document sections the <c>WorldMutation</c> vocabulary targets — the stable-id subject a
/// <see cref="WorldCapability.Mutate"/> grant scopes to. A section names a coarse row set; a mutation is checked against
/// exactly one.</summary>
public enum WorldSection : byte {
    /// <summary>The locomotion kit rows, the default seat kit, and the kit→entity assignment policy.</summary>
    Kits,

    /// <summary>The diegetic screen rows.</summary>
    Screens,

    /// <summary>The placeable camera rows.</summary>
    Cameras,

    /// <summary>The named spawn-point list.</summary>
    Spawns,

    /// <summary>The profileless locomotion defaults.</summary>
    Motion,

    /// <summary>The census defaults (document-only).</summary>
    Population,

    /// <summary>The render-lever defaults and quality-preset table (document-only).</summary>
    Render,

    /// <summary>The data-side addon descriptor rows.</summary>
    Addons,

    /// <summary>The per-world binding overlays — targeted by the <c>WorldMutation.UpsertBindingOverlay</c> /
    /// <c>WorldMutation.RemoveBindingOverlay</c> mutations.</summary>
    Bindings,

    /// <summary>The creation asset rows — inline-canonical <c>puck.creation.v1</c> documents with pinned hashes.</summary>
    Creations,

    /// <summary>The placement instance rows — creations stamped into the world by reference.</summary>
    Placements,

    /// <summary>The editor/authoring policy row — headroom, placement scale envelope, candidate targeting,
    /// the sole-editor layout split, and the drag-preview deadline (see <see cref="WorldAuthoringDefaults"/>).</summary>
    Authoring,

    /// <summary>The placeable speaker rows (the audio arc) — targeted by <c>WorldMutation.UpsertSpeaker</c> /
    /// <c>WorldMutation.RemoveSpeaker</c>.</summary>
    Speakers,

    /// <summary>The tune asset rows — inline-canonical <c>puck.audio.v1</c> documents with pinned hashes.</summary>
    Tunes,

    /// <summary>The synth-patch asset rows — inline-canonical <c>puck.synth.v1</c> documents with pinned hashes.</summary>
    Patches,

    /// <summary>The audio host-section defaults (master gain, attenuation coalescing, the listener policy).</summary>
    Audio,

    /// <summary>The contact-solver tuning (the <c>WorldMutation.SetCollision</c> mutation).</summary>
    Collision,

    /// <summary>The host-section defaults — window/backend/present/pacing/timing/genlock presentation intent
    /// (see <see cref="WorldHostDefaults"/>).</summary>
    Host,

    /// <summary>The window-composition defaults — the seat rig and the authored named layouts (the
    /// <c>WorldMutation.SetViewDefaults</c> / <c>WorldMutation.UpsertViewLayout</c> /
    /// <c>WorldMutation.RemoveViewLayout</c> mutations).</summary>
    Views,

    /// <summary>The look rows and the look→entity assignment policy — the appearance peer of <see cref="Kits"/>,
    /// targeted by <c>WorldMutation.UpsertLook</c> / <c>WorldMutation.RemoveLook</c> /
    /// <c>WorldMutation.SetLookAssignment</c>. Presentation-only authority (restyle the crowd, never reshape it).</summary>
    Looks,

    /// <summary>The document-authored grant rows (see <see cref="WorldDefinition.Grants"/>) — capability holds a world
    /// ships with, applied at boot alongside the permissive seed. Targeted by <c>WorldMutation.UpsertGrant</c> /
    /// <c>WorldMutation.RemoveGrant</c>.</summary>
    Grants,

    /// <summary>The world-scope HUD panel rows and the HUD section defaults — targeted by
    /// <c>WorldMutation.UpsertHudPanel</c> / <c>WorldMutation.RemoveHudPanel</c> /
    /// <c>WorldMutation.UpsertHudElement</c> / <c>WorldMutation.RemoveHudElement</c> /
    /// <c>WorldMutation.SetHudDefaults</c>. Presentation-only authority (overlay geometry, never simulation
    /// state).</summary>
    Hud,

    /// <summary>The genre-neutral <c>state</c> rows (score, rounds, inventory, flags) — targeted by
    /// <c>WorldMutation.UpsertStateRow</c> / <c>WorldMutation.RemoveStateRow</c>. It is simulation state,
    /// unlike <see cref="Hud"/>: a principal holding <see cref="WorldCapability.Mutate"/> here changes values the
    /// game itself reads as its own state.</summary>
    State,

    /// <summary>The participant input-hold policy.</summary>
    InputHold,

    /// <summary>The world-scoped <c>rules</c> rows — targeted by <c>WorldMutation.UpsertWorldRule</c> /
    /// <c>WorldMutation.RemoveWorldRule</c>. Gates authoring a rule only. A rule's own evaluation and its
    /// fired effects never consult this table: they act as <see cref="WorldPrincipal.World"/>, which
    /// <c>Server.WorldServer.TryAdmitMutation</c> exempts structurally — the same standing a per-body
    /// <c>ActionEffect</c> has always had (an authored program is the world acting on itself, not an actor
    /// submitting). Holding Mutate here is therefore holding the power to write the program, which is the only
    /// authority question a rule raises.</summary>
    Rules,

    /// <summary>The group + membership binding substrate — the group-kind policy catalog and the group roster rows
    /// (see <c>Puck.World.WorldGroupsSection</c>), targeted by <c>WorldMutation.UpsertGroupKind</c> /
    /// <c>WorldMutation.RemoveGroupKind</c> / <c>WorldMutation.FormGroup</c> /
    /// <c>WorldMutation.JoinGroup</c> / <c>WorldMutation.LeaveGroup</c> /
    /// <c>WorldMutation.KickMember</c> / <c>WorldMutation.OfferOwnership</c> /
    /// <c>WorldMutation.SettleOwnership</c>. A roster row is one shape whether it was boot-authored or formed live:
    /// <c>world.reset</c>/<c>world.load</c>/<c>world.reload</c> restore the server's base document, so a live-formed
    /// row (never written back to that base) simply is not there after — the party-vs-roster split falls out of the
    /// ordinary whole-document swap, free.</summary>
    Groups,

    /// <summary>The <c>properties</c> section — the carrier-property name vocabulary, targeted by
    /// <c>WorldMutation.SetProperty</c>.</summary>
    Properties,

    /// <summary>The <c>interactions</c> section — the generalized property-interaction table, targeted by
    /// <c>WorldMutation.UpsertInteraction</c>/<c>WorldMutation.RemoveInteraction</c>. Gates authoring an interaction only, on the same terms
    /// <see cref="Rules"/> does: an interaction's own evaluation and its fired effects act as
    /// <see cref="WorldPrincipal.World"/>, structurally exempt from this table — see <see cref="Rules"/>'s remarks
    /// and <c>WorldGrants</c>'s untrusted-narrowing rule, which extends to this section for the identical laundering
    /// reason.</summary>
    Interactions,

    /// <summary>The player seed palette, picker tuning, and the control feel a seat of this document wakes with
    /// (<see cref="WorldPlayerDefaults.SeatLook"/>) — targeted by <c>WorldMutation.SetPlayerDefaults</c>.
    /// Presentation-only authority in practice: nothing this section carries rides a <c>CommandSnapshot</c>, so a
    /// grant here retunes how a seat feels without touching what the simulation does.</summary>
    PlayerDefaults,

    /// <summary>The <c>market</c> section — the local auction house's config and live listing ledger, targeted by
    /// <c>WorldMutation.CreateMarketListing</c>/<c>WorldMutation.PlaceMarketBid</c>/
    /// <c>WorldMutation.BuyoutMarketListing</c>/<c>WorldMutation.CancelMarketListing</c>/
    /// <c>WorldMutation.SettleMarketListing</c>. The engine's own deadline sweep fires the last of these as
    /// <see cref="WorldPrincipal.World"/>, the same structural exemption <see cref="Groups"/>' escrow reclaim uses —
    /// never gated by a grant.</summary>
    Market,
}
/// <summary>Which flavor of subject a <see cref="GrantSubject"/> addresses.</summary>
public enum GrantSubjectKind : byte {
    /// <summary>The wildcard — the capability over every subject of its natural domain.</summary>
    All,

    /// <summary>A single body, by 0-based entity index.</summary>
    Body,

    /// <summary>A single screen, by engine screen index.</summary>
    Screen,

    /// <summary>A single world-document section.</summary>
    Section,

    /// <summary>A single <c>state</c> row, by its stable string name (<see cref="GrantSubject.Id"/>) — the
    /// <see cref="WorldCapability.Edit"/> subject <see cref="WorldStateRow.Name"/> addresses, whether the row is
    /// shaped as a scalar slot or a keyed table (a slot is a table with one key — see
    /// <see cref="WorldStateRow"/>'s remarks). Narrows <c>WorldMutation.UpsertStateRow</c>/<c>RemoveStateRow</c>
    /// (the whole-row write), <c>WorldMutation.UpsertStateCell</c>/<c>RemoveStateCell</c> (the per-cell write), and
    /// <c>WorldMutation.Generate</c> (the draw-site write over the same concrete <c>state:&lt;Row&gt;</c> subject) —
    /// one subject for the one row.</summary>
    State,

    /// <summary>The shared window-composition authority — the live <c>view.override layout</c>/<c>view.override camera</c> overrides that
    /// change what every seat sees (not a body, a screen, or a section). A director principal can hold it exclusively to
    /// own the shot for a recording session.</summary>
    Composition,

    /// <summary>A single named region — a placement's optional volume facet (see
    /// <see cref="Puck.World.WorldPlacementRegion"/>), addressed by the carrying <see cref="WorldPlacement.Id"/>.
    /// Legitimate only for <see cref="WorldCapability.Observe"/> (the region-enter/exit event family — see
    /// <c>Server.WorldEventFeed</c>); there is no query verb over a region, so this subject kind is never
    /// bound-checked against the document the way <see cref="Body"/> is bound against the live population — an
    /// event simply never fires for a name no placement carries.</summary>
    Region,

    /// <summary>A single local seat, by 0-based slot index (<c>0..LocalSeatCount-1</c>) — the seat join/leave event
    /// family's subject (see <c>Server.WorldEventFeed</c>). Legitimate only for <see cref="WorldCapability.Observe"/>.
    /// Distinct from <see cref="Body"/>: a seat index and its body index are numerically identical for a local seat,
    /// but this kind names the occupancy edge, never the body's pose or drive authority.</summary>
    Seat,

    /// <summary>A single <c>creations</c> row, by its stable id (<see cref="GrantSubject.Id"/>) — a row-scoped
    /// <see cref="WorldCapability.Mutate"/> subject admitting <c>WorldMutation.UpsertCreation</c>/
    /// <c>WorldMutation.RemoveCreation</c> naming exactly this id.
    /// <remarks>An alternative to a <see cref="Section"/> hold over <see cref="WorldSection.Creations"/>, never a
    /// narrowing beneath one: <c>Server.WorldServer.TryAdmitMutation</c>'s section gate is a disjunction, so a
    /// section holder still reaches every row and a row holder reaches no other. The id may name a row that does not
    /// exist yet (creating it is the granted act), so it is shape-checked — non-blank, and not a
    /// <c>state.&lt;row&gt;</c> reference, since <see cref="WorldCreation.Id"/> resolves one to a different string —
    /// never bound-checked against the live document.</remarks></summary>
    Creation,

    /// <summary>A single <c>placements</c> row, by its stable <see cref="WorldPlacement.Id"/> value
    /// (<see cref="GrantSubject.Id"/>) — <see cref="Creation"/>'s peer over <see cref="WorldSection.Placements"/>,
    /// admitting <c>WorldMutation.UpsertPlacement</c>/<c>WorldMutation.RemovePlacement</c> naming exactly this id.
    /// <remarks>Distinct from <see cref="Region"/>, which addresses the same placement's volume facet for
    /// <see cref="WorldCapability.Observe"/> and confers no write authority.</remarks></summary>
    Placement,

    /// <summary>A single authored <c>adjacencies</c> row, by its stable <see cref="WorldAdjacency.Name"/> value
    /// (<see cref="GrantSubject.Id"/>) — <see cref="Region"/>'s twin for the federation seam: the
    /// <c>linkEstablished</c>/<c>linkDropped</c> world event family's gating subject (see
    /// <c>Server.WorldEventFeed</c>). Legitimate only for <see cref="WorldCapability.Observe"/>, untrusted
    /// principals only, and — exactly like <see cref="Region"/> — never bound-checked against the document: an event
    /// simply never fires for a name no adjacency row carries.</summary>
    Adjacency,
}
/// <summary>The typed target a <see cref="WorldGrant"/> scopes to — a wildcard, a body, a screen, a document section,
/// or one named row of a section. A zero-alloc value key into the grant table's per-capability subject sets: row names
/// are strings, so the subject matches <see cref="WorldPrincipal"/>'s shape (an index lane plus a nullable string lane;
/// record-struct equality covers both).</summary>
/// <param name="Kind">The subject flavor.</param>
/// <param name="Value">The 0-based body/screen/seat index, or the <see cref="WorldSection"/> ordinal for a section;
/// zero for every named and wildcard kind.</param>
/// <param name="Id">The state, region, creation, placement, or adjacency id for named subject kinds;
/// <see langword="null"/> otherwise.</param>
public readonly record struct GrantSubject(GrantSubjectKind Kind, int Value, string? Id = null) {
    /// <summary>Gets the wildcard subject — the capability over its whole domain.</summary>
    public static GrantSubject All { get; } = new(
        Kind: GrantSubjectKind.All,
        Value: 0
    );
    /// <summary>Gets the shared window-composition authority subject. Not reachable from the console: this is the only site
    /// that constructs it (the grant seed does so directly), and <c>world.grant</c>/<c>world.revoke</c> parse no token
    /// for it, so a composition row can be echoed by <c>world.grants</c> but never granted or revoked. The
    /// exclusive-acquisition story it exists for is unimplementable until the grammar can name it.</summary>
    public static GrantSubject Composition { get; } = new(
        Kind: GrantSubjectKind.Composition,
        Value: 0
    );

    /// <summary>Creates a single authored <c>adjacencies</c> row subject by its stable row name.</summary>
    /// <param name="name">The adjacency row name (<see cref="WorldAdjacency.Name"/>).</param>
    public static GrantSubject Adjacency(string name) => new(
        Id: name,
        Kind: GrantSubjectKind.Adjacency,
        Value: 0
    );
    /// <summary>Creates a single body by 0-based entity index.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public static GrantSubject Body(int index) => new(
        Kind: GrantSubjectKind.Body,
        Value: index
    );
    /// <summary>Creates a single <c>creations</c> row by its stable id.</summary>
    /// <param name="id">The creation row id.</param>
    public static GrantSubject Creation(string id) => new(
        Id: id,
        Kind: GrantSubjectKind.Creation,
        Value: 0
    );
    /// <summary>Describes a short stable label for console echoes — <c>all</c>, <c>body:&lt;n&gt;</c>, <c>screen:&lt;n&gt;</c>,
    /// <c>section:&lt;name&gt;</c>, <c>state:&lt;name&gt;</c>, <c>composition</c>, <c>region:&lt;name&gt;</c>,
    /// <c>seat:&lt;n&gt;</c>, <c>creation:&lt;id&gt;</c>, <c>placement:&lt;id&gt;</c>,
    /// <c>adjacency:&lt;name&gt;</c>.</summary>
    /// <returns>The label.</returns>
    public string Describe() => Kind switch {
        GrantSubjectKind.All => "all",
        GrantSubjectKind.Body => $"body:{Value}",
        GrantSubjectKind.Screen => $"screen:{Value}",
        GrantSubjectKind.Section => $"section:{((WorldSection)Value).ToString().ToLowerInvariant()}",
        GrantSubjectKind.State => $"state:{Id}",
        GrantSubjectKind.Composition => "composition",
        GrantSubjectKind.Region => $"region:{Id}",
        GrantSubjectKind.Seat => $"seat:{Value}",
        GrantSubjectKind.Creation => $"creation:{Id}",
        GrantSubjectKind.Placement => $"placement:{Id}",
        GrantSubjectKind.Adjacency => $"adjacency:{Id}",
        _ => "?",
    };
    /// <summary>Creates a single <c>placements</c> row by its stable id.</summary>
    /// <param name="id">The placement row id.</param>
    public static GrantSubject Placement(string id) => new(
        Id: id,
        Kind: GrantSubjectKind.Placement,
        Value: 0
    );
    /// <summary>Creates a single named region by its carrying placement's stable id.</summary>
    /// <param name="name">The region name (the carrying <see cref="WorldPlacement.Id"/>).</param>
    public static GrantSubject Region(string name) => new(
        Id: name,
        Kind: GrantSubjectKind.Region,
        Value: 0
    );
    /// <summary>Creates a single screen by engine screen index.</summary>
    /// <param name="index">The engine screen index.</param>
    public static GrantSubject Screen(int index) => new(
        Kind: GrantSubjectKind.Screen,
        Value: index
    );
    /// <summary>Creates a single local seat by 0-based slot index.</summary>
    /// <param name="index">The 0-based seat index.</param>
    public static GrantSubject Seat(int index) => new(
        Kind: GrantSubjectKind.Seat,
        Value: index
    );
    /// <summary>Creates a single world-document section.</summary>
    /// <param name="section">The section.</param>
    public static GrantSubject Section(WorldSection section) => new(
        Kind: GrantSubjectKind.Section,
        Value: ((int)section)
    );
    /// <summary>Creates a single <c>state</c> row by its stable string name — slot-shaped or table-shaped alike (a slot is a
    /// table with one key).</summary>
    /// <param name="name">The state row name.</param>
    public static GrantSubject State(string name) => new(
        Id: name,
        Kind: GrantSubjectKind.State,
        Value: 0
    );
    /// <summary>Parses a subject token (<c>all</c> | <c>body:&lt;n&gt;</c> | <c>screen:&lt;n&gt;</c> |
    /// <c>section:&lt;name&gt;</c> | <c>state:&lt;name&gt;</c> | <c>region:&lt;name&gt;</c> | <c>seat:&lt;n&gt;</c> |
    /// <c>creation:&lt;id&gt;</c> | <c>placement:&lt;id&gt;</c> | <c>adjacency:&lt;name&gt;</c>) — shared by
    /// <c>Puck.World.GrantSubjectJsonConverter</c>
    /// and <c>Puck.World.WorldGrantCommandModule</c>'s <c>world.grant</c>/<c>world.revoke</c> console verbs, so a
    /// document-sourced subject (a <c>WorldCapabilityRequest.Subject</c>, a <see cref="WorldGrant.Subject"/> row)
    /// always canonicalizes through the identical grammar a console token does; there is no other way to construct a
    /// denormalized <see cref="GrantSubject"/> (a stray non-zero <see cref="Value"/>/<see cref="Id"/> the wildcard or
    /// section kinds do not use) from either surface, which is what keeps a document subject and a live grant table
    /// entry comparable by value.</summary>
    /// <param name="token">The token to parse.</param>
    /// <param name="subject">The parsed subject, on success.</param>
    /// <returns><see langword="true"/> when the token parsed.</returns>
    public static bool TryParse(ReadOnlySpan<char> token, out GrantSubject subject) {
        subject = All;

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "all"
        )) {
            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "body:"
        ) &&
            int.TryParse(
            s: token[5..],
            style: NumberStyles.Integer,
            provider: CultureInfo.InvariantCulture,
            result: out var body
        ) &&
            (body >= 0)
        ) {
            subject = Body(index: body);

            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "screen:"
        ) &&
            int.TryParse(
            s: token[7..],
            style: NumberStyles.Integer,
            provider: CultureInfo.InvariantCulture,
            result: out var screen
        ) &&
            (screen >= 0)
        ) {
            subject = Screen(index: screen);

            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "section:"
        ) &&
            (token.Length > 8) &&
            TryParseSectionName(
            name: token[8..],
            section: out var section
        )
        ) {
            subject = Section(section: section);

            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "state:"
        ) &&
            (token.Length > 6)
        ) {
            subject = State(name: token[6..].ToString());

            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "region:"
        ) &&
            (token.Length > 7)
        ) {
            subject = Region(name: token[7..].ToString());

            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "seat:"
        ) &&
            int.TryParse(
            s: token[5..],
            style: NumberStyles.Integer,
            provider: CultureInfo.InvariantCulture,
            result: out var seat
        ) &&
            (seat >= 0)
        ) {
            subject = Seat(index: seat);

            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "creation:"
        ) &&
            (token.Length > 9)
        ) {
            subject = Creation(id: token[9..].ToString());

            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "placement:"
        ) &&
            (token.Length > 10)
        ) {
            subject = Placement(id: token[10..].ToString());

            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "adjacency:"
        ) &&
            (token.Length > 10)
        ) {
            subject = Adjacency(name: token[10..].ToString());

            return true;
        }

        return false;
    }
    /// <summary>Parses a bare <see cref="WorldSection"/> member name — no <c>section:</c> prefix — the shared
    /// predicate behind <see cref="TryParse"/>'s own <c>section:</c> branch and the addon ABI's name-keyed
    /// <c>Ask</c> boundary (<c>Server.WorldAddonRuntime.ResolveAsks</c>), so a console token and a guest-declared
    /// section name are refused by the identical rule rather than two implementations free to drift apart.
    /// <see cref="Enum.TryParse{TEnum}(ReadOnlySpan{char},bool,out TEnum)"/> alone accepts a bare numeric token and
    /// silently resolves to whichever member happens to carry that ordinal — a bare renumbered ordinal minting an
    /// unintended section is exactly the failure mode a name-keyed ask exists to close — so a name whose first
    /// character is not a letter is refused outright, never handed to <c>Enum.TryParse</c>.</summary>
    /// <param name="name">The bare member name, case-insensitive.</param>
    /// <param name="section">The parsed section, on success; <see langword="default"/> otherwise.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> names a defined <see cref="WorldSection"/>
    /// member.</returns>
    public static bool TryParseSectionName(ReadOnlySpan<char> name, out WorldSection section) {
        section = default;

        return (
            (!name.IsEmpty) &&
            char.IsLetter(c: name[0]) &&
            Enum.TryParse<WorldSection>(
            ignoreCase: true,
            result: out section,
            value: name
        ) &&
            Enum.IsDefined(value: section)
        );
    }
}
/// <summary>Which rule decided an authority check — the verdict half of <c>Server.WorldGrants.Allows</c>. Exactly one
/// rule fires per check, and the rule is the decision path, never a re-derivation: two of the five are denials whose
/// difference is invisible in a bare <see langword="bool"/> — being beaten by an exclusive reservation is not the
/// same state as never having been granted, and collapsing them hides an advertised-authority-with-no-effect
/// ambiguity from anyone debugging the grant table.</summary>
public enum GrantRule : byte {
    /// <summary>Denied — no row of the principal's capability set names the subject, and no wildcard covers it.</summary>
    [Refusal(door: "grant.authority", condition: "no row of the principal's capability set names the subject, and no wildcard covers it", kind: RefusalKind.Verdict)]
    NoHold,

    /// <summary>Denied — another principal exclusively reserves the subject (the exclusivity override beats every
    /// grant, including a live concrete row the caller genuinely holds). <see cref="GrantVerdict.Reserver"/> names the
    /// principal that won.</summary>
    [Refusal(door: "grant.authority", condition: "another principal exclusively reserves the subject, overriding every grant including one the caller genuinely holds", kind: RefusalKind.Verdict)]
    BeatenByReserver,

    /// <summary>Allowed — the caller is the exclusive reserver of the subject.</summary>
    ReserverMatch,

    /// <summary>Allowed — a row names the subject itself. Reported in preference to
    /// <see cref="WildcardHold"/> when both would apply: the concrete row is the more specific basis.</summary>
    ConcreteHold,

    /// <summary>Allowed — the <see cref="GrantSubject.All"/> wildcard row covers the subject.</summary>
    WildcardHold,

    /// <summary>Allowed — the caller holds no row of its own, but is a current member of a group whose own row (a
    /// grant to <see cref="WorldPrincipal.Group"/>) names the subject or its wildcard. Decided fresh on every check
    /// against the group table's live membership (never cached at grant time), so a member who leaves is denied on
    /// its very next check — the hold evaporates, it is never latched. <see cref="GrantVerdict.Group"/> names which
    /// group decided it. Checked only after the caller's own concrete/wildcard rows miss — a principal's own hold
    /// always wins first.</summary>
    GroupHold,

    /// <summary>Allowed — the caller holds no row of its own and is not a member of a reaching group, but owns a
    /// group (directly, via a document-authored <c>WorldOwnership</c> binding naming the caller as
    /// <see cref="OwnershipOwnerKind.Principal"/>; or transitively, via being a current member of a group that owns
    /// it under <see cref="OwnershipOwnerKind.Group"/>) whose own row names the subject or its wildcard. The same
    /// fallback shape as <see cref="GroupHold"/> — an owner reaches whatever the owned group's own rows hold — but
    /// sourced from an ownership binding rather than a membership roster: ownership is a deciding fact the door
    /// consults, never a grant row of its own (<c>WorldGrants</c> never mints one for it). Decided fresh against the
    /// live document's ownership/membership state, same as <see cref="GroupHold"/>; checked only after both the
    /// caller's own rows and <see cref="GroupHold"/> miss. <see cref="GrantVerdict.Group"/> names which owned group
    /// decided it.</summary>
    OwnershipHold,

    /// <summary>Denied — the target body carries a nonzero cell on a state row declaring
    /// <c>WorldStateRow.GatesDrive</c> (a state fact, not a grant): refused regardless of any Drive hold the caller
    /// genuinely has, including an exclusive reservation, for as long as that cell reads nonzero. Scoped to the
    /// intent-admission door alone (<c>WorldServer.ApplyIntentSubmission</c>) and its <c>world.why</c> read-back —
    /// never folded into the general <see cref="WorldPrincipal"/> capability check every other Drive/body query
    /// (session join/leave, an administrator's own lookup) also runs, because those ask "may this principal ever
    /// drive this body", a question a temporary status effect must not answer for them.
    /// <see cref="GrantVerdict.GateRow"/> names the deciding row.</summary>
    [Refusal(door: "grant.authority", condition: "the target body carries a nonzero cell on a state row declaring gatesDrive", kind: RefusalKind.Verdict)]
    DriveGated,
}
/// <summary>The result of one <c>Server.WorldGrants.Allows</c> check: allowed-or-not plus the rule that decided it —
/// produced inside the check on the deciding control path, never derived after the fact (a parallel explain function
/// would be a second implementation of the decision, free to drift). Implicitly converts to <see langword="bool"/> so
/// every boolean call site reads unchanged. Stack-only, allocation-free.
/// <para>Binding constraints for every consumer: a verdict is a function of (state, position-within-tick) — grants
/// and revokes apply synchronously inside the command-apply window, so any re-derivation claim owes a position pin;
/// a verdict may depend only on Simulation-lane state; and once-per-episode reporting latches are not part of the
/// verdict (the verdict says which rule fired, a latch says whether to print).</para></summary>
/// <param name="Rule">The rule that decided the check.</param>
/// <param name="Reserver">The exclusive reserver that beat the caller, for <see cref="GrantRule.BeatenByReserver"/>;
/// <see langword="null"/> otherwise.</param>
/// <param name="Group">The group id that decided it, for <see cref="GrantRule.GroupHold"/>/
/// <see cref="GrantRule.OwnershipHold"/>; <see langword="null"/> otherwise.</param>
/// <param name="GateRow">The deciding state row's name, for <see cref="GrantRule.DriveGated"/>; <see langword="null"/>
/// otherwise.</param>
public readonly record struct GrantVerdict(GrantRule Rule, WorldPrincipal? Reserver = null, string? Group = null, string? GateRow = null) {
    /// <summary>Gets a value indicating whether the check passed — <see langword="true"/> for the five allowing rules.</summary>
    public bool IsAllowed => (Rule is GrantRule.ReserverMatch or GrantRule.ConcreteHold or GrantRule.WildcardHold or GrantRule.GroupHold or GrantRule.OwnershipHold);

    /// <summary>Returns the verdict as a bare pass/fail, so boolean call sites read unchanged.</summary>
    /// <param name="verdict">The verdict.</param>
    public static implicit operator bool(GrantVerdict verdict) => verdict.IsAllowed;

    /// <summary>Describes a short stable label for <c>world.why</c> echoes — <c>no-hold</c>, <c>beaten-by-reserver</c>,
    /// <c>reserver-match</c>, <c>concrete-hold</c>, <c>wildcard-hold</c>, <c>group-hold</c>, <c>ownership-hold</c>,
    /// <c>drive-gated</c>.</summary>
    /// <returns>The label.</returns>
    public string Describe() => Rule switch {
        GrantRule.NoHold => "no-hold",
        GrantRule.BeatenByReserver => "beaten-by-reserver",
        GrantRule.ReserverMatch => "reserver-match",
        GrantRule.ConcreteHold => "concrete-hold",
        GrantRule.WildcardHold => "wildcard-hold",
        GrantRule.GroupHold => $"group-hold(group:{Group})",
        GrantRule.OwnershipHold => $"ownership-hold(group:{Group})",
        GrantRule.DriveGated => $"drive-gated(state:{GateRow})",
        _ => "?",
    };
    /// <summary>Describes the denial reason for a refusal message — the denial rules produce different text, which is the
    /// point: "exclusively reserved by seat1" and "no grant names it" were previously one indistinguishable line.
    /// Only meaningful when <see cref="IsAllowed"/> is <see langword="false"/>.</summary>
    /// <returns>The reason fragment.</returns>
    public string DescribeDenial() => Rule switch {
        GrantRule.BeatenByReserver => $"exclusively reserved by {(Reserver?.Describe() ?? "?")}",
        GrantRule.NoHold => "no grant names it",
        GrantRule.DriveGated => $"gated by state row '{GateRow}' — a nonzero per-body cell there refuses Drive regardless of any hold, including an exclusive reservation, until it reads zero again",
        _ => "not denied",
    };
}
/// <summary>One grant row — the wire payload of <c>world.grant</c>/<c>world.revoke</c>: a principal holds a capability
/// over a subject, optionally exclusive (the engagement latch generalized — acquiring an exclusive grant a live holder
/// owns is rejected). Revoke ignores <see cref="Exclusive"/>. The same shape doubles as the document row of
/// <see cref="WorldDefinition.Grants"/> — a world shipping a hold reviewably rather than only typing it live — applied
/// at boot through the identical <c>Server.WorldServer.Grant</c> path <c>world.grant</c> submits through, so an
/// illegitimate or conflicting authored row prints the same loud accept/reject line an operator would see typing it.
/// One shape, one decision procedure, whichever door authority walks through.</summary>
/// <param name="Principal">The acting identity the grant is for.</param>
/// <param name="Capability">The capability conferred.</param>
/// <param name="Subject">The subject the capability scopes to.</param>
/// <param name="Exclusive">Whether the grant is held exclusively (single holder per capability+subject).</param>
/// <param name="Budget">The per-tick dispatch allowance for the row's capability — compute, not space (a request
/// costs a host dispatch, not a record in a region). Only an <see cref="WorldCapability.Observe"/> or
/// <see cref="WorldCapability.Drive"/> grant to an untrusted principal (<see cref="PrincipalKind.Addon"/>/
/// <see cref="PrincipalKind.Peer"/>) may carry one today: the grant door (<c>Server.WorldGrants.TryGrant</c>)
/// requires it there (a defaulted budget would silently decide a DoS ceiling), refuses <c>0</c> (accepted-and-inert —
/// grant nothing instead), and refuses it everywhere else (a trusted principal's grant, or <c>Present</c>/<c>Control</c>/
/// <c>Mutate</c>/<c>Edit</c>) — those doors do not meter yet, and a field admitted ahead of enforcement would be a lie
/// in the schema. The effective ceiling at the addon door is
/// <c>min(Budget, Puck.Scripting.AddonAbi.MaxOutCells)</c> — deliberately not enforced here at the grant door, so the
/// grant schema and the ABI capacity constant stay free to move independently.</param>
/// <param name="Reach">An untrusted contributor's channel reach on a <see cref="WorldCapability.Drive"/> row,
/// carried alone: which declared ordinals this contributor may touch at all. Reach is not consent; a reached channel
/// contributes nothing until the occupying seat authors a positive ceiling for it.</param>
/// <param name="Consent">The ordinals named by the occupying seat's ceiling gesture on its own Drive row. The seat
/// may issue the gesture repeatedly to give different channels different ceilings; each gesture writes only the
/// ordinals it names and leaves the rest unchanged, while revocation clears the whole ceiling value.</param>
/// <remarks>An empty reach or consent mask is refused at the door, mirroring <see cref="Budget"/>'s <c>0</c> refusal:
/// reach-nothing is accepted-and-inert, so grant nothing instead. An unoccupied body is unaffected by any of this
/// (occupancy gates the pool's very existence — see <c>Server.WorldPopulation.IsHumanOccupied</c> — so a bot
/// body the grant already lets this principal drive keeps full authority regardless).</remarks>
/// <param name="Ceiling">The pool ceiling <c>c</c> (raw Q16.16, <c>0 &lt; c ≤ One</c>) bounding how far the untrusted
/// pool may pull the channels <see cref="Consent"/> names away from the human's own value. It is one number per
/// (seat, channel), authored by the seat, and the grant door enforces exactly that: a row carrying a ceiling must be
/// the occupying seat's own row (<c>seatN drive body:N</c>) and must name the channels it applies to. It is never
/// carried on a contributor's row and never derived across rows — no combination of contributor-declared numbers
/// (max, sum, or min) is defensible, and a contract nobody can state as a single number is not one. <c>0</c> is
/// refused at the door, mirroring <see cref="Budget"/>'s <c>0</c> refusal verbatim: pool-but-never-reach is
/// accepted-and-inert, so grant nothing instead of a ceiling that can never fire. A ceiling authored in the world
/// document is withheld at boot (the row itself still applies) — see <c>Server.WorldServer</c>'s constructor:
/// the document may pre-wire a contributor's reach, but consent is a thing only a seated human grants live.</param>
/// <param name="KindMask">The <see cref="MutationKindMask"/> a row admits — legal only on a
/// <see cref="WorldCapability.Mutate"/> row over a concrete <see cref="GrantSubjectKind.Section"/>,
/// <see cref="GrantSubjectKind.Creation"/>, or <see cref="GrantSubjectKind.Placement"/> subject, or an
/// <see cref="WorldCapability.Edit"/> row over a concrete <see cref="GrantSubjectKind.State"/> subject (never the
/// wildcard — "which kinds" presupposes one bounded target — and never any other capability). The grant door refuses
/// a bit outside the target's own declared kind set (<c>WorldMutationKindCatalog.KindsOf(section)</c>, where a
/// row-scoped subject resolves to the section that owns it, or <c>KindsOf(WorldSection.State)</c> for an Edit row)
/// and refuses an effective mask of zero (an admitted-but-inert
/// bit set is a grant that lies — the identical "grant nothing instead" rule <see cref="Budget"/>'s <c>0</c> and
/// <see cref="Ceiling"/>'s <c>0</c> already enforce). On an Edit row this is what separates bumping a state row from
/// redefining it: <c>verbs:UpsertStateCell,RemoveStateCell</c> admits the per-cell writes while denying the
/// whole-row <c>UpsertStateRow</c>/<c>RemoveStateRow</c> that could re-author the row's envelope. An unmasked Edit
/// row keeps full reach over its subject, so deny-by-default plus opt-in narrowing holds and no seeded row changes
/// meaning. A <see langword="null"/> mask on a re-grant of the same
/// (<see cref="Principal"/>, <see cref="Capability"/>, <see cref="Subject"/>) row clears a previously-recorded mask
/// — unlike <see cref="Budget"/>/<see cref="Reach"/>, which only ever write when the incoming grant carries one and
/// otherwise leave the prior value untouched; a mask a re-grant does not repeat is a mask the operator meant to take
/// back, not one this door defaults into surviving silently. Revoking the row clears it outright. When a principal
/// holds both a concrete row and the (trusted-only) wildcard row, the deciding row from
/// <see cref="GrantVerdict.Rule"/> governs which mask applies — <see cref="GrantRule.ConcreteHold"/> beats
/// <see cref="GrantRule.WildcardHold"/>, exactly as it does for the bare allow/deny check.</param>
/// <param name="WriteMask">The <see cref="DocumentWriteMask"/> a row admits on the cross-document durable-state
/// write-back channel — legal only on a <see cref="WorldCapability.Mutate"/> row over a concrete
/// <see cref="GrantSubjectKind.State"/> subject, the one door that speaks <see cref="WorldDocumentWriteKind"/>
/// operations (see <c>Server.WorldOwnedWorlds.Decide</c>). A separate field from <see cref="KindMask"/>, not a second
/// reading of it: the two vocabularies share a bit-lane shape and nothing else, and collapsing them into one
/// <c>ulong</c> made bit 0 mean <c>UpsertKit</c> on one row and <c>Set</c> on another. Same zero/inadmissible-bit
/// refusals and same clear-on-re-grant rule as <see cref="KindMask"/>.</param>
/// <param name="EventBudget">The per-tick event-cell allowance for an <see cref="WorldCapability.Observe"/> row over
/// an event-bearing subject (<see cref="GrantSubjectKind.Body"/>, <see cref="GrantSubjectKind.Screen"/>,
/// <see cref="GrantSubjectKind.Region"/>, <see cref="GrantSubjectKind.Seat"/>, or
/// <see cref="GrantSubjectKind.Adjacency"/>) — a grant-row property alongside
/// <see cref="Budget"/>, metering a different cost: <see cref="Budget"/> meters query dispatch (a guest asking), this
/// meters event push volume (the host telling) — two separate meters, never one renamed. A row with no
/// <see cref="EventBudget"/> still observes normally (a bare <c>observe body:&lt;n&gt;</c> keeps working exactly as
/// before) but receives no events for that subject. Required (refused by name otherwise) on an Observe row over
/// <see cref="GrantSubjectKind.Region"/>, <see cref="GrantSubjectKind.Seat"/>, <see cref="GrantSubjectKind.Screen"/>,
/// or <see cref="GrantSubjectKind.Adjacency"/>,
/// since those subject kinds carry no other live meaning — an event-bearing subject with no event budget would be
/// accepted-and-inert, the identical rule <see cref="Budget"/>'s own <c>0</c>-refusal enforces. That requirement
/// stacks with (never replaces) the pre-existing rule that every untrusted principal's Observe row also needs
/// <see cref="Budget"/>: the dispatch meter does not know a subject carries no query verb, so an
/// <c>observe region:&lt;name&gt;</c> row needs both <see cref="Budget"/> and <see cref="EventBudget"/> today. Refused
/// on any capability but <see cref="WorldCapability.Observe"/>, and <c>eventBudget:0</c> is refused unconditionally
/// (grant nothing instead).</param>
/// <param name="HoldCeiling">The timed-channel-press ceiling in raw Q16.16 seconds. Legal only on a
/// <see cref="WorldCapability.Drive"/> row and bounded by the server's engine backstop. Omission selects
/// <see cref="DefaultHoldSeconds"/>; zero forbids timed holds while leaving live held input untouched.</param>
public readonly record struct WorldGrant(WorldPrincipal Principal, WorldCapability Capability, GrantSubject Subject, bool Exclusive, ushort? Budget = null, ChannelReachMask? Reach = null, ChannelConsentMask? Consent = null, long? Ceiling = null, MutationKindMask? KindMask = null, ushort? EventBudget = null, long? HoldCeiling = null, DocumentWriteMask? WriteMask = null) {
    /// <summary>The default timed-press policy, in seconds, for a Drive row that omits <see cref="HoldCeiling"/>.</summary>
    public const float DefaultHoldSeconds = 2f;
}
/// <summary>One capability/subject pair a <see cref="WorldAddonRow"/>'s manifest requests (see
/// <see cref="WorldAddonRow.Requests"/>) — a designation only, never authority: requesting is not receiving. Deny by
/// default holds regardless of what a manifest names here; the console's grant table (live, via <c>world.grant</c>) or
/// the document's own <see cref="WorldDefinition.Grants"/> section decide what subset, if any, is actually held. The
/// requesting principal is always the addon's own <see cref="WorldPrincipal.Addon"/> identity — implicit, never carried
/// on the row itself, because a manifest can only ever ask on its own behalf.</summary>
/// <param name="Capability">The capability requested.</param>
/// <param name="Subject">The subject requested.</param>
public readonly record struct WorldCapabilityRequest(WorldCapability Capability, GrantSubject Subject);
