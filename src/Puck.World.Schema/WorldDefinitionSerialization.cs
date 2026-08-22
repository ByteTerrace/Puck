using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
/// conversion (writes the exact declared member name, refuses a numeric token on read) — most at the enum's own
/// declaration via <c>[JsonConverter(typeof(StrictEnumConverter&lt;TEnum&gt;))]</c> (<see cref="BodyMotionOp"/>,
/// <see cref="IntentSource"/>, <see cref="WorldContactRequirement"/>, <see cref="ActionFact"/>,
/// <see cref="ShadowTier"/>, <see cref="WorldRenderScaleTier"/>,
/// <see cref="Puck.Abstractions.Presentation.PresentMode"/>, <see cref="Puck.World.Protocol.WorldCapability"/>); one —
/// <see cref="CommandPhase"/> (<c>Puck.Commands</c>) — lives in a project that does not reference
/// <c>Puck.Abstractions</c>, so its closed <see cref="StrictEnumConverter{TEnum}"/> instance is listed on this
/// context below instead (a source-gen context may register a closed generic converter for a type it does not own,
/// unlike the non-generic factory this replaced, which the generator refused unconditionally).
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
// The seat rig's input-policy sibling; this entry exposes the typed accessor world.view.look deserializes through.
[JsonSerializable(typeof(WorldSeatLook))]
[JsonSerializable(typeof(WorldSeatViewControl))]
[JsonSerializable(typeof(WorldViewDefaults))]
[JsonSerializable(typeof(WorldViewLayout))]
[JsonSerializable(typeof(WorldSpawnPoint[]))]
[JsonSerializable(typeof(WorldMotionDefaults))]
[JsonSerializable(typeof(WorldRenderDefaults))]
[JsonSerializable(typeof(WorldAddonRow))]
// The per-world binding overlay row the world.row.set bindingOverlays verb parses as ONE inline-JSON argument — the same wire
// shape as the document section. Its BindingProfileDocument (from Puck.Commands) is registered explicitly so source-gen
// emits its metadata for both the canonical writer and the verb accessor.
[JsonSerializable(typeof(WorldBindingOverlay))]
[JsonSerializable(typeof(BindingProfileDocument))]
// The creation/placement rows (the world.row.set creations / world.row.set placements payload shapes). The embedded
// puck.creation.v1 document rides CreationDocumentJsonConverter — its OWN canonical serializer — never this context's
// member policies (see the converter's remarks).
[JsonSerializable(typeof(WorldCreation))]
[JsonSerializable(typeof(WorldPlacement))]
// The editor/authoring policy row (the world.row.set authoring payload shape).
[JsonSerializable(typeof(WorldAuthoringDefaults))]
// The contact-solver tuning (the world.row.set collision payload shape) and the velocity-response array (a kit row's
// own, via world.row.set kits). Both are also reachable from WorldDefinition/WorldMotionModel already; these entries
// expose the typed accessors.
[JsonSerializable(typeof(WorldCollision))]
[JsonSerializable(typeof(WorldCollider))]
[JsonSerializable(typeof(MotionResponse[]))]
// The audio sections: the speaker row + tune/patch asset rows + the audio defaults (the world.row.set speakers /
// world.row.set tunes / world.row.set patches / world.row.set audio payload shapes). The embedded puck.audio.v1 / puck.synth.v1
// documents ride their families' OWN canonical serializer shape, matching CreationDocumentJsonConverter's.
[JsonSerializable(typeof(WorldSpeaker))]
// The speaker union's nested kinds collide by simple name with the camera/screen-source unions' (Fixed/Anchored and
// None/Machine); explicit TypeInfoPropertyName entries resolve the source-gen collision (SYSLIB1031).
[JsonSerializable(typeof(OverlayPredicate.Now), TypeInfoPropertyName = "OverlayPredicateNow")]
[JsonSerializable(typeof(OverlayPredicate.Recently), TypeInfoPropertyName = "OverlayPredicateRecently")]
[JsonSerializable(typeof(OverlayPredicate.All), TypeInfoPropertyName = "OverlayPredicateAll")]
[JsonSerializable(typeof(OverlayPredicate.Any), TypeInfoPropertyName = "OverlayPredicateAny")]
[JsonSerializable(typeof(OverlayPredicate.Not), TypeInfoPropertyName = "OverlayPredicateNot")]
[JsonSerializable(typeof(WorldSpeaker.Fixed), TypeInfoPropertyName = "WorldSpeakerFixed")]
[JsonSerializable(typeof(WorldSpeaker.Anchored), TypeInfoPropertyName = "WorldSpeakerAnchored")]
[JsonSerializable(typeof(WorldSpeakerSource.None), TypeInfoPropertyName = "WorldSpeakerSourceNone")]
[JsonSerializable(typeof(WorldSpeakerSource.Machine), TypeInfoPropertyName = "WorldSpeakerSourceMachine")]
[JsonSerializable(typeof(WorldTune))]
[JsonSerializable(typeof(WorldPatch))]
// puck.music.v1 / puck.judge.v1 are referenced, never embedded — plain Name/Source/Hash rows, no bridging converter.
[JsonSerializable(typeof(WorldMusicRow))]
[JsonSerializable(typeof(WorldJudgeRow))]
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
// with WorldCreation / other "Catalog"/"Creation" nouns (SYSLIB1031), following the WorldSpeaker precedent above.
[JsonSerializable(typeof(WorldLook))]
[JsonSerializable(typeof(WorldLookSource.Catalog), TypeInfoPropertyName = "WorldLookSourceCatalog")]
[JsonSerializable(typeof(WorldLookSource.Creation), TypeInfoPropertyName = "WorldLookSourceCreation")]
[JsonSerializable(typeof(WorldDistribution))]
[JsonSerializable(typeof(WorldDistributionRegion.Disc), TypeInfoPropertyName = "WorldDistributionRegionDisc")]
[JsonSerializable(typeof(WorldDistributionRegion.Points), TypeInfoPropertyName = "WorldDistributionRegionPoints")]
[JsonSerializable(typeof(WorldDistributionRegion.Lattice), TypeInfoPropertyName = "WorldDistributionRegionLattice")]
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
[JsonSerializable(typeof(WorldStateRow))]
[JsonSerializable(typeof(WorldStateSection))]
// The stochastic SOURCE family — reachable both as a document `generators` row and inline inside a site's draw
// facet. Registered on its own so WorldStateRowJsonConverter can read/write the facet through a typed accessor (the
// row converter is hand-written; its nested objects are ordinary strict-parsed STJ).
[JsonSerializable(typeof(WorldGenerator))]
// The authored-randomness facet a state row (WorldStateRow.Draw), the population section
// (WorldPopulationDefaults.CapacityDraw), or the host section (WorldHostDefaults.BackendDraw) may declare.
[JsonSerializable(typeof(WorldDraw))]
// The continuous-accumulation trait a state row's SLOT cell, or (independently) any of a keyed row's OWN cells, may
// declare — read/written by WorldStateRowJsonConverter through this typed accessor, the same "hand-written row,
// ordinary strict-parsed nested object" split the generator table above already uses, at either grain.
[JsonSerializable(typeof(WorldStateAdvance))]
// The rules section rows (the world.row.set rules payload shape). Also reachable from WorldDefinition already; this entry
// exposes the typed WorldJsonContext.Default.WorldRule accessor the verb deserializes through.
[JsonSerializable(typeof(WorldRule))]
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
    // CommandPhase (Puck.Commands) cannot carry a [JsonConverter] attribute at its own declaration without a new
    // ProjectReference to Puck.Abstractions from that leaner project; registering its CLOSED StrictEnumConverter<T>
    // instance here instead keeps the strict posture without that edge.
    Converters = new[] { typeof(Puck.Assets.Documents.Vector2JsonConverter), typeof(Puck.Assets.Documents.Vector3JsonConverter), typeof(Puck.Assets.Documents.QuaternionJsonConverter), typeof(CommandValueJsonConverter), typeof(CreationDocumentJsonConverter), typeof(AudioDocumentJsonConverter), typeof(SynthPatchDocumentJsonConverter), typeof(WorldBackendPreferenceJsonConverter), typeof(SurfaceFormatJsonConverter), typeof(GrantSubjectJsonConverter), typeof(WorldPrincipalJsonConverter), typeof(ChannelReachMaskJsonConverter), typeof(ChannelConsentMaskJsonConverter), typeof(MutationKindMaskJsonConverter), typeof(DocumentWriteMaskJsonConverter), typeof(WorldStateRowJsonConverter), typeof(WorldSafeNameJsonConverter), typeof(WorldCellNameJsonConverter), typeof(WorldDestinationDurabilityJsonConverter), typeof(WorldPortalTravelJsonConverter), typeof(WorldPortalArrivalJsonConverter), typeof(WorldDestinationScopeJsonConverter), typeof(StrictEnumConverter<CommandPhase>), typeof(StrictEnumConverter<ChannelRole>), typeof(StrictEnumConverter<BindingActivatorMode>), typeof(StrictEnumConverter<BindingEntryMode>), typeof(StrictEnumConverter<BindingWheelSpatialSelectionMode>), typeof(StrictEnumConverter<BindingWheelPlacement>), typeof(StrictEnumConverter<BindingWheelRingSelectionMode>) },
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
public sealed partial class WorldJsonContext : JsonSerializerContext {
}

internal sealed class CommandValueJsonConverter : JsonConverter<CommandValue> {
    private static float ReadComponent(ref Utf8JsonReader reader) {
        if (
            !reader.Read() ||
            (reader.TokenType != JsonTokenType.Number)
        ) {
            throw new JsonException(message: "CommandValue raw components must be finite numbers.");
        }

        var value = reader.GetSingle();

        return (float.IsFinite(f: value)
            ? value
            : throw new JsonException(message: "CommandValue raw components must be finite numbers.")
        );
    }
    private static Vector4 ReadRaw(ref Utf8JsonReader reader) {
        if (reader.TokenType != JsonTokenType.StartArray) {
            throw new JsonException(message: "CommandValue raw must be a four-element array.");
        }

        var x = ReadComponent(reader: ref reader);
        var y = ReadComponent(reader: ref reader);
        var z = ReadComponent(reader: ref reader);
        var w = ReadComponent(reader: ref reader);

        if (
            !reader.Read() ||
            (reader.TokenType != JsonTokenType.EndArray)
        ) {
            throw new JsonException(message: "CommandValue raw must contain exactly four elements.");
        }

        return new Vector4(
            w: w,
            x: x,
            y: y,
            z: z
        );
    }

    public override CommandValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.StartObject) {
            throw new JsonException(message: "a CommandValue must be an object with kind and raw members.");
        }

        CommandValueKind? kind = null;
        Vector4? raw = null;

        while (
            reader.Read() &&
            (reader.TokenType != JsonTokenType.EndObject)
        ) {
            if (reader.TokenType != JsonTokenType.PropertyName) {
                throw new JsonException(message: "a CommandValue member name was expected.");
            }

            var property = reader.GetString();

            if (!reader.Read()) {
                throw new JsonException(message: $"CommandValue member '{property}' has no value.");
            }

            switch (property) {
                case "kind":
                    var token = ((reader.TokenType == JsonTokenType.String)
                        ? reader.GetString()
                        : null
                    );

                    if (
                        (token is null) ||
                        !Enum.TryParse<CommandValueKind>(
                        ignoreCase: false,
                        result: out var parsed,
                        value: token
                    ) ||
                        !Enum.IsDefined(value: parsed)
                    ) {
                        throw new JsonException(message: $"CommandValue kind '{token}' is not declared.");
                    }
                    kind = parsed;
                    break;
                case "raw":
                    raw = ReadRaw(reader: ref reader);
                    break;
                default:
                    throw new JsonException(message: $"CommandValue contains unmapped member '{property}'.");
            }
        }

        return new CommandValue(
            Kind: (kind ?? throw new JsonException(message: "CommandValue requires member 'kind'.")),
            Raw: (raw ?? throw new JsonException(message: "CommandValue requires member 'raw'."))
        );
    }
    public override void Write(Utf8JsonWriter writer, CommandValue value, JsonSerializerOptions options) {
        writer.WriteStartObject();
        writer.WriteString(
            propertyName: "kind",
            value: value.Kind.ToString()
        );
        writer.WriteStartArray(propertyName: "raw");
        writer.WriteNumberValue(value: value.Raw.X);
        writer.WriteNumberValue(value: value.Raw.Y);
        writer.WriteNumberValue(value: value.Raw.Z);
        writer.WriteNumberValue(value: value.Raw.W);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
internal sealed class ChannelReachMaskJsonConverter : JsonConverter<ChannelReachMask> {
    public override ChannelReachMask Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(Bits: reader.GetUInt64());
    public override void Write(Utf8JsonWriter writer, ChannelReachMask value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value: value.Bits);
}
internal sealed class ChannelConsentMaskJsonConverter : JsonConverter<ChannelConsentMask> {
    public override ChannelConsentMask Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(Bits: reader.GetUInt64());
    public override void Write(Utf8JsonWriter writer, ChannelConsentMask value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value: value.Bits);
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
internal sealed class MutationKindMaskJsonConverter : JsonConverter<MutationKindMask> {
    /// <inheritdoc/>
    public override MutationKindMask Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var token = ((reader.TokenType == JsonTokenType.String)
            ? reader.GetString()
            : null
        );

        if (!MutationKindMask.TryParse(
            mask: out var mask,
            text: token,
            unknown: out var unknown
        )) {
            throw new JsonException(message: $"a verb mask is a comma-separated list of WorldMutation kind names (e.g. \"UpsertStateCell,RemoveStateCell\"); '{(string.IsNullOrEmpty(value: unknown)
                ? (token ?? "(absent)")
                : unknown)}' names no declared mutation kind.");
        }

        return mask;
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, MutationKindMask value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value: value.Describe());
}
/// <summary>
/// Reads and writes a <see cref="DocumentWriteMask"/> as the same comma-separated operation-name token
/// <c>world.grant</c>'s <c>writes:&lt;name,…&gt;</c> takes (<c>"Set,Add"</c>) — the cross-document durable-state
/// channel's own vocabulary, visibly different on the page from a
/// <see cref="MutationKindMaskJsonConverter">verb mask</see> rather than an identically-shaped bit lane.
/// </summary>
internal sealed class DocumentWriteMaskJsonConverter : JsonConverter<DocumentWriteMask> {
    /// <inheritdoc/>
    public override DocumentWriteMask Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var token = ((reader.TokenType == JsonTokenType.String)
            ? reader.GetString()
            : null
        );

        if (!DocumentWriteMask.TryParse(
            mask: out var mask,
            text: token,
            unknown: out var unknown
        )) {
            throw new JsonException(message: $"a write mask is a comma-separated list of {DocumentWriteMask.All.Describe()}; '{(string.IsNullOrEmpty(value: unknown)
                ? (token ?? "(absent)")
                : unknown)}' names no declared operation.");
        }

        return mask;
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DocumentWriteMask value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value: value.Describe());
}
/// <summary>
/// Reads and writes <see cref="WorldStateRow"/> — the cell substrate's one C# type — as one authored JSON shape (see
/// <see cref="WorldStateRow"/>'s remarks): a <c>name</c>, a <c>kind</c> (<c>int</c>|<c>fixed</c>|<c>bool</c>|
/// <c>text</c>), the optional envelope fields (<c>min</c>/<c>max</c>/<c>capacity</c>/<c>nonNegative</c>), and
/// either a bare <c>value</c> — sugar for the one cell keyed <see cref="WorldStateRow.SlotKey"/> — or a <c>cells</c>
/// array of <c>{"key","value"}</c> objects. Two optional fields, never two discriminators: a row carrying both is
/// refused by name, as is a <c>value</c> beside a <c>capacity</c> (declaring a capacity is declaring a keyed row).
/// Omitting both is a declared-but-empty row. There is no <c>$type</c> and no <c>rows</c> member — the two retired
/// spellings of the pre-collapse shape refuse as unmapped members like any other stale field.
/// <para>A fixed-kind value (<c>value</c>, <c>min</c>, <c>max</c>, or a cell's own <c>value</c>) is a decimal string
/// parsed/formatted through
/// <see cref="FixedQ4816.TryParse(string?,IFormatProvider?,out FixedQ4816)"/>/<see cref="FixedQ4816.ToString()"/> —
/// never the raw Q48.16 bit pattern; only the per-cell mutation wire and the addon ABI channel convention stay raw
/// (see <c>Puck.World.Protocol.WorldMutation.UpsertStateCell</c>'s remarks). An int-kind value is a plain JSON
/// number (a timer's non-negative floor is <see cref="WorldStateRow.NonNegative"/>, enforced at validation, never a
/// parse-time concern here). Unmapped members and a wrong-shaped value are hard parse failures, by name, matching
/// every other row in the document graph's strict posture — a custom converter opts out of the context-wide
/// <c>UnmappedMemberHandling.Disallow</c> policy, so this converter re-implements it by hand.</para>
/// </summary>
internal sealed class WorldStateRowJsonConverter : JsonConverter<WorldStateRow> {
    private const string Shape = "{\"name\":…,\"kind\":\"int\"|\"fixed\"|\"bool\"|\"text\",\"value\":… or \"cells\":[{\"key\":…,\"value\":…,\"provenance\":…,\"advance\":{\"rateNumerator\":…,\"rateDenominator\":…,\"epochTick\":…}}],\"min\":…,\"max\":…,\"capacity\":…,\"nonNegative\":…,\"gatesDrive\":…,\"evicts\":…,\"advance\":{\"rateNumerator\":…,\"rateDenominator\":…,\"epochTick\":…},\"draw\":{\"source\":… or \"generator\":{\"source\":\"markov\"|\"uniformRange\"|\"weightedNumeric\"|\"streamDraw\",…},\"timing\":\"boot\"|\"tickPeriod\"|\"event\"},\"drawCursor\":…,\"drawDecks\":[…]}";

    private static string DescribeCellKind(CellKind cellKind) => cellKind switch {
        CellKind.Int => "int",
        CellKind.Fixed => "fixed",
        CellKind.Bool => "bool",
        CellKind.Text => "text",
        _ => throw new JsonException(message: $"CellKind '{cellKind}' has no JSON token."),
    };
    private static WorldStateCell ReadCell(CellKind cellKind, WorldCellName key, JsonElement element, string context, WorldStateAdvance? advance = null, string? provenance = null) => cellKind switch {
        CellKind.Text => new WorldStateCell(
        Key: key,
        Text: RequireString(
            context: context,
            element: element
        ),
        Advance: advance,
        Provenance: provenance
    ),
        CellKind.Bool => new WorldStateCell(
        Key: key,
        Value: (RequireBool(
            context: context,
            element: element
        )
        ? 1
        : 0),
        Advance: advance,
        Provenance: provenance
    ),
        _ => new WorldStateCell(
        Key: key,
        Value: RequireNumeric(
            context: context,
            element: element,
            kind: cellKind
        ),
        Advance: advance,
        Provenance: provenance
    ),
    };
    private static List<WorldStateCell> ReadCells(string name, CellKind cellKind, JsonElement cellsElement) {
        if (cellsElement.ValueKind != JsonValueKind.Array) {
            throw new JsonException(message: $"state row '{name}'.cells must be an array of {{\"key\":…,\"value\":…}} objects.");
        }

        var cells = new List<WorldStateCell>();
        var index = 0;

        foreach (var entry in cellsElement.EnumerateArray()) {
            if (entry.ValueKind != JsonValueKind.Object) {
                throw new JsonException(message: $"state row '{name}'.cells[{index}] must be an object.");
            }

            string? key = null;
            JsonElement? cellValue = null;
            JsonElement? cellAdvance = null;
            string? provenance = null;

            foreach (var member in entry.EnumerateObject()) {
                switch (member.Name) {
                    case "key":
                        key = ((member.Value.ValueKind == JsonValueKind.String)
                            ? member.Value.GetString()
                            : null
                        );
                        break;
                    case "value":
                        cellValue = member.Value;
                        break;
                    case "advance":
                        cellAdvance = member.Value;
                        break;
                    case "provenance":
                        provenance = ((member.Value.ValueKind == JsonValueKind.String)
                            ? member.Value.GetString()
                            : null
                        );
                        break;
                    default:
                        throw new JsonException(message: $"state row '{name}'.cells[{index}] contains unmapped member '{member.Name}'.");
                }
            }

            if (string.IsNullOrEmpty(value: key)) {
                throw new JsonException(message: $"state row '{name}'.cells[{index}] requires member 'key'.");
            }
            if (!WorldCellName.TryParse(
                candidate: key,
                name: out var cellKey,
                reason: out var keyReason
            )) {
                throw new JsonException(message: $"state row '{name}'.cells[{index}].key '{key}' {keyReason}.");
            }
            if (cellValue is not { } cellValueElement) {
                throw new JsonException(message: $"state row '{name}'.cells[{index}] requires member 'value'.");
            }

            var advance = ((cellAdvance is { } cellAdvanceElement)
                ? (cellAdvanceElement.Deserialize(jsonTypeInfo: WorldJsonContext.Default.WorldStateAdvance)
                    ?? throw new JsonException(message: $"state row '{name}'.cells[{index}].advance must be an object."))
                : null
            );

            cells.Add(item: ReadCell(
                cellKind: cellKind,
                key: cellKey,
                element: cellValueElement,
                context: $"state row '{name}'.cells[{index}].value",
                advance: advance,
                provenance: provenance
            ));
            index++;
        }

        return cells;
    }
    // Engine-minted per-context dealt masks, by the source's context declaration ordinal. Refused off-shape rather
    // than coerced: these are the one part of a draw site's position the cursor cannot express.
    private static List<long> ReadDrawDecks(string name, JsonElement element) {
        if (element.ValueKind != JsonValueKind.Array) {
            throw new JsonException(message: $"state row '{name}'.drawDecks must be an array of per-context dealt masks.");
        }

        var masks = new List<long>(capacity: element.GetArrayLength());

        foreach (var entry in element.EnumerateArray()) {
            masks.Add(item: RequireInt64(
                element: entry,
                context: $"state row '{name}'.drawDecks[{masks.Count}]"
            ));
        }

        return masks;
    }
    private static bool RequireBool(JsonElement element, string context) => element.ValueKind switch {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new JsonException(message: $"{context} must be a boolean."),
    };
    private static CellKind RequireCellKind(JsonElement element, string context) {
        var token = ((element.ValueKind == JsonValueKind.String)
            ? element.GetString()
            : null
        );

        return token switch {
            "int" => CellKind.Int,
            "fixed" => CellKind.Fixed,
            "bool" => CellKind.Bool,
            "text" => CellKind.Text,
            _ => throw new JsonException(message: $"{context} '{(token ?? "(absent)")}' must be one of 'int', 'fixed', 'bool', 'text'."),
        };
    }
    // Fixed-kind values are human-authored DECIMAL TEXT, never the raw Q48.16 bit pattern — the "in passing" fix
    // this converter carries: the document, the console verb JSON, and every echo now agree on one spelling.
    private static long RequireFixed(JsonElement element, string context) {
        if (
            (element.ValueKind != JsonValueKind.String) ||
            !FixedQ4816.TryParse(
            s: element.GetString(),
            provider: CultureInfo.InvariantCulture,
            result: out var parsed
        )
        ) {
            throw new JsonException(message: $"{context} must be a decimal string parseable as FixedQ4816 (e.g. \"12.5\"), never raw bits.");
        }

        return parsed.Value;
    }
    private static int RequireInt32(JsonElement element, string context) {
        if (
            (element.ValueKind != JsonValueKind.Number) ||
            !element.TryGetInt32(value: out var parsed)
        ) {
            throw new JsonException(message: $"{context} must be a whole number.");
        }

        return parsed;
    }
    private static long RequireInt64(JsonElement element, string context) {
        if (
            (element.ValueKind != JsonValueKind.Number) ||
            !element.TryGetInt64(value: out var parsed)
        ) {
            throw new JsonException(message: $"{context} must be a whole number.");
        }

        return parsed;
    }
    private static long RequireNumeric(CellKind kind, JsonElement element, string context) => kind switch {
        CellKind.Fixed => RequireFixed(
        context: context,
        element: element
    ),
        _ => RequireInt64(
        context: context,
        element: element
    ),
    };
    private static string RequireString(JsonElement element, string context) =>
        ((element.ValueKind == JsonValueKind.String)
            ? (element.GetString() ?? string.Empty)
            : throw new JsonException(message: $"{context} must be a string.")
        );
    private static void WriteCellValue(Utf8JsonWriter writer, string propertyName, CellKind kind, WorldStateCell cell) {
        switch (kind) {
            case CellKind.Text:
                writer.WriteString(
                    propertyName: propertyName,
                    value: (cell.Text ?? string.Empty)
                );
                break;
            case CellKind.Bool:
                writer.WriteBoolean(
                    propertyName: propertyName,
                    value: (cell.Value != 0)
                );
                break;
            case CellKind.Fixed:
                writer.WriteString(
                    propertyName: propertyName,
                    value: FixedQ4816.FromRawBits(value: cell.Value).ToString()
                );
                break;
            default:
                writer.WriteNumber(
                    propertyName: propertyName,
                    value: cell.Value
                );
                break;
        }
    }
    private static void WriteOptionalNumeric(Utf8JsonWriter writer, string propertyName, CellKind kind, long? raw) {
        if (raw is not { } rawValue) {
            return;
        }

        if (kind == CellKind.Fixed) {
            writer.WriteString(
                propertyName: propertyName,
                value: FixedQ4816.FromRawBits(value: rawValue).ToString()
            );
        } else {
            writer.WriteNumber(
                propertyName: propertyName,
                value: rawValue
            );
        }
    }

    public override WorldStateRow Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.StartObject) {
            throw new JsonException(message: "a state row must be a JSON object.");
        }

        string? name = null;
        JsonElement? kind = null;
        JsonElement? value = null;
        JsonElement? cells = null;
        JsonElement? min = null;
        JsonElement? max = null;
        JsonElement? capacity = null;
        JsonElement? nonNegative = null;
        JsonElement? gatesDrive = null;
        JsonElement? evicts = null;
        JsonElement? draw = null;
        JsonElement? drawCursor = null;
        JsonElement? drawDecks = null;
        JsonElement? advance = null;

        while (
            reader.Read() &&
            (reader.TokenType != JsonTokenType.EndObject)
        ) {
            if (reader.TokenType != JsonTokenType.PropertyName) {
                throw new JsonException(message: "a state row member name was expected.");
            }

            var property = reader.GetString();

            if (!reader.Read()) {
                throw new JsonException(message: $"state row member '{property}' has no value.");
            }

            switch (property) {
                case "name":
                    name = ((reader.TokenType == JsonTokenType.String)
                        ? reader.GetString()
                        : null
                    );
                    break;
                case "kind":
                    kind = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "value":
                    value = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "cells":
                    cells = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "min":
                    min = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "max":
                    max = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "capacity":
                    capacity = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "nonNegative":
                    nonNegative = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "gatesDrive":
                    gatesDrive = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "evicts":
                    evicts = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "draw":
                    draw = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "drawCursor":
                    drawCursor = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "drawDecks":
                    drawDecks = JsonElement.ParseValue(reader: ref reader);
                    break;
                case "advance":
                    advance = JsonElement.ParseValue(reader: ref reader);
                    break;
                default:
                    throw new JsonException(message: $"state row contains unmapped member '{property}' — a state row is {Shape}.");
            }
        }

        if (string.IsNullOrEmpty(value: name)) {
            throw new JsonException(message: $"a state row requires member 'name' — a state row is {Shape}.");
        }
        if (!WorldCellName.TryParse(
            candidate: name,
            name: out var rowName,
            reason: out var nameReason
        )) {
            throw new JsonException(message: $"state row 'name' '{name}' {nameReason}.");
        }
        if (kind is not { } kindElement) {
            throw new JsonException(message: $"state row '{name}' requires member 'kind' (int|fixed|bool|text).");
        }
        if (
            (value is not null) &&
            (cells is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares both 'value' and 'cells' — 'value' IS the one-cell spelling of 'cells' (it addresses the reserved key '{WorldStateRow.SlotKey}'); author one or the other.");
        }
        if (
            (value is not null) &&
            (capacity is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'value' beside 'capacity' — declaring a capacity is declaring a keyed row, whose cells are authored under 'cells'.");
        }
        // 'draw' sits BESIDE 'value'/'cells' (the shape 'advance' already follows, never XOR against them): the cell
        // is the site's CURRENT value and 'draw' is what decides it. An authored 'value' therefore PRE-EMPTS the first
        // fill — the resolver only fills a site carrying no cell yet — which is the deliberate authored-override door.
        if (
            (advance is not null) &&
            (draw is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares both 'advance' and 'draw' — a row is a continuous accumulator or an authored-randomness draw site, never both.");
        }
        if (
            (draw is not null) &&
            (capacity is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'draw' beside 'capacity' — a draw site is a scalar (slot) row; a keyed row has no ONE cell for a draw to fill.");
        }
        if (
            (drawCursor is not null) &&
            (draw is null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'drawCursor' without 'draw' — drawCursor is engine bookkeeping for a draw site alone.");
        }
        if (
            (drawDecks is not null) &&
            (draw is null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'drawDecks' without 'draw' — drawDecks is engine bookkeeping for a draw site alone.");
        }
        if (
            (advance is not null) &&
            (capacity is not null)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'advance' beside 'capacity' — advance is a scalar (slot) row trait; a keyed row's cells have no single value to accumulate.");
        }
        // A NON-EMPTY cells array only: the canonical writer emits "cells": [] for every non-slot row, including an
        // advance row declared with no value at all, so refusing on the member's mere presence would make that
        // legitimate shape refuse itself the first time it round-tripped through the wire codec.
        if (
            (advance is not null) &&
            (cells is { ValueKind: JsonValueKind.Array } authored) &&
            (authored.GetArrayLength() > 0)
        ) {
            throw new JsonException(message: $"state row '{name}' declares 'advance' beside a non-empty 'cells' array — advance is a scalar (slot) row trait; author it with 'value' or leave the row empty until the first explicit set.");
        }

        var cellKind = RequireCellKind(
            context: $"state row '{name}'.kind",
            element: kindElement
        );

        return new WorldStateRow(
            Name: rowName,
            Kind: cellKind,
            Min: ((min is { } minElement)
            ? RequireNumeric(
                    context: $"state row '{name}'.min",
                    element: minElement,
                    kind: cellKind
                )
            : null),
            Max: ((max is { } maxElement)
            ? RequireNumeric(
                    context: $"state row '{name}'.max",
                    element: maxElement,
                    kind: cellKind
                )
            : null),
            Capacity: ((capacity is { } capacityElement)
            ? RequireInt32(
                    context: $"state row '{name}'.capacity",
                    element: capacityElement
                )
            : null),
            NonNegative: ((nonNegative is { } nonNegativeElement) && RequireBool(
                context: $"state row '{name}'.nonNegative",
                element: nonNegativeElement
            )),
            GatesDrive: ((gatesDrive is { } gatesDriveElement) && RequireBool(
                context: $"state row '{name}'.gatesDrive",
                element: gatesDriveElement
            )),
            Evicts: ((evicts is { } evictsElement) && RequireBool(
                context: $"state row '{name}'.evicts",
                element: evictsElement
            )),
            Cells: ((value is { } valueElement)
            ? [ReadCell(
                        cellKind: cellKind,
                        key: WorldStateRow.SlotKey,
                        element: valueElement,
                        context: $"state row '{name}'.value"
                    )]
            : ((cells is { } cellsElement)
                ? ReadCells(
                        cellKind: cellKind,
                        cellsElement: cellsElement,
                        name: name
                    )
                : [])),
            Advance: ((advance is { } advanceElement)
            ? (advanceElement.Deserialize(jsonTypeInfo: WorldJsonContext.Default.WorldStateAdvance)
                    ?? throw new JsonException(message: $"state row '{name}'.advance must be an object."))
            : null),
            Draw: ((draw is { } drawElement)
            ? (drawElement.Deserialize(jsonTypeInfo: WorldJsonContext.Default.WorldDraw)
                    ?? throw new JsonException(message: $"state row '{name}'.draw must be an object."))
            : null),
            DrawCursor: ((drawCursor is { } drawCursorElement)
            ? RequireInt64(
                    context: $"state row '{name}'.drawCursor",
                    element: drawCursorElement
                )
            : 0L),
            DrawDecks: ((drawDecks is { } drawDecksElement)
            ? ReadDrawDecks(
                    element: drawDecksElement,
                    name: name
                )
            : null)
        );
    }
    public override void Write(Utf8JsonWriter writer, WorldStateRow value, JsonSerializerOptions options) {
        ArgumentNullException.ThrowIfNull(argument: value);

        writer.WriteStartObject();
        writer.WriteString(
            propertyName: "name",
            value: value.Name
        );
        writer.WriteString(
            propertyName: "kind",
            value: DescribeCellKind(cellKind: value.Kind)
        );
        WriteOptionalNumeric(
            writer: writer,
            propertyName: "min",
            kind: value.Kind,
            raw: value.Min
        );
        WriteOptionalNumeric(
            writer: writer,
            propertyName: "max",
            kind: value.Kind,
            raw: value.Max
        );

        if (value.Capacity is { } declaredCapacity) {
            writer.WriteNumber(
                propertyName: "capacity",
                value: declaredCapacity
            );
        }

        if (value.NonNegative) {
            writer.WriteBoolean(
                propertyName: "nonNegative",
                value: true
            );
        }

        if (value.GatesDrive) {
            writer.WriteBoolean(
                propertyName: "gatesDrive",
                value: true
            );
        }

        if (value.Evicts) {
            writer.WriteBoolean(
                propertyName: "evicts",
                value: true
            );
        }

        // The one authored shape's two carriers: a slot writes back the SAME `value` sugar it was authored with, so a
        // load->save round-trip is byte-identical; every other shape writes its cells keyed.
        if (value.IsSlot) {
            WriteCellValue(
                writer: writer,
                propertyName: "value",
                kind: value.Kind,
                cell: value.Cells![0]
            );
        } else {
            writer.WriteStartArray(propertyName: "cells");

            foreach (var cell in (value.Cells ?? [])) {
                writer.WriteStartObject();
                writer.WriteString(
                    propertyName: "key",
                    value: cell.Key
                );
                WriteCellValue(
                    writer: writer,
                    propertyName: "value",
                    kind: value.Kind,
                    cell: cell
                );

                if (cell.Advance is { } cellAdvance) {
                    writer.WritePropertyName(propertyName: "advance");
                    JsonSerializer.Serialize(
                        writer: writer,
                        value: cellAdvance,
                        jsonTypeInfo: WorldJsonContext.Default.WorldStateAdvance
                    );
                }

                if (cell.Provenance is { } provenance) {
                    writer.WriteString(
                        propertyName: "provenance",
                        value: provenance
                    );
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        // Advance and draw are mutually exclusive (see WorldStateAdvance's remarks), so write order between them never
        // collides in practice. The facet goes last, after the drawn value's own cell, and its bookkeeping last of all
        // — so a saved draw site reads top-down as "what it is, then where it is".
        if (value.Advance is { } advance) {
            writer.WritePropertyName(propertyName: "advance");
            JsonSerializer.Serialize(
                writer: writer,
                value: advance,
                jsonTypeInfo: WorldJsonContext.Default.WorldStateAdvance
            );
        }

        if (value.Draw is { } draw) {
            writer.WritePropertyName(propertyName: "draw");
            JsonSerializer.Serialize(
                writer: writer,
                value: draw,
                jsonTypeInfo: WorldJsonContext.Default.WorldDraw
            );
        }

        if (value.DrawCursor != 0L) {
            writer.WriteNumber(
                propertyName: "drawCursor",
                value: value.DrawCursor
            );
        }

        if (value.DrawDecks is { Count: > 0 } drawDecks) {
            writer.WritePropertyName(propertyName: "drawDecks");
            writer.WriteStartArray();

            foreach (var mask in drawDecks) {
                writer.WriteNumberValue(value: mask);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
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
/// Reads and writes a <see cref="WorldBackendPreference"/> as an explicit lowercase token (<c>auto</c> / <c>directx</c>
/// / <c>vulkan</c>) rather than the context's camelCase enum policy, which would emit <c>directX</c> — a spelling no one
/// types and gratuitously divergent from World's token style. The <c>--backend</c> boot flag, the
/// <c>host.backendDraw</c> resolver and the <c>world.host</c> read-back all speak the same map, so nothing that reads
/// or prints a backend disagrees with the document.
/// </summary>
internal sealed class WorldBackendPreferenceJsonConverter : JsonConverter<WorldBackendPreference>, IJsonSchemaStringConverter {
    /// <inheritdoc/>
    public IReadOnlyList<string>? SchemaTokens { get; } = [WorldHostTokens.BackendAuto, WorldHostTokens.BackendDirectX, WorldHostTokens.BackendVulkan];

    /// <inheritdoc/>
    public override WorldBackendPreference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        return (WorldHostTokens.ParseBackend(token: reader.GetString())
            ?? throw new JsonException(message: $"backend '{reader.GetString()}' must be '{WorldHostTokens.BackendAuto}', '{WorldHostTokens.BackendDirectX}', or '{WorldHostTokens.BackendVulkan}'."));
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, WorldBackendPreference value, JsonSerializerOptions options) {
        writer.WriteStringValue(value: WorldHostTokens.BackendToken(backend: value));
    }
}
/// <summary>
/// Reads and writes the two authorable <see cref="SurfaceFormat"/> values as explicit tokens (<c>r8g8b8a8</c> /
/// <c>b8g8r8a8</c>) rather than the context's camelCase enum policy, which would emit the unreadable <c>r8G8B8A8Unorm</c>.
/// <see cref="SurfaceFormat.Unknown"/> and any other member are rejected at read (the validator also rejects
/// <see cref="SurfaceFormat.Unknown"/> — the hole the Demo's string list could not express). The
/// <c>world.host</c> read-back prints through the same map.
/// </summary>
internal sealed class SurfaceFormatJsonConverter : JsonConverter<SurfaceFormat>, IJsonSchemaStringConverter {
    // Deliberately NOT SurfaceFormat.Unknown, and deliberately NOT every enum member Write's own fallback arm could
    // in principle produce — this list is the READ-accepted set (see IJsonSchemaStringConverter's own remarks),
    // and Read below refuses Unknown by name exactly like the validator does.
    /// <inheritdoc/>
    public IReadOnlyList<string>? SchemaTokens { get; } = [WorldHostTokens.SurfaceFormatRgba, WorldHostTokens.SurfaceFormatBgra];

    /// <inheritdoc/>
    public override SurfaceFormat Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        return (WorldHostTokens.ParseSurfaceFormat(token: reader.GetString())
            ?? throw new JsonException(message: $"surfaceFormat '{reader.GetString()}' must be '{WorldHostTokens.SurfaceFormatRgba}' or '{WorldHostTokens.SurfaceFormatBgra}'."));
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, SurfaceFormat value, JsonSerializerOptions options) {
        writer.WriteStringValue(value: WorldHostTokens.SurfaceFormatToken(format: value));
    }
}
/// <summary>
/// Reads and writes a <see cref="WorldDestinationDurability"/> as the lowercase token
/// <c>Puck.World.WorldInstanceHost</c>'s <c>world.transfer</c> verb already speaks (<c>ephemeral</c> /
/// <c>persisted</c>) rather than the context's camelCase enum policy, so an authored destination row and the console
/// grammar its diegetic trigger drives never disagree on spelling. See <see cref="WorldDestinationTokens"/>.
/// </summary>
internal sealed class WorldDestinationDurabilityJsonConverter : JsonConverter<WorldDestinationDurability>, IJsonSchemaStringConverter {
    /// <inheritdoc/>
    public IReadOnlyList<string>? SchemaTokens { get; } = [WorldDestinationTokens.DurabilityEphemeral, WorldDestinationTokens.DurabilityPersisted];

    /// <inheritdoc/>
    public override WorldDestinationDurability Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        return (WorldDestinationTokens.ParseDurability(token: reader.GetString())
            ?? throw new JsonException(message: $"destination durability '{reader.GetString()}' must be '{WorldDestinationTokens.DurabilityEphemeral}' or '{WorldDestinationTokens.DurabilityPersisted}'."));
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, WorldDestinationDurability value, JsonSerializerOptions options) {
        writer.WriteStringValue(value: WorldDestinationTokens.DurabilityToken(durability: value));
    }
}
/// <summary>
/// Reads and writes a <see cref="WorldPortalTravel"/> as the lowercase token <c>world.transfer</c>'s <c>party</c>
/// slot argument already speaks (<c>party</c> / <c>body</c>) rather than the context's camelCase enum policy. See
/// <see cref="WorldDestinationTokens"/>.
/// </summary>
internal sealed class WorldPortalTravelJsonConverter : JsonConverter<WorldPortalTravel>, IJsonSchemaStringConverter {
    /// <inheritdoc/>
    public IReadOnlyList<string>? SchemaTokens { get; } = [WorldDestinationTokens.TravelParty, WorldDestinationTokens.TravelBody];

    /// <inheritdoc/>
    public override WorldPortalTravel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        return (WorldDestinationTokens.ParseTravel(token: reader.GetString())
            ?? throw new JsonException(message: $"portal travel '{reader.GetString()}' must be '{WorldDestinationTokens.TravelParty}' or '{WorldDestinationTokens.TravelBody}'."));
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, WorldPortalTravel value, JsonSerializerOptions options) {
        writer.WriteStringValue(value: WorldDestinationTokens.TravelToken(travel: value));
    }
}
/// <summary>
/// Reads and writes a <see cref="WorldPortalArrival"/> as the lowercase token <c>spawn</c>/<c>mapped</c> rather than
/// the context's camelCase enum policy, mirroring <see cref="WorldPortalTravelJsonConverter"/>. See
/// <see cref="WorldDestinationTokens"/>.
/// </summary>
internal sealed class WorldPortalArrivalJsonConverter : JsonConverter<WorldPortalArrival>, IJsonSchemaStringConverter {
    /// <inheritdoc/>
    public IReadOnlyList<string>? SchemaTokens { get; } = [WorldDestinationTokens.ArrivalSpawn, WorldDestinationTokens.ArrivalMapped];

    /// <inheritdoc/>
    public override WorldPortalArrival Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        return (WorldDestinationTokens.ParseArrival(token: reader.GetString())
            ?? throw new JsonException(message: $"portal arrival '{reader.GetString()}' must be '{WorldDestinationTokens.ArrivalSpawn}' or '{WorldDestinationTokens.ArrivalMapped}'."));
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, WorldPortalArrival value, JsonSerializerOptions options) {
        writer.WriteStringValue(value: WorldDestinationTokens.ArrivalToken(arrival: value));
    }
}
/// <summary>
/// Reads and writes a <see cref="WorldDestinationScope"/> as the lowercase token docs/vision.md's "Durability,
/// scope and generation" names (<c>user</c> / <c>group</c> / <c>global</c>) rather than the context's camelCase enum
/// policy, mirroring <see cref="WorldDestinationDurabilityJsonConverter"/>. See <see cref="WorldDestinationTokens"/>.
/// </summary>
internal sealed class WorldDestinationScopeJsonConverter : JsonConverter<WorldDestinationScope>, IJsonSchemaStringConverter {
    /// <inheritdoc/>
    public IReadOnlyList<string>? SchemaTokens { get; } = [WorldDestinationTokens.ScopeUser, WorldDestinationTokens.ScopeGroup, WorldDestinationTokens.ScopeGlobal];

    /// <inheritdoc/>
    public override WorldDestinationScope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        return (WorldDestinationTokens.ParseScope(token: reader.GetString())
            ?? throw new JsonException(message: $"destination scope '{reader.GetString()}' must be '{WorldDestinationTokens.ScopeUser}', '{WorldDestinationTokens.ScopeGroup}', or '{WorldDestinationTokens.ScopeGlobal}'."));
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, WorldDestinationScope value, JsonSerializerOptions options) {
        writer.WriteStringValue(value: WorldDestinationTokens.ScopeToken(scope: value));
    }
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
internal sealed class GrantSubjectJsonConverter : JsonConverter<GrantSubject> {
    /// <inheritdoc/>
    public override GrantSubject Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var token = reader.GetString();

        if (
            (token is null) ||
            !GrantSubject.TryParse(
            subject: out var subject,
            token: token
        )
        ) {
            throw new JsonException(message: $"grant subject '{token}' must be 'all', 'body:<n>', 'screen:<n>', 'section:<name>', 'state:<name>', 'region:<name>', 'seat:<n>', 'creation:<id>', or 'placement:<id>'.");
        }

        return subject;
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, GrantSubject value, JsonSerializerOptions options) {
        writer.WriteStringValue(value: value.Describe());
    }
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
internal sealed class WorldPrincipalJsonConverter : JsonConverter<WorldPrincipal> {
    /// <inheritdoc/>
    public override WorldPrincipal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var token = reader.GetString();

        if (
            (token is null) ||
            !WorldPrincipal.TryParse(
            principal: out var principal,
            token: token
        )
        ) {
            throw new JsonException(message: $"principal '{token}' must be 'seat1'..'seat4', 'console', 'addon:<name>', 'peer:<n>:<generation>', or 'document:<id>'.");
        }

        return principal;
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, WorldPrincipal value, JsonSerializerOptions options) {
        writer.WriteStringValue(value: value.Describe());
    }
}
/// <summary>
/// Bridges an embedded <see cref="Puck.Forge.Authoring.CreationDocument"/> (a <see cref="WorldCreation.Document"/>) through
/// the creation contract's own serializer shape (<see cref="Puck.Assets.Documents.DocumentJsonOptions.Shared"/> — member
/// order, string enums, and the Vector2/Vector3/Quaternion array converters) instead of this context's
/// policies, so the inline-canonical embed carries exactly the member vocabulary
/// <see cref="Puck.Forge.Authoring.CreationCanonicalizer"/> hashes. Formatting (indent/newlines) rides the outer canonical
/// writer, which is deterministic — the ouroboros round-trip covers the composition.
/// </summary>
internal sealed class CreationDocumentJsonConverter : JsonConverter<Puck.Forge.Authoring.CreationDocument> {
    /// <inheritdoc/>
    public override Puck.Forge.Authoring.CreationDocument? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<Puck.Forge.Authoring.CreationDocument>(
            reader: ref reader,
            options: Puck.Assets.Documents.DocumentJsonOptions.Shared
        );
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Puck.Forge.Authoring.CreationDocument value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(
            writer: writer,
            value: value,
            options: Puck.Assets.Documents.DocumentJsonOptions.Shared
        );
}
/// <summary>
/// Bridges an embedded <see cref="Puck.Forge.Authoring.AudioDocument"/> (a <see cref="WorldTune.Document"/>) through the
/// audio contract's own serializer shape (<see cref="Puck.Assets.Documents.DocumentJsonOptions.Shared"/>) instead of this
/// context's policies, so the inline-canonical embed carries exactly the member vocabulary
/// <see cref="Puck.Forge.Authoring.AudioCanonicalizer"/> hashes, matching <see cref="CreationDocumentJsonConverter"/>'s approach.
/// </summary>
internal sealed class AudioDocumentJsonConverter : JsonConverter<Puck.Forge.Authoring.AudioDocument> {
    /// <inheritdoc/>
    public override Puck.Forge.Authoring.AudioDocument? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<Puck.Forge.Authoring.AudioDocument>(
            reader: ref reader,
            options: Puck.Assets.Documents.DocumentJsonOptions.Shared
        );
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Puck.Forge.Authoring.AudioDocument value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(
            writer: writer,
            value: value,
            options: Puck.Assets.Documents.DocumentJsonOptions.Shared
        );
}
/// <summary>
/// Bridges an embedded <see cref="Puck.Forge.Authoring.SynthPatchDocument"/> (a <see cref="WorldPatch.Document"/>) through
/// the synth contract's own serializer shape — see <see cref="AudioDocumentJsonConverter"/>.
/// </summary>
internal sealed class SynthPatchDocumentJsonConverter : JsonConverter<Puck.Forge.Authoring.SynthPatchDocument> {
    /// <inheritdoc/>
    public override Puck.Forge.Authoring.SynthPatchDocument? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<Puck.Forge.Authoring.SynthPatchDocument>(
            reader: ref reader,
            options: Puck.Assets.Documents.DocumentJsonOptions.Shared
        );
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Puck.Forge.Authoring.SynthPatchDocument value, JsonSerializerOptions options) =>
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
    /// when that file exists and names a <c>basis</c>, the write is the delta whose merge over the composed basis
    /// chain reproduces <paramref name="definition"/> exactly (<see cref="WorldDocumentBasis.Diff"/>, with its
    /// <c>basis</c> member first), so a derived world's save stays a derived world. The computed delta is proved by
    /// re-merging before anything is written; a delta that cannot reproduce the document — or a basis that cannot be
    /// peeked or composed — degrades to the flat <see cref="Save"/> with <paramref name="note"/> naming why. A
    /// target file that does not exist, or declares no basis, is the ordinary flat save.</summary>
    /// <param name="definition">The definition to write.</param>
    /// <param name="path">The destination file path — also the file whose derivation is preserved.</param>
    /// <param name="basisPath">The absolute basis path the write preserved, or <see langword="null"/> for a flat
    /// save.</param>
    /// <param name="note">The one-line reason a derived target degraded to a flat save, or empty.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    public static long SavePreservingBasis(WorldDefinition definition, string path, out string? basisPath, out string note) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentException.ThrowIfNullOrEmpty(argument: path);

        basisPath = null;
        note = string.Empty;

        if (!File.Exists(path: path)) {
            return Save(
                definition: definition,
                path: path
            );
        }

        if (!WorldDefinitionFileSource.TryPeekBasis(
            basisPath: out var peeked,
            path: path,
            reason: out var peekReason
        )) {
            note = $"saved flat: {peekReason}";

            return Save(
                definition: definition,
                path: path
            );
        }

        if (peeked is null) {
            return Save(
                definition: definition,
                path: path
            );
        }

        if (!WorldDefinitionFileSource.TryComposeDocumentTree(
            path: peeked,
            reason: out var composeReason,
            tree: out var basisTree
        )) {
            note = $"saved flat: {composeReason}";

            return Save(
                definition: definition,
                path: path
            );
        }

        var targetTree = ((JsonObject)JsonNode.Parse(json: System.Text.Encoding.UTF8.GetString(bytes: Serialize(definition: definition)))!);
        var delta = WorldDocumentBasis.Diff(
            basis: basisTree!,
            target: targetTree
        );

        if (
            !WorldDocumentBasis.TryMerge(
            basis: basisTree!,
            composed: out var proved,
            overlay: delta,
            reason: out var mergeReason
        ) ||
            !JsonNode.DeepEquals(
            node1: proved,
            node2: targetTree
        )
        ) {
            note = $"saved flat: the computed delta could not reproduce the document over {peeked}{((mergeReason is { Length: > 0 })
                ? $" ({mergeReason})"
                : "")}.";

            return Save(
                definition: definition,
                path: path
            );
        }

        // `basis` leads the written document so a reader knows it is a delta before reading anything else. The
        // authored spelling is the target-relative path with forward slashes — portable across the checked-in
        // assets and a copied state directory alike.
        var targetDirectory = (Path.GetDirectoryName(path: Path.GetFullPath(path: path)) ?? ".");
        var relative = Path.GetRelativePath(
            path: peeked,
            relativeTo: targetDirectory
        ).Replace(
            newChar: '/',
            oldChar: '\\'
        );
        var output = new JsonObject {
            [propertyName: WorldDocumentBasis.BasisMemberName] = relative,
        };

        foreach (var (name, value) in delta) {
            output[propertyName: name] = value?.DeepClone();
        }

        var bytes = CanonicalJsonDocument.Serialize(node: output);

        File.WriteAllBytes(
            bytes: bytes,
            path: path
        );
        basisPath = peeked;

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
