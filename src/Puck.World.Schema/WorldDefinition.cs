using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Text;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The definition of this world — the aggregate describing what the world is, distinct from the live session state that
/// plays in it. It gathers named spawn points (<see cref="SpawnPoints"/>), motion defaults (<see cref="Motion"/>), and
/// render-lever defaults and quality presets (<see cref="Render"/>). Every consumer takes it by construction.
/// </summary>
/// <remarks>These serialization-friendly records are populated from world documents. A document declares only what it
/// wants to state — every section below is optional. Each section's primary-constructor parameter carries the "Raw"
/// suffix and the section's true JSON key (<c>[property: JsonPropertyName]</c>); the resolved, non-nullable property of
/// the section's plain name (declared in the body below) is what every consumer reads, and states the section's ABSENT
/// semantics on its own summary. <c>with</c>-expressions that replace a live section (the mutation-compose pipeline)
/// target the "Raw" parameter name.</remarks>
public sealed record WorldDefinition(
    [property: JsonPropertyName("motion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldMotionDefaults? MotionRaw = null,
    [property: JsonPropertyName("spawnPoints"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldSpawnPoint>? SpawnPointsRaw = null,
    [property: JsonPropertyName("render"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderDefaults? RenderRaw = null,
    [property: JsonPropertyName("screens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldScreen>? ScreensRaw = null,
    [property: JsonPropertyName("cameras"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldCamera>? CamerasRaw = null,
    [property: JsonPropertyName("population"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPopulationDefaults? PopulationRaw = null,
    [property: JsonPropertyName("playerDefaults"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlayerDefaults? PlayerDefaultsRaw = null,
    [property: JsonPropertyName("channels"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldChannel>? ChannelsRaw = null,
    [property: JsonPropertyName("targetRegisters"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldTargetRegister>? TargetRegistersRaw = null,
    [property: JsonPropertyName("bodyMotionPrograms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<BodyMotionProgram>? BodyMotionProgramsRaw = null,
    [property: JsonPropertyName("kits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldKit>? KitsRaw = null,
    [property: JsonPropertyName("defaultSeatKit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefaultSeatKitRaw = null,
    [property: JsonPropertyName("assignment"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRowAssignment? AssignmentRaw = null,
    [property: JsonPropertyName("addons"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldAddonRow>? AddonsRaw = null,
    [property: JsonPropertyName("bindingOverlays"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldBindingOverlay>? BindingOverlaysRaw = null,
    [property: JsonPropertyName("storage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldStorageDefaults? StorageRaw = null,
    [property: JsonPropertyName("creations"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldCreation>? CreationsRaw = null,
    [property: JsonPropertyName("placements"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldPlacement>? PlacementsRaw = null,
    [property: JsonPropertyName("authoring"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldAuthoringDefaults? AuthoringRaw = null,
    [property: JsonPropertyName("speakers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldSpeaker>? SpeakersRaw = null,
    [property: JsonPropertyName("tunes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldTune>? TunesRaw = null,
    [property: JsonPropertyName("patches"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldPatch>? PatchesRaw = null,
    [property: JsonPropertyName("audio"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldAudioDefaults? AudioRaw = null,
    [property: JsonPropertyName("collision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCollision? CollisionRaw = null,
    [property: JsonPropertyName("gravity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldGravity? GravityRaw = null,
    [property: JsonPropertyName("host"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldHostDefaults? HostRaw = null,
    [property: JsonPropertyName("views"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldViewDefaults? ViewsRaw = null,
    [property: JsonPropertyName("looks"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldLook>? LooksRaw = null,
    [property: JsonPropertyName("lookAssignment"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRowAssignment? LookAssignmentRaw = null,
    [property: JsonPropertyName("links"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldScreenLink>? LinksRaw = null,
    [property: JsonPropertyName("grants"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGrant>? GrantsRaw = null,
    [property: JsonPropertyName("hud"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldHudSection? HudRaw = null,
    [property: JsonPropertyName("icons"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldIconographySection? IconsRaw = null,
    [property: JsonPropertyName("state"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldStateSection? StateRaw = null,
    [property: JsonPropertyName("inputHold"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldInputHoldAuthoring? InputHoldRaw = null,
    [property: JsonPropertyName("theme"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldThemeSection? ThemeRaw = null,
    [property: JsonPropertyName("markers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldMarkerRow>? MarkersRaw = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldRule>? Rules = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldIdentityDefinition? Identity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldGroupsSection? Groups = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPropertyRegistrySection? Properties = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldInteractionsSection? Interactions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldGenerationDefaults? Generation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGeneratorRow>? Generators = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldWaterSection? Water = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldReference>? References = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPortalsSection? Portals = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSimulationDefaults? Simulation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldDestination>? Destinations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldAdmissionEntry>? Admission = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldMarketSection? Market = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldAdjacency>? Adjacencies = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TextFontCatalogDefinition? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldMetadataSection? Metadata = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldUpdateDefaults? Update = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldMusicRow>? Music = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldJudgeRow>? Judges = null,
    [property: JsonPropertyName("seatModes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldSeatModeFamily>? SeatModesRaw = null
) {
    /// <summary>The document schema version. A loader rejects any other value; the canonical writer always emits it.</summary>
    public const string SchemaVersion = "puck.world.def.v1";

    /// <summary>Gets the data-side addon descriptors — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldAddonRow> Addons => (AddonsRaw ?? []);
    /// <summary>Gets the kit→entity assignment policy — ABSENT resolves to <see cref="WorldRowAssignment.Default"/>.</summary>
    [JsonIgnore]
    public WorldRowAssignment Assignment => (AssignmentRaw ?? WorldRowAssignment.Default);
    /// <summary>Gets the audio host-section defaults — ABSENT resolves to <see cref="WorldAudioDefaults.Absent"/>
    /// (silent); the standard values are authored in <c>standard.world.json</c>.</summary>
    [JsonIgnore]
    public WorldAudioDefaults Audio => (AudioRaw ?? WorldAudioDefaults.Absent);
    /// <summary>Gets the editor/authoring policy row — ABSENT resolves to <see cref="WorldAuthoringDefaults.Absent"/>
    /// (no headroom, no editing); the standard policy is authored in <c>standard.world.json</c>.</summary>
    [JsonIgnore]
    public WorldAuthoringDefaults Authoring => (AuthoringRaw ?? WorldAuthoringDefaults.Absent);
    /// <summary>Gets the basis document this file layers over, as a file path resolved against this document's own
    /// directory — the document-composition member (see <c>WorldDocumentBasis</c>). A file naming a basis is a
    /// delta: it authors only what differs, inheriting every omitted member from the (recursively composed) basis
    /// chain. Unrelated to the coordinate basis the validator's geometry speaks of.</summary>
    /// <remarks>Resolved and consumed at the file-load boundary (<see cref="WorldDefinitionFileSource"/>), which
    /// strips the member from the composed tree — a live document therefore always carries <see langword="null"/>
    /// here, the validator refuses anything else, and every serialization of a running document (a save fold, the
    /// replica egress, a replay embed) is self-contained. A wire-arriving document has no directory to resolve
    /// against, so a non-null basis on that path refuses rather than resolving.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Basis { get; init; }
    /// <summary>Gets the per-world binding overlays — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldBindingOverlay> BindingOverlays => (BindingOverlaysRaw ?? []);
    /// <summary>Gets the versioned fixed-phase body motion programs kits select by name — ABSENT resolves to none
    /// (a kit naming one then refuses by name).</summary>
    [JsonIgnore]
    public IReadOnlyList<BodyMotionProgram> BodyMotionPrograms => (BodyMotionProgramsRaw ?? []);
    /// <summary>Gets the body-owned ephemeral state declarations compiled into every body's ordinal register file.</summary>
    [JsonIgnore]
    public IReadOnlyList<ActionStateSlot> BodyState => (StateRaw?.Body ?? []);
    /// <summary>Gets the placeable cameras a <see cref="WorldScreenSource.View"/> screen renders the world from —
    /// ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldCamera> Cameras => (CamerasRaw ?? []);
    /// <summary>Gets the world's channel table — ABSENT resolves to none. The engine declares no channel of its own:
    /// the standard movement set is AUTHORED, in <c>Assets/worlds/standard.world.json</c>, and a world inherits it by
    /// naming that document as its basis. A binding or kit naming a channel the composed document does not declare
    /// refuses by name.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldChannel> Channels => (ChannelsRaw ?? []);
    /// <summary>Gets the contact-solver tuning — ABSENT resolves to <see cref="WorldCollision.Absent"/> (inert); a
    /// document whose census implies a body is refused for authoring no <c>collision</c>, so only a bodyless world
    /// ever reads the placeholder.</summary>
    [JsonIgnore]
    public WorldCollision Collision => (CollisionRaw ?? WorldCollision.Absent);
    /// <summary>Gets the compiled form of <see cref="InputHold"/> — every <c>*Ticks</c> field in simulation ticks, the
    /// unit <c>Server.WorldInputHoldRuntime</c> actually consumes. <see cref="InputHold"/> itself stays the authored
    /// seconds shape (see its remarks); this compiles it once through <see cref="SimulationRateHz"/>, for the identical
    /// reason <see cref="PopulationReconnectGraceTicks"/> does.</summary>
    [JsonIgnore]
    public WorldInputHoldSettings CompiledInputHold => InputHold.Compile(ratePerSecond: ((uint)SimulationRateHz));
    /// <summary>Gets the creation asset rows — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldCreation> Creations => (CreationsRaw ?? []);
    /// <summary>Gets the kit row (by name) every seat body constructs from — ABSENT resolves to the sole declared
    /// kit's name when exactly one kit is declared, else empty (nothing to derive; a document that also declares
    /// local seats then refuses by name for naming no kit row).</summary>
    [JsonIgnore]
    public string DefaultSeatKit => (DefaultSeatKitRaw ?? ((Kits.Count == 1)
        ? Kits[0].Name
        : string.Empty));
    /// <summary>Gets the stable document id used when this world submits to another document.</summary>
    public string? DocumentId { get; init; }
    /// <summary>Gets the unknown top-level members captured during deserialization, declared identically on every versioned
    /// document root here and validated
    /// through the shared <see cref="DocumentExtensionsPolicy"/> regime (see <see cref="WorldDefinitionValidator"/>): a
    /// reserved-prefix key ('$' schema-like keys, '_' comments) round-trips as an intentional escape hatch, but any
    /// other unrecognized key is a hard load failure — not a passive round-trip bag — because an unknown section
    /// surviving silently is how authoring drift starts. Null when the document carries no unknown members. A
    /// settable (not <c>init</c>) accessor is required: System.Text.Json appends to it during deserialization.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
    /// <summary>Gets the document-authored grant rows — ABSENT resolves to none (the permissive boot seed still
    /// applies; this section only ADDS to it).</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldGrant> Grants => (GrantsRaw ?? []);
    /// <summary>Gets the gravitational field — ABSENT resolves to <see cref="WorldGravity.Default"/>.</summary>
    [JsonIgnore]
    public WorldGravity Gravity => (GravityRaw ?? WorldGravity.Default);
    /// <summary>Gets the host-section defaults — ABSENT resolves to <see cref="WorldHostDefaults.Absent"/> (no
    /// presentation); the standard windowed boot is authored in <c>standard.world.json</c>.</summary>
    [JsonIgnore]
    public WorldHostDefaults Host => (HostRaw ?? WorldHostDefaults.Absent);
    /// <summary>Gets the <c>hud</c> section — ABSENT resolves to <see cref="WorldHudSection.Absent"/> (disabled, no
    /// cursor, no panels); the standard enabled row is authored in <c>standard.world.json</c>.</summary>
    [JsonIgnore]
    public WorldHudSection Hud => (HudRaw ?? WorldHudSection.Absent);
    /// <summary>Gets the identity-owned state declarations compiled into every body's ordinal register file and
    /// synchronized through the durable document seam.</summary>
    [JsonIgnore]
    public IReadOnlyList<ActionStateSlot> IdentityState => (StateRaw?.Identity ?? []);
    /// <summary>Gets the <c>icons</c> section — ABSENT resolves to <see cref="WorldIconographySection.Absent"/> (no
    /// icons); the standard repertoire is authored in <c>standard.world.json</c>.</summary>
    [JsonIgnore]
    public WorldIconographySection Icons => (IconsRaw ?? WorldIconographySection.Absent);
    /// <summary>Gets the participant input-hold policy, authored shape — ABSENT resolves to
    /// <see cref="WorldInputHoldAuthoring.Absent"/> (inert).</summary>
    [JsonIgnore]
    public WorldInputHoldAuthoring InputHold => (InputHoldRaw ?? WorldInputHoldAuthoring.Absent);
    /// <summary>Gets the world's locomotion kits — ABSENT resolves to none. A kit row is required only when the
    /// document's population implies a body to move: a zero-capacity census (see <see cref="Population"/>) needs no
    /// kit at all — the derived refusal <see cref="WorldDefinitionValidator"/> applies rather than a flat floor.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldKit> Kits => (KitsRaw ?? []);
    /// <summary>Gets the cable-link rows — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldScreenLink> Links => (LinksRaw ?? []);
    /// <summary>Gets the look→entity assignment policy — ABSENT resolves to <see cref="WorldRowAssignment.Default"/>.</summary>
    [JsonIgnore]
    public WorldRowAssignment LookAssignment => (LookAssignmentRaw ?? WorldRowAssignment.Default);
    /// <summary>Gets the look rows — ABSENT resolves to none, which resolves every entity to the implicit single
    /// catalog look (<see cref="WorldLook.Implicit"/>) — no branch special-cases "the author authored none".</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldLook> Looks => (LooksRaw ?? []);
    /// <summary>Gets the marker rows — ABSENT resolves to none (no marker channel output).</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldMarkerRow> Markers => (MarkersRaw ?? []);
    /// <summary>Gets the profileless locomotion speeds a stand-in with no seated profile advances on — ABSENT
    /// resolves to <see cref="WorldMotionDefaults.Default"/> (inert, near-zero).</summary>
    [JsonIgnore]
    public WorldMotionDefaults Motion => (MotionRaw ?? WorldMotionDefaults.Default);
    /// <summary>Gets the synth-patch asset rows — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldPatch> Patches => (PatchesRaw ?? []);
    /// <summary>Gets the placement instance rows — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldPlacement> Placements => (PlacementsRaw ?? []);
    /// <summary>Gets the authored player-profile seed palette and picker tuning — ABSENT resolves to
    /// <see cref="WorldPlayerDefaults.Default"/>.</summary>
    [JsonIgnore]
    public WorldPlayerDefaults PlayerDefaults => (PlayerDefaultsRaw ?? WorldPlayerDefaults.Default);
    /// <summary>Gets the local/network census — ABSENT resolves to <see cref="WorldPopulationDefaults.Default"/>
    /// (zero local seats, zero capacity — see <see cref="WorldPopulationDefaults"/>'s own ABSENT semantics for every
    /// nested field).</summary>
    [JsonIgnore]
    public WorldPopulationDefaults Population => (PopulationRaw ?? WorldPopulationDefaults.Default);
    /// <summary>Gets the compiled form of <see cref="WorldPopulationDefaults.ReconnectGraceSeconds"/> — a
    /// <see cref="CompiledTickDuration"/>, the unit <c>Server.WorldPopulation</c> actually consumes. Not a raw tick
    /// count: at <see cref="SimulationRateHz"/> 0 a positive authored grace has no tick mapping at all
    /// (<see cref="CompiledTickDuration.Never"/> — a disconnected body parks forever rather than tearing down
    /// immediately), which a raw <see langword="int"/> could not distinguish from an authored-disabled zero grace
    /// (<see cref="CompiledTickDuration.IsZero"/>, the immediate-teardown case, unaffected by the rate). Lives here
    /// rather than on <see cref="WorldPopulationDefaults"/> itself because compiling a duration needs
    /// <see cref="SimulationRateHz"/>, which only the whole document can supply — see
    /// <see cref="SimulationRateHz"/>'s remarks. Read once at construction/rebuild, like the rest of
    /// <see cref="Population"/> — a live edit takes effect on the next disconnect, never retroactively on an
    /// already-parked body.</summary>
    [JsonIgnore]
    public CompiledTickDuration PopulationReconnectGraceTicks => WorldSimulationTickConversion.CompiledDuration(
        seconds: Population.ReconnectGraceSeconds,
        ratePerSecond: ((uint)SimulationRateHz)
    );
    /// <summary>Gets the render-lever boot defaults and quality-preset table — ABSENT resolves to
    /// <see cref="WorldRenderDefaults.Absent"/> (inert levers, no presets); the standard posture is authored in
    /// <c>standard.world.json</c>.</summary>
    [JsonIgnore]
    public WorldRenderDefaults Render => (RenderRaw ?? WorldRenderDefaults.Absent);
    /// <summary>Gets the document schema tag — <see cref="SchemaVersion"/> for a well-formed document.</summary>
    public string Schema { get; init; } = SchemaVersion;
    /// <summary>Gets the authored per-seat mode families (see <see cref="WorldSeatModeFamily"/>) — ABSENT resolves to
    /// none. A world declares none when it wants no <c>player.mode</c>-addressable seat state at all.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldSeatModeFamily> SeatModes => (SeatModesRaw ?? []);
    /// <summary>Gets the diegetic screens standing in the plaza — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldScreen> Screens => (ScreensRaw ?? []);
    /// <summary>Gets the effective simulation rate in Hz — <see cref="Simulation"/>'s authored
    /// <see cref="WorldSimulationDefaults.RateHz"/>, or <c>0</c> (a resident, non-stepping world) when this world
    /// authors no <see cref="Simulation"/> section; the standard 240 Hz is authored in <c>standard.world.json</c>.
    /// The seam every simulation-tick-scoped duration on this
    /// document compiles through (see <see cref="PopulationReconnectGraceTicks"/>, <see cref="CompiledInputHold"/>):
    /// computed here, on the fully-parsed aggregate, rather than threaded as a parameter to each sub-section's own
    /// converter, because a sub-section (e.g. <see cref="WorldPopulationDefaults"/>, a struct) has no reference back to
    /// the document that carries both it and the rate, and the rate itself is just another sibling property in the same
    /// JSON object being parsed — there is no ordering guarantee that would let a nested converter see it first. A
    /// caller that already holds a <see cref="WorldDefinition"/> reads this property directly; nothing threads a raw
    /// rate parameter by hand.</summary>
    [JsonIgnore]
    public int SimulationRateHz => (Simulation?.RateHz ?? 0);
    /// <summary>Gets the named spawn poses seats and population policies reference — ABSENT resolves to empty,
    /// EXCEPT that a spawn-point id of <see cref="WorldSpawnPointDefaults.ImplicitOriginId"/> is always resolvable:
    /// when this document authors no <c>spawnPoints</c> section at all, one implicit point at world-space zero is
    /// added under that id — the point <see cref="WorldPopulationDefaults.SeatSpawns"/>' own absence derivation
    /// addresses. A document that authors its own <c>spawnPoints</c> (even an explicit empty list) gets no implicit
    /// point; a seat spawn naming one it does not declare then refuses by name like any other dangling reference.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldSpawnPoint> SpawnPoints => (SpawnPointsRaw ?? [WorldSpawnPointDefaults.ImplicitOrigin]);
    /// <summary>Gets the placeable speaker rows — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldSpeaker> Speakers => (SpeakersRaw ?? []);
    /// <summary>Gets the <c>state</c> section — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldStateRow> State => (StateRaw?.World ?? []);
    /// <summary>Gets the storage host-section defaults — ABSENT resolves to <see cref="WorldStorageDefaults.None"/>
    /// (cloud unwired, identity declined).</summary>
    [JsonIgnore]
    public WorldStorageDefaults Storage => (StorageRaw ?? WorldStorageDefaults.None);
    /// <summary>Gets the named per-body target registers and their designation envelopes — ABSENT resolves to
    /// none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldTargetRegister> TargetRegisters => (TargetRegistersRaw ?? []);
    /// <summary>Gets the <c>theme</c> section — ABSENT resolves to <see cref="WorldThemeSection.Absent"/> (a zeroed
    /// token block, no chrome); the standard "Instrument + grafts" recipe is authored in
    /// <c>standard.world.json</c>.</summary>
    [JsonIgnore]
    public WorldThemeSection Theme => (ThemeRaw ?? WorldThemeSection.Absent);
    /// <summary>Gets the tune asset rows — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldTune> Tunes => (TunesRaw ?? []);
    /// <summary>Gets the window composition — ABSENT resolves to <see cref="WorldViewDefaults.Absent"/>, which
    /// composes nothing; a document whose census implies a body is refused for authoring no <c>views</c>, so only a
    /// seatless world ever reads the placeholder.</summary>
    [JsonIgnore]
    public WorldViewDefaults Views => (ViewsRaw ?? WorldViewDefaults.Absent);

    /// <summary>Returns a copy with its document-owned world-state rows replaced while preserving the body and
    /// identity declaration lanes.</summary>
    public WorldDefinition WithWorldState(IReadOnlyList<WorldStateRow> rows) => this with {
        StateRaw = ((StateRaw ?? new WorldStateSection()) with { World = rows }),
    };
}
