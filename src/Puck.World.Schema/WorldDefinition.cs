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
    [property: JsonPropertyName("host"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldHostDefaults? HostRaw = null,
    [property: JsonPropertyName("views"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldViewDefaults? ViewsRaw = null,
    // An empty Looks list resolves every entity to WorldLook.Implicit (the occupant-owned catalog pick at full gait);
    // NO branch special-cases "the author authored none".
    [property: JsonPropertyName("looks"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldLook>? LooksRaw = null,
    [property: JsonPropertyName("lookAssignment"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRowAssignment? LookAssignmentRaw = null,
    [property: JsonPropertyName("links"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldScreenLink>? LinksRaw = null,
    [property: JsonPropertyName("grants"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGrant>? GrantsRaw = null,
    [property: JsonPropertyName("hud"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldHudSection? HudRaw = null,
    [property: JsonPropertyName("state"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldStateSection? StateRaw = null,
    [property: JsonPropertyName("inputHold"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldInputHoldAuthoring? InputHoldRaw = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldRule>? Rules = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldIdentityDefinition? Identity = null,
    // OPTIONAL, exactly like Rules above: a required section would refuse every existing world at boot for
    // declaring nothing. A composer reads `current.Groups ?? WorldGroupsSection.Empty`, the identical
    // `current.Rules ?? []` fallback Rules' own composer arms use.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldGroupsSection? Groups = null,
    // OPTIONAL, exactly like Groups above — same fallback shape (`current.Properties ?? WorldPropertyRegistrySection.Empty`).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPropertyRegistrySection? Properties = null,
    // OPTIONAL, exactly like Properties above (`current.Interactions ?? WorldInteractionsSection.Empty`).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldInteractionsSection? Interactions = null,
    // OPTIONAL, exactly like Interactions above (`current.Generation?.WorldSeed ?? 0UL`).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldGenerationDefaults? Generation = null,
    // OPTIONAL, exactly like Generation above (`current.Generators ?? []`).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldGeneratorRow>? Generators = null,
    // OPTIONAL, exactly like Generators above — a null section IS the dry world, no fallback object needed.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldWaterSection? Water = null,
    // OPTIONAL, exactly like Water above — a null section names nothing, no fallback list needed.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldReference>? References = null,
    // OPTIONAL, exactly like References above — a null section resolves every portal facet's absent travel to
    // WorldPortalTravel.Body, no fallback object needed.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPortalsSection? Portals = null,
    // OPTIONAL, exactly like Portals above — a world authoring none reads WorldSimulationDefaults.DefaultRateHz
    // (240 Hz) through SimulationRateHz below, the fixed rate every world ran at before this section existed, so
    // nothing already checked in needs an edit to keep its exact byte-for-byte boot behavior.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSimulationDefaults? Simulation = null,
    // OPTIONAL, exactly like Simulation above — a null section names no destinations. Trailing by design: added
    // over the shipped section set rather than inserted beside References/Portals, so every existing document's
    // member ORDER (irrelevant to JSON parsing, but relevant to anyone diffing a document by eye) stays untouched.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldDestination>? Destinations = null,
    // OPTIONAL, exactly like Destinations above — a null section names no admission entries, which is DENY BY
    // DEFAULT for the TCP door: no remote peer can ever verify against an absent/empty section, matching an empty
    // Puck.Attestation.TrustList's own posture. Trailing by design, for the identical reason Destinations is.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldAdmissionEntry>? Admission = null,
    // OPTIONAL, exactly like Admission above — a null section IS today's no-market behavior, no fallback object
    // needed beyond `current.Market ?? WorldMarketSection.Empty`. Trailing by design, for the identical reason
    // every optional section above it is.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldMarketSection? Market = null,
    // OPTIONAL topology. An adjacency is an invisible authority boundary, never a portal or screen facet.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldAdjacency>? Adjacencies = null,
    // OPTIONAL presentation asset topology. Null means the world declares no world-space text fonts. Every font is
    // relative to and contained beneath the world document's directory, with its bytes content-pinned.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TextFontCatalogDefinition? Text = null,
    // OPTIONAL, trailing over every section shipped before it, for the identical reason every optional section
    // above it is: a required Metadata would force every checked-in world to author one for no behavioral reason.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldMetadataSection? Metadata = null,
    // OPTIONAL, trailing over every section shipped before it, for the identical reason every optional section
    // above it is: a null section runs the app's own hardcoded self-update defaults (Puck.Launcher.AddSelfUpdate's
    // UpdateOptions), no fallback object needed.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldUpdateDefaults? Update = null,
    // OPTIONAL, trailing over every section shipped before it — a null section names no music score, and the world
    // steps no MusicClock/MusicDirector. Boot-only, like Simulation/References/Portals above: nothing mutates it
    // live yet.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldMusicRow>? Music = null,
    // OPTIONAL, trailing over Music above, for the identical reason — a null section names no judge window sets.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldJudgeRow>? Judges = null
) {
    /// <summary>The document schema version. A loader rejects any other value; the canonical writer always emits it.</summary>
    public const string SchemaVersion = "puck.world.def.v1";

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
    /// <summary>Gets the compiled form of <see cref="InputHold"/> — every <c>*Ticks</c> field in simulation ticks, the
    /// unit <c>Server.WorldInputHoldRuntime</c> actually consumes. <see cref="InputHold"/> itself stays the authored
    /// seconds shape (see its remarks); this compiles it once through <see cref="SimulationRateHz"/>, for the identical
    /// reason <see cref="PopulationReconnectGraceTicks"/> does.</summary>
    [JsonIgnore]
    public WorldInputHoldSettings CompiledInputHold => InputHold.Compile(ratePerSecond: ((uint)SimulationRateHz));
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
    /// <summary>Gets the document schema tag — <see cref="SchemaVersion"/> for a well-formed document.</summary>
    public string Schema { get; init; } = SchemaVersion;
    /// <summary>Gets the effective simulation rate in Hz — <see cref="Simulation"/>'s authored
    /// <see cref="WorldSimulationDefaults.RateHz"/>, or <see cref="WorldSimulationDefaults.DefaultRateHz"/> (240) when
    /// this world authors no <see cref="Simulation"/> section. The seam every simulation-tick-scoped duration on this
    /// document compiles through (see <see cref="PopulationReconnectGraceTicks"/>, <see cref="CompiledInputHold"/>):
    /// computed here, on the fully-parsed aggregate, rather than threaded as a parameter to each sub-section's own
    /// converter, because a sub-section (e.g. <see cref="WorldPopulationDefaults"/>, a struct) has no reference back to
    /// the document that carries both it and the rate, and the rate itself is just another sibling property in the same
    /// JSON object being parsed — there is no ordering guarantee that would let a nested converter see it first. A
    /// caller that already holds a <see cref="WorldDefinition"/> reads this property directly; nothing threads a raw
    /// rate parameter by hand.</summary>
    [JsonIgnore]
    public int SimulationRateHz => (Simulation?.RateHz ?? WorldSimulationDefaults.DefaultRateHz);

    /// <summary>Gets the profileless locomotion speeds a stand-in with no seated profile advances on — ABSENT
    /// resolves to <see cref="WorldMotionDefaults.Default"/> (inert, near-zero).</summary>
    [JsonIgnore]
    public WorldMotionDefaults Motion => (MotionRaw ?? WorldMotionDefaults.Default);
    /// <summary>Gets the named spawn poses seats and population policies reference — ABSENT resolves to empty,
    /// EXCEPT that a spawn-point id of <see cref="WorldSpawnPointDefaults.ImplicitOriginId"/> is always resolvable:
    /// when this document authors no <c>spawnPoints</c> section at all, one implicit point at world-space zero is
    /// added under that id — the point <see cref="WorldPopulationDefaults.SeatSpawns"/>' own absence derivation
    /// addresses. A document that authors its own <c>spawnPoints</c> (even an explicit empty list) gets no implicit
    /// point; a seat spawn naming one it does not declare then refuses by name like any other dangling reference.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldSpawnPoint> SpawnPoints => (SpawnPointsRaw ?? [WorldSpawnPointDefaults.ImplicitOrigin]);
    /// <summary>Gets the render-lever boot defaults and quality-preset table — ABSENT resolves to
    /// <see cref="WorldRenderDefaults.Default"/>.</summary>
    [JsonIgnore]
    public WorldRenderDefaults Render => (RenderRaw ?? WorldRenderDefaults.Default);
    /// <summary>Gets the diegetic screens standing in the plaza — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldScreen> Screens => (ScreensRaw ?? []);
    /// <summary>Gets the placeable cameras a <see cref="WorldScreenSource.View"/> screen renders the world from —
    /// ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldCamera> Cameras => (CamerasRaw ?? []);
    /// <summary>Gets the local/network census — ABSENT resolves to <see cref="WorldPopulationDefaults.Default"/>
    /// (zero local seats, zero capacity — see <see cref="WorldPopulationDefaults"/>'s own ABSENT semantics for every
    /// nested field).</summary>
    [JsonIgnore]
    public WorldPopulationDefaults Population => (PopulationRaw ?? WorldPopulationDefaults.Default);
    /// <summary>Gets the authored player-profile seed palette and picker tuning — ABSENT resolves to
    /// <see cref="WorldPlayerDefaults.Default"/>.</summary>
    [JsonIgnore]
    public WorldPlayerDefaults PlayerDefaults => (PlayerDefaultsRaw ?? WorldPlayerDefaults.Default);
    /// <summary>Gets the world's channel table — ABSENT resolves to the three movement channels the shipped
    /// <c>Assets/worlds/default.world.json</c> binding document names directly (<c>forward</c>/<c>strafe</c>/
    /// <c>turn</c>); every shipped world already declares these identically. A kit naming any other channel still
    /// refuses by name, exactly as an unknown channel does today.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldChannel> Channels => (ChannelsRaw ?? WorldChannelDefaults.Standard);
    /// <summary>Gets the named per-body target registers and their designation envelopes — ABSENT resolves to
    /// none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldTargetRegister> TargetRegisters => (TargetRegistersRaw ?? []);
    /// <summary>Gets the versioned fixed-phase body motion programs kits select by name — ABSENT resolves to none
    /// (a kit naming one then refuses by name).</summary>
    [JsonIgnore]
    public IReadOnlyList<BodyMotionProgram> BodyMotionPrograms => (BodyMotionProgramsRaw ?? []);
    /// <summary>Gets the world's locomotion kits — ABSENT resolves to none. A kit row is required only when the
    /// document's population implies a body to move: a zero-capacity census (see <see cref="Population"/>) needs no
    /// kit at all — the derived refusal <see cref="WorldDefinitionValidator"/> applies rather than a flat floor.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldKit> Kits => (KitsRaw ?? []);
    /// <summary>Gets the kit row (by name) every seat body constructs from — ABSENT resolves to the sole declared
    /// kit's name when exactly one kit is declared, else empty (nothing to derive; a document that also declares
    /// local seats then refuses by name for naming no kit row).</summary>
    [JsonIgnore]
    public string DefaultSeatKit => (DefaultSeatKitRaw ?? ((Kits.Count == 1) ? Kits[0].Name : string.Empty));
    /// <summary>Gets the kit→entity assignment policy — ABSENT resolves to <see cref="WorldRowAssignment.Default"/>.</summary>
    [JsonIgnore]
    public WorldRowAssignment Assignment => (AssignmentRaw ?? WorldRowAssignment.Default);
    /// <summary>Gets the data-side addon descriptors — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldAddonRow> Addons => (AddonsRaw ?? []);
    /// <summary>Gets the per-world binding overlays — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldBindingOverlay> BindingOverlays => (BindingOverlaysRaw ?? []);
    /// <summary>Gets the storage host-section defaults — ABSENT resolves to <see cref="WorldStorageDefaults.None"/>
    /// (cloud unwired, identity declined).</summary>
    [JsonIgnore]
    public WorldStorageDefaults Storage => (StorageRaw ?? WorldStorageDefaults.None);
    /// <summary>Gets the creation asset rows — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldCreation> Creations => (CreationsRaw ?? []);
    /// <summary>Gets the placement instance rows — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldPlacement> Placements => (PlacementsRaw ?? []);
    /// <summary>Gets the editor/authoring policy row — ABSENT resolves to <see cref="WorldAuthoringDefaults.Default"/>.</summary>
    [JsonIgnore]
    public WorldAuthoringDefaults Authoring => (AuthoringRaw ?? WorldAuthoringDefaults.Default);
    /// <summary>Gets the placeable speaker rows — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldSpeaker> Speakers => (SpeakersRaw ?? []);
    /// <summary>Gets the tune asset rows — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldTune> Tunes => (TunesRaw ?? []);
    /// <summary>Gets the synth-patch asset rows — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldPatch> Patches => (PatchesRaw ?? []);
    /// <summary>Gets the audio host-section defaults — ABSENT resolves to <see cref="WorldAudioDefaults.Default"/>.</summary>
    [JsonIgnore]
    public WorldAudioDefaults Audio => (AudioRaw ?? WorldAudioDefaults.Default);
    /// <summary>Gets the contact-solver tuning — ABSENT resolves to <see cref="WorldCollision.Default"/>.</summary>
    [JsonIgnore]
    public WorldCollision Collision => (CollisionRaw ?? WorldCollision.Default);
    /// <summary>Gets the host-section defaults — ABSENT resolves to <see cref="WorldHostDefaults.Default"/>.</summary>
    [JsonIgnore]
    public WorldHostDefaults Host => (HostRaw ?? WorldHostDefaults.Default);
    /// <summary>Gets the window-composition defaults — ABSENT resolves to <see cref="WorldViewDefaults.Default"/>.</summary>
    [JsonIgnore]
    public WorldViewDefaults Views => (ViewsRaw ?? WorldViewDefaults.Default);
    /// <summary>Gets the look rows — ABSENT resolves to none, which resolves every entity to the implicit single
    /// catalog look (<see cref="WorldLook.Implicit"/>) — no branch special-cases "the author authored none".</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldLook> Looks => (LooksRaw ?? []);
    /// <summary>Gets the look→entity assignment policy — ABSENT resolves to <see cref="WorldRowAssignment.Default"/>.</summary>
    [JsonIgnore]
    public WorldRowAssignment LookAssignment => (LookAssignmentRaw ?? WorldRowAssignment.Default);
    /// <summary>Gets the cable-link rows — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldScreenLink> Links => (LinksRaw ?? []);
    /// <summary>Gets the document-authored grant rows — ABSENT resolves to none (the permissive boot seed still
    /// applies; this section only ADDS to it).</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldGrant> Grants => (GrantsRaw ?? []);
    /// <summary>Gets the <c>hud</c> section — ABSENT resolves to <see cref="WorldHudSection.Default"/> (enabled, no
    /// authored panels).</summary>
    [JsonIgnore]
    public WorldHudSection Hud => (HudRaw ?? WorldHudSection.Default);
    /// <summary>Gets the <c>state</c> section — ABSENT resolves to none.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldStateRow> State => (StateRaw?.World ?? []);
    /// <summary>Gets the body-owned ephemeral state declarations compiled into every body's ordinal register file.</summary>
    [JsonIgnore]
    public IReadOnlyList<ActionStateSlot> BodyState => (StateRaw?.Body ?? []);
    /// <summary>Gets the identity-owned state declarations compiled into every body's ordinal register file and
    /// synchronized through the durable document seam.</summary>
    [JsonIgnore]
    public IReadOnlyList<ActionStateSlot> IdentityState => (StateRaw?.Identity ?? []);

    /// <summary>Returns a copy with its document-owned world-state rows replaced while preserving the body and
    /// identity declaration lanes.</summary>
    public WorldDefinition WithWorldState(IReadOnlyList<WorldStateRow> rows) => this with {
        StateRaw = ((StateRaw ?? new WorldStateSection()) with { World = rows }),
    };
    /// <summary>Gets the participant input-hold policy, authored shape — ABSENT resolves to
    /// <see cref="WorldInputHoldAuthoring.Default"/> (inert).</summary>
    [JsonIgnore]
    public WorldInputHoldAuthoring InputHold => (InputHoldRaw ?? WorldInputHoldAuthoring.Default);
}
