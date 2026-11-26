using Puck.Assets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.World.Protocol;

namespace Puck.World;
/// <summary>A recipient's side of a <see cref="WorldProjectionFeed"/>: the projection it holds, which a whole
/// delivery replaces and a delta merges into, and the definition hydrated from it.</summary>
/// <param name="content">The recipient content cache, or the shared store.</param>
/// <param name="fetch">The authorized fetch door for objects absent from the cache.</param>
/// <param name="basis">Reads the document the recipient holds for the sender's world, which a replica's manifest is
/// assembled over: its own copy of the world, then each document the stream delivered; <see langword="null"/> assembles
/// every manifest whole.</param>
/// <param name="seed">An optional initial local definition whose literal prototypes may satisfy matching pins.</param>
public sealed class WorldProjectionHold(ContentAddressedStore? content = null, Func<ContentPin, byte[]?>? fetch = null, Func<WorldDefinition?>? basis = null, WorldDefinition? seed = null) {
    private WorldProjectionDocument? m_projection;
    private JsonObject? m_tree;
    private Dictionary<string, int>? m_observationOrdinals;

    /// <summary>Assembles the document a replica's manifest names over the document the recipient holds
    /// (<see cref="WorldDocumentManifest.TryAssemble"/>).</summary>
    /// <param name="manifest">The manifest's bytes.</param>
    /// <param name="definition">The assembled, validated document on success.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the manifest assembled into a document this process may present.</returns>
    public bool TryHoldReplica(ReadOnlySpan<byte> manifest, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldDefinition? definition, out string reason) =>
        WorldDocumentManifest.TryAssemble(
            basis: (basis?.Invoke() ?? seed),
            definition: out definition,
            held: assembled => WorldContentIndex.Fetch(definition: assembled, fetch: fetch, held: content),
            manifest: manifest,
            reason: out reason,
            resolve: WorldDocumentManifest.Resolve(content: content, fetch: fetch)
        );

    /// <summary>Gets the definition hydrated from the projection held, or <see langword="null"/> before the first
    /// whole projection.</summary>
    public WorldDefinition? Definition { get; private set; }
    /// <summary>Gets the projection held, or <see langword="null"/> before the first whole projection.</summary>
    public WorldProjectionDocument? Projection => m_projection;

    /// <summary>Holds a whole projection and hydrates it.</summary>
    /// <param name="utf8Json">The projection's bytes.</param>
    /// <param name="definition">The hydrated definition on success.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the projection parsed and hydrated; the hold is unchanged otherwise.</returns>
    public bool TryHold(ReadOnlySpan<byte> utf8Json, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldDefinition? definition, out string reason) {
        definition = null;
        var prior = (Definition ?? basis?.Invoke());

        if (
            !WorldProjection.TryDeserialize(
            projection: out var projection,
            reason: out reason,
            utf8Json: utf8Json
        ) ||
            !WorldProjection.TryToDefinition(
            content: content,
            definition: out definition,
            fallbackKnown: seed,
            fetch: fetch,
            known: prior,
            projection: projection!,
            reason: out reason
        )
        ) {
            definition = null;

            return false;
        }

        m_projection = projection;
        m_tree = WorldProjectionDelta.Tree(utf8Json: utf8Json);
        m_observationOrdinals = IndexObservations(observations: projection!.Observations);
        Definition = definition;

        return true;
    }

    private static Dictionary<string, int>? IndexObservations(IReadOnlyList<WorldObservedRow>? observations) {
        if ((observations is null) || (observations.Count == 0)) { return null; }
        var map = new Dictionary<string, int>(capacity: observations.Count, comparer: StringComparer.Ordinal);
        for (var index = 0; (index < observations.Count); index++) {
            map[observations[index].Name] = index;
        }
        return map;
    }

    private bool TryPatchObservationsInPlace(WorldProjectionDocument heldProjection, JsonObject delta, out IReadOnlyList<WorldObservedRow>? patchedObservations, out List<(int Ordinal, JsonNode MergedNode)>? treeUpdates, out bool observationsValuesOnly, out JsonObject remainingDelta) {
        patchedObservations = null;
        treeUpdates = null;
        observationsValuesOnly = true;
        remainingDelta = delta;

        if (!delta.TryGetPropertyValue(propertyName: "observations", jsonNode: out var obsPatchNode) ||
            (obsPatchNode is not JsonArray patchRows) || (patchRows.Count == 0) ||
            WorldDocumentBasis.IsReplaceMarker(node: patchRows[0]) ||
            (m_tree?["observations"] is not JsonArray heldObsTree) ||
            (heldProjection.Observations is not { Count: > 0 } heldObsList) ||
            (heldObsTree.Count != heldObsList.Count)) {
            return false;
        }
        var indexMap = (m_observationOrdinals ??= IndexObservations(observations: heldObsList));
        if ((indexMap is null) || (indexMap.Count != heldObsList.Count)) {
            return false;
        }

        var ordinals = new int[patchRows.Count];
        var basisSlice = new JsonArray();
        var overlaySlice = new JsonArray();

        for (var i = 0; (i < patchRows.Count); i++) {
            if ((patchRows[i] is not JsonObject patchObj) ||
                patchObj.ContainsKey(propertyName: WorldDocumentBasis.DropMemberName) ||
                patchObj.ContainsKey(propertyName: WorldDocumentBasis.BeforeMemberName) ||
                (patchObj["name"] is not JsonValue nameVal) ||
                !nameVal.TryGetValue<string>(value: out var rowName) ||
                !indexMap.TryGetValue(key: rowName, value: out var ordinal)) {
                return false;
            }
            for (var prior = 0; (prior < i); prior++) {
                if (ordinals[prior] == ordinal) { return false; }
            }
            ordinals[i] = ordinal;
            basisSlice.Add(value: heldObsTree[ordinal]?.DeepClone());
            overlaySlice.Add(value: patchObj.DeepClone());
        }

        if (!WorldDocumentBasis.TryMerge(
            basis: new JsonObject { ["observations"] = basisSlice },
            overlay: new JsonObject { ["observations"] = overlaySlice },
            composed: out var sliceComposed,
            reason: out _
        ) || (sliceComposed?["observations"] is not JsonArray mergedSlice) || (mergedSlice.Count != ordinals.Length)) {
            return false;
        }

        WorldObservedRow[]? deserialized;
        try {
            deserialized = JsonSerializer.Deserialize(
                utf8Json: CanonicalJsonDocument.SerializeCompact(node: mergedSlice),
                jsonTypeInfo: WorldJsonContext.Untrusted.WorldObservedRowArray
            );
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
            return false;
        }
        if ((deserialized is null) || (deserialized.Length != ordinals.Length)) {
            return false;
        }

        var updated = new WorldObservedRow[heldObsList.Count];
        for (var i = 0; (i < heldObsList.Count); i++) { updated[i] = heldObsList[i]; }
        var updates = new List<(int Ordinal, JsonNode MergedNode)>(capacity: ordinals.Length);
        for (var i = 0; (i < ordinals.Length); i++) {
            var ordinal = ordinals[i];
            if (observationsValuesOnly && !WorldProjectionDelta.SameObservationRowShape(left: heldObsList[ordinal], right: deserialized[i])) {
                observationsValuesOnly = false;
            }
            updated[ordinal] = deserialized[i];
            updates.Add(item: (ordinal, WorldProjectionDelta.Tree(value: deserialized[i], jsonTypeInfo: WorldJsonContext.Default.For<WorldObservedRow>())));
        }

        var reduced = new JsonObject();
        foreach (var (member, value) in delta) {
            if (!string.Equals(a: member, b: "observations", comparisonType: StringComparison.Ordinal)) {
                reduced[member] = value?.DeepClone();
            }
        }

        patchedObservations = updated;
        treeUpdates = updates;
        remainingDelta = reduced;
        return true;
    }
    /// <summary>Merges a delta over the projection held and hydrates the result. A delta of the timeline alone replaces
    /// the held definition's timeline and hydrates nothing else.</summary>
    /// <param name="utf8Json">The delta's bytes.</param>
    /// <param name="definition">The hydrated definition on success.</param>
    /// <param name="valuesOnly">Whether the delta changes only values (<see cref="WorldProjectionDelivery.ValuesOnly"/>).</param>
    /// <param name="timelineOnly">Whether the delta changes the timeline alone
    /// (<see cref="WorldProjectionDelivery.TimelineOnly"/>).</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the delta merged and the result hydrated; the hold is unchanged
    /// otherwise.</returns>
    public bool TryApply(ReadOnlySpan<byte> utf8Json, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldDefinition? definition, out bool valuesOnly, out bool timelineOnly, out string reason) {
        definition = null;
        valuesOnly = false;
        timelineOnly = false;

        if ((m_tree is null) || (m_projection is not { } heldProjection) || (Definition is not { } held)) {
            reason = "a projection delta arrived before any whole projection it could merge over.";

            return false;
        }

        JsonObject delta;

        try {
            delta = WorldProjectionDelta.Tree(utf8Json: utf8Json);
        } catch (JsonException exception) {
            reason = $"the projection delta is not JSON: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        var fastObs = TryPatchObservationsInPlace(
            heldProjection: heldProjection,
            delta: delta,
            patchedObservations: out var patchedObservations,
            treeUpdates: out var obsTreeUpdates,
            observationsValuesOnly: out var obsValuesOnly,
            remainingDelta: out var effectiveDelta
        );

        if (!WorldProjectionDelta.TryMerge(
            delta: effectiveDelta,
            held: m_tree,
            merged: out var merged,
            reason: out reason
        )) {
            return false;
        }

        valuesOnly = (fastObs
            ? (obsValuesOnly && WorldProjectionDelta.HasOnlyValueMembers(delta: delta))
            : WorldProjectionDelta.IsValuesOnly(
                delta: delta,
                previousObservations: m_tree["observations"],
                nextObservations: (merged.TryGetValue(key: "observations", value: out var observations) ? observations : m_tree["observations"])
            ));
        timelineOnly = WorldProjectionDelta.IsTimelineOnly(delta: delta);
        WorldProjectionDocument? nextProjection;

        if (timelineOnly) {
            WorldTimelineSection? timeline;
            var provenance = heldProjection.Provenance;

            try {
                timeline = merged[WorldProjectionDelta.Timeline]?.Deserialize(jsonTypeInfo: WorldJsonContext.Untrusted.WorldTimelineSection);
                if (merged.TryGetValue(key: "provenance", value: out var provenanceNode)) {
                    provenance = provenanceNode?.Deserialize(jsonTypeInfo: WorldJsonContext.Untrusted.For<WorldProjectionProvenance>()) ??
                        throw new JsonException(message: "projection member 'provenance' cannot be null.");
                }
            } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
                reason = $"the projection delta's timeline is not a timeline: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

                return false;
            }
            if (provenance.Tier != WorldDisclosureTier.Presentation) {
                reason = $"projection provenance names tier '{provenance.Tier}'; only '{WorldDisclosureTier.Presentation}' composes a projection document.";

                return false;
            }

            nextProjection = (heldProjection with { Provenance = provenance, Timeline = timeline });
            definition = (held with { TimelineRaw = timeline });

            if (WorldProjection.Uncarried(definition: definition) is { } uncarried) {
                definition = null;
                reason = uncarried;

                return false;
            }
        } else {
            if (!TryMergeProjection(
                current: (fastObs ? (heldProjection with { Observations = patchedObservations }) : heldProjection),
                delta: effectiveDelta,
                heldTree: m_tree,
                merged: merged,
                next: out nextProjection,
                reason: out reason
            ) || !WorldProjection.TryToDefinition(
                content: content,
                definition: out definition,
                fallbackKnown: (basis?.Invoke() ?? seed),
                fetch: fetch,
                known: held,
                previousProjection: heldProjection,
                projection: nextProjection!,
                reason: out reason
            )) {
                definition = null;

                return false;
            }
        }

        foreach (var (member, node) in merged) {
            if (node is null) {
                _ = m_tree.Remove(propertyName: member);
            } else {
                m_tree[member] = node.DeepClone();
            }
        }
        if (fastObs) {
            var heldObsArray = (JsonArray)m_tree["observations"]!;
            foreach (var (ordinal, mergedNode) in obsTreeUpdates!) {
                heldObsArray[ordinal] = mergedNode.DeepClone();
            }
        } else if (merged.ContainsKey(key: "observations")) {
            m_observationOrdinals = IndexObservations(observations: nextProjection!.Observations);
        }

        m_projection = nextProjection;
        Definition = definition;
        reason = string.Empty;

        return true;
    }

    // Deserializes only the top-level members the delta touched and shares unchanged rows inside model sections
    // (such as placements), keeping every untouched member of the held projection by reference.
    private static bool TryMergeProjection(WorldProjectionDocument current, JsonObject heldTree, JsonObject delta, Dictionary<string, JsonNode?> merged, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldProjectionDocument? next, out string reason) {
        next = current;
        Dictionary<string, JsonElement>? extensions = null;

        T ReadRequired<T>(string member, JsonNode? node) {
            if (node is null) {
                throw new JsonException(message: $"projection member '{member}' cannot be null.");
            }
            var value = JsonSerializer.Deserialize(
                utf8Json: CanonicalJsonDocument.SerializeCompact(node: node),
                jsonTypeInfo: (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)WorldJsonContext.Untrusted.Options.GetTypeInfo(type: typeof(T))
            );

            return (value ?? throw new JsonException(message: $"projection member '{member}' deserialized to null."));
        }

        T? ReadOptional<T>(JsonNode? node) => ((node is null)
            ? default
            : JsonSerializer.Deserialize(
                utf8Json: CanonicalJsonDocument.SerializeCompact(node: node),
                jsonTypeInfo: (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)WorldJsonContext.Untrusted.Options.GetTypeInfo(type: typeof(T))
            ));

        T ShareSection<T>(string member, T value, T? basis) where T : class => (
            (basis is not null) &&
            heldTree.TryGetPropertyValue(propertyName: member, jsonNode: out var basisNode) &&
            (basisNode is not null) &&
            merged.TryGetValue(key: member, value: out var valueNode) &&
            (valueNode is not null) &&
            delta.TryGetPropertyValue(propertyName: member, jsonNode: out var patchNode) &&
            (patchNode is not null)
                ? WorldDocumentWalker.Share(basis: basis, basisTree: basisNode, patch: patchNode, value: value, valueTree: valueNode)
                : value
        );

        try {
            foreach (var (member, node) in merged) {
                switch (member) {
                    case "schema":
                        next = (next with { Schema = ReadRequired<string>(member: member, node: node) });
                        break;
                    case "provenance":
                        next = (next with { Provenance = ReadRequired<WorldProjectionProvenance>(member: member, node: node) });
                        break;
                    case "motion":
                        next = (next with { Motion = ReadRequired<WorldMotionDefaults>(member: member, node: node) });
                        break;
                    case "spawnPoints":
                        next = (next with { SpawnPoints = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldSpawnPoint>>(member: member, node: node), basis: next.SpawnPoints) });
                        break;
                    case "render":
                        next = (next with { Render = ShareSection(member: member, value: ReadRequired<WorldRenderDefaults>(member: member, node: node), basis: next.Render) });
                        break;
                    case "screens":
                        next = (next with { Screens = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldScreen>>(member: member, node: node), basis: next.Screens) });
                        break;
                    case "screenMaterials":
                        next = (next with { ScreenMaterials = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldScreenMaterial>>(member: member, node: node), basis: next.ScreenMaterials) });
                        break;
                    case "cameras":
                        next = (next with { Cameras = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldCamera>>(member: member, node: node), basis: next.Cameras) });
                        break;
                    case "population":
                        next = (next with { Population = ReadRequired<WorldBodiesDefaults>(member: member, node: node) });
                        break;
                    case "playerDefaults":
                        next = (next with { PlayerDefaults = ShareSection(member: member, value: ReadRequired<WorldPlayerDefaults>(member: member, node: node), basis: next.PlayerDefaults) });
                        break;
                    case "channels":
                        next = (next with { Channels = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldChannel>>(member: member, node: node), basis: next.Channels) });
                        break;
                    case "kits":
                        next = (next with { Kits = ReadRequired<IReadOnlyList<WorldProjectedKit>>(member: member, node: node) });
                        break;
                    case "defaultSeatKit":
                        next = (next with { DefaultSeatKit = ReadRequired<string>(member: member, node: node) });
                        break;
                    case "assignment":
                        next = (next with { Assignment = ShareSection(member: member, value: ReadRequired<WorldRowAssignment>(member: member, node: node), basis: next.Assignment) });
                        break;
                    case "bindingOverlays":
                        next = (next with { BindingOverlays = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldBindingOverlay>>(member: member, node: node), basis: next.BindingOverlays) });
                        break;
                    case "prototypes":
                        next = (next with { Creations = ReadRequired<IReadOnlyList<WorldPrototypeReference>>(member: member, node: node) });
                        break;
                    case "placements":
                        next = (next with { Placements = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldPlacement>>(member: member, node: node), basis: next.Placements) });
                        break;
                    case "speakers":
                        next = (next with { Speakers = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldSpeaker>>(member: member, node: node), basis: next.Speakers) });
                        break;
                    case "tunes":
                        next = (next with { Tunes = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldTune>>(member: member, node: node), basis: next.Tunes) });
                        break;
                    case "patches":
                        next = (next with { Patches = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldPatch>>(member: member, node: node), basis: next.Patches) });
                        break;
                    case "audio":
                        next = (next with { Audio = ShareSection(member: member, value: ReadRequired<WorldAudioDefaults>(member: member, node: node), basis: next.Audio) });
                        break;
                    case "collision":
                        next = (next with { Collision = ShareSection(member: member, value: ReadRequired<WorldCollision>(member: member, node: node), basis: next.Collision) });
                        break;
                    case "views": {
                            var views = ReadOptional<WorldViewDefaults>(node: node);
                            next = (next with { Views = ((views is null) ? null : ShareSection(member: member, value: views, basis: next.Views)) });
                            break;
                        }
                    case "looks":
                        next = (next with { Looks = ShareSection(member: member, value: ReadRequired<IReadOnlyList<WorldLook>>(member: member, node: node), basis: next.Looks) });
                        break;
                    case "lookAssignment":
                        next = (next with { LookAssignment = ShareSection(member: member, value: ReadRequired<WorldRowAssignment>(member: member, node: node), basis: next.LookAssignment) });
                        break;
                    case "dynamics":
                        next = (next with { Dynamics = ShareSection(member: member, value: ReadRequired<IReadOnlyList<DynamicsRow>>(member: member, node: node), basis: next.Dynamics) });
                        break;
                    case "hud":
                        next = (next with { Hud = ShareSection(member: member, value: ReadRequired<WorldHudSection>(member: member, node: node), basis: next.Hud) });
                        break;
                    case "fieldRegions":
                        next = (next with { FieldRegions = ReadOptional<IReadOnlyList<WorldFieldsSection>>(node: node) });
                        break;
                    case "simulation": {
                            var simulation = ReadOptional<WorldSimulationDefaults>(node: node);
                            next = (next with { Simulation = ((simulation is null) ? null : ShareSection(member: member, value: simulation, basis: next.Simulation)) });
                            break;
                        }
                    case "interactions": {
                            var interactions = ReadOptional<WorldInteractionsSection>(node: node);
                            next = (next with { Interactions = ((interactions is null) ? null : ShareSection(member: member, value: interactions, basis: next.Interactions)) });
                            break;
                        }
                    case "references": {
                            var references = ReadOptional<IReadOnlyList<WorldReference>>(node: node);
                            next = (next with { References = ((references is null) ? null : ShareSection(member: member, value: references, basis: next.References)) });
                            break;
                        }
                    case "destinations": {
                            var destinations = ReadOptional<IReadOnlyList<WorldDestination>>(node: node);
                            next = (next with { Destinations = ((destinations is null) ? null : ShareSection(member: member, value: destinations, basis: next.Destinations)) });
                            break;
                        }
                    case "adjacencies": {
                            var adjacencies = ReadOptional<IReadOnlyList<WorldAdjacency>>(node: node);
                            next = (next with { Adjacencies = ((adjacencies is null) ? null : ShareSection(member: member, value: adjacencies, basis: next.Adjacencies)) });
                            break;
                        }
                    case "metadata":
                        next = (next with { Metadata = ReadOptional<WorldProjectedMetadata>(node: node) });
                        break;
                    case "observations":
                        next = (next with { Observations = ReadOptional<IReadOnlyList<WorldObservedRow>>(node: node) });
                        break;
                    case "spaces":
                        next = (next with { Spaces = ReadOptional<IReadOnlyList<StateSpace>>(node: node) });
                        break;
                    case "timeline": {
                            var timeline = ReadOptional<WorldTimelineSection>(node: node);
                            next = (next with { Timeline = ((timeline is null) ? null : ShareSection(member: member, value: timeline, basis: next.Timeline)) });
                            break;
                        }
                    case "music": {
                            var music = ReadOptional<IReadOnlyList<WorldMusicRow>>(node: node);
                            next = (next with { Music = ((music is null) ? null : ShareSection(member: member, value: music, basis: next.Music)) });
                            break;
                        }
                    case "haptics": {
                            var haptics = ReadOptional<IReadOnlyList<WorldHapticRow>>(node: node);
                            next = (next with { Haptics = ((haptics is null) ? null : ShareSection(member: member, value: haptics, basis: next.Haptics)) });
                            break;
                        }
                    case "keyLights": {
                            var keyLights = ReadOptional<WorldKeyLightSection>(node: node);
                            next = (next with { KeyLights = ((keyLights is null) ? null : ShareSection(member: member, value: keyLights, basis: next.KeyLights)) });
                            break;
                        }
                    case "highlights": {
                            var highlights = ReadOptional<IReadOnlyList<WorldHighlightRow>>(node: node);
                            next = (next with { Highlights = ((highlights is null) ? null : ShareSection(member: member, value: highlights, basis: next.Highlights)) });
                            break;
                        }
                    case "shades":
                        next = (next with { Shades = ReadOptional<IReadOnlyDictionary<string, Puck.SignedDistance.SdfShadeDefinition>>(node: node) });
                        break;
                    case "text": {
                            var text = ReadOptional<Puck.Text.TextFontCatalogDefinition>(node: node);
                            next = (next with { Text = ((text is null) ? null : ShareSection(member: member, value: text, basis: next.Text)) });
                            break;
                        }
                    case "machines":
                        next = (next with { Machines = ReadOptional<IReadOnlyList<WorldProjectedMachine>>(node: node) });
                        break;
                    case "navigation": {
                            var navigation = ReadOptional<WorldNavigationSection>(node: node);
                            next = (next with { Navigation = ((navigation is null) ? null : ShareSection(member: member, value: navigation, basis: next.Navigation)) });
                            break;
                        }
                    case "inputModes":
                        next = (next with { InputModes = ReadOptional<WorldProjectedInputModes>(node: node) });
                        break;
                    case "seatModes": {
                            var seatModes = ReadOptional<IReadOnlyList<WorldSeatModeFamily>>(node: node);
                            next = (next with { SeatModes = ((seatModes is null) ? null : ShareSection(member: member, value: seatModes, basis: next.SeatModes)) });
                            break;
                        }
                    case "inputEventsPerStep":
                        next = (next with { InputEventsPerStep = ((node is null) ? null : JsonSerializer.Deserialize(utf8Json: CanonicalJsonDocument.SerializeCompact(node: node), jsonTypeInfo: WorldJsonContext.Untrusted.For<int>())) });
                        break;
                    case "bodyState": {
                            var bodyState = ReadOptional<IReadOnlyList<WorldProjectedBodyRegister>>(node: node);
                            next = (next with { BodyState = ((bodyState is null) ? null : ShareSection(member: member, value: bodyState, basis: next.BodyState)) });
                            break;
                        }
                    case "identityState": {
                            var identityState = ReadOptional<IReadOnlyList<WorldProjectedBodyRegister>>(node: node);
                            next = (next with { IdentityState = ((identityState is null) ? null : ShareSection(member: member, value: identityState, basis: next.IdentityState)) });
                            break;
                        }
                    default:
                        if (!DocumentExtensionsPolicy.IsReservedKey(key: member)) {
                            reason = $"projection contains unrecognized top-level member '{member}'.";
                            next = null;

                            return false;
                        }
                        extensions ??= ((next.Extensions is { } heldExtensions)
                            ? new Dictionary<string, JsonElement>(dictionary: heldExtensions, comparer: StringComparer.Ordinal)
                            : new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal));
                        if (node is null) {
                            _ = extensions.Remove(key: member);
                        } else {
                            extensions[member] = JsonSerializer.Deserialize<JsonElement>(utf8Json: CanonicalJsonDocument.SerializeCompact(node: node));
                        }
                        break;
                }
            }
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
            reason = $"the projection is not a valid {WorldProjectionDocument.SchemaVersion} document: {exception.Message.ReplaceLineEndings(replacementText: " ")}";
            next = null;

            return false;
        }

        if (extensions is not null) {
            next = (next with { Extensions = ((extensions.Count == 0) ? null : extensions) });
        }
        if (!string.Equals(a: next.Schema, b: WorldProjectionDocument.SchemaVersion, comparisonType: StringComparison.Ordinal)) {
            reason = $"projection schema '{next.Schema}' is not {WorldProjectionDocument.SchemaVersion}.";
            next = null;

            return false;
        }
        if (next.Provenance.Tier != WorldDisclosureTier.Presentation) {
            reason = $"projection provenance names tier '{next.Provenance.Tier}'; only '{WorldDisclosureTier.Presentation}' composes a projection document.";
            next = null;

            return false;
        }

        reason = string.Empty;

        return true;
    }
}
/// <summary>The member delta a projection travels in: the one document delta (<see cref="WorldDocumentBasis"/>)
/// applied to a projection's top-level members.</summary>
public static class WorldProjectionDelta {
    /// <summary>The projection member a timeline travels in.</summary>
    public const string Timeline = "timeline";

    // The members whose change moves values only: what a recipient re-reads without rebuilding anything shaped.
    private static readonly HashSet<string> ValueMembers = new(comparer: StringComparer.Ordinal) { Timeline, "observations", "provenance" };

    /// <summary>Returns a serialized projection's tree.</summary>
    /// <param name="utf8Json">The projection's bytes.</param>
    /// <returns>The tree.</returns>
    /// <exception cref="JsonException">The bytes are not a JSON object.</exception>
    public static JsonObject Tree(ReadOnlySpan<byte> utf8Json) => CanonicalJsonDocument.ParseObject(utf8Json);
    /// <summary>Returns a typed member's canonical tree, as a projection carries it.</summary>
    public static JsonNode Tree<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo) where T : class =>
        JsonNode.Parse(utf8Json: CanonicalJsonDocument.SerializeCompact(jsonTypeInfo: jsonTypeInfo, value: value))!;
    /// <summary>Returns a timeline's canonical tree, as a projection carries it.</summary>
    /// <param name="timeline">The timeline.</param>
    /// <returns>The tree.</returns>
    public static JsonNode Tree(WorldTimelineSection timeline) => Tree(
        value: timeline,
        jsonTypeInfo: WorldJsonContext.Default.WorldTimelineSection
    );
    /// <summary>Returns the delta whose merge over <paramref name="held"/> reproduces <paramref name="next"/>, checked
    /// by merging it.</summary>
    /// <param name="held">The tree the recipient holds.</param>
    /// <param name="next">The tree it is owed.</param>
    /// <returns>The delta, empty when the trees are equal, or <see langword="null"/> when no delta reproduces
    /// <paramref name="next"/>, which is then owed whole.</returns>
    public static JsonObject? Diff(JsonObject held, JsonObject next) {
        ArgumentNullException.ThrowIfNull(argument: held);
        ArgumentNullException.ThrowIfNull(argument: next);

        var delta = WorldDocumentBasis.Diff(
            basis: held,
            target: next
        );

        if (delta.Count == 0) {
            return delta;
        }

        if (!TryMerge(
            delta: delta,
            held: held,
            merged: out var merged,
            reason: out _
        )) {
            return null;
        }

        foreach (var (member, node) in merged) {
            if (!JsonNode.DeepEquals(
                node1: node,
                node2: (next.TryGetPropertyValue(jsonNode: out var expected, propertyName: member) ? expected : null)
            ) || ((node is null) && next.ContainsKey(propertyName: member))) {
                return null;
            }
        }

        return delta;
    }
    /// <summary>Merges a delta over the members of a held tree it names, without touching the held tree.</summary>
    /// <param name="held">The tree held.</param>
    /// <param name="delta">The delta.</param>
    /// <param name="merged">Each member the delta names, merged; a member the delta removes maps to
    /// <see langword="null"/>.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when every member merged.</returns>
    public static bool TryMerge(JsonObject held, JsonObject delta, out Dictionary<string, JsonNode?> merged, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: held);
        ArgumentNullException.ThrowIfNull(argument: delta);

        merged = new Dictionary<string, JsonNode?>(comparer: StringComparer.Ordinal);

        foreach (var (member, change) in delta) {
            var basis = new JsonObject();

            if (held.TryGetPropertyValue(jsonNode: out var current, propertyName: member)) {
                basis[member] = current?.DeepClone();
            }

            if (!WorldDocumentBasis.TryMerge(
                basis: basis,
                composed: out var composed,
                overlay: new JsonObject { [member] = change?.DeepClone() },
                reason: out reason
            )) {
                return false;
            }

            merged[member] = (composed!.TryGetPropertyValue(jsonNode: out var node, propertyName: member)
                ? node?.DeepClone()
                : null);
        }

        reason = string.Empty;

        return true;
    }
    /// <summary>Returns whether a delta changes the timeline and nothing a recipient hydrates: the timeline, and at most
    /// the provenance that numbers the delivery.</summary>
    /// <param name="delta">The delta.</param>
    /// <returns><see langword="true"/> when the delta names the timeline and nothing but the provenance beside
    /// it.</returns>
    public static bool IsTimelineOnly(JsonObject delta) {
        ArgumentNullException.ThrowIfNull(argument: delta);

        return delta.ContainsKey(propertyName: Timeline) &&
            ((delta.Count == 1) || ((delta.Count == 2) && delta.ContainsKey(propertyName: "provenance")));
    }
    /// <summary>Returns whether every member a delta names is a value-only projection member.</summary>
    public static bool HasOnlyValueMembers(JsonObject delta) {
        ArgumentNullException.ThrowIfNull(argument: delta);

        foreach (var (member, _) in delta) {
            if (!ValueMembers.Contains(item: member)) {
                return false;
            }
        }
        return true;
    }
    /// <summary>Returns whether two typed observation rows have the same disclosed shape and cell keys.</summary>
    public static bool SameObservationRowShape(WorldObservedRow left, WorldObservedRow right) {
        if (!string.Equals(a: left.Name, b: right.Name, comparisonType: StringComparison.Ordinal) ||
            (left.Kind != right.Kind) ||
            !string.Equals(a: left.Space, b: right.Space, comparisonType: StringComparison.Ordinal) ||
            (left.Cells.Count != right.Cells.Count)) {
            return false;
        }
        for (var index = 0; (index < left.Cells.Count); index++) {
            var leftCell = left.Cells[index];
            var rightCell = right.Cells[index];
            if ((leftCell.Hidden != rightCell.Hidden) ||
                !string.Equals(a: leftCell.Key, b: rightCell.Key, comparisonType: StringComparison.Ordinal)) {
                return false;
            }
        }
        return true;
    }
    /// <summary>Returns whether a delta changes only values, preserving the observed row ordinals and cell layout.</summary>
    /// <param name="delta">The delta.</param>
    /// <param name="previousObservations">The observations held before the delta.</param>
    /// <param name="nextObservations">The observations after merging the delta.</param>
    /// <returns><see langword="true"/> when the recipient can refresh its existing bindings.</returns>
    public static bool IsValuesOnly(JsonObject delta, JsonNode? previousObservations, JsonNode? nextObservations) {
        if (!HasOnlyValueMembers(delta: delta)) {
            return false;
        }

        return (!delta.ContainsKey(propertyName: "observations") || JsonNode.DeepEquals(
            node1: ObservationShape(observations: previousObservations),
            node2: ObservationShape(observations: nextObservations)
        ));
    }

    private static JsonArray? ObservationShape(JsonNode? observations) {
        if (observations is not JsonArray rows) {
            return null;
        }

        var shape = new JsonArray();

        foreach (var row in rows) {
            if (row is not JsonObject observed) {
                continue; // Hydration refuses malformed rows.
            }

            var keys = new JsonArray();

            if (observed["cells"] is JsonArray cells) {
                foreach (var cell in cells) {
                    if (cell is JsonObject value) {
                        keys.Add(item: new JsonObject {
                            ["key"] = value["key"]?.DeepClone(),
                            ["hidden"] = value["hidden"]?.DeepClone(),
                        });
                    }
                }
            }

            shape.Add(item: new JsonObject {
                ["name"] = observed["name"]?.DeepClone(),
                ["kind"] = observed["kind"]?.DeepClone(),
                ["space"] = observed["space"]?.DeepClone(),
                ["cells"] = keys,
            });
        }

        return shape;
    }
}
