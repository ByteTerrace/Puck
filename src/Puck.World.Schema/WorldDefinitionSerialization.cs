using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The System.Text.Json source-generation context for the world document (<c>puck.world.def.v1</c>) — the only
/// sanctioned entry point for (de)serializing a <see cref="WorldDefinition"/>. Source-gen (not runtime reflection) keeps
/// the load/save boundary trimming/AOT-clean; every row type in the document graph rejects an unmapped member by
/// default (<c>UnmappedMemberHandling = Disallow</c> below) — an authoring typo or a stale field fails loud, by name
/// and by row type, rather than vanishing silently. The one carve-out is <see cref="WorldDefinition"/>'s own root:
/// its <see cref="WorldDefinition.Extensions"/> property carries <c>[JsonExtensionData]</c>, which STJ always prefers
/// over the ambient Disallow default — an unmapped top-level member still round-trips into that bag and is judged by
/// <see cref="DocumentExtensionsPolicy"/> instead (a reserved '$'/'_' prefix passes; any
/// other key is a validator rejection). Nothing else in the graph carries that attribute, so every nested row is
/// unconditionally strict. Every enum the document graph carries declares its own strict by-name
/// conversion (writes the exact declared member name, refuses a numeric token on read) at the enum's OWN
/// declaration via <c>[JsonConverter(typeof(StrictEnumConverter&lt;TEnum&gt;))]</c> (<see cref="Puck.Physics.Motion.BodyMotionOp"/>,
/// <see cref="IntentSource"/>, <see cref="WorldContactRequirement"/>, <see cref="Puck.Physics.Motion.ActionFact"/>,
/// <see cref="ActionStateComparison"/>, <see cref="ChannelRole"/>, <see cref="ShadowTier"/>,
/// <see cref="WorldRenderScaleTier"/>, <see cref="Puck.Abstractions.Presentation.PresentMode"/>,
/// <see cref="Puck.World.Protocol.WorldCapability"/>, and every <c>Puck.Commands</c> binding enum) — never on this
/// context's converter list. That is the point rather than a tidiness: a converter listed on a context binds THAT
/// context alone, so the same enum reached through a second context (Puck.Commands' own
/// <c>BindingProfileJsonContext</c>) would write a different token. Declaring it on the type decides the wire form
/// once, and every context that reaches the enum inherits the decision.
/// <c>UseStringEnumConverter</c> writes by name too but has no <c>allowIntegerValues</c> knob, so it still accepts a
/// numeric wire value on read. <see cref="Vector2"/>, <see cref="Vector3"/> and <see cref="Quaternion"/> ride
/// <see cref="Puck.Assets.Documents.Vector2JsonConverter"/>/<see cref="Puck.Assets.Documents.Vector3JsonConverter"/>/<see cref="Puck.Assets.Documents.QuaternionJsonConverter"/>
/// as <c>[x, y]</c>/<c>[x, y, z]</c>/<c>[x, y, z, w]</c> arrays — the same converters and spelling
/// <see cref="Puck.Assets.Documents.DocumentJsonOptions.Shared"/> registers for every other document family, so a
/// vector never carries two spellings depending which document it sits in — and
/// <see cref="Puck.World.Protocol.GrantSubject"/>/<see cref="Puck.World.Protocol.WorldPrincipal"/>
/// each ride a token converter (<see cref="GrantSubjectJsonConverter"/>/<see cref="WorldPrincipalJsonConverter"/>) so a
/// document-authored grant reads the same compact <c>world.grant</c> tokens rather than a raw field object.
/// </summary>
[JsonSerializable(typeof(WorldDefinition))]
// puck.world.projection.v1 — the egress document (see WorldProjection). It rides this same context deliberately:
// one strictness policy, one enum regime, one Vector3 spelling for both document families.
[JsonSerializable(typeof(WorldProjectionDocument))]
[JsonSerializable(typeof(WorldProjectedKit))]
// The row shapes the runtime mutation verbs parse as ONE inline-JSON argument — the same wire shape as the document
// section, so an editor/agent speaks one grammar. Every one is reachable from WorldDefinition already; these entries
// only expose the typed WorldJsonContext.Default.<Type> accessors the verbs deserialize through.
[JsonSerializable(typeof(WorldKit))]
[JsonSerializable(typeof(WorldScreen))]
[JsonSerializable(typeof(WorldCamera))]
// An authored camera rig is an ordered op-list program (the bodyMotionPrograms pattern promoted to cameras).
[JsonSerializable(typeof(WorldCameraProgram))]
[JsonSerializable(typeof(WorldCameraProgramOp))]
[JsonSerializable(typeof(WorldCameraSubject))]
// WorldAnchor.Placement and WorldCameraSubject.Placement share a simple name, which the source generator would
// otherwise resolve to one generated accessor for both (SYSLIB1031). Naming this arm explicitly keeps both.
[JsonSerializable(typeof(WorldCameraSubject.Placement), TypeInfoPropertyName = "WorldCameraSubjectPlacement")]
[JsonSerializable(typeof(ValueToken.Select), TypeInfoPropertyName = "ValueTokenSelect")]
// The postfix object spelling ValueExpressionJsonConverter reads and writes; the expression type itself rides
// the converter, so the object arm needs its own entry to be reachable.
[JsonSerializable(typeof(ValueExpressionTokens))]
[JsonSerializable(typeof(PatternNode.Symbol), TypeInfoPropertyName = "WorldPatternNodeSymbol")]
[JsonSerializable(typeof(PatternNode.AnySymbol), TypeInfoPropertyName = "WorldPatternNodeAnySymbol")]
[JsonSerializable(typeof(PatternNode.None), TypeInfoPropertyName = "WorldPatternNodeNone")]
[JsonSerializable(typeof(PatternNode.Except), TypeInfoPropertyName = "WorldPatternNodeExcept")]
[JsonSerializable(typeof(PatternNode.Nothing), TypeInfoPropertyName = "WorldPatternNodeNothing")]
[JsonSerializable(typeof(PatternNode.Sequence), TypeInfoPropertyName = "WorldPatternNodeSequence")]
[JsonSerializable(typeof(PatternNode.Choice), TypeInfoPropertyName = "WorldPatternNodeChoice")]
[JsonSerializable(typeof(PatternNode.Optional), TypeInfoPropertyName = "WorldPatternNodeOptional")]
[JsonSerializable(typeof(PatternNode.Star), TypeInfoPropertyName = "WorldPatternNodeStar")]
[JsonSerializable(typeof(PatternNode.Plus), TypeInfoPropertyName = "WorldPatternNodePlus")]
[JsonSerializable(typeof(PatternNode.Repeat), TypeInfoPropertyName = "WorldPatternNodeRepeat")]
[JsonSerializable(typeof(PatternNode.Both), TypeInfoPropertyName = "WorldPatternNodeBoth")]
[JsonSerializable(typeof(PatternNode.Complement), TypeInfoPropertyName = "WorldPatternNodeComplement")]
// The seat rig's input-policy sibling; this entry exposes the typed accessor world.view.look deserializes through.
[JsonSerializable(typeof(WorldSeatCameraFeel))]
[JsonSerializable(typeof(WorldSeatViewControl))]
[JsonSerializable(typeof(WorldViewDefaults))]
[JsonSerializable(typeof(WorldViewLayout))]
[JsonSerializable(typeof(WorldViewStudy))]
[JsonSerializable(typeof(WorldSpawnPoint[]))]
[JsonSerializable(typeof(WorldMotionDefaults))]
[JsonSerializable(typeof(WorldRenderDefaults))]
[JsonSerializable(typeof(WorldAddonRow))]
// The per-world binding overlay row the world.row.set bindingOverlays verb parses as ONE inline-JSON argument — the same wire
// shape as the document section. Its BindingProfileDocument (from Puck.Commands) is registered explicitly so source-gen
// emits its metadata for both the canonical writer and the verb accessor. That metadata is generated here rather
// than borrowed from Puck.Commands.BindingProfileJsonContext (a source-gen context cannot delegate a type to
// another context), but the two agree by construction: every bespoke spelling in that graph — CommandValue,
// ChannelRef, DocumentIdentifier, and each binding enum — is declared on the TYPE, so neither context is free to
// spell one differently. Puck.World.Schema.Tests pins the agreement on the shipped basis world's own bindings.
[JsonSerializable(typeof(WorldBindingOverlay))]
[JsonSerializable(typeof(BindingProfileDocument))]
// The creation/placement rows (the world.row.set creations / world.row.set placements payload shapes). The embedded
// puck.creation.v1 document rides CreationDocumentJsonConverter — its OWN canonical serializer — never this context's
// member policies (see the converter's remarks).
[JsonSerializable(typeof(WorldPrototype))]
[JsonSerializable(typeof(WorldPlacement))]
[JsonSerializable(typeof(WorldPlacementSpatialVolume))]
[JsonSerializable(typeof(WorldPlacementReflowRequest))]
[JsonSerializable(typeof(WorldSpatialShape))]
// The editor/authoring policy row (the world.row.set authoring payload shape).
[JsonSerializable(typeof(WorldPlacementPolicyDefaults))]
// The contact-solver tuning (the world.row.set collision payload shape) and the velocity-response array (a kit row's
// own, via world.row.set kits). Both are also reachable from WorldDefinition/WorldMotion already; these entries
// expose the typed accessors.
[JsonSerializable(typeof(WorldCollision))]
[JsonSerializable(typeof(WorldCollider))]
[JsonSerializable(typeof(WorldShaping[]))]
// The audio sections: the speaker row + tune/patch asset REFERENCE rows + the audio defaults (the world.row.set
// speakers / world.row.set tunes / world.row.set patches / world.row.set audio payload shapes). WorldTune/WorldPatch
// are plain name/source/hash rows — no embedded document, no bridging converter (see WorldMusicRow, registered
// below, for the same shape).
[JsonSerializable(typeof(WorldSpeaker))]
// The speaker union's nested kinds collide by simple name with the camera/screen-source unions' (Fixed/Anchored and
// None/Machine); explicit TypeInfoPropertyName entries resolve the source-gen collision (SYSLIB1031).
[JsonSerializable(typeof(OverlayPredicate.Now), TypeInfoPropertyName = "OverlayPredicateNow")]
[JsonSerializable(typeof(OverlayPredicate.Recently), TypeInfoPropertyName = "OverlayPredicateRecently")]
[JsonSerializable(typeof(OverlayPredicate.All), TypeInfoPropertyName = "OverlayPredicateAll")]
[JsonSerializable(typeof(OverlayPredicate.Any), TypeInfoPropertyName = "OverlayPredicateAny")]
[JsonSerializable(typeof(OverlayPredicate.Not), TypeInfoPropertyName = "OverlayPredicateNot")]
[JsonSerializable(typeof(OverlayPredicate.Speaking), TypeInfoPropertyName = "OverlayPredicateSpeaking")]
[JsonSerializable(typeof(OverlayPredicate.Near), TypeInfoPropertyName = "OverlayPredicateNear")]
[JsonSerializable(typeof(OverlayPredicate.State), TypeInfoPropertyName = "OverlayPredicateState")]
[JsonSerializable(typeof(OverlaySubject.Seat), TypeInfoPropertyName = "OverlaySubjectSeat")]
[JsonSerializable(typeof(OverlaySubject.Placement), TypeInfoPropertyName = "OverlaySubjectPlacement")]
[JsonSerializable(typeof(OverlaySubject.Entity), TypeInfoPropertyName = "OverlaySubjectEntity")]
[JsonSerializable(typeof(OverlaySubject.AnySeat), TypeInfoPropertyName = "OverlaySubjectAnySeat")]
[JsonSerializable(typeof(OverlaySubject.RecentSpeaker), TypeInfoPropertyName = "OverlaySubjectRecentSpeaker")]
[JsonSerializable(typeof(WorldAnchor.Seat), TypeInfoPropertyName = "WorldAnchorSeat")]
[JsonSerializable(typeof(WorldAnchor.RecentSpeaker), TypeInfoPropertyName = "WorldAnchorRecentSpeaker")]
[JsonSerializable(typeof(WorldRenderLight))]
[JsonSerializable(typeof(WorldRenderLight.Directional), TypeInfoPropertyName = "WorldRenderLightDirectional")]
[JsonSerializable(typeof(WorldRenderLight.Hemisphere), TypeInfoPropertyName = "WorldRenderLightHemisphere")]
[JsonSerializable(typeof(WorldRenderLight.Rim), TypeInfoPropertyName = "WorldRenderLightRim")]
// WorldRenderLight.Point and WorldMarkerSource.Point share a simple name (see the WorldCameraSubject.Placement
// note above) — named explicitly.
[JsonSerializable(typeof(WorldRenderLight.Point), TypeInfoPropertyName = "WorldRenderLightPoint")]
[JsonSerializable(typeof(WorldRenderSkyLayer))]
[JsonSerializable(typeof(WorldRenderSkyLayer.Gradient), TypeInfoPropertyName = "WorldRenderSkyLayerGradient")]
[JsonSerializable(typeof(WorldRenderSkyLayer.Fog), TypeInfoPropertyName = "WorldRenderSkyLayerFog")]
[JsonSerializable(typeof(WorldRenderSkyLayer.SunDisc), TypeInfoPropertyName = "WorldRenderSkyLayerSunDisc")]
[JsonSerializable(typeof(WorldRenderSkyLayer.Stars), TypeInfoPropertyName = "WorldRenderSkyLayerStars")]
[JsonSerializable(typeof(WorldRenderSkyLayer.Clouds), TypeInfoPropertyName = "WorldRenderSkyLayerClouds")]
[JsonSerializable(typeof(WorldCameraAnchorCandidate))]
[JsonSerializable(typeof(WorldLookCue))]
[JsonSerializable(typeof(WorldHudFrameCandidate))]
[JsonSerializable(typeof(WorldSpeaker.Fixed), TypeInfoPropertyName = "WorldSpeakerFixed")]
[JsonSerializable(typeof(WorldSpeaker.Anchored), TypeInfoPropertyName = "WorldSpeakerAnchored")]
[JsonSerializable(typeof(WorldSpeakerSource.None), TypeInfoPropertyName = "WorldSpeakerSourceNone")]
[JsonSerializable(typeof(WorldSpeakerSource.Machine), TypeInfoPropertyName = "WorldSpeakerSourceMachine")]
[JsonSerializable(typeof(WorldTune))]
[JsonSerializable(typeof(WorldPatch))]
// puck.music.v1 is referenced, never embedded — a plain Name/Source/Hash row, no bridging converter.
[JsonSerializable(typeof(WorldMusicRow))]
[JsonSerializable(typeof(WorldAudioDefaults))]
[JsonSerializable(typeof(WorldAudioCue))]
// The probes section rows (the document `probes` section) and the frame-source vocabulary a probe socket shares
// with a screen row's own Camera/View/Probe/Capture arms. WorldFrameSource is registered explicitly so
// WorldProbe.Inputs' dictionary values (and this suite's own round-trip law) reach it through a typed
// WorldJsonContext.Default accessor; WorldScreenSource.Probe collides by simple name with
// WorldProbeParameterTarget.Probe, so its TypeInfoPropertyName entry (below) resolves that source-gen collision
// (SYSLIB1031), following the WorldSpeaker/WorldLook precedent above.
[JsonSerializable(typeof(WorldProbe))]
[JsonSerializable(typeof(WorldFrameSource), TypeInfoPropertyName = "WorldFrameSource")]
[JsonSerializable(typeof(WorldProbeBinding.Axis), TypeInfoPropertyName = "WorldProbeBindingAxis")]
[JsonSerializable(typeof(WorldProbeBinding.Parameter), TypeInfoPropertyName = "WorldProbeBindingParameter")]
[JsonSerializable(typeof(WorldProbeBinding.Control), TypeInfoPropertyName = "WorldProbeBindingControl")]
[JsonSerializable(typeof(WorldProbeParameterTarget.Extension), TypeInfoPropertyName = "WorldProbeParameterTargetExtension")]
[JsonSerializable(typeof(WorldProbeParameterTarget.Probe), TypeInfoPropertyName = "WorldProbeParameterTargetProbe")]
[JsonSerializable(typeof(WorldScreenSource.Probe), TypeInfoPropertyName = "WorldScreenSourceProbe")]
// The host-section defaults row (the world.row.set host payload shape + the document `host` section). WorldBackendPreference
// and SurfaceFormat ride explicit name-map converters (below) rather than the camelCase enum policy, which would emit
// "directX" / "r8G8B8A8Unorm"; PresentMode keeps the generic camelCase converter (immediate/adaptive/…).
[JsonSerializable(typeof(WorldHostDefaults))]
// The LOOK rows (the world.row.set looks payload shape + the document `looks`/`lookAssignment` sections). The polymorphic
// look-source derived types carry explicit TypeInfoPropertyName entries so the source-gen simple names never collide
// with WorldPrototype / other "Catalog"/"Creation" nouns (SYSLIB1031), following the WorldSpeaker precedent above.
[JsonSerializable(typeof(WorldLook))]
[JsonSerializable(typeof(WorldLookSource.Catalog), TypeInfoPropertyName = "WorldLookSourceCatalog")]
[JsonSerializable(typeof(WorldLookSource.Creation), TypeInfoPropertyName = "WorldLookSourceCreation")]
// The dynamics section rows (the world.row.set dynamics payload shape). Also reachable from WorldDefinition already;
// this entry exposes the typed WorldJsonContext.Default.DynamicsRow accessor the verb deserializes through.
[JsonSerializable(typeof(DynamicsRow))]
// The curves section rows (the world.row.set curves payload shape). Also reachable from WorldDefinition already;
// this entry exposes the typed WorldJsonContext.Default.WorldCurveRow accessor the verb deserializes through.
[JsonSerializable(typeof(WorldCurveRow))]
[JsonSerializable(typeof(WorldDistribution))]
[JsonSerializable(typeof(WorldDistributionRegion.Disc), TypeInfoPropertyName = "WorldDistributionRegionDisc")]
[JsonSerializable(typeof(WorldDistributionRegion.Points), TypeInfoPropertyName = "WorldDistributionRegionPoints")]
[JsonSerializable(typeof(WorldDistributionRegion.Lattice), TypeInfoPropertyName = "WorldDistributionRegionLattice")]
[JsonSerializable(typeof(WorldDistributionRegion.Noise), TypeInfoPropertyName = "WorldDistributionRegionNoise")]
[JsonSerializable(typeof(WorldDistributionRegion.Scatter), TypeInfoPropertyName = "WorldDistributionRegionScatter")]
// The hud section rows (the world.row.set hud.panels / world.row.set hud.defaults payload shapes; an element rides its panel row). Also
// reachable from WorldDefinition already; these entries expose the typed WorldJsonContext.Default.<Type> accessors.
[JsonSerializable(typeof(WorldHudPanel))]
[JsonSerializable(typeof(WorldHudElement))]
[JsonSerializable(typeof(WorldHudDefaults))]
// The state section rows (the world.row.set state payload shape). Also reachable from WorldDefinition already; this entry
// exposes the typed WorldJsonContext.Default.WorldStateRow accessor the verb deserializes through. WorldStateRow is
// one sealed record (the CELL substrate) with ONE authored JSON shape — no $type discriminator at all — hand-written
// in WorldStateRowJsonConverter so the `value`-vs-`cells` exclusivity and the decimal fixed-point spelling refuse by
// name rather than defaulting.
// The row's own name and kind — hand-parsed by StateRowJsonConverter<TRow>, never an ordinarily reachable
// property, so its own IJsonSchemaNodeConverter.BuildSchema needs an explicit root to export either through.
[JsonSerializable(typeof(CellName))]
[JsonSerializable(typeof(CellKind))]
[JsonSerializable(typeof(WorldStateRow))]
[JsonSerializable(typeof(WorldStateSection))]
// The stochastic SOURCE family — reachable both as a document `generators` row and inline inside a site's draw
// facet. Registered on its own so WorldStateRowJsonConverter can read/write the facet through a typed accessor (the
// row converter is hand-written; its nested objects are ordinary strict-parsed STJ).
[JsonSerializable(typeof(StateGenerator))]
// The authored-randomness facet a state row (WorldStateRow.Draw), the population section
// (bodies.capacityRow reading a boot-drawn row), or the host section (WorldHostDefaults.BackendDraw) may declare.
[JsonSerializable(typeof(Draw), TypeInfoPropertyName = "StateDraw")]
[JsonSerializable(typeof(WorldStateFieldTrait))]
[JsonSerializable(typeof(StateDomain))]
// Disambiguates the generated type-info property name from LatticeTopology.Ring, an unrelated type
// sharing the simple name "Ring" (see SYSLIB1031).
[JsonSerializable(typeof(StateDomain.Ring), TypeInfoPropertyName = "WorldStateDomainRing")]
[JsonSerializable(typeof(StateTransform))]
[JsonSerializable(typeof(WorldObservedRow[]))]
[JsonSerializable(typeof(StatePhase))]
[JsonSerializable(typeof(StateVisibility))]
[JsonSerializable(typeof(StateKnowledge))]
[JsonSerializable(typeof(StateObservation))]
[JsonSerializable(typeof(LatticeTopology))]
[JsonSerializable(typeof(WorldFieldTopology))]
// Disambiguates the generated type-info property name from WorldCollider.Box, an unrelated type sharing the simple
// name "Box" (see SYSLIB1031).
[JsonSerializable(typeof(LatticeTopology.Box), TypeInfoPropertyName = "WorldStateLatticeTopologyBox")]
// The continuous-accumulation trait a state row's SLOT cell, or (independently) any of a keyed row's OWN cells, may
// declare — read/written by WorldStateRowJsonConverter through this typed accessor, the same "hand-written row,
// ordinary strict-parsed nested object" split the generator table above already uses, at either grain.
[JsonSerializable(typeof(StateAdvance))]
[JsonSerializable(typeof(StateCycle))]
// The derived-board trait a CellsOf-domain row may declare — same "hand-written row, ordinary strict-parsed nested
// object" split.
[JsonSerializable(typeof(StateInverse))]
// The rules section rows (the world.row.set rules payload shape). Also reachable from WorldDefinition already; this entry
// exposes the typed WorldJsonContext.Default.WorldRule accessor the verb deserializes through.
[JsonSerializable(typeof(WorldRule))]
// The world-registered action arms (WorldRuleVocabulary): registered onto ActionEffect/ActionPredicate at
// resolution by WorldJsonVocabulary.Extend, so each needs its own metadata here.
[JsonSerializable(typeof(WorldEffect.SetVerticalVelocity))]
[JsonSerializable(typeof(WorldEffect.ScaleVerticalVelocity))]
[JsonSerializable(typeof(WorldEffect.PlanarImpulse))]
[JsonSerializable(typeof(WorldEffect.StartTimer))]
[JsonSerializable(typeof(WorldEffect.Designate))]
[JsonSerializable(typeof(WorldEffect.EmitCue))]
[JsonSerializable(typeof(WorldEffect.SetBodyVerticalVelocity))]
[JsonSerializable(typeof(WorldEffect.ScaleBodyVerticalVelocity))]
[JsonSerializable(typeof(WorldEffect.ApplyBodyImpulse))]
[JsonSerializable(typeof(WorldEffect.ApplyRigidImpulse))]
[JsonSerializable(typeof(WorldEffect.DesignateBody))]
[JsonSerializable(typeof(WorldEffect.PaintField))]
[JsonSerializable(typeof(WorldEffect.UpsertHudPanel))]
[JsonSerializable(typeof(WorldEffect.RemoveHudPanel))]
[JsonSerializable(typeof(WorldEffect.UpsertPlacement))]
[JsonSerializable(typeof(WorldEffect.RemovePlacement))]
[JsonSerializable(typeof(WorldEffect.Save))]
[JsonSerializable(typeof(WorldEffect.Pose))]
[JsonSerializable(typeof(WorldEffect.SetIdentityFact))]
[JsonSerializable(typeof(WorldIdentityFacts))]
[JsonSerializable(typeof(WorldPredicate.Now))]
[JsonSerializable(typeof(WorldPredicate.Recently))]
[JsonSerializable(typeof(WorldPredicate.TimerElapsed))]
[JsonSerializable(typeof(WorldPredicate.Held))]
// The inputHold section row (the world.row.set inputHold payload shape + the document `inputHold` section) — its
// AUTHORED shape (seconds, never the compiled simulation-tick fields WorldInputHoldSettings carries; see
// WorldInputHoldAuthoring's remarks). WorldInputHoldSettings itself is never a JSON target any more (nothing
// serializes the compiled shape directly), so it carries no entry here.
[JsonSerializable(typeof(WorldInputHoldAuthoring))]
[JsonSerializable(typeof(WorldInputHoldParticipantAuthoring))]
// The groups section (the world.row.set groups.kinds payload shape). Also reachable from WorldDefinition already; this entry
// exposes the typed WorldJsonContext.Default.WorldGroupKind accessor the verb deserializes through.
[JsonSerializable(typeof(WorldGroupsSection))]
[JsonSerializable(typeof(WorldGroupKind))]
[JsonSerializable(typeof(WorldGroup))]
[JsonSerializable(typeof(WorldOwnership))]
// The properties/interactions sections (the world.row.set interactions.interactions payload shape). Also reachable from WorldDefinition
// already; this entry exposes the typed WorldJsonContext.Default.WorldInteraction accessor the verb deserializes
// through. WorldPropertyRegistrySection needs no accessor of its own — world.row.set properties.names/world.row.remove properties.names take a bare name,
// never an inline-JSON row.
[JsonSerializable(typeof(WorldPropertyRegistrySection))]
[JsonSerializable(typeof(WorldInteractionsSection))]
[JsonSerializable(typeof(WorldInteraction))]
[JsonSerializable(typeof(WorldFieldsSection))]
[JsonSerializable(typeof(WorldReaction))]
[JsonSerializable(typeof(WorldFieldRow))]
[JsonSerializable(typeof(WorldLatticeFill))]
[JsonSerializable(typeof(WorldAdjacency))]
[JsonSerializable(typeof(WorldAdjacencyBoundary))]
// The signed border claim's payload shape — a separate document family, sharing this context's strictness and
// Vector3/enum spellings so a boundary reads identically here and in the world document.
[JsonSerializable(typeof(WorldCounterpartAttestation))]
// The silo document (puck.silo.def.v1) — a separate document family (Puck.World.Silo's own composition input,
// never embedded in or referenced from a world document), sharing this context's strictness and naming policy so
// its own JSON Schema generation rides the same exporter machinery as every world-document family.
[JsonSerializable(typeof(WorldSiloDefinition))]
[JsonSourceGenerationOptions(
    // Puck.Commands' own types are absent from this list deliberately: CommandValue and every binding enum carry
    // their converter at their own declaration now (Puck.Commands references Puck.Abstractions for exactly that),
    // so this context and Puck.Commands.BindingProfileJsonContext read the shape off the TYPE rather than each
    // repeating a registration the other could drift from.
    Converters = new[] { typeof(Puck.Assets.Documents.Vector2JsonConverter), typeof(Puck.Assets.Documents.Vector3JsonConverter), typeof(Puck.Assets.Documents.QuaternionJsonConverter), typeof(CreationDocumentJsonConverter), typeof(WorldBackendPreferenceJsonConverter), typeof(SurfaceFormatJsonConverter), typeof(GrantSubjectJsonConverter), typeof(WorldPrincipalJsonConverter), typeof(ChannelReachMaskJsonConverter), typeof(ChannelConsentMaskJsonConverter), typeof(MutationKindMaskJsonConverter), typeof(DocumentWriteMaskJsonConverter), typeof(WorldStateRowJsonConverter), typeof(SafeNameJsonConverter), typeof(CellNameJsonConverter), typeof(WorldDestinationDurabilityJsonConverter), typeof(WorldPortalTravelJsonConverter), typeof(WorldPortalArrivalJsonConverter), typeof(WorldDestinationScopeJsonConverter) },
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    // The OTHER half of strict parse. UnmappedMemberHandling below refuses a member the model does not have; this
    // refuses a member the model REQUIRES and the document does not carry. Without it, a constructor parameter with
    // no C# default is silently filled — an enum lands on 0, a reference on null — so the generated schema (which
    // marks such a parameter `required`, correctly) and the loader would disagree about the contract, and the
    // document would lose the argument with absence answered by someone else's numbers, invisible to any battery.
    //
    // The consequence is that "no C# default" now MEANS required, everywhere in the document graph. A member that is
    // genuinely optional says so with an explicit default; a member that is genuinely required is authored in every
    // document. There is no third state any more, which is the point.
    RespectRequiredConstructorParameters = true,
    // The context-wide default: an unmapped member on ANY row in the graph is a hard parse failure, not a silent
    // drop. WorldDefinition's [JsonExtensionData] root carve-out (see the type doc above) is the only exception —
    // STJ routes a root-level unmapped member there regardless of this setting.
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true
)]
internal sealed partial class WorldJsonSourceContext : JsonSerializerContext {
}

/// <summary>The serializer every world document, verb payload, and wire codec reads and writes through: the
/// source-generated metadata of <see cref="WorldJsonSourceContext"/> plus the arms the document adds at runtime to
/// the engine's polymorphic bases (<see cref="WorldJsonVocabulary"/>). The typed accessors mirror the generated
/// context's, resolved through <see cref="Options"/> so every nested shape sees the same extended resolver.</summary>
public sealed class WorldJsonContext : IJsonTypeInfoResolver {
    /// <summary>Gets the one shared instance.</summary>
    public static WorldJsonContext Default { get; } = new();

    private WorldJsonContext() {
        var options = new JsonSerializerOptions(options: WorldJsonSourceContext.Default.Options) {
            TypeInfoResolver = WorldJsonSourceContext.Default.WithAddedModifier(modifier: WorldJsonVocabulary.Extend),
        };

        options.MakeReadOnly();
        Options = options;
    }

    /// <summary>Gets the read-only options carrying the extended resolver.</summary>
    public JsonSerializerOptions Options { get; }

    /// <inheritdoc/>
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => Options.TypeInfoResolver!.GetTypeInfo(type: type, options: options);

    private JsonTypeInfo<T> Get<T>() => ((JsonTypeInfo<T>)Options.GetTypeInfo(type: typeof(T)));

    /// <summary>Gets the type info for <see cref="BindingProfileDocument"/>.</summary>
    public JsonTypeInfo<BindingProfileDocument> BindingProfileDocument => Get<BindingProfileDocument>();
    /// <summary>Gets the type info for <see cref="WorldFrameSource"/>.</summary>
    public JsonTypeInfo<WorldFrameSource> WorldFrameSource => Get<WorldFrameSource>();
    /// <summary>Gets the type info for <see cref="WorldDefinition"/>.</summary>
    public JsonTypeInfo<WorldDefinition> WorldDefinition => Get<WorldDefinition>();
    /// <summary>Gets the type info for <see cref="BodyMotionProgram"/>.</summary>
    public JsonTypeInfo<BodyMotionProgram> BodyMotionProgram => Get<BodyMotionProgram>();
    /// <summary>Gets the type info for <see cref="StateVisibility"/>.</summary>
    public JsonTypeInfo<StateVisibility> StateVisibility => Get<StateVisibility>();
    /// <summary>Gets the type info for <see cref="StateCycle"/>.</summary>
    public JsonTypeInfo<StateCycle> StateCycle => Get<StateCycle>();
    /// <summary>Gets the type info for <see cref="StateAdvance"/>.</summary>
    public JsonTypeInfo<StateAdvance> StateAdvance => Get<StateAdvance>();
    /// <summary>Gets the type info for <see cref="WorldRule"/>.</summary>
    public JsonTypeInfo<WorldRule> WorldRule => Get<WorldRule>();
    /// <summary>Gets the type info for <see cref="WorldStateRow"/>.</summary>
    public JsonTypeInfo<WorldStateRow> WorldStateRow => Get<WorldStateRow>();
    /// <summary>Gets the type info for <see cref="WorldScreen"/>.</summary>
    public JsonTypeInfo<WorldScreen> WorldScreen => Get<WorldScreen>();
    /// <summary>Gets the type info for <see cref="WorldPrototype"/>.</summary>
    public JsonTypeInfo<WorldPrototype> WorldPrototype => Get<WorldPrototype>();
    /// <summary>Gets the type info for <see cref="WorldPlacement"/>.</summary>
    public JsonTypeInfo<WorldPlacement> WorldPlacement => Get<WorldPlacement>();
    /// <summary>Gets the type info for <see cref="WorldInteraction"/>.</summary>
    public JsonTypeInfo<WorldInteraction> WorldInteraction => Get<WorldInteraction>();
    /// <summary>Gets the type info for <see cref="WorldHudPanel"/>.</summary>
    public JsonTypeInfo<WorldHudPanel> WorldHudPanel => Get<WorldHudPanel>();
    /// <summary>Gets the type info for <see cref="WorldGroupKind"/>.</summary>
    public JsonTypeInfo<WorldGroupKind> WorldGroupKind => Get<WorldGroupKind>();
    /// <summary>Gets the type info for <see cref="DynamicsRow"/>.</summary>
    public JsonTypeInfo<DynamicsRow> DynamicsRow => Get<DynamicsRow>();
    /// <summary>Gets the type info for <see cref="WorldCurveRow"/>.</summary>
    public JsonTypeInfo<WorldCurveRow> WorldCurveRow => Get<WorldCurveRow>();
    /// <summary>Gets the type info for <see cref="WorldCounterpartAttestation"/>.</summary>
    public JsonTypeInfo<WorldCounterpartAttestation> WorldCounterpartAttestation => Get<WorldCounterpartAttestation>();
    /// <summary>Gets the type info for <see cref="ValueExpression"/>.</summary>
    public JsonTypeInfo<ValueExpression> ValueExpression => Get<ValueExpression>();
    /// <summary>Gets the type info for <see cref="WorldViewLayout"/>.</summary>
    public JsonTypeInfo<WorldViewLayout> WorldViewLayout => Get<WorldViewLayout>();
    /// <summary>Gets the type info for <see cref="WorldViewStudy"/>.</summary>
    public JsonTypeInfo<WorldViewStudy> WorldViewStudy => Get<WorldViewStudy>();
    /// <summary>Gets the type info for <see cref="WorldTune"/>.</summary>
    public JsonTypeInfo<WorldTune> WorldTune => Get<WorldTune>();
    /// <summary>Gets the type info for <see cref="StatePhase"/>.</summary>
    public JsonTypeInfo<StatePhase> StatePhase => Get<StatePhase>();
    /// <summary>Gets the type info for <see cref="StateObservation"/>.</summary>
    public JsonTypeInfo<StateObservation> StateObservation => Get<StateObservation>();
    /// <summary>Gets the type info for <see cref="StateKnowledge"/>.</summary>
    public JsonTypeInfo<StateKnowledge> StateKnowledge => Get<StateKnowledge>();
    /// <summary>Gets the type info for <see cref="WorldStateFieldTrait"/>.</summary>
    public JsonTypeInfo<WorldStateFieldTrait> WorldStateFieldTrait => Get<WorldStateFieldTrait>();
    /// <summary>Gets the type info for <see cref="StateDomain"/>.</summary>
    public JsonTypeInfo<StateDomain> StateDomain => Get<StateDomain>();
    /// <summary>Gets the type info for <see cref="WorldSpeaker"/>.</summary>
    public JsonTypeInfo<WorldSpeaker> WorldSpeaker => Get<WorldSpeaker>();
    /// <summary>Gets the type info for <see cref="WorldSpawnPoint"/> arrays.</summary>
    public JsonTypeInfo<WorldSpawnPoint[]> WorldSpawnPointArray => Get<WorldSpawnPoint[]>();
    /// <summary>Gets the type info for <see cref="WorldSiloDefinition"/>.</summary>
    public JsonTypeInfo<WorldSiloDefinition> WorldSiloDefinition => Get<WorldSiloDefinition>();
    /// <summary>Gets the type info for <see cref="WorldSeatViewControl"/>.</summary>
    public JsonTypeInfo<WorldSeatViewControl> WorldSeatViewControl => Get<WorldSeatViewControl>();
    /// <summary>Gets the type info for <see cref="WorldSeatCameraFeel"/>.</summary>
    public JsonTypeInfo<WorldSeatCameraFeel> WorldSeatCameraFeel => Get<WorldSeatCameraFeel>();
    /// <summary>Gets the type info for <see cref="WorldRenderDefaults"/>.</summary>
    public JsonTypeInfo<WorldRenderDefaults> WorldRenderDefaults => Get<WorldRenderDefaults>();
    /// <summary>Gets the type info for <see cref="WorldProjectionDocument"/>.</summary>
    public JsonTypeInfo<WorldProjectionDocument> WorldProjectionDocument => Get<WorldProjectionDocument>();
    /// <summary>Gets the type info for <see cref="WorldPlacementPolicyDefaults"/>.</summary>
    public JsonTypeInfo<WorldPlacementPolicyDefaults> WorldPlacementPolicyDefaults => Get<WorldPlacementPolicyDefaults>();
    /// <summary>Gets the type info for <see cref="WorldPatch"/>.</summary>
    public JsonTypeInfo<WorldPatch> WorldPatch => Get<WorldPatch>();
    /// <summary>Gets the type info for <see cref="WorldObservedRow"/> arrays.</summary>
    public JsonTypeInfo<WorldObservedRow[]> WorldObservedRowArray => Get<WorldObservedRow[]>();
    /// <summary>Gets the type info for <see cref="WorldMotionDefaults"/>.</summary>
    public JsonTypeInfo<WorldMotionDefaults> WorldMotionDefaults => Get<WorldMotionDefaults>();
    /// <summary>Gets the type info for <see cref="WorldLook"/>.</summary>
    public JsonTypeInfo<WorldLook> WorldLook => Get<WorldLook>();
    /// <summary>Gets the type info for <see cref="WorldKit"/>.</summary>
    public JsonTypeInfo<WorldKit> WorldKit => Get<WorldKit>();
    /// <summary>Gets the type info for <see cref="WorldInputHoldAuthoring"/>.</summary>
    public JsonTypeInfo<WorldInputHoldAuthoring> WorldInputHoldAuthoring => Get<WorldInputHoldAuthoring>();
    /// <summary>Gets the type info for <see cref="WorldHudElement"/>.</summary>
    public JsonTypeInfo<WorldHudElement> WorldHudElement => Get<WorldHudElement>();
    /// <summary>Gets the type info for <see cref="WorldHudDefaults"/>.</summary>
    public JsonTypeInfo<WorldHudDefaults> WorldHudDefaults => Get<WorldHudDefaults>();
    /// <summary>Gets the type info for <see cref="WorldHostDefaults"/>.</summary>
    public JsonTypeInfo<WorldHostDefaults> WorldHostDefaults => Get<WorldHostDefaults>();
    /// <summary>Gets the type info for <see cref="Draw"/>.</summary>
    public JsonTypeInfo<Draw> Draw => Get<Draw>();
    /// <summary>Gets the type info for <see cref="WorldCollision"/>.</summary>
    public JsonTypeInfo<WorldCollision> WorldCollision => Get<WorldCollision>();
    /// <summary>Gets the type info for <see cref="WorldCameraProgram"/>.</summary>
    public JsonTypeInfo<WorldCameraProgram> WorldCameraProgram => Get<WorldCameraProgram>();
    /// <summary>Gets the type info for <see cref="WorldCamera"/>.</summary>
    public JsonTypeInfo<WorldCamera> WorldCamera => Get<WorldCamera>();
    /// <summary>Gets the type info for <see cref="WorldBindingOverlay"/>.</summary>
    public JsonTypeInfo<WorldBindingOverlay> WorldBindingOverlay => Get<WorldBindingOverlay>();
    /// <summary>Gets the type info for <see cref="WorldAudioDefaults"/>.</summary>
    public JsonTypeInfo<WorldAudioDefaults> WorldAudioDefaults => Get<WorldAudioDefaults>();
    /// <summary>Gets the type info for <see cref="WorldAddonRow"/>.</summary>
    public JsonTypeInfo<WorldAddonRow> WorldAddonRow => Get<WorldAddonRow>();
    /// <summary>Gets the type info for <see cref="ValueExpressionTokens"/>.</summary>
    public JsonTypeInfo<ValueExpressionTokens> ValueExpressionTokens => Get<ValueExpressionTokens>();
    /// <summary>Gets the type info for <see cref="StateTransform"/>.</summary>
    public JsonTypeInfo<StateTransform> StateTransform => Get<StateTransform>();
    /// <summary>Gets the type info for <see cref="LatticeTopology"/>.</summary>
    public JsonTypeInfo<LatticeTopology> LatticeTopology => Get<LatticeTopology>();
}

/// <summary>The arms the world document adds to the engine's polymorphic bases: each engine union declares only the
/// cases the state library owns, and this modifier appends the document's own cases under their discriminators
/// wherever <see cref="WorldJsonContext"/> resolves the base.</summary>
public static class WorldJsonVocabulary {
    /// <summary>Appends the document's derived arms to a resolved base type's polymorphism options.</summary>
    /// <param name="typeInfo">The type info being resolved.</param>
    public static void Extend(JsonTypeInfo typeInfo) {
        ArgumentNullException.ThrowIfNull(argument: typeInfo);

        if ((typeInfo.Type == typeof(LatticeTopology)) && (typeInfo.PolymorphismOptions is { } lattices)) {
            lattices.DerivedTypes.Add(item: new JsonDerivedType(derivedType: typeof(WorldFieldTopology), typeDiscriminator: "field"));
        }
        WorldRuleVocabulary.Instance.ExtendJson(typeInfo: typeInfo);
    }
}

/// <summary>The shared shape behind a plain 64-bit lane's wire form: a bare JSON number, round-tripped through the
/// closed subclass's own <c>Bits</c> constructor/property.</summary>
internal abstract class BitMaskJsonConverter<T> : JsonConverter<T>, IJsonSchemaTypeConverter where T : struct {
    private static readonly string[] AcceptedSchemaTypes = ["integer"];

    /// <inheritdoc/>
    public IReadOnlyList<string> SchemaTypes => AcceptedSchemaTypes;

    /// <summary>Builds <typeparamref name="T"/> from its raw bit lane.</summary>
    protected abstract T Create(ulong bits);
    /// <summary>Reads back <paramref name="value"/>'s raw bit lane.</summary>
    protected abstract ulong GetBits(T value);

    /// <inheritdoc/>
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Create(bits: reader.GetUInt64());
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteNumberValue(value: GetBits(value: value));
}
internal sealed class ChannelReachMaskJsonConverter : BitMaskJsonConverter<ChannelReachMask> {
    /// <inheritdoc/>
    protected override ChannelReachMask Create(ulong bits) => new(Bits: bits);
    /// <inheritdoc/>
    protected override ulong GetBits(ChannelReachMask value) => value.Bits;
}
internal sealed class ChannelConsentMaskJsonConverter : BitMaskJsonConverter<ChannelConsentMask> {
    /// <inheritdoc/>
    protected override ChannelConsentMask Create(ulong bits) => new(Bits: bits);
    /// <inheritdoc/>
    protected override ulong GetBits(ChannelConsentMask value) => value.Bits;
}
/// <summary>
/// Reads and writes a <see cref="MutationKindMask"/> as the same comma-separated kind-name token
/// <c>world.grant</c>'s <c>verbs:&lt;name,…&gt;</c> takes (<c>"UpsertStateRow,RemoveStateRow"</c>) rather than the
/// raw <c>{"bits":211106232532992}</c> object this context's member policies would emit. A raw lane is exactly where
/// the mask-vocabulary confusion this type's own remarks describe was invisible: two grant rows carrying
/// <c>{"bits":3}</c> mean entirely different things depending on their subject kind, and no reviewer can see it. A
/// name list cannot be misread, and an unknown name refuses by name at parse rather than folding to a silently
/// narrower mask.
/// </summary>
/// <summary>The shared shape behind a comma-separated declared-name lane: <c>Read</c> parses via the closed
/// subclass's own vocabulary and refuses an unrecognized name by it (never a silently narrower mask); <c>Write</c>
/// prints the same name list back.</summary>
internal abstract class NameListMaskJsonConverter<T> : JsonConverter<T>, IJsonSchemaNodeConverter where T : struct {
    /// <summary>Gets the mask's own noun, read into the refusal template as "a &lt;kind&gt; mask is …".</summary>
    protected abstract string MaskKind { get; }
    /// <summary>Gets the declared vocabulary description read into the refusal template.</summary>
    protected abstract string Vocabulary { get; }
    /// <summary>Gets the row noun an unrecognized name refuses against ("… names no declared &lt;noun&gt;.").</summary>
    protected abstract string Noun { get; }
    /// <summary>Gets a regex constraining the comma-separated wire form to the declared vocabulary, or
    /// <see langword="null"/> when the vocabulary is not reachable at schema-generation time (installed later by a
    /// module initializer this generator never runs).</summary>
    protected virtual string? SchemaPattern => null;

    /// <summary>Parses the comma-separated name list, reporting the first unrecognized name.</summary>
    protected abstract bool TryParse(string? text, out T mask, out string? unknown);
    /// <summary>Prints <paramref name="value"/>'s comma-separated name list.</summary>
    protected abstract string Describe(T value);

    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) {
        var obj = new JsonObject { ["type"] = "string" };

        if (SchemaPattern is { } pattern) {
            obj["pattern"] = pattern;
        }

        return obj;
    }

    /// <inheritdoc/>
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var token = ((reader.TokenType == JsonTokenType.String)
            ? reader.GetString()
            : null
        );

        if (!TryParse(
            mask: out var mask,
            text: token,
            unknown: out var unknown
        )) {
            throw new JsonException(message: $"a {MaskKind} mask is a comma-separated list of {Vocabulary}; '{(string.IsNullOrEmpty(value: unknown)
                ? (token ?? "(absent)")
                : unknown)}' names no declared {Noun}.");
        }

        return mask;
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value: Describe(value: value));
}
internal sealed class MutationKindMaskJsonConverter : NameListMaskJsonConverter<MutationKindMask> {
    /// <inheritdoc/>
    protected override string MaskKind => "verb";
    /// <inheritdoc/>
    protected override string Vocabulary => "WorldMutation kind names (e.g. \"UpsertStateCell,RemoveStateCell\")";
    /// <inheritdoc/>
    protected override string Noun => "mutation kind";

    /// <inheritdoc/>
    protected override bool TryParse(string? text, out MutationKindMask mask, out string? unknown) => MutationKindMask.TryParse(
        mask: out mask,
        text: text,
        unknown: out unknown
    );
    /// <inheritdoc/>
    protected override string Describe(MutationKindMask value) => value.Describe();
}
/// <summary>
/// Reads and writes a <see cref="DocumentWriteMask"/> as the same comma-separated operation-name token
/// <c>world.grant</c>'s <c>writes:&lt;name,…&gt;</c> takes (<c>"Set,Add"</c>) — the cross-document durable-state
/// channel's own vocabulary, visibly different on the page from a
/// <see cref="MutationKindMaskJsonConverter">verb mask</see> rather than an identically-shaped bit lane.
/// </summary>
internal sealed class DocumentWriteMaskJsonConverter : NameListMaskJsonConverter<DocumentWriteMask> {
    /// <inheritdoc/>
    protected override string MaskKind => "write";
    /// <inheritdoc/>
    protected override string Vocabulary => DocumentWriteMask.All.Describe();
    /// <inheritdoc/>
    protected override string Noun => "operation";
    /// <inheritdoc/>
    protected override string SchemaPattern {
        get {
            var names = DocumentWriteMask.All.Describe().Split(separator: ',');
            var alternation = string.Join(separator: '|', values: names);

            return $"^({alternation})(,({alternation}))*$";
        }
    }

    /// <inheritdoc/>
    protected override bool TryParse(string? text, out DocumentWriteMask mask, out string? unknown) => DocumentWriteMask.TryParse(
        mask: out mask,
        text: text,
        unknown: out unknown
    );
    /// <inheritdoc/>
    protected override string Describe(DocumentWriteMask value) => value.Describe();
}
/// <summary>
/// Reads and writes <see cref="WorldStateRow"/> — the cell substrate's one C# type — as one authored JSON shape (see
/// <see cref="WorldStateRow"/>'s remarks): a <c>name</c>, a <c>kind</c> (<see cref="CellKind"/>'s own declared member
/// name), the optional envelope fields (<c>min</c>/<c>max</c>/<c>capacity</c>/<c>nonNegative</c>), and
/// either a bare <c>value</c> — sugar for the one cell keyed <see cref="StateRow.SlotKey"/> — or a <c>cells</c>
/// array of <c>{"key","value"}</c> objects. Two optional fields, never two discriminators: a row carrying both is
/// refused by name, as is a <c>value</c> beside a <c>capacity</c> (declaring a capacity is declaring a keyed row).
/// Omitting both is a declared-but-empty row. There is no <c>$type</c> and no <c>rows</c> member — the two retired
/// spellings of the pre-collapse shape refuse as unmapped members like any other stale field.
/// <para>A fixed-kind value (<c>value</c>, <c>min</c>, <c>max</c>, or a cell's own <c>value</c>) is a decimal string
/// parsed/formatted through
/// <see cref="FixedQ4816.TryParse(string?,IFormatProvider?,out FixedQ4816)"/>/<see cref="FixedQ4816.ToString()"/> —
/// never the raw Q48.16 bit pattern; only the per-cell mutation wire and the addon ABI channel convention stay raw
/// (see <c>Puck.World.Protocol.WorldMutation.UpsertStateCell</c>'s remarks). An int-kind value is a plain JSON
/// number (a timer's non-negative floor is <see cref="StateRow.NonNegative"/>, enforced at validation, never a
/// parse-time concern here). Unmapped members and a wrong-shaped value are hard parse failures, by name, matching
/// every other row in the document graph's strict posture — a custom converter opts out of the context-wide
/// <c>UnmappedMemberHandling.Disallow</c> policy, so this converter re-implements it by hand.</para>
/// </summary>
/// <summary>The document row's wire shape: the engine's <see cref="StateRowJsonConverter{TRow}"/> plus the two members
/// only a world reads, <c>gatesDrive</c> beside the flags and <c>field</c> beside the traits.</summary>
internal sealed class WorldStateRowJsonConverter : StateRowJsonConverter<WorldStateRow> {
    /// <inheritdoc/>
    public override string Shape => "{\"name\":…,\"kind\":\"Int\"|\"Fixed\"|\"Bool\"|\"Text\",\"value\":… or \"cells\":[{\"key\":…,\"value\":…,\"provenance\":…,\"advance\":{\"rateNumerator\":…,\"rateDenominator\":…,\"epochTick\":…},\"dynamics\":{\"row\":…,\"y0\":…,\"v0\":…,\"epochTick\":…},\"cycle\":{\"word\":[…],\"power\":…,\"output\":\"Step\"|\"Turns\"|\"Cos\"|\"Sin\"|\"Node\"|\"ProjectionX\"|\"ProjectionY\"|\"Ring\",\"ticksPerStep\":…,\"epochTick\":…,\"substepTicks\":…}}],\"min\":…,\"max\":…,\"capacity\":…,\"nonNegative\":…,\"gatesDrive\":…,\"evicts\":…,\"advance\":{\"rateNumerator\":…,\"rateDenominator\":…,\"epochTick\":…},\"dynamics\":{\"row\":…,\"y0\":…,\"v0\":…,\"epochTick\":…},\"cycle\":{\"word\":[…],\"power\":…,\"output\":\"Step\"|\"Turns\"|\"Cos\"|\"Sin\"|\"Node\"|\"ProjectionX\"|\"ProjectionY\"|\"Ring\",\"ticksPerStep\":…,\"epochTick\":…,\"substepTicks\":…},\"field\":{\"initial\":…,\"min\":…,\"max\":…,\"heightScale\":…,\"color\":…,\"paint\":[…]},\"draw\":{\"source\":… or \"generator\":{\"source\":\"Markov\"|\"UniformRange\"|\"WeightedNumeric\"|\"StreamDraw\"|\"SymmetryOrbit\",…},\"timing\":\"Boot\"|\"TickPeriod\"|\"Event\"},\"drawCursor\":…,\"drawnMasks\":[…],\"historyCursor\":…,\"visibility\":{…},\"knowledge\":{…},\"phase\":{…},\"phaseOf\":…,\"valuesFrom\":…,\"domain\":{\"$type\":\"slot\"|\"keys\"|\"keysOf\"|\"cellsOf\"|\"ring\",…},\"inverse\":{\"tokens\":…,\"codes\":…}}";

    /// <inheritdoc/>
    protected override bool ClaimsMember(string name) => (name is "gatesDrive" or "field");
    /// <inheritdoc/>
    protected override bool DeclaresDrawSite(RowMembers members) => members.Claimed.ContainsKey(key: "field");
    /// <inheritdoc/>
    protected override void Validate(RowMembers members) {
        if ((members.Cycle is not null) && members.Claimed.ContainsKey(key: "field")) {
            throw new JsonException(message: $"state row '{members.Name}' declares both 'cycle' and 'field' — a physical-field row's cells are the field's.");
        }
    }
    /// <inheritdoc/>
    protected override IReadOnlyList<SchemaClaimedMember> SchemaClaimedMembers(Func<Type, JsonNode> exportType) => [
        new(Name: "gatesDrive", Schema: new JsonObject { ["type"] = "boolean" }),
        new(Name: "field", Schema: exportType(typeof(WorldStateFieldTrait))),
    ];
    /// <inheritdoc/>
    protected override IReadOnlyList<string> SchemaDrawSiteMembers => ["field"];
    /// <inheritdoc/>
    protected override IReadOnlyList<string> SchemaCycleExclusiveMembers => ["field"];
    /// <inheritdoc/>
    protected override WorldStateRow Create(StateRow row, RowMembers members, JsonSerializerOptions options) => new(
        row: row,
        gatesDrive: (members.Claimed.TryGetValue(key: "gatesDrive", value: out var gatesDrive) && RequireBool(
            context: $"state row '{members.Name}'.gatesDrive",
            element: gatesDrive
        )),
        field: (members.Claimed.TryGetValue(key: "field", value: out var field)
            ? ReadNested<WorldStateFieldTrait>(element: field, options: options, context: $"state row '{members.Name}'.field")
            : null)
    );
    /// <inheritdoc/>
    protected override void WriteFlags(Utf8JsonWriter writer, WorldStateRow row) {
        if (row.GatesDrive) {
            writer.WriteBoolean(
                propertyName: "gatesDrive",
                value: true
            );
        }
    }
    /// <inheritdoc/>
    protected override void WriteTraits(Utf8JsonWriter writer, WorldStateRow row, JsonSerializerOptions options) {
        if (row.Field is { } fieldTrait) {
            WriteNested(writer: writer, propertyName: "field", value: fieldTrait, options: options);
        }
    }
}

/// <summary>One participant-specific input-hold override, authored shape — see
/// <see cref="WorldInputHoldAuthoring"/>. Mirrors <see cref="WorldInputHoldParticipant"/> field for field except
/// <see cref="Seconds"/> in place of the compiled <see cref="WorldInputHoldParticipant.Ticks"/>.</summary>
public sealed record WorldInputHoldParticipantAuthoring(int BodyIndex, float Seconds, bool Equalized);
/// <summary>
/// <see cref="WorldInputHoldSettings"/>'s authored shape — <c>ceilingSeconds</c>/<c>lowerAfterSeconds</c>/
/// <c>defaultSeconds</c> and each participant's own <c>seconds</c>, never the <c>*Ticks</c> fields the runtime
/// actually consumes. The shape <see cref="WorldDefinition.InputHold"/> itself stores — ordinary strict-parsed STJ,
/// no custom converter: compiling to ticks needs a simulation rate, and a JSON converter mid-parse of the whole
/// document has no reliable way to see a sibling section (the rate) that has not necessarily parsed yet. Compiling
/// is deferred instead to <see cref="Compile"/>, called by <see cref="WorldDefinition.CompiledInputHold"/> once the
/// full document (and so its <see cref="WorldDefinition.SimulationRateHz"/>) exists.
/// </summary>
public sealed record WorldInputHoldAuthoring(
    float CeilingSeconds,
    float LowerAfterSeconds,
    float DefaultSeconds,
    bool EqualizeByDefault,
    IReadOnlyList<WorldInputHoldParticipantAuthoring> Participants
) {
    /// <summary>Gets the inert input-hold policy — a minimal positive ceiling and lower-after (1/240 s, a plain
    /// duration constant small enough to read as "no hold" yet positive at every legal rate — the engine holds no
    /// default rate to derive it from), zero default hold, no equalization, no participants.</summary>
    public static WorldInputHoldAuthoring Absent { get; } = new(
        CeilingSeconds: (1f / 240f),
        DefaultSeconds: 0f,
        EqualizeByDefault: false,
        LowerAfterSeconds: (1f / 240f),
        Participants: []
    );

    /// <summary>Compiles this authored (seconds) row to its compiled (ticks) shape at <paramref name="ratePerSecond"/>
    /// — the inverse of <see cref="WorldInputHoldSettings.ToAuthoring"/>. Every checked-in world authors durations
    /// that divide the rate they are authored against exactly, so this round-trips exactly for everything this
    /// codebase ships today (see <see cref="WorldSimulationTickConversion.SecondsFromTicks"/>'s remarks for the one
    /// narrow exception, reachable only through the addon-mutation ABI's raw ticks).</summary>
    /// <param name="ratePerSecond">The simulation rate (Hz) this row compiles against — a world's own
    /// <see cref="WorldDefinition.SimulationRateHz"/>.</param>
    public WorldInputHoldSettings Compile(uint ratePerSecond) {
        var participants = new WorldInputHoldParticipant[Participants.Count];

        for (var index = 0; (index < participants.Length); index++) {
            var participant = Participants[index];

            participants[index] = new WorldInputHoldParticipant(
                BodyIndex: participant.BodyIndex,
                Ticks: checked((int)WorldSimulationTickConversion.DurationTicks(
                    seconds: participant.Seconds,
                    ratePerSecond: ratePerSecond
                )),
                Equalized: participant.Equalized
            );
        }

        return new WorldInputHoldSettings(
            CeilingTicks: checked((int)WorldSimulationTickConversion.DurationTicks(
                seconds: CeilingSeconds,
                ratePerSecond: ratePerSecond
            )),
            LowerAfterTicks: checked((int)WorldSimulationTickConversion.DurationTicks(
                seconds: LowerAfterSeconds,
                ratePerSecond: ratePerSecond
            )),
            DefaultTicks: checked((int)WorldSimulationTickConversion.DurationTicks(
                seconds: DefaultSeconds,
                ratePerSecond: ratePerSecond
            )),
            EqualizeByDefault: EqualizeByDefault,
            Participants: participants
        );
    }
}

/// <summary>
/// The shared shape behind every enum this document graph reads/writes as an explicit lowercase token rather than
/// the context's camelCase enum policy: a <c>Read</c> that parses via <paramref name="tokens"/>'s owning vocabulary
/// and refuses by name, and a <c>Write</c> that prints through the same map, so nothing that reads or prints a
/// value disagrees with the document. A closed subclass supplies only the vocabulary's parse/print pair and the
/// field name a refusal names.
/// </summary>
internal abstract class TokenEnumJsonConverter<T>(string fieldName, IReadOnlyList<string> tokens) : JsonConverter<T>, IJsonSchemaStringConverter where T : struct {
    /// <inheritdoc/>
    public IReadOnlyList<string>? SchemaTokens { get; } = tokens;

    /// <summary>Parses <paramref name="token"/> against the closed vocabulary, or <see langword="null"/> when it names none.</summary>
    protected abstract T? Parse(string? token);
    /// <summary>Prints <paramref name="value"/>'s declared token.</summary>
    protected abstract string ToToken(T value);

    /// <inheritdoc/>
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var token = reader.GetString();

        return (Parse(token: token) ?? throw new JsonException(message: $"{fieldName} '{token}' must be {DescribeTokens()}."));
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value: ToToken(value: value));

    private string DescribeTokens() {
        var quoted = SchemaTokens!.Select(selector: static token => $"'{token}'").ToArray();

        return (quoted.Length switch {
            1 => quoted[0],
            2 => $"{quoted[0]} or {quoted[1]}",
            _ => $"{string.Join(separator: ", ", values: quoted[..^1])}, or {quoted[^1]}",
        });
    }
}
/// <summary>
/// Reads and writes a <see cref="WorldBackendPreference"/> as an explicit lowercase token (<c>auto</c> / <c>directx</c>
/// / <c>vulkan</c>), which would otherwise emit <c>directX</c> — a spelling no one types and gratuitously divergent
/// from World's token style. The <c>--backend</c> boot flag, the <c>host.backendDraw</c> resolver and the
/// <c>world.host</c> read-back all speak the same map.
/// </summary>
internal sealed class WorldBackendPreferenceJsonConverter() : TokenEnumJsonConverter<WorldBackendPreference>("backend", [WorldHostTokens.BackendAuto, WorldHostTokens.BackendDirectX, WorldHostTokens.BackendVulkan]) {
    /// <inheritdoc/>
    protected override WorldBackendPreference? Parse(string? token) => WorldHostTokens.ParseBackend(token: token);
    /// <inheritdoc/>
    protected override string ToToken(WorldBackendPreference value) => WorldHostTokens.BackendToken(backend: value);
}
/// <summary>
/// Reads and writes the two authorable <see cref="SurfaceFormat"/> values as explicit tokens (<c>r8g8b8a8</c> /
/// <c>b8g8r8a8</c>), which would otherwise emit the unreadable <c>r8G8B8A8Unorm</c>.
/// <see cref="SurfaceFormat.Unknown"/> and any other member are rejected at read (the validator also rejects
/// <see cref="SurfaceFormat.Unknown"/> — the hole the Demo's string list could not express). The
/// <c>world.host</c> read-back prints through the same map.
/// </summary>
internal sealed class SurfaceFormatJsonConverter() : TokenEnumJsonConverter<SurfaceFormat>("surfaceFormat", [WorldHostTokens.SurfaceFormatRgba, WorldHostTokens.SurfaceFormatBgra]) {
    /// <inheritdoc/>
    protected override SurfaceFormat? Parse(string? token) => WorldHostTokens.ParseSurfaceFormat(token: token);
    /// <inheritdoc/>
    protected override string ToToken(SurfaceFormat value) => WorldHostTokens.SurfaceFormatToken(format: value);
}
/// <summary>
/// Reads and writes a <see cref="WorldDestinationDurability"/> as the lowercase token
/// <c>Puck.World.WorldInstanceHost</c>'s <c>world.transfer</c> verb already speaks (<c>ephemeral</c> /
/// <c>persisted</c>), so an authored destination row and the console grammar its diegetic trigger drives never
/// disagree on spelling. See <see cref="WorldDestinationTokens"/>.
/// </summary>
internal sealed class WorldDestinationDurabilityJsonConverter() : TokenEnumJsonConverter<WorldDestinationDurability>("destination durability", [WorldDestinationTokens.DurabilityEphemeral, WorldDestinationTokens.DurabilityPersisted]) {
    /// <inheritdoc/>
    protected override WorldDestinationDurability? Parse(string? token) => WorldDestinationTokens.ParseDurability(token: token);
    /// <inheritdoc/>
    protected override string ToToken(WorldDestinationDurability value) => WorldDestinationTokens.DurabilityToken(durability: value);
}
/// <summary>
/// Reads and writes a <see cref="WorldPortalTravel"/> as the lowercase token <c>world.transfer</c>'s <c>party</c>
/// slot argument already speaks (<c>party</c> / <c>body</c>). See <see cref="WorldDestinationTokens"/>.
/// </summary>
internal sealed class WorldPortalTravelJsonConverter() : TokenEnumJsonConverter<WorldPortalTravel>("portal travel", [WorldDestinationTokens.TravelParty, WorldDestinationTokens.TravelBody]) {
    /// <inheritdoc/>
    protected override WorldPortalTravel? Parse(string? token) => WorldDestinationTokens.ParseTravel(token: token);
    /// <inheritdoc/>
    protected override string ToToken(WorldPortalTravel value) => WorldDestinationTokens.TravelToken(travel: value);
}
/// <summary>
/// Reads and writes a <see cref="WorldPortalArrival"/> as the lowercase token <c>spawn</c>/<c>mapped</c>, mirroring
/// <see cref="WorldPortalTravelJsonConverter"/>. See <see cref="WorldDestinationTokens"/>.
/// </summary>
internal sealed class WorldPortalArrivalJsonConverter() : TokenEnumJsonConverter<WorldPortalArrival>("portal arrival", [WorldDestinationTokens.ArrivalSpawn, WorldDestinationTokens.ArrivalMapped]) {
    /// <inheritdoc/>
    protected override WorldPortalArrival? Parse(string? token) => WorldDestinationTokens.ParseArrival(token: token);
    /// <inheritdoc/>
    protected override string ToToken(WorldPortalArrival value) => WorldDestinationTokens.ArrivalToken(arrival: value);
}
/// <summary>
/// Reads and writes a <see cref="WorldDestinationScope"/> as the lowercase token docs/vision.md's "Durability,
/// scope and generation" names (<c>user</c> / <c>group</c> / <c>global</c>), mirroring
/// <see cref="WorldDestinationDurabilityJsonConverter"/>. See <see cref="WorldDestinationTokens"/>.
/// </summary>
internal sealed class WorldDestinationScopeJsonConverter() : TokenEnumJsonConverter<WorldDestinationScope>("destination scope", [WorldDestinationTokens.ScopeUser, WorldDestinationTokens.ScopeGroup, WorldDestinationTokens.ScopeGlobal]) {
    /// <inheritdoc/>
    protected override WorldDestinationScope? Parse(string? token) => WorldDestinationTokens.ParseScope(token: token);
    /// <inheritdoc/>
    protected override string ToToken(WorldDestinationScope value) => WorldDestinationTokens.ScopeToken(scope: value);
}
/// <summary>
/// Reads and writes a <see cref="GrantSubject"/> as the same compact token <c>world.grant</c> takes — <c>all</c>,
/// <c>body:&lt;n&gt;</c>, <c>screen:&lt;n&gt;</c>, <c>section:&lt;name&gt;</c>, <c>state:&lt;name&gt;</c>,
/// <c>creation:&lt;id&gt;</c>, <c>placement:&lt;id&gt;</c> — rather than
/// this context's member policies, which would emit a raw <c>{"kind":0,"value":5,"id":null}</c> object and a bare
/// numeric <see cref="WorldSection"/> ordinal for a section subject (opaque without the enum's declaration order open
/// beside it). Parsing rides <see cref="GrantSubject.TryParse"/> — the identical grammar the console
/// itself grants through — so a document-sourced subject can only ever be the same canonical shape a live grant uses;
/// there is no way to author the denormalized encoding a raw-object shape would have permitted (a stray non-zero
/// <c>Value</c>/<c>Id</c> the wildcard or section kinds never carry), which is exactly what would have seated a
/// phantom grant a HashSet/dictionary lookup can never match. Writing rides <see cref="GrantSubject.Describe"/> — the
/// same label the console's own accept/reject lines print, so a saved document and a printed line never disagree on
/// spelling. <see cref="GrantSubjectKind.Composition"/> is never emitted (nothing constructs it outside the grant
/// table's own boot seed) and is rejected on read like any other token the grammar does not recognize.
/// </summary>
internal sealed class GrantSubjectJsonConverter : TryParseStringJsonConverter<GrantSubject> {
    /// <inheritdoc/>
    protected override bool TryParse(string? candidate, out GrantSubject value, out string reason) {
        if (
            (candidate is not null) &&
            GrantSubject.TryParse(
            subject: out value,
            token: candidate
        )
        ) {
            reason = string.Empty;

            return true;
        }

        value = default;
        reason = "must be 'all', 'body:<n>', 'screen:<n>', 'section:<name>', 'state:<name>', 'region:<name>', 'seat:<n>', 'creation:<id>', or 'placement:<id>'";

        return false;
    }
    /// <inheritdoc/>
    protected override string ToValue(GrantSubject value) => value.Describe();
}
/// <summary>
/// Reads and writes a <see cref="WorldPrincipal"/> as the same compact token <c>world.grant</c> takes —
/// <c>seat1</c>..<c>seat4</c>, <c>console</c>, <c>addon:&lt;name&gt;</c>, <c>peer:&lt;n&gt;:&lt;generation&gt;</c> — rather than this
/// context's member policies, which would emit a raw <c>{"kind":0,"index":0,"name":null}</c> object. Parsing rides
/// <see cref="WorldPrincipal.TryParse"/> — the identical grammar the console itself grants through —
/// so a document-sourced principal (a <see cref="WorldGrant.Principal"/> row) can only ever be the same canonical
/// shape a live grant uses, matching <see cref="GrantSubjectJsonConverter"/>'s reasoning exactly. Writing rides
/// <see cref="WorldPrincipal.Describe"/>, the same label the console's own accept/reject lines print.
/// </summary>
internal sealed class WorldPrincipalJsonConverter : TryParseStringJsonConverter<WorldPrincipal> {
    /// <inheritdoc/>
    protected override bool TryParse(string? candidate, out WorldPrincipal value, out string reason) {
        if (
            (candidate is not null) &&
            WorldPrincipal.TryParse(
            principal: out value,
            token: candidate
        )
        ) {
            reason = string.Empty;

            return true;
        }

        value = default;
        reason = "must be 'seat1'..'seat4', 'console', 'addon:<name>', 'peer:<n>:<generation>', or 'document:<id>'";

        return false;
    }
    /// <inheritdoc/>
    protected override string ToValue(WorldPrincipal value) => value.Describe();
}
/// <summary>
/// Bridges an embedded <see cref="Puck.World.Authoring.CreationDocument"/> (a <see cref="WorldPrototype.Document"/>) through
/// the creation contract's own serializer shape (<see cref="Puck.Assets.Documents.DocumentJsonOptions.Shared"/> — member
/// order, string enums, and the Vector2/Vector3/Quaternion array converters) instead of this context's
/// policies, so the inline-canonical embed carries exactly the member vocabulary
/// <see cref="Puck.World.Authoring.CreationCanonicalizer"/> hashes. Formatting (indent/newlines) rides the outer canonical
/// writer, which is deterministic — the ouroboros round-trip covers the composition.
/// </summary>
internal sealed class CreationDocumentJsonConverter : JsonConverter<Puck.World.Authoring.CreationDocument>, IJsonSchemaNodeConverter {
    /// <inheritdoc/>
    // A fully self-contained fragment: $id makes any "#/$defs/…" the exporter emits for a repeated CreationDocument
    // shape resolve against THIS fragment's own root, never the enclosing WorldDefinition schema's, wherever this
    // node ends up embedded in the larger document tree. The exporter requires an explicit TypeInfoResolver on the
    // options it walks; DocumentJsonOptions.Shared relies on STJ's implicit reflection default instead (attached
    // lazily on first (de)serialize, never by the exporter), so this reads a resolver-bearing COPY rather than
    // risking a mutation of the shared, possibly-already-frozen singleton.
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) {
        var options = new JsonSerializerOptions(Puck.Assets.Documents.DocumentJsonOptions.Shared) {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        // This walk never reaches WorldSchema's own TransformSchemaNode (a different JsonSerializerOptions, outside
        // WorldJsonContext's metadata), so the same converter-hidden-shape defect ApplyConverterVocabulary fixes
        // there needs its own narrow fixup here — the only converters this document family's own graph carries
        // beyond what the exporter already understands natively (a plain JsonStringEnumConverter) are the shared
        // Vector2/Vector3/Quaternion array converters.
        var exporterOptions = new JsonSchemaExporterOptions {
            TransformSchemaNode = (context, node) => DropNullableMembersFromRequired(
                context: context,
                node: ((node is JsonValue value) && value.TryGetValue<bool>(value: out var permissive) && permissive)
                    ? (context.TypeInfo.Type == typeof(Vector2)
                        ? Puck.Assets.Documents.FixedArityNumberArraySchema.Build(arity: 2)
                        : (context.TypeInfo.Type == typeof(Vector3)
                            ? Puck.Assets.Documents.FixedArityNumberArraySchema.Build(arity: 3)
                            : ((context.TypeInfo.Type == typeof(Quaternion))
                                ? Puck.Assets.Documents.FixedArityNumberArraySchema.Build(arity: 4)
                                : node)))
                    : node
            ),
        };
        var schema = options.GetJsonSchemaAsNode(type: typeof(Puck.World.Authoring.CreationDocument), exporterOptions: exporterOptions).AsObject();

        schema["$id"] = Puck.World.Authoring.CreationDocument.CurrentSchema;

        return schema;
    }
    // Document doctrine for this whole family (see CreationDocument's own remarks) declares every optional member
    // nullable and every nullable member optional; the exporter's own "required" computation only reads a
    // constructor parameter's DEFAULT VALUE, blind to that convention, so a nullable positional-record parameter
    // authored without "= null" (the common case here — the null IS the documented default) still lands in
    // "required". Corrected once, generically, against the record's own primary constructor rather than by chasing
    // every such parameter's declaration by hand.
    private static JsonNode DropNullableMembersFromRequired(JsonSchemaExporterContext context, JsonNode node) {
        if (
            (node is JsonObject obj) &&
            (obj["required"] is JsonArray required) &&
            (required.Count > 0) &&
            (context.TypeInfo.Type.GetConstructors() is [{ } constructor, ..])
        ) {
            var nullabilityContext = new NullabilityInfoContext();
            var nullableNames = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

            foreach (var parameter in constructor.GetParameters()) {
                if ((parameter.Name is { } name) && IsNullableParameter(parameter: parameter, context: nullabilityContext)) {
                    nullableNames.Add(item: name);
                }
            }

            var kept = new JsonArray();

            foreach (var entry in required) {
                if (!((entry is JsonValue entryValue) && entryValue.TryGetValue<string>(value: out var entryName) && nullableNames.Contains(item: entryName))) {
                    kept.Add(item: entry?.DeepClone());
                }
            }

            if (kept.Count > 0) {
                obj["required"] = kept;
            } else {
                obj.Remove(propertyName: "required");
            }
        }

        return node;
    }
    private static bool IsNullableParameter(ParameterInfo parameter, NullabilityInfoContext context) =>
        (Nullable.GetUnderlyingType(nullableType: parameter.ParameterType) is not null) ||
        (!parameter.ParameterType.IsValueType && (context.Create(parameterInfo: parameter).WriteState == NullabilityState.Nullable));
    /// <inheritdoc/>
    public override Puck.World.Authoring.CreationDocument? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<Puck.World.Authoring.CreationDocument>(
            reader: ref reader,
            options: Puck.Assets.Documents.DocumentJsonOptions.Shared
        );
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Puck.World.Authoring.CreationDocument value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(
            writer: writer,
            value: value,
            options: Puck.Assets.Documents.DocumentJsonOptions.Shared
        );
}

/// <summary>
/// The canonical serializer for the world document — the ouroboros round-trip.
/// The round-trip is an observed property, not an acceptance criterion.
/// <see cref="Save"/> emits a stable canonical
/// form (member order = record declaration order, invariant number formatting, no incidental whitespace drift): UTF-8
/// with no BOM, LF newlines, two-space indentation, and exactly one trailing newline at EOF, so a load→save reproduces
/// the file byte-for-byte and world files stay diffable and git-friendly.
/// </summary>
public static class WorldDefinitionSerialization {
    /// <summary>Deserializes, migrates, and validates a definition from its canonical UTF-8 JSON bytes — the inverse
    /// of <see cref="Serialize"/> for an in-memory round-trip (the replay recording's rehydration path). The bytes
    /// ride a file a user can hand-edit or truncate, so every malformed, incomplete, or invalid document arrives as
    /// one <see cref="InvalidDataException"/> the caller reports rather than an escaping parse fault.
    /// <see cref="WorldDefinitionMigrations.Apply"/> runs before validation, exactly as it does in
    /// <see cref="WorldDefinitionFileSource.TryLoad"/>, so a stale embedded document from before a field existed
    /// validates the same way a stale file does.</summary>
    /// <param name="utf8Json">The canonical UTF-8 JSON bytes.</param>
    /// <returns>The deserialized, validated definition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a valid <c>puck.world.def.v1</c> document.</exception>
    public static WorldDefinition Deserialize(byte[] utf8Json) {
        ArgumentNullException.ThrowIfNull(argument: utf8Json);

        try {
            var definition = (JsonSerializer.Deserialize(
                utf8Json: utf8Json,
                jsonTypeInfo: WorldJsonContext.Default.WorldDefinition
            )
                ?? throw new InvalidDataException(message: "the embedded world definition deserialized to null."));

            definition = WorldDefinitionMigrations.Apply(definition: definition);

            if (!WorldStateDocumentValues.TryResolve(
                definition: definition,
                reason: out var spatialReason
            )) {
                throw new InvalidOperationException(message: spatialReason);
            }

            // An embedded document already crossed a boundary that proved its cross-document claims (a boot load,
            // replay recording, identity issue, or authority projection). This storage-free rehydration cannot
            // reproduce that proof, so it validates every fact owned by the document while retaining the authored
            // claim. Live file loads remain the boundary that must resolve and prove the neighbour.
            if (!WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var reason
            )) {
                throw new InvalidOperationException(message: reason);
            }

            return definition;
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
            throw new InvalidDataException(
                message: $"the embedded world definition is not a valid {WorldDefinition.SchemaVersion} document: {exception.Message.ReplaceLineEndings(replacementText: " ")}",
                innerException: exception
            );
        }
    }
    /// <summary>Writes a definition to <paramref name="path"/> in canonical form (the <c>world.save</c> path).</summary>
    /// <param name="definition">The definition to write.</param>
    /// <param name="path">The destination file path.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    public static long Save(WorldDefinition definition, string path) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentException.ThrowIfNullOrEmpty(argument: path);

        var bytes = Serialize(definition: definition);

        File.WriteAllBytes(
            bytes: bytes,
            path: path
        );

        return bytes.LongLength;
    }
    /// <summary>Writes a definition to <paramref name="path"/>, preserving the derivation of the file it overwrites:
    /// when that file exists and names a <c>basis</c> and/or <c>imports</c>, the write is the delta whose merge over
    /// the composed basis-plus-imports stack reproduces <paramref name="definition"/> exactly
    /// (<see cref="WorldDocumentBasis.Diff"/>, with the preserved <c>basis</c>/<c>imports</c> members first), so a
    /// derived or composed world's save stays derived. The computed delta is proved by re-merging before anything is
    /// written; a delta that cannot reproduce the document — or a stack that cannot be peeked or composed —
    /// degrades to the flat <see cref="Save"/> with <paramref name="note"/> naming why. A target file that does not
    /// exist, or declares neither, is the ordinary flat save.</summary>
    /// <param name="definition">The definition to write.</param>
    /// <param name="path">The destination file path — also the file whose derivation is preserved.</param>
    /// <param name="basisPath">The absolute basis path the write preserved, or <see langword="null"/> when the
    /// target declares none or the save degraded to flat.</param>
    /// <param name="imports">The absolute import paths the write preserved, in authored order, or empty when the
    /// target declares none or the save degraded to flat.</param>
    /// <param name="note">The one-line reason a derived target degraded to a flat save, or empty.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    public static long SavePreservingBasis(WorldDefinition definition, string path, out string? basisPath, out IReadOnlyList<string> imports, out string note) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentException.ThrowIfNullOrEmpty(argument: path);

        basisPath = null;
        imports = [];
        note = string.Empty;

        if (!File.Exists(path: path)) {
            return Save(
                definition: definition,
                path: path
            );
        }

        if (!WorldDefinitionFileSource.TryPeekBasis(
            basisPath: out var peekedBasis,
            path: path,
            reason: out var peekBasisReason
        )) {
            note = $"saved flat: {peekBasisReason}";

            return Save(
                definition: definition,
                path: path
            );
        }

        if (!WorldDefinitionFileSource.TryPeekImports(
            imports: out var peekedImports,
            path: path,
            reason: out var peekImportsReason
        )) {
            note = $"saved flat: {peekImportsReason}";

            return Save(
                definition: definition,
                path: path
            );
        }

        if (
            (peekedBasis is null) &&
            (peekedImports.Count == 0)
        ) {
            return Save(
                definition: definition,
                path: path
            );
        }

        if (!WorldDefinitionFileSource.TryComposeStackTree(
            path: path,
            reason: out var composeReason,
            stack: out var stackTree
        )) {
            note = $"saved flat: {composeReason}";

            return Save(
                definition: definition,
                path: path
            );
        }

        var targetTree = ((JsonObject)JsonNode.Parse(json: System.Text.Encoding.UTF8.GetString(bytes: Serialize(definition: definition)))!);
        var delta = WorldDocumentBasis.Diff(
            basis: stackTree!,
            target: targetTree
        );

        if (
            !WorldDocumentBasis.TryMerge(
            basis: stackTree!,
            composed: out var proved,
            overlay: delta,
            reason: out var mergeReason
        ) ||
            !JsonNode.DeepEquals(
            node1: proved,
            node2: targetTree
        )
        ) {
            note = $"saved flat: the computed delta could not reproduce the document over its basis/imports stack{((mergeReason is { Length: > 0 })
                ? $" ({mergeReason})"
                : "")}.";

            return Save(
                definition: definition,
                path: path
            );
        }

        // `basis`/`imports` lead the written document so a reader knows it is composed before reading anything
        // else. The authored spelling is the target-relative path with forward slashes — portable across the
        // checked-in assets and a copied state directory alike.
        var targetDirectory = (Path.GetDirectoryName(path: Path.GetFullPath(path: path)) ?? ".");
        var output = new JsonObject();

        if (peekedBasis is { } basis) {
            output[propertyName: WorldDocumentBasis.BasisMemberName] = Path.GetRelativePath(
                path: basis,
                relativeTo: targetDirectory
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            );
        }

        if (peekedImports.Count > 0) {
            var importsArray = new JsonArray();

            foreach (var (importPath, alias) in peekedImports) {
                var entry = new JsonObject {
                    [propertyName: WorldImport.DocumentMemberName] = Path.GetRelativePath(
                        path: importPath,
                        relativeTo: targetDirectory
                    ).Replace(
                        newChar: '/',
                        oldChar: '\\'
                    ),
                };

                if (alias is not null) {
                    entry[propertyName: WorldImport.AsMemberName] = alias;
                }

                importsArray.Add(value: entry);
            }

            output[propertyName: WorldDocumentBasis.ImportsMemberName] = importsArray;
        }

        foreach (var (name, value) in delta) {
            output[propertyName: name] = value?.DeepClone();
        }

        var bytes = CanonicalJsonDocument.Serialize(node: output);

        File.WriteAllBytes(
            bytes: bytes,
            path: path
        );
        basisPath = peekedBasis;
        imports = [.. peekedImports.Select(selector: static import => import.Path)];

        return bytes.LongLength;
    }
    /// <summary>Serializes a definition to its canonical UTF-8 bytes (no BOM, LF newlines, one trailing newline).</summary>
    /// <param name="definition">The definition to serialize.</param>
    /// <returns>The canonical UTF-8 byte form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static byte[] Serialize(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return CanonicalJsonDocument.Serialize(
            jsonTypeInfo: WorldJsonContext.Default.WorldDefinition,
            value: definition
        );
    }
}
