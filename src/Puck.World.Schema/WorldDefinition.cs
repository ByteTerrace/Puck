using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
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
/// target the "Raw" parameter name.
/// <para>The named static instance a resolved section/table type carries states which shape it is: <c>Absent</c> is a
/// section's own "the author declared nothing" resolution, semantically inert (off, zeroed, no presentation).
/// <c>Empty</c> is the identity element on a compiled table, mask, or collection type (zero rows, zero bits) — the
/// composition seed a builder starts folding into, not a section's resolved-absence value. <c>Default</c> is a
/// chosen, non-inert baseline for a section that has no "off" state of its own (a tuning row, a distribution). A
/// section whose own closed vocabulary already reserves a named "none" member (<see cref="WorldStorageDefaults.None"/>)
/// uses that member directly rather than one of the three generic names.</para></remarks>
public sealed record WorldDefinition(
    [property: JsonPropertyName("motion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldMotionDefaults? MotionRaw = null,
    [property: JsonPropertyName("spawnPoints"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldSpawnPoint>? SpawnPointsRaw = null,
    [property: JsonPropertyName("render"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderDefaults? RenderRaw = null,
    [property: JsonPropertyName("screens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldScreen>? ScreensRaw = null,
    [property: JsonPropertyName("cameras"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldCamera>? CamerasRaw = null,
    [property: JsonPropertyName("bodies"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldBodiesDefaults? PopulationRaw = null,
    [property: JsonPropertyName("seatDefaults"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlayerDefaults? PlayerDefaultsRaw = null,
    [property: JsonPropertyName("channels"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldChannel>? ChannelsRaw = null,
    [property: JsonPropertyName("targetRegisters"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldTargetRegister>? TargetRegistersRaw = null,
    [property: JsonPropertyName("bodyMotionPrograms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<BodyMotionProgram>? BodyMotionProgramsRaw = null,
    [property: JsonPropertyName("kits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldKitsSection? KitsRaw = null,
    [property: JsonPropertyName("defaultSeatKit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefaultSeatKitRaw = null,
    [property: JsonPropertyName("addons"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldAddonRow>? AddonsRaw = null,
    [property: JsonPropertyName("bindingOverlays"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldBindingOverlay>? BindingOverlaysRaw = null,
    [property: JsonPropertyName("storage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldStorageDefaults? StorageRaw = null,
    [property: JsonPropertyName("prototypes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldPrototype>? CreationsRaw = null,
    [property: JsonPropertyName("placements"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementsSection? PlacementsRaw = null,
    [property: JsonPropertyName("speakers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldSpeaker>? SpeakersRaw = null,
    [property: JsonPropertyName("tunes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldTune>? TunesRaw = null,
    [property: JsonPropertyName("patches"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldPatch>? PatchesRaw = null,
    [property: JsonPropertyName("audio"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldAudioDefaults? AudioRaw = null,
    [property: JsonPropertyName("collision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCollision? CollisionRaw = null,
    [property: JsonPropertyName("gravity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldGravity? GravityRaw = null,
    [property: JsonPropertyName("host"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldHostDefaults? HostRaw = null,
    [property: JsonPropertyName("views"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldViewDefaults? ViewsRaw = null,
    [property: JsonPropertyName("looks"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldLooksSection? LooksRaw = null,
    [property: JsonPropertyName("dynamics"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldDynamicsRow>? DynamicsRaw = null,
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldReference>? References = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPortalsSection? Portals = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSimulationDefaults? Simulation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldDestination>? Destinations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldAdmissionEntry>? Admission = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldAdjacency>? Adjacencies = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TextFontCatalogDefinition? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldMetadataSection? Metadata = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldUpdateDefaults? Update = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldMusicRow>? Music = null,
    [property: JsonPropertyName("seatModes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldSeatModeFamily>? SeatModesRaw = null,
    [property: JsonPropertyName("probes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldProbe>? ProbesRaw = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCapturesSection? Captures = null,
    [property: JsonPropertyName("curves"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldCurveRow>? CurvesRaw = null,
    [property: JsonPropertyName("navigation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldNavigationSection? NavigationRaw = null,
    [property: JsonPropertyName("patterns"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldPatternRow>? PatternsRaw = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldTableRow>? Tables = null
) {
    /// <summary>The document schema version. A loader rejects any other value; the canonical writer always emits it.</summary>
    public const string SchemaVersion = "puck.world.def.v1";

    /// <summary>Gets the data-side addon descriptors — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldAddonRow> Addons => (AddonsRaw ?? []);
    /// <summary>Gets the runtime lattice composite compiled from the state section's topology and lattice-shaped
    /// rows, or <see langword="null"/> when the state section declares no lattice. The state section is the single
    /// authored source; this accessor is the engine's compiled view of it.</summary>
    [JsonIgnore]
    public WorldFieldsSection? Fields => GetCompiledFields();
    /// <summary>Gets the typed descriptor catalog compiled from the authored <c>state</c> section. Runtime processors
    /// resolve names against this catalog once, retain <see cref="WorldStateHandle"/> values, and then use ordinal
    /// descriptor access without repeated string lookup.</summary>
    [JsonIgnore]
    public WorldStateCatalog StateCatalog => GetStateCatalog();
    /// <summary>Gets the typed deterministic program compiled from the lattice-shaped state rows and their ordered
    /// reactions, or <see langword="null"/> when the state section declares no lattice topology.</summary>
    [JsonIgnore]
    public WorldFieldProgram? FieldProgram => GetFieldProgram();
    /// <summary>Bridge spellings for compose sites: each writes ONE member of its dealt section, preserving the
    /// other. The document spelling is the section object; these never serialize.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldKit>? KitRowsRaw { get => KitsRaw?.Rows; init => KitsRaw = ((KitsRaw ?? new WorldKitsSection()) with { Rows = value }); }
    /// <summary>Gets or initializes the kit assignment through the kits section (see <see cref="KitRowsRaw"/>).</summary>
    [JsonIgnore]
    public WorldRowAssignment? AssignmentRaw { get => KitsRaw?.Assignment; init => KitsRaw = ((KitsRaw ?? new WorldKitsSection()) with { Assignment = value }); }
    /// <summary>Gets or initializes the look rows through the looks section (see <see cref="KitRowsRaw"/>).</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldLook>? LookRowsRaw { get => LooksRaw?.Rows; init => LooksRaw = ((LooksRaw ?? new WorldLooksSection()) with { Rows = value }); }
    /// <summary>Gets or initializes the look assignment through the looks section (see <see cref="KitRowsRaw"/>).</summary>
    [JsonIgnore]
    public WorldRowAssignment? LookAssignmentRaw { get => LooksRaw?.Assignment; init => LooksRaw = ((LooksRaw ?? new WorldLooksSection()) with { Assignment = value }); }
    /// <summary>Gets or initializes the placement rows through the placements section (see <see cref="KitRowsRaw"/>).</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldPlacement>? PlacementRowsRaw { get => PlacementsRaw?.Rows; init => PlacementsRaw = ((PlacementsRaw ?? new WorldPlacementsSection()) with { Rows = value }); }
    /// <summary>Gets every placement's resolved WORLD-space transform, keyed by <see cref="WorldPlacement.Id"/> — a
    /// row naming no <see cref="WorldPlacement.Parent"/> resolves to its own authored Position/YawDegrees unchanged;
    /// a row naming one composes over that parent's own resolved frame (see <see cref="WorldPlacementFrameCompilation"/>).
    /// Compiled once per distinct <see cref="PlacementsRaw"/> instance and cached; every consumer of a placement's
    /// WORLD transform reads THIS, never the row's own Position/YawDegrees directly.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, CompiledPlacementFrame> PlacementFrames => WorldPlacementFrameCompilation.Resolve(section: PlacementsRaw);
    /// <summary>Gets or initializes the placement policy through the placements section (see <see cref="KitRowsRaw"/>).</summary>
    [JsonIgnore]
    public WorldPlacementPolicyDefaults? AuthoringRaw { get => PlacementsRaw?.Policy; init => PlacementsRaw = ((PlacementsRaw ?? new WorldPlacementsSection()) with { Policy = value }); }
    /// <summary>Gets the kit→entity assignment policy — ABSENT resolves to <see cref="WorldRowAssignment.Default"/>.</summary>
    [JsonIgnore]
    public WorldRowAssignment Assignment => (KitsRaw?.Assignment ?? WorldRowAssignment.Default);
    /// <summary>Gets the audio host-section defaults — ABSENT resolves to <see cref="WorldAudioDefaults.Absent"/>
    /// (silent); the standard values are authored in <c>standard.world.json</c>.</summary>
    [JsonIgnore]
    public WorldAudioDefaults Audio => (AudioRaw ?? WorldAudioDefaults.Absent);
    /// <summary>Gets the editor/authoring policy row — ABSENT derives from the placement rows
    /// (<see cref="WorldPlacementPolicyDefaults.DeriveFrom"/>: no live placement authoring, the scale envelope the
    /// authored rows span); a world wanting live placement authoring declares the block deliberately.</summary>
    [JsonIgnore]
    public WorldPlacementPolicyDefaults Authoring => (PlacementsRaw?.Policy ?? WorldPlacementPolicyDefaults.DeriveFrom(placements: Placements));
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
    /// <summary>Gets the ordered fragment documents this file imports, each a file path resolved against this
    /// document's own directory — the fan-in half of composition beside <see cref="Basis"/>'s single-parent chain
    /// (see <c>WorldDocumentBasis</c>). Composition order is the basis chain, then each import in list order, then
    /// this file's own body.</summary>
    /// <remarks>Resolved and consumed at the file-load boundary exactly like <see cref="Basis"/>: a live document
    /// always carries <see langword="null"/> here, the validator refuses anything else, and a wire-arriving document
    /// (no directory to resolve imports against) refuses a non-null value the same way <see cref="Basis"/> does.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Imports { get; init; }
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
    public IReadOnlyList<WorldPrototype> Creations => (CreationsRaw ?? []);
    /// <summary>Gets the kit row (by name) every seat body constructs from — ABSENT resolves to the sole declared
    /// kit's name when exactly one kit is declared, else empty (nothing to derive; a document that also declares
    /// local seats then refuses by name for naming no kit row).</summary>
    [JsonIgnore]
    public string DefaultSeatKit => (DefaultSeatKitRaw ?? ((Kits.Count == 1)
        ? Kits[0].Name
        : string.Empty));
    /// <summary>Gets the stable document id used when this world submits to another document.</summary>
    public string? DocumentId { get; init; }
    /// <summary>Gets the named second-order "personality" rows every follower consumer (looks, camera booms, kit
    /// planar shaping, state cells) names by <see cref="WorldDynamicsRow.Name"/> — ABSENT resolves to none, so an
    /// unauthored world is unchanged.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldDynamicsRow> Dynamics => (DynamicsRaw ?? []);
    /// <summary>Gets the named curvature-first spline rows a camera path op or a sim curve-follow target names by
    /// <see cref="WorldCurveRow.Name"/> — ABSENT resolves to none, so an unauthored world is unchanged.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldCurveRow> Curves => (CurvesRaw ?? []);
    /// <summary>The pattern-language table, or empty when the document declares none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldPatternRow> Patterns => (PatternsRaw ?? []);
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
    public IReadOnlyList<WorldKit> Kits => (KitsRaw?.Rows ?? []);
    /// <summary>Gets the look→entity assignment policy — ABSENT resolves to <see cref="WorldRowAssignment.Default"/>.</summary>
    [JsonIgnore]
    public WorldRowAssignment LookAssignment => (LooksRaw?.Assignment ?? WorldRowAssignment.Default);
    /// <summary>Gets the look rows — ABSENT resolves to none. A consumer resolving an entity's look row (or the
    /// whole table) reads the empty case through <see cref="WorldDefinitionRows.ResolveLook"/>/
    /// <see cref="WorldDefinitionRows.ResolveLookRows"/>, the one place that falls back to the implicit single
    /// catalog look (<see cref="WorldLook.Implicit"/>).</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldLook> Looks => (LooksRaw?.Rows ?? []);
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
    public IReadOnlyList<WorldPlacement> Placements => (PlacementsRaw?.Rows ?? []);
    /// <summary>Gets the authored player-profile seed palette and picker tuning — ABSENT resolves to
    /// <see cref="WorldPlayerDefaults.Default"/>.</summary>
    [JsonIgnore]
    public WorldPlayerDefaults PlayerDefaults => (PlayerDefaultsRaw ?? WorldPlayerDefaults.Default);
    /// <summary>Gets the local/network census — ABSENT resolves to <see cref="WorldBodiesDefaults.Default"/>
    /// (zero local seats, zero capacity — see <see cref="WorldBodiesDefaults"/>'s own ABSENT semantics for every
    /// nested field).</summary>
    [JsonIgnore]
    public WorldBodiesDefaults Population => (PopulationRaw ?? WorldBodiesDefaults.Default);
    /// <summary>Gets the compiled form of <see cref="WorldBodiesDefaults.ReconnectGraceSeconds"/> — a
    /// <see cref="CompiledTickDuration"/>, the unit <c>Server.WorldPopulation</c> actually consumes. Not a raw tick
    /// count: at <see cref="SimulationRateHz"/> 0 a positive authored grace has no tick mapping at all
    /// (<see cref="CompiledTickDuration.Never"/> — a disconnected body parks forever rather than tearing down
    /// immediately), which a raw <see langword="int"/> could not distinguish from an authored-disabled zero grace
    /// (<see cref="CompiledTickDuration.IsZero"/>, the immediate-teardown case, unaffected by the rate). Lives here
    /// rather than on <see cref="WorldBodiesDefaults"/> itself because compiling a duration needs
    /// <see cref="SimulationRateHz"/>, which only the whole document can supply — see
    /// <see cref="SimulationRateHz"/>'s remarks. Read once at construction/rebuild, like the rest of
    /// <see cref="Population"/> — a live edit takes effect on the next disconnect, never retroactively on an
    /// already-parked body.</summary>
    [JsonIgnore]
    public CompiledTickDuration PopulationReconnectGraceTicks => WorldSimulationTickConversion.CompiledDuration(
        seconds: Population.ReconnectGraceSeconds,
        ratePerSecond: ((uint)SimulationRateHz)
    );

    /// <summary>Returns the compiled form of one adjacency row's
    /// <see cref="WorldAdjacency.LivenessGraceSeconds"/> — a <see cref="CompiledTickDuration"/> in simulation ticks,
    /// the unit <c>Server.WorldEventFeed</c>'s link-liveness pass consumes. Lives here rather than on
    /// <see cref="WorldAdjacency"/> itself because compiling a duration needs <see cref="SimulationRateHz"/>, which
    /// only the whole document supplies — the same reason <see cref="PopulationReconnectGraceTicks"/> does.</summary>
    /// <param name="adjacency">The adjacency row.</param>
    /// <returns><see cref="CompiledTickDuration.IsZero"/> for an unauthored (disabled) edge,
    /// <see cref="CompiledTickDuration.Never"/> for a positive grace at rate 0, a finite tick count otherwise.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="adjacency"/> is <see langword="null"/>.</exception>
    public CompiledTickDuration AdjacencyLivenessGraceTicks(WorldAdjacency adjacency) {
        ArgumentNullException.ThrowIfNull(argument: adjacency);

        return WorldSimulationTickConversion.CompiledDuration(
            seconds: adjacency.LivenessGraceSeconds,
            ratePerSecond: ((uint)SimulationRateHz)
        );
    }
    /// <summary>Derives the machine cable groups from the declared <see cref="Screens"/> rows' machine sources — the
    /// world DECLARES per-machine cable ports (<see cref="WorldMachineCable"/> on
    /// <see cref="WorldScreenSource.Machine"/>), and the engine derives each cable's ordered screen set here.
    /// Members are ordered by their authored <see cref="WorldMachineCable.Position"/>, groups by cable name
    /// (ordinal), so the derivation is reproducible regardless of screen-row order. Shape rules (two or more ports,
    /// unique contiguous positions, no port outside a declared row's own source) are
    /// <see cref="WorldDefinitionValidator"/>'s; this derivation orders whatever is authored.</summary>
    /// <returns>The derived groups, in cable-name order; empty when no declared machine source names a cable.</returns>
    public IReadOnlyList<WorldMachineCableGroup> MachineCableGroups() {
        SortedDictionary<string, List<(int Position, int Screen)>>? cables = null;

        foreach (var screen in Screens) {
            if (screen?.Source is not WorldScreenSource.Machine { Cable: { } cable }) {
                continue;
            }

            cables ??= new SortedDictionary<string, List<(int, int)>>(comparer: StringComparer.Ordinal);

            if (!cables.TryGetValue(
                key: cable.Name,
                value: out var members
            )) {
                members = [];
                cables[cable.Name] = members;
            }

            members.Add(item: (cable.Position, screen.Index));
        }

        if (cables is null) {
            return [];
        }

        var groups = new List<WorldMachineCableGroup>(capacity: cables.Count);

        foreach (var (name, members) in cables) {
            members.Sort(comparison: static (a, b) => a.Position.CompareTo(value: b.Position));
            groups.Add(item: new WorldMachineCableGroup(
                Name: name,
                Screens: [.. members.Select(selector: static member => member.Screen)]
            ));
        }

        return groups;
    }

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
    /// <summary>Gets the declared probe rows — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldProbe> Probes => (ProbesRaw ?? []);
    /// <summary>Gets the effective simulation rate in Hz — <see cref="Simulation"/>'s authored
    /// <see cref="WorldSimulationDefaults.RateHz"/>, or <c>0</c> (a resident, non-stepping world) when this world
    /// authors no <see cref="Simulation"/> section; the standard 240 Hz is authored in <c>standard.world.json</c>.
    /// The seam every simulation-tick-scoped duration on this
    /// document compiles through (see <see cref="PopulationReconnectGraceTicks"/>, <see cref="CompiledInputHold"/>):
    /// computed here, on the fully-parsed aggregate, rather than threaded as a parameter to each sub-section's own
    /// converter, because a sub-section (e.g. <see cref="WorldBodiesDefaults"/>, a struct) has no reference back to
    /// the document that carries both it and the rate, and the rate itself is just another sibling property in the same
    /// JSON object being parsed — there is no ordering guarantee that would let a nested converter see it first. A
    /// caller that already holds a <see cref="WorldDefinition"/> reads this property directly; nothing threads a raw
    /// rate parameter by hand.</summary>
    [JsonIgnore]
    public int SimulationRateHz => (Simulation?.RateHz ?? 0);
    /// <summary>Gets the named spawn poses seats and population policies reference — ABSENT resolves to empty,
    /// EXCEPT that a spawn-point id of <see cref="WorldSpawnPointDefaults.ImplicitOriginId"/> is always resolvable:
    /// when this document authors no <c>spawnPoints</c> section at all, one implicit point at world-space zero is
    /// added under that id — the point <see cref="WorldBodiesDefaults.SeatSpawns"/>' own absence derivation
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
    /// <summary>Gets the bounded surface, free-volume, and live-medium navigation authoring — ABSENT resolves to no domains.</summary>
    [JsonIgnore]
    public WorldNavigationSection Navigation => (NavigationRaw ?? WorldNavigationSection.Absent);
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
    public WorldDefinition WithWorldState(IReadOnlyList<WorldStateRow> rows) {
        var updated = this with {
            StateRaw = ((StateRaw ?? new WorldStateSection()) with { World = rows }),
        };

        PreserveCompatibleCompilation(target: updated);

        return updated;
    }

    private static readonly RuntimeCompilationCache s_absentStateCompilation = new();
    private static readonly ConditionalWeakTable<WorldStateSection, RuntimeCompilationCache> s_runtimeCompilationCaches = new();

    private WorldFieldsSection? GetCompiledFields() {
        var cache = GetCompilationCache(state: StateRaw);

        lock (cache.SyncRoot) {
            if (
                !cache.FieldsCompiled ||
                !WorldFieldsSection.MatchesState(
                    composite: cache.Fields,
                    state: StateRaw
                )
            ) {
                cache.Fields = WorldFieldsSection.Compile(state: StateRaw);
                cache.FieldsCompiled = true;
            }

            return cache.Fields;
        }
    }
    private WorldStateCatalog GetStateCatalog() {
        var cache = GetCompilationCache(state: StateRaw);

        lock (cache.SyncRoot) {
            if (
                (cache.StateCatalog is null) ||
                !cache.StateCatalog.MatchesShape(section: StateRaw)
            ) {
                cache.StateCatalog = WorldStateCatalog.Compile(section: StateRaw);
            }

            return cache.StateCatalog;
        }
    }
    private WorldFieldProgram? GetFieldProgram() {
        var cache = GetCompilationCache(state: StateRaw);

        lock (cache.SyncRoot) {
            var fields = GetCompiledFields();
            var catalog = GetStateCatalog();

            if (
                !cache.FieldProgramCompiled ||
                ((fields is null) != (cache.FieldProgram is null)) ||
                (
                    (fields is not null) &&
                    (
                        (cache.FieldProgram is null) ||
                        (cache.FieldProgramFields is null) ||
                        !ReferenceEquals(objA: cache.FieldProgram.StateCatalog, objB: catalog) ||
                        !cache.FieldProgramFields.HasSameProgram(other: fields)
                    )
                )
            ) {
                cache.FieldProgram = ((fields is null)
                    ? null
                    : WorldFieldProgram.Compile(document: fields, state: catalog)
                );
                cache.FieldProgramFields = fields;
                cache.FieldProgramCompiled = true;
            }

            return cache.FieldProgram;
        }
    }
    private void PreserveCompatibleCompilation(WorldDefinition target) {
        if (!TryGetCompilationCache(state: StateRaw, cache: out var sourceCache)) {
            return;
        }

        var targetCache = GetCompilationCache(state: target.StateRaw);

        lock (sourceCache.SyncRoot) {
            lock (targetCache.SyncRoot) {
                if (sourceCache.FieldsCompiled) {
                    var candidate = WorldFieldsSection.Compile(state: target.StateRaw);

                    targetCache.Fields = (
                        ((sourceCache.Fields is not null) &&
                        (candidate is not null) &&
                        sourceCache.Fields.HasSameCompilation(other: candidate))
                            ? sourceCache.Fields
                            : candidate
                    );
                    targetCache.FieldsCompiled = true;
                }

                if (sourceCache.StateCatalog is not null) {
                    var candidate = WorldStateCatalog.Compile(section: target.StateRaw);

                    targetCache.StateCatalog = (sourceCache.StateCatalog.HasSameShape(other: candidate)
                        ? sourceCache.StateCatalog
                        : candidate
                    );
                }

                if (sourceCache.FieldProgramCompiled) {
                    var fields = (targetCache.FieldsCompiled
                        ? targetCache.Fields
                        : WorldFieldsSection.Compile(state: target.StateRaw)
                    );
                    var catalog = (targetCache.StateCatalog ?? WorldStateCatalog.Compile(section: target.StateRaw));

                    targetCache.Fields = fields;
                    targetCache.FieldsCompiled = true;
                    targetCache.StateCatalog = catalog;
                    targetCache.FieldProgram = (
                        ((sourceCache.FieldProgram is not null) &&
                        (sourceCache.FieldProgramFields is not null) &&
                        (fields is not null) &&
                        ReferenceEquals(objA: sourceCache.FieldProgram.StateCatalog, objB: catalog) &&
                        sourceCache.FieldProgramFields.HasSameProgram(other: fields))
                            ? sourceCache.FieldProgram
                            : ((fields is null)
                                ? null
                                : WorldFieldProgram.Compile(document: fields, state: catalog)
                    ));
                    targetCache.FieldProgramFields = fields;
                    targetCache.FieldProgramCompiled = true;
                }
            }
        }
    }
    private static RuntimeCompilationCache GetCompilationCache(WorldStateSection? state) => (
        (state is null)
            ? s_absentStateCompilation
            : s_runtimeCompilationCaches.GetOrCreateValue(key: state)
    );
    private static bool TryGetCompilationCache(WorldStateSection? state, out RuntimeCompilationCache cache) {
        if (state is null) {
            cache = s_absentStateCompilation;

            return true;
        }

        return s_runtimeCompilationCaches.TryGetValue(key: state, value: out cache!);
    }

    private sealed class RuntimeCompilationCache {
        public object SyncRoot { get; } = new();
        public bool FieldsCompiled { get; set; }
        public WorldFieldsSection? Fields { get; set; }
        public WorldStateCatalog? StateCatalog { get; set; }
        public bool FieldProgramCompiled { get; set; }
        public WorldFieldProgram? FieldProgram { get; set; }
        public WorldFieldsSection? FieldProgramFields { get; set; }
    }
}
