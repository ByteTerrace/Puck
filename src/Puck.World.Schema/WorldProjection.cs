using Puck.Commands;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>The <c>metadata</c> section as a Presentation-tier peer sees it — <see cref="WorldMetadataSection.Title"/>
/// and <see cref="WorldMetadataSection.Description"/> only. Authors, tags, and <see cref="WorldMetadataSection.Custom"/>
/// stay behind the authority; see <see cref="WorldProjectionDocument"/>'s remarks.</summary>
/// <param name="Title">The world's author-facing display name, when authored.</param>
/// <param name="Description">The author description, when authored.</param>
public sealed record WorldProjectedMetadata(string? Title = null, string? Description = null);
/// <summary>
/// One kit as a visitor's client sees it — the embodiment facts (which motion tuning a body wears, what shape it
/// occupies, whether it depenetrates). The projection document's own row type, not a <see cref="WorldKit"/> with
/// holes: it carries no member for the kit's <c>producers</c>/<c>actions</c>, which are the world's game logic and
/// are read only by the authority that runs them.
/// </summary>
/// <param name="Name">The kit's name — the identity a placement/assignment row addresses.</param>
/// <param name="BodyMotionProgram">The program name the destination advances this kit on. A name only; the
/// <c>bodyMotionPrograms</c> section itself never crosses below the replica tier.</param>
/// <param name="Motion">The kit's motion tuning, which is what decides how a client interpolates and frames a body
/// wearing it.</param>
/// <param name="Collider">The kit's collider, when it authors one.</param>
/// <param name="BodyContact">Whether two bodies wearing this kit physically depenetrate.</param>
public sealed record WorldProjectedKit(
    string Name,
    string BodyMotionProgram,
    WorldMotion Motion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCollider? Collider = null,
    WorldBodyContactMode BodyContact = WorldBodyContactMode.Overlap
);
/// <summary>Where a projection came from and what it is. Every projection carries this: a document that cannot say
/// which authority composed it, at what revision, and under which tier is a document a reader has to guess
/// about.</summary>
/// <param name="Authority">The composing authority's own addressable namespace (a <c>host.authority</c> endpoint, or
/// the process-local <c>boot</c> namespace for an in-process authority).</param>
/// <param name="DocumentId">The source document's stable id, when it carries one.</param>
/// <param name="Revision">The source document's revision at composition.</param>
/// <param name="Tier">The tier this projection was composed at — always
/// <see cref="WorldDisclosureTier.Presentation"/> today, since <see cref="WorldDisclosureTier.Replica"/> sends the
/// definition verbatim and <see cref="WorldDisclosureTier.Frames"/> sends no document at all.</param>
public sealed record WorldProjectionProvenance(string Authority, string? DocumentId, int Revision, WorldDisclosureTier Tier);
/// <summary>
/// <c>puck.world.projection.v1</c> — what an authority hands a peer holding the
/// <see cref="WorldDisclosureTier.Presentation"/> tier. A separate versioned document rather than a
/// <see cref="WorldDefinition"/> with sections nulled out: a partial definition either refuses at
/// <see cref="WorldDefinitionValidator"/> or misreports what it carries. This type's member list
/// is the disclosure decision: a section that must not leave an authority below the replica tier has no member here.
/// <para>Absent by construction: <c>rules</c>, <c>grants</c>, <c>state</c>, <c>admission</c>,
/// <c>generation</c>, <c>generators</c>, <c>groups</c>, <c>properties</c>, <c>addons</c>, <c>storage</c>,
/// <c>host</c>, <c>authoring</c>, <c>identity</c>, <c>inputHold</c>, <c>targetRegisters</c>,
/// <c>bodyMotionPrograms</c>, <c>portals</c>, and every kit's <c>producers</c>/<c>actions</c> (see
/// <see cref="WorldProjectedKit"/>).</para>
/// <para><see cref="Adjacencies"/>, <see cref="Destinations"/>, <see cref="References"/>, and
/// <see cref="Interactions"/> cross because <see cref="WorldAdjacencyPolicy.TryDeriveOverlap(WorldDefinition, WorldDefinition, out Puck.Maths.FixedQ4816, out string)"/> reads them from both
/// sides of a seam and must derive the same depth on each; withholding one side's inputs desymmetrizes it silently.</para>
/// <para><see cref="Metadata"/> crosses in reduced form: <see cref="WorldProjectedMetadata"/> carries
/// <c>title</c>/<c>description</c> only — a world's authored name and blurb are harmless to a presentation-tier
/// peer. <c>authors</c>, <c>tags</c>, and <c>custom</c> never cross; <c>custom</c> in particular is an unbounded
/// author scratch bag that may hold notes never meant to leave the authority.</para>
/// </summary>
/// <param name="Provenance">Who composed this, from what, at which revision and tier.</param>
/// <param name="Motion">The world's motion defaults.</param>
/// <param name="SpawnPoints">The authored spawn points.</param>
/// <param name="Render">The render defaults.</param>
/// <param name="Screens">The screen surfaces.</param>
/// <param name="Cameras">The camera rows.</param>
/// <param name="Population">The population defaults — capacity, distribution, seat activation.</param>
/// <param name="PlayerDefaults">The player defaults.</param>
/// <param name="Channels">The channel table — the ordinal vocabulary an intent image is addressed in.</param>
/// <param name="Kits">The kit roster, projected (see <see cref="WorldProjectedKit"/>).</param>
/// <param name="DefaultSeatKit">The default seat kit's name.</param>
/// <param name="Assignment">The body-to-kit assignment.</param>
/// <param name="BindingOverlays">The world's binding layers — a visitor's seat composes over them.</param>
/// <param name="Creations">The embedded creation documents rendering resolves shapes from.</param>
/// <param name="Placements">The placement rows, as the recipient's state disclosure deals them
/// (<see cref="WorldStateDisclosure.Disclose"/>).</param>
/// <param name="Speakers">The speaker rows.</param>
/// <param name="Tunes">The tune assets.</param>
/// <param name="Patches">The synth patch assets.</param>
/// <param name="Audio">The audio defaults.</param>
/// <param name="Collision">The contact tuning — a client reads body radius/height from it to frame and interpolate.</param>
/// <param name="Views">The authored window composition (slots, layouts, seat framing), or <see langword="null"/>
/// when the document authors none — a seatless world composes no seat view.</param>
/// <param name="Looks">The look rows.</param>
/// <param name="LookAssignment">The body-to-look assignment.</param>
/// <param name="Dynamics">The named second-order "personality" rows every look/camera/kit follower reference
/// resolves against.</param>
/// <param name="Hud">The HUD section.</param>
/// <param name="Fields">The field lattice declaration — a presentation peer renders field geometry from the snapshot's
/// cell deltas and needs the lattice footprint, height scales, and colours to do it.</param>
/// <param name="Simulation">The authored simulation rate, when the world authors one.</param>
/// <param name="Interactions">The interaction table — carried for its distance reach alone (see the type remarks).</param>
/// <param name="References">The named neighbouring documents.</param>
/// <param name="Destinations">The scoped destination rows layered over those references.</param>
/// <param name="Adjacencies">The reciprocal boundary rows.</param>
/// <param name="Metadata">The title/description half of <c>metadata</c>, when the world authors one — see the type
/// remarks.</param>
/// <param name="Observations">Explicitly disclosed state observations, without draw bookkeeping and with no trait but
/// the follower an eased cell carries.</param>
/// <param name="Spaces">The vector spaces the disclosed vector rows among <paramref name="Observations"/> name, and no
/// other: a space declares only a model, a revision, and a dimension count, and a vector row loads only against its
/// own.</param>
/// <param name="Timeline">The world's clocks as a recipient evaluates them from the tick it presents: each tick clock as
/// authored, and each state clock a carried value keys on as an anchored clock holding the anchor of its phase the
/// recipient was last sent (<see cref="WorldClockAnchorLedger"/>), never the row it reads. A state clock no carried
/// value keys on does not cross.</param>
public sealed record WorldProjectionDocument(
    WorldProjectionProvenance Provenance,
    WorldMotionDefaults Motion,
    IReadOnlyList<WorldSpawnPoint> SpawnPoints,
    WorldRenderDefaults Render,
    IReadOnlyList<WorldScreen> Screens,
    IReadOnlyList<WorldCamera> Cameras,
    WorldBodiesDefaults Population,
    WorldPlayerDefaults PlayerDefaults,
    IReadOnlyList<WorldChannel> Channels,
    IReadOnlyList<WorldProjectedKit> Kits,
    string DefaultSeatKit,
    WorldRowAssignment Assignment,
    IReadOnlyList<WorldBindingOverlay> BindingOverlays,
    [property: System.Text.Json.Serialization.JsonPropertyName("prototypes")] IReadOnlyList<WorldPrototype> Creations,
    IReadOnlyList<WorldPlacement> Placements,
    IReadOnlyList<WorldSpeaker> Speakers,
    IReadOnlyList<WorldTune> Tunes,
    IReadOnlyList<WorldPatch> Patches,
    WorldAudioDefaults Audio,
    WorldCollision Collision,
    WorldViewDefaults? Views,
    IReadOnlyList<WorldLook> Looks,
    WorldRowAssignment LookAssignment,
    IReadOnlyList<DynamicsRow> Dynamics,
    WorldHudSection Hud,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldFieldsSection? Fields = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSimulationDefaults? Simulation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldInteractionsSection? Interactions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldReference>? References = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldDestination>? Destinations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldAdjacency>? Adjacencies = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldProjectedMetadata? Metadata = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldObservedRow>? Observations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StateSpace>? Spaces = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldTimelineSection? Timeline = null
) {
    /// <summary>The document schema version. A reader refuses any other value; the canonical writer always emits it.</summary>
    public const string SchemaVersion = "puck.world.projection.v1";

    /// <summary>Gets the unknown top-level members captured during deserialization — the same
    /// <see cref="DocumentExtensionsPolicy"/> regime <see cref="WorldDefinition.Extensions"/> rides: a reserved-prefix
    /// ('$'/'_') key round-trips untouched, any other key refuses at
    /// <see cref="WorldProjection.TryDeserialize"/>.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
    /// <summary>Gets the document schema tag — <see cref="SchemaVersion"/> for a well-formed projection.</summary>
    public string Schema { get; init; } = SchemaVersion;
}
/// <summary>
/// The one egress door. A raw <see cref="WorldDefinition"/> leaves an authority only at
/// <see cref="WorldDisclosureTier.Replica"/>; every other remote egress composes through <see cref="Compose"/> and
/// sends a <see cref="WorldProjectionDocument"/>. Local in-process consumers (the boot client, a colocated instance)
/// read the definition directly — colocated trust is home trust.
/// </summary>
/// <remarks>
/// <para><see cref="TryToDefinition"/> rebuilds a <see cref="WorldDefinition"/> from a projection so a receiving
/// consumer keeps its existing type. The wire carries the projection; the receiver constructs a locally-valid
/// document whose undisclosed sections carry their neutral built-in defaults. A hydrated document is never saved,
/// journaled, or treated as a source of authority.</para>
/// <para>A projection is flat: <see cref="Compose"/> answers every <c>state.&lt;row&gt;[.&lt;key&gt;]</c> document
/// value from the composing authority's own state and sends the literal, because the projection discloses no state
/// section for a receiver to answer one against.</para>
/// <para>At <see cref="WorldDisclosureTier.Replica"/> <see cref="Compose"/> answers <see langword="null"/> and the
/// caller sends the definition whole, in the compact form every definition travels in
/// (<see cref="WorldDefinitionSerialization.SerializeCompact"/>). For a flat document that download re-serializes to
/// the authored file's canonical bytes; for a document loaded from a <c>basis</c> delta it is the flattened
/// composition — self-contained by
/// construction, since a live document never carries a basis (see <see cref="WorldDefinition.Basis"/>), and a
/// receiver has no directory to resolve one against.</para>
/// </remarks>
public static class WorldProjection {
    /// <summary>Gets the definition an observer holds when nothing of a world is disclosed to it — the
    /// <see cref="WorldDisclosureTier.Frames"/> tier, or an observation withheld before its first delivery: a document
    /// authoring no section at all.</summary>
    public static WorldDefinition Undisclosed { get; } = WorldDefinitionSerialization.Deserialize(utf8Json: """{"schema":"puck.world.definition.v1"}"""u8.ToArray());

    // A projection discloses no `state` section, so a retained `state.<row>[.<key>]` reference would reach the peer
    // as a pointer into a table it was never handed — read as one, it faults; resolved as one, it refuses. The egress
    // is therefore flat: every reference is answered from this authority's own state and dropped.
    //
    // The rows above are the LIVE document's own objects, and their value holders carry the authored reference
    // canonical write-back preserves, so the flattening runs on a rehydrated private copy.
    private static WorldProjectionDocument Flatten(WorldProjectionDocument projection, WorldDefinition definition) {
        if (!WorldStateDocumentValues.HasReference(graph: projection)) {
            return projection;
        }

        if (
            !TryDeserialize(
            utf8Json: Serialize(projection: projection),
            projection: out var copy,
            reason: out var reason
        ) ||
            (copy is null)
        ) {
            throw new InvalidOperationException(message: $"the composed projection did not round-trip: {reason}");
        }

        if (!WorldStateDocumentValues.TryFlatten(
            graph: copy,
            reason: out var flattenReason,
            source: definition
        )) {
            throw new InvalidOperationException(message: $"the composed projection could not be flattened: {flattenReason}");
        }

        return copy;
    }

    /// <summary>Composes what <paramref name="tier"/> authorizes a peer to receive of <paramref name="definition"/>.</summary>
    /// <param name="definition">The authority's live document.</param>
    /// <param name="tier">The tier the admission door decided for this peer.</param>
    /// <param name="authority">The composing authority's addressable namespace.</param>
    /// <param name="revision">The document revision this composition names.</param>
    /// <returns>The projection at <see cref="WorldDisclosureTier.Presentation"/>; <see langword="null"/> at
    /// <see cref="WorldDisclosureTier.Replica"/> (the caller sends the definition verbatim) and at
    /// <see cref="WorldDisclosureTier.Frames"/> (the caller sends no document at all).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <param name="recipient">The authenticated recipient, or null for public observation.</param>
    /// <param name="arena">The authority's live store, which every disclosed value and audience is read from.</param>
    /// <param name="time">The clocks a disclosed cell's value-over-time trait is read at.</param>
    /// <param name="unrestricted">Whether to compose as a reader every restriction admits — the most any recipient could
    /// be handed, which a measurement sizing for every possible recipient reads — instead of as
    /// <paramref name="recipient"/>.</param>
    /// <param name="anchors">The anchors the recipient holds, which the composed timeline carries while their
    /// predictions hold and replaces where they miss, or <see langword="null"/> for a one-off composition that no
    /// recipient keeps anchors from, which carries each state clock's anchor at <paramref name="time"/>.</param>
    /// <exception cref="InvalidOperationException">A carried value keys on a state clock whose row the recipient may not
    /// read, or binds a state cell it may not read: the composition refuses by name before any derived value is
    /// emitted.</exception>
    public static WorldProjectionDocument? Compose(WorldDefinition definition, WorldDisclosureTier tier, string authority, int revision, StateArena arena, in ArenaTime time, Principal? recipient = null, bool unrestricted = false, WorldClockAnchorLedger? anchors = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (tier != WorldDisclosureTier.Presentation) {
            return null;
        }

        // Every state clock a carried value keys on is a dependency of a derived value: it must pass the disclosure
        // boundary before anything is composed, or nothing is.
        var keyed = KeyedClocks(definition: definition);
        var bound = WorldKeyedValues.BoundOf(definition: definition);

        if (!unrestricted) {
            RefuseHiddenClocks(
                arena: arena,
                definition: definition,
                keyed: keyed,
                recipient: recipient
            );
            RefuseHiddenBindings(
                arena: arena,
                bound: bound,
                definition: definition,
                recipient: recipient
            );
        }

        // Placements cross as the recipient's disclosure deals them: a dealt placement reads the cells it was dealt
        // from, so one dealt from a cell this recipient may not read is re-dealt without it.
        var placements = WorldStateDisclosure.Disclose(
            arena: arena,
            definition: definition,
            recipient: recipient,
            unrestricted: unrestricted
        ).Definition.Placements;
        var kits = new WorldProjectedKit[definition.Kits.Count];

        for (var index = 0; (index < kits.Length); index++) {
            var kit = definition.Kits[index];

            kits[index] = new WorldProjectedKit(
                Name: kit.Name,
                BodyMotionProgram: kit.BodyMotionProgram,
                Motion: kit.Motion,
                Collider: kit.Collider,
                BodyContact: kit.BodyContact
            );
        }

        var projection = new WorldProjectionDocument(
            Provenance: new WorldProjectionProvenance(
                Authority: authority,
                DocumentId: definition.DocumentId,
                Revision: revision,
                Tier: WorldDisclosureTier.Presentation
            ),
            Motion: definition.Motion,
            SpawnPoints: definition.SpawnPoints,
            Render: definition.Render,
            Screens: definition.Screens,
            Cameras: definition.Cameras,
            Population: definition.Population,
            PlayerDefaults: definition.PlayerDefaults,
            Channels: definition.Channels,
            Kits: kits,
            DefaultSeatKit: definition.DefaultSeatKit,
            Assignment: definition.Assignment,
            BindingOverlays: definition.BindingOverlays,
            Creations: definition.Creations,
            Placements: placements,
            Speakers: definition.Speakers,
            Tunes: definition.Tunes,
            Patches: definition.Patches,
            Audio: definition.Audio,
            Collision: definition.Collision,
            Views: definition.ViewsRaw,
            Looks: definition.Looks,
            LookAssignment: definition.LookAssignment,
            Dynamics: definition.Dynamics,
            Hud: definition.Hud,
            Simulation: definition.Simulation,
            Interactions: definition.Interactions,
            References: definition.References,
            Destinations: definition.Destinations,
            Adjacencies: definition.Adjacencies,
            Metadata: ((definition.Metadata is { } metadata)
            ? new WorldProjectedMetadata(
                    Title: metadata.Title,
                    Description: metadata.Description
                )
            : null),
            Timeline: CarriedClocks(
                anchors: anchors,
                definition: definition,
                keyed: keyed,
                time: in time
            )
        );

        WorldStateDisclosure.ValidateBindings(
            arena: arena,
            definition: definition,
            graph: projection,
            recipient: recipient,
            unrestricted: unrestricted
        );
        var observations = WorldStateDisclosure.Compose(
            arena: arena,
            definition: definition,
            presented: PresentedRows(
                bound: bound,
                definition: definition
            ),
            recipient: recipient,
            time: in time,
            unrestricted: unrestricted
        );

        projection = projection with {
            Observations = observations,
            Spaces = SpacesOf(
                observations: observations,
                spaces: definition.StateRaw?.Spaces
            ),
        };
        return Flatten(
            definition: definition,
            projection: projection
        );
    }

    // Every clock a value of the document keys on, by the path that first names it: a keyed section's own clock and
    // each keyed value's.
    private static Dictionary<string, string> KeyedClocks(WorldDefinition definition) {
        var keyed = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var (path, clock) in new[] { ("render.lighting", definition.Render.Lighting?.Clock), ("render.sky", definition.Render.Sky?.Clock) }) {
            if (clock is not null) {
                _ = keyed.TryAdd(key: clock, value: path);
            }
        }

        foreach (var value in WorldKeyedValues.Of(definition: definition)) {
            _ = keyed.TryAdd(key: value.Track.Clock, value: value.Path);
        }

        return keyed;
    }
    // A state clock reads its row's slot, so a keyed value on it is a reading of that cell: a recipient that may not
    // read the cell is sent no value derived from it.
    private static void RefuseHiddenClocks(WorldDefinition definition, StateArena arena, Dictionary<string, string> keyed, Principal? recipient) {
        foreach (var clock in (definition.Timeline.Clocks ?? [])) {
            if (
                (clock?.State is not { } rowName) ||
                !keyed.TryGetValue(key: clock.Name, value: out var path) ||
                (definition.State.FirstOrDefault(predicate: row => string.Equals(
                    a: row.Name.Value,
                    b: rowName,
                    comparisonType: StringComparison.Ordinal
                )) is not { } row)
            ) {
                continue;
            }

            if (!WorldStateDisclosure.CanRead(
                arena: arena,
                definition: definition,
                key: WorldStateRow.SlotKey,
                recipient: recipient,
                row: row
            )) {
                throw new InvalidOperationException(message: $"{path} keys on clock '{clock.Name}', which reads state row '{rowName}' this recipient may not read; a hidden source sends no derived value.");
            }
        }
    }
    // A bindable bound to a state cell is a reading of that cell, under the rule a state clock's keys are: a recipient
    // that may not read the cell, or, for a value read per body, every cell of its row, is sent no value derived from
    // it.
    private static void RefuseHiddenBindings(WorldDefinition definition, StateArena arena, IReadOnlyList<WorldBoundValue> bound, Principal? recipient) {
        foreach (var (path, binding) in bound) {
            if (definition.State.FirstOrDefault(predicate: row => string.Equals(
                a: row.Name.Value,
                b: binding.Row,
                comparisonType: StringComparison.Ordinal
            )) is not { } row) {
                continue;
            }

            bool Reads(CellName key) => WorldStateDisclosure.CanRead(
                arena: arena,
                definition: definition,
                key: key,
                recipient: recipient,
                row: row
            );
            var perBody = string.Equals(
                a: binding.Key,
                b: StateBinding.BodyKey,
                comparisonType: StringComparison.Ordinal
            );

            // A key no cell name spells reads nothing, which the validator refuses; a per-body read reads every cell.
            var reads = (perBody
                ? (Reads(key: WorldStateRow.SlotKey) && (row.Cells ?? []).All(predicate: held => Reads(key: held.Key)))
                : (!CellName.TryParse(
                    candidate: (binding.Key ?? WorldStateRow.SlotKey.Value),
                    name: out var cell,
                    reason: out _
                ) || Reads(key: cell)));

            if (reads) {
                continue;
            }

            throw new InvalidOperationException(message: $"{path} binds state row '{binding.Row}' this recipient may not read; a hidden source sends no derived value.");
        }
    }
    // The rows a presented bindable reads, which cross as observations so the recipient reads the value the authority
    // presents rather than its fallback; a field row crosses as the field it is, never also as an observation.
    private static HashSet<string>? PresentedRows(WorldDefinition definition, IReadOnlyList<WorldBoundValue> bound) {
        if (bound.Count == 0) {
            return null;
        }

        var rows = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var (_, binding) in bound) {
            if (definition.State.FirstOrDefault(predicate: row => string.Equals(
                a: row.Name.Value,
                b: binding.Row,
                comparisonType: StringComparison.Ordinal
            )) is { Field: null }) {
                _ = rows.Add(item: binding.Row);
            }
        }

        return rows;
    }
    // The timeline a projection carries: each tick clock as authored, each state clock a value keys on as the anchored
    // clock the recipient's ledger carries for it, or, for a one-off composition no recipient holds anchors from, the
    // anchor of its phase now; and nothing else; null when that is no clock.
    private static WorldTimelineSection? CarriedClocks(WorldDefinition definition, Dictionary<string, string> keyed, WorldClockAnchorLedger? anchors, in ArenaTime time) {
        var clocks = new List<WorldClock>();

        foreach (var clock in (definition.Timeline.Clocks ?? [])) {
            if (clock is null) {
                continue;
            }

            if (clock.IsTickClock) {
                clocks.Add(item: clock);
            } else if (clock.IsStateClock && keyed.ContainsKey(key: clock.Name)) {
                clocks.Add(item: ((anchors is not null)
                    ? anchors.Carry(
                        clock: clock,
                        definition: definition,
                        engineTick: time.EngineTick,
                        tick: time.Tick
                    )
                    : new WorldClock(
                        Anchor: WorldClockAnchors.Read(
                            clock: clock,
                            definition: definition,
                            engineTick: time.EngineTick,
                            tick: time.Tick
                        ),
                        Name: clock.Name,
                        SpanSeconds: clock.SpanSeconds
                    )));
            }
        }

        return ((clocks.Count == 0)
            ? null
            : new WorldTimelineSection(Clocks: clocks));
    }

    /// <summary>Returns why a hydrated definition's clocks do not stand, or <see langword="null"/> when they do: a value
    /// keyed on a clock the timeline does not declare would resolve to its fallback, a state clock would read a row no
    /// projection carries, and an anchor that names a rate without the ticks it is per predicts nothing.</summary>
    /// <param name="definition">The hydrated definition.</param>
    /// <returns>The named refusal, or <see langword="null"/>.</returns>
    public static string? Uncarried(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var clock in (definition.Timeline.Clocks ?? [])) {
            if ((clock is null) || string.IsNullOrWhiteSpace(value: clock.Name) || !names.Add(item: clock.Name)) {
                return "projection clocks must be non-null and named uniquely.";
            }

            if (!double.IsFinite(d: clock.Span) || (clock.Span <= 0d)) {
                return $"projection clock '{clock.Name}' must have a finite positive span.";
            }

            if (clock.IsTickClock) {
                if ((clock.Anchor is not null) || !WorldClocks.TryWholeTicks(seconds: clock.PeriodSeconds!.Value, ticks: out _) ||
                    ((clock.StartSeconds is { } start) && (!double.IsFinite(d: start) || (start < 0d) || (start >= clock.Span)))) {
                    return $"projection tick clock '{clock.Name}' must have a whole-tick period, a start inside its span, and no anchor.";
                }
            } else if (clock.StartSeconds is not null) {
                return $"projection anchored clock '{clock.Name}' cannot carry a tick clock's start.";
            }

            if (clock.IsStateClock) {
                return $"projection carries clock '{clock.Name}' over state row '{clock.State}'; a projection carries a state clock as an anchor of its phase, never its row.";
            }

            if (clock.Anchor is { IsWellFormed: false }) {
                return $"projection anchors clock '{clock.Name}' with rate {clock.Anchor.Rate} over {clock.Anchor.Step} engine ticks; a rate names the ticks it is per, and only a rate does.";
            }
        }

        foreach (var (clock, path) in KeyedClocks(definition: definition)) {
            if (!WorldKeyResolver.TryClock(
                clock: out _,
                name: clock,
                timeline: definition.Timeline
            )) {
                return $"projection keys {path} on clock '{clock}', which it does not carry.";
            }
        }

        return null;
    }

    // The declared spaces a disclosed vector row names, in declaration order; null when no vector row was disclosed.
    private static StateSpace[]? SpacesOf(IReadOnlyList<WorldObservedRow>? observations, IReadOnlyList<StateSpace>? spaces) {
        if (
            (observations is null) ||
            (spaces is null)
        ) {
            return null;
        }

        var named = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var observed in observations) {
            if (observed.Space is { } space) {
                _ = named.Add(item: space);
            }
        }

        return ((named.Count == 0)
            ? null
            : [.. spaces.Where(predicate: space => named.Contains(item: space.Name.Value))]);
    }

    /// <summary>Serializes a projection to its compact canonical UTF-8 bytes, the form it travels to a recipient in:
    /// <see cref="Serialize"/>'s members and order with no whitespace.</summary>
    /// <param name="projection">The projection.</param>
    /// <returns>The compact canonical UTF-8 byte form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projection"/> is <see langword="null"/>.</exception>
    public static byte[] SerializeCompact(WorldProjectionDocument projection) {
        ArgumentNullException.ThrowIfNull(argument: projection);

        return CanonicalJsonDocument.SerializeCompact(
            jsonTypeInfo: WorldJsonContext.Default.WorldProjectionDocument,
            value: projection
        );
    }
    /// <summary>Serializes a projection to its canonical UTF-8 bytes.</summary>
    /// <param name="projection">The projection.</param>
    /// <returns>The canonical UTF-8 byte form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projection"/> is <see langword="null"/>.</exception>
    public static byte[] Serialize(WorldProjectionDocument projection) {
        ArgumentNullException.ThrowIfNull(argument: projection);

        return CanonicalJsonDocument.Serialize(
            jsonTypeInfo: WorldJsonContext.Default.WorldProjectionDocument,
            value: projection
        );
    }
    /// <summary>Parses a projection from untrusted bytes, refusing by name — the same Try-shaped, never-throwing
    /// discipline every other wire leaf follows.</summary>
    /// <param name="utf8Json">The document bytes.</param>
    /// <param name="projection">The projection on success.</param>
    /// <param name="reason">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the bytes are a well-formed <c>puck.world.projection.v1</c> document.</returns>
    public static bool TryDeserialize(ReadOnlySpan<byte> utf8Json, out WorldProjectionDocument? projection, out string reason) {
        projection = null;

        try {
            projection = JsonSerializer.Deserialize(
                utf8Json: utf8Json,
                jsonTypeInfo: WorldJsonContext.Untrusted.WorldProjectionDocument
            );
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
            reason = $"the projection is not a valid {WorldProjectionDocument.SchemaVersion} document: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        if (projection is null) {
            reason = "the projection deserialized to null.";

            return false;
        }

        if (!string.Equals(
            a: projection.Schema,
            b: WorldProjectionDocument.SchemaVersion,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"projection schema '{projection.Schema}' is not {WorldProjectionDocument.SchemaVersion}.";
            projection = null;

            return false;
        }

        foreach (var key in ((projection.Extensions?.Keys) ?? ((ICollection<string>)Array.Empty<string>()))) {
            if (!DocumentExtensionsPolicy.IsReservedKey(key: key)) {
                reason = $"projection contains unrecognized top-level member '{key}'.";
                projection = null;

                return false;
            }
        }

        if (projection.Provenance.Tier != WorldDisclosureTier.Presentation) {
            reason = $"projection provenance names tier '{projection.Provenance.Tier}'; only '{WorldDisclosureTier.Presentation}' composes a projection document.";
            projection = null;

            return false;
        }

        reason = string.Empty;

        return true;
    }
    /// <summary>Rebuilds a locally-valid <see cref="WorldDefinition"/> from a projection — see the class remarks. Every
    /// undisclosed section arrives as its neutral built-in default, never as a fabricated stand-in for what the
    /// composing authority actually authored. The <see cref="WorldProjectionDocument.Observations"/> the projection
    /// was composed with arrive as plain <c>state.world</c> rows holding exactly the disclosed cells, since the
    /// composition already chose them for this recipient, and a vector row arrives in the vector space
    /// <see cref="WorldProjectionDocument.Spaces"/> carries for it.</summary>
    /// <remarks>
    /// The hydration runs the same document-value resolution pass a file load runs, so a delivered definition is
    /// indistinguishable from a loaded one. <see cref="Compose"/> flattens what it sends, so a peer that still names a
    /// state cell no observed row carries refuses here rather than faulting later at the first read of the value.
    /// </remarks>
    /// <param name="projection">The projection.</param>
    /// <param name="definition">The hydrated definition on success.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the projection hydrated and every document value resolved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projection"/> is <see langword="null"/>.</exception>
    public static bool TryToDefinition(WorldProjectionDocument projection, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldDefinition? definition, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: projection);

        definition = null;

        // Nullable annotations do not constrain collection elements on the JSON boundary.
        if ((projection.Kits is null) || projection.Kits.Any(predicate: static kit => (kit is null)) ||
            (projection.Observations?.Any(predicate: static row => ((row is null) || (row.Cells is null) || row.Cells.Any(predicate: static cell => (cell is null)))) == true)) {
            reason = "projection kits, observations and observed cells must be non-null rows.";

            return false;
        }

        var state = WorldFieldsSection.ToStateSection(composite: projection.Fields);

        if (projection.Observations is { Count: > 0 } observations) {
            var rows = new List<WorldStateRow>(collection: (state.World ?? []));

            foreach (var observed in observations) {
                if (!TryObservedRow(
                    observed: observed,
                    reason: out reason,
                    row: out var row
                )) {
                    return false;
                }

                rows.Add(item: row);
            }

            state = (state with {
                Spaces = projection.Spaces,
                World = rows,
            });
        }

        var kits = new WorldKit[projection.Kits.Count];

        for (var index = 0; (index < kits.Length); index++) {
            var kit = projection.Kits[index];

            // An empty producer/action table is the honest hydration: this client never runs either, and the
            // authority that does never reads this document.
            kits[index] = new WorldKit(
                Name: kit.Name,
                BodyMotionProgram: kit.BodyMotionProgram,
                Motion: kit.Motion,
                Collider: kit.Collider,
                BodyContact: kit.BodyContact
            );
        }

        var hydrated = new WorldDefinition(
            MotionRaw: projection.Motion,
            SpawnPointsRaw: projection.SpawnPoints,
            RenderRaw: projection.Render,
            ScreensRaw: projection.Screens,
            CamerasRaw: projection.Cameras,
            PopulationRaw: projection.Population,
            PlayerDefaultsRaw: projection.PlayerDefaults,
            ChannelsRaw: projection.Channels,
            TargetRegistersRaw: [],
            BodyMotionProgramsRaw: [],
            KitsRaw: new WorldKitsSection(
                Assignment: projection.Assignment,
                Rows: kits
            ),
            DefaultSeatKitRaw: projection.DefaultSeatKit,
            AddonsRaw: [],
            BindingOverlaysRaw: projection.BindingOverlays,
            StorageRaw: new WorldStorageDefaults(),
            CreationsRaw: projection.Creations,
            PlacementsRaw: new WorldPlacementsSection(Rows: projection.Placements),
            SpeakersRaw: projection.Speakers,
            TunesRaw: projection.Tunes,
            PatchesRaw: projection.Patches,
            AudioRaw: projection.Audio,
            CollisionRaw: projection.Collision,
            HostRaw: null,
            ViewsRaw: projection.Views,
            LooksRaw: new WorldLooksSection(
                Assignment: projection.LookAssignment,
                Rows: projection.Looks
            ),
            DynamicsRaw: projection.Dynamics,
            GrantsRaw: [],
            HudRaw: projection.Hud,
            StateRaw: state,
            InputHoldRaw: new WorldInputHoldAuthoring(
                CeilingSeconds: 0f,
                DefaultSeconds: 0f,
                EqualizeByDefault: false,
                LowerAfterSeconds: 0f,
                Participants: []
            ),
            Simulation: projection.Simulation,
            Interactions: projection.Interactions,
            References: projection.References,
            Destinations: projection.Destinations,
            Adjacencies: projection.Adjacencies,
            Metadata: ((projection.Metadata is { } metadata)
            ? new WorldMetadataSection(
                    Title: metadata.Title,
                    Description: metadata.Description
                )
            : null),
            TimelineRaw: projection.Timeline
        ) {
            DocumentId = projection.Provenance.DocumentId,
        };

        if (Uncarried(definition: hydrated) is { } uncarried) {
            reason = uncarried;

            return false;
        }

        if (!WorldStateDocumentValues.TryResolve(
            definition: hydrated,
            reason: out reason
        )) {
            return false;
        }

        definition = hydrated;

        return true;
    }

    // An observed row crosses as a plain state row of the literals the recipient was disclosed: no visibility, no
    // placeholder cell, since a placeholder names no key and a withheld cell is simply absent, and no trait but the
    // follower an eased cell carries, so the recipient eases it as the authority presents it. A slot's follower is its
    // row's, since a slot cell carries no trait of its own.
    private static bool TryObservedRow(WorldObservedRow observed, out WorldStateRow row, out string reason) {
        row = null!;

        if (!CellName.TryParse(
            candidate: observed.Name,
            name: out var name,
            reason: out var nameReason
        )) {
            reason = $"projection observation row '{observed.Name}' {nameReason}";

            return false;
        }

        if (!Enum.IsDefined(value: observed.Kind)) {
            reason = $"projection observation row '{observed.Name}' carries kind '{observed.Kind}', which is no cell kind";

            return false;
        }

        if ((observed.Min is { } min) && (observed.Max is { } max) && (min > max)) {
            reason = $"projection observation row '{observed.Name}' carries an envelope whose min {min} exceeds its max {max}";

            return false;
        }

        var cells = new List<StateCell>(capacity: observed.Cells.Count);
        StateDynamics? slotDynamics = null;

        foreach (var cell in observed.Cells) {
            if (cell.Hidden) {
                continue;
            }

            if ((cell.Dynamics is not null) && (observed.Kind is not (CellKind.Fixed or CellKind.Int))) {
                reason = $"projection observation row '{observed.Name}' cell '{cell.Key}' eases, which only a Fixed or Int row does";

                return false;
            }

            if ((cell.Clock is not null) && (cell.Dynamics is null)) {
                reason = $"projection observation row '{observed.Name}' cell '{cell.Key}' carries a follower's clock without the dynamics it eases by";

                return false;
            }

            if (!CellName.TryParse(
                candidate: cell.Key,
                name: out var key,
                reason: out var keyReason
            )) {
                reason = $"projection observation row '{observed.Name}' cell '{cell.Key}' {keyReason}";

                return false;
            }

            if (
                (observed.Kind == CellKind.Vector) &&
                (cell.Vector is null)
            ) {
                reason = $"projection observation row '{observed.Name}' cell '{cell.Key}' is a vector cell carrying no vector";

                return false;
            }

            var slot = (key == WorldStateRow.SlotKey);

            if (slot) {
                slotDynamics = cell.Dynamics;
            }

            cells.Add(item: new StateCell(
                Clock: cell.Clock,
                Dynamics: (slot
                    ? null
                    : cell.Dynamics),
                Key: key,
                Observation: cell.Observation,
                Value: (observed.Kind switch {
                    CellKind.Text => CellValue.Text(value: cell.Text),
                    CellKind.Vector => CellValue.Vector(components: cell.Vector!.Memory),
                    _ => CellValue.FromNumber(
                        kind: observed.Kind,
                        raw: cell.Value
                    ),
                })
            ));
        }

        // A slot's follower is the row's default, which every other cell that eases by none opts out of.
        if (slotDynamics is not null) {
            for (var index = 0; (index < cells.Count); index++) {
                if ((cells[index].Key != WorldStateRow.SlotKey) && (cells[index].Dynamics is null)) {
                    cells[index] = (cells[index] with { Behavior = StateCellBehavior.None });
                }
            }
        }

        row = new WorldStateRow(
            Cells: cells,
            Dynamics: slotDynamics,
            Kind: observed.Kind,
            Max: observed.Max,
            Min: observed.Min,
            Name: name,
            Space: observed.Space
        );
        reason = string.Empty;

        return true;
    }
}
