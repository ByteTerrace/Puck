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
/// One kit as a visitor's client sees it — the embodiment facts (which motion model a body wears, what shape it
/// occupies, whether it depenetrates). The projection document's own row type, not a <see cref="WorldKit"/> with
/// holes: it carries no member for the kit's <c>producers</c>/<c>actions</c>, which are the world's game logic and
/// are read only by the authority that runs them.
/// </summary>
/// <param name="Name">The kit's name — the identity a placement/assignment row addresses.</param>
/// <param name="BodyMotionProgram">The program name the destination advances this kit on. A name only; the
/// <c>bodyMotionPrograms</c> section itself never crosses below the replica tier.</param>
/// <param name="Motion">The kit's motion model — the arm and its tuning, which is what decides how a client
/// interpolates and frames a body wearing it.</param>
/// <param name="Collider">The kit's collider, when it authors one.</param>
/// <param name="BodyContact">Whether two bodies wearing this kit physically depenetrate.</param>
public sealed record WorldProjectedKit(
    string Name,
    string BodyMotionProgram,
    WorldMotionModel Motion,
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
/// <para>Absent by construction: <c>rules</c>, <c>grants</c>, <c>state</c>, <c>market</c>, <c>admission</c>,
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
/// <param name="Placements">The placement rows.</param>
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
/// <param name="Observations">Explicitly disclosed literal state observations, without executable or draw bookkeeping traits.</param>
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
    IReadOnlyList<WorldDynamicsRow> Dynamics,
    WorldHudSection Hud,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldFieldsSection? Fields = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSimulationDefaults? Simulation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldInteractionsSection? Interactions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldReference>? References = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldDestination>? Destinations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldAdjacency>? Adjacencies = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldProjectedMetadata? Metadata = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldObservedRow>? Observations = null
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
/// caller serializes the definition verbatim. For a flat document that download is hash-identical to the authored
/// file; for a document loaded from a <c>basis</c> delta it is the flattened composition — self-contained by
/// construction, since a live document never carries a basis (see <see cref="WorldDefinition.Basis"/>), and a
/// receiver has no directory to resolve one against.</para>
/// </remarks>
public static class WorldProjection {
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
    public static WorldProjectionDocument? Compose(WorldDefinition definition, WorldDisclosureTier tier, string authority, int revision, WorldPrincipal? recipient = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (tier != WorldDisclosureTier.Presentation) {
            return null;
        }

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
            Placements: definition.Placements,
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
            : null)
        );

        WorldStateDisclosure.ValidateBindings(definition, projection, recipient);
        projection = projection with { Observations = WorldStateDisclosure.Compose(definition, recipient) };
        return Flatten(
            definition: definition,
            projection: projection
        );
    }

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

        if (!TryDeserialize(
            utf8Json: Serialize(projection: projection),
            projection: out var copy,
            reason: out var reason
        ) || (copy is null)) {
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
    /// <summary>Rebuilds a locally-valid <see cref="WorldDefinition"/> from a projection — see the class remarks. Every
    /// undisclosed section arrives as its neutral built-in default, never as a fabricated stand-in for what the
    /// composing authority actually authored.</summary>
    /// <remarks>
    /// The hydration runs the same document-value resolution pass a file load runs, so a delivered definition is
    /// indistinguishable from a loaded one. <see cref="Compose"/> flattens what it sends, so a peer that still names a
    /// state cell is naming one this projection carries no section for: that refuses here rather than faulting later
    /// at the first read of the value.
    /// </remarks>
    /// <param name="projection">The projection.</param>
    /// <param name="definition">The hydrated definition on success.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the projection hydrated and every document value resolved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="projection"/> is <see langword="null"/>.</exception>
    public static bool TryToDefinition(WorldProjectionDocument projection, out WorldDefinition? definition, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: projection);

        definition = null;

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
            StateRaw: WorldFieldsSection.ToStateSection(composite: projection.Fields),
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
            : null)
        ) {
            DocumentId = projection.Provenance.DocumentId,
        };

        if (!WorldStateDocumentValues.TryResolve(
            definition: hydrated,
            reason: out reason
        )) {
            return false;
        }

        definition = hydrated;

        return true;
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
                jsonTypeInfo: WorldJsonContext.Default.WorldProjectionDocument
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
}
