using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.World;

/// <summary>
/// Layered world-document composition — the mechanism behind the document root's <c>basis</c> member (see
/// <see cref="WorldDefinition.Basis"/>). A world file naming a basis is a delta: at load,
/// <see cref="WorldDefinitionFileSource"/> resolves the basis chain (each file's <c>basis</c> against its own
/// directory), merges each derived tree over its basis with <see cref="TryMerge"/>, strips the consumed <c>basis</c>
/// member, and hands the composed tree to the one strict parse → migrate → validate gate every flat document already
/// crosses. Composition is raw-JSON-tree work, deliberately: a partial basis document (a template authoring only the
/// shared sections) cannot parse as a <see cref="WorldDefinition"/> on its own — the context's required-member
/// posture refuses it — so the model only ever sees the finished composition.
/// </summary>
/// <remarks>
/// <para>Merge rules (derived over basis, derived wins):</para>
/// <list type="bullet">
/// <item><description>Objects merge member-wise, recursively: a member the derived tree authors replaces or refines
/// the basis's; a member it omits inherits; an authored <c>null</c> removes the inherited member (how a derived
/// world clears an optional section its basis authors — e.g. <c>"gravity": null</c> derives a zero-gravity world
/// from a gravity-carrying basis).</description></item>
/// <item><description>A <c>$type</c>-discriminated object whose derived discriminator differs from the basis's
/// replaces wholesale — the arms of a union share no members, so merging across them could only compose a row the
/// strict parse refuses.</description></item>
/// <item><description>A row list whose rows all carry the document's settled identity vocabulary — the first of
/// <c>id</c> / <c>name</c> / <c>index</c> present on every row of both lists — merges by key: a derived row refines
/// its same-key basis row in place (basis order is preserved — list order is meaning for spawn seats and rules), a
/// new key appends in authored order, and a tombstone row <c>{"&lt;key&gt;": …, "$drop": true}</c> removes the named
/// basis row. A tombstone naming no basis row refuses by name.</description></item>
/// <item><description>A list that cannot key (scalar elements, mixed shapes, or duplicate keys on either side)
/// replaces wholesale, and a keyed list can opt into the same with a leading marker row
/// <c>{"$replace": true}</c>.</description></item>
/// </list>
/// <para><c>$drop</c>/<c>$replace</c> are compose-time vocabulary only: <see cref="TryMerge"/> consumes them, so
/// they never reach the parser, and one appearing anywhere a keyed list is not being merged refuses by name rather
/// than flowing through to an unmapped-member parse failure.</para>
/// <para><c>basis</c> here is the document a delta layers over — unrelated to the coordinate basis the validator's
/// <c>TryCardinalBasis</c>/<c>WorldFaceCatalog</c> geometry speaks of.</para>
/// <para><see cref="Diff"/> is the inverse: given a basis tree and a target tree it computes the delta whose
/// <see cref="TryMerge"/> over the basis reproduces the target — the derivation-preserving <c>world.save</c> path.
/// Callers must verify the round trip (<see cref="TryMerge"/> + <see cref="JsonNode.DeepEquals"/>) before trusting a
/// computed delta, and fall back to a flat save when it fails.</para>
/// <para><c>imports</c> is the fan-in half of composition beside <c>basis</c>'s single-parent chain: an ordered list
/// of fragment paths (each resolved against the importing file's own directory, exactly like <c>basis</c>), letting
/// several documents each own one disjoint slice of a world (one per game in the garden) rather than forcing every
/// slice through one basis chain. Composition order is the basis chain first, then each import fully resolved (its
/// own <c>basis</c>/<c>imports</c> included) and folded left to right in authored list order via
/// <see cref="TryMergeImports"/>, then the file's own body last — each step an ordinary <see cref="TryMerge"/>
/// (basis, then the folded import layer, then the file's own body, each refining the one before). Both consumed
/// members are stripped before the strict parse, exactly like <c>basis</c>. Collision policy: within the basis
/// chain a derived row REFINES its same-key basis row (unchanged, the rule above) — imports are the opposite,
/// SIBLINGS with no priority order between them, so a same-key row, a same-name object member, or a same list both
/// authored by two imports is a refusal by name (naming the two importing fragments) UNLESS the importing file's own
/// body also declares that same path — the explicit resolution <see cref="TryMergeImports"/>'s remarks describe. Two
/// imports agreeing on a value (typically because they share a common ancestor somewhere in their own basis/import
/// graphs) never collide, so a shared basis diamond composes fine. Cycles across basis and import edges together
/// refuse by name, and both share the one <see cref="MaxChainDepth"/> ceiling.</para>
/// </remarks>
public static class WorldDocumentBasis {
    /// <summary>The document root member naming the basis document this file layers over.</summary>
    public const string BasisMemberName = "basis";
    /// <summary>The tombstone member: a row carrying <c>"$drop": true</c> beside its identity key removes the
    /// same-key basis row during a keyed-list merge.</summary>
    public const string DropMemberName = "$drop";
    /// <summary>The document root member naming, in order, the fragment documents this file imports — the fan-in
    /// half of composition beside the single-parent <see cref="BasisMemberName"/> chain (see the type remarks).</summary>
    public const string ImportsMemberName = "imports";
    /// <summary>The basis-chain depth ceiling, refusals included — a recursion bound, not an authoring target; a
    /// chain this deep is unreadable long before it is unloadable. Shared by the import graph: any path through
    /// basis and import edges together is capped at this many documents.</summary>
    public const int MaxChainDepth = 8;
    /// <summary>The wholesale-replacement marker: a keyed list whose FIRST element is exactly
    /// <c>{"$replace": true}</c> replaces the basis list outright instead of merging by key.</summary>
    public const string ReplaceMemberName = "$replace";

    // The settled row-identity vocabulary, in precedence order (see the type remarks and documents.md's "Identity
    // conventions"): stable string ids, then names, then a state cell's own key, then the screen family's position
    // index.
    private static readonly string[] RowKeyPrecedence = ["id", "name", "key", "index"];

    private static string DescribeKey(JsonNode? value) {
        return (value?.ToJsonString() ?? "(absent)");
    }
    private static JsonNode DiffList(JsonArray basis, JsonArray target) {
        if (!TryFindRowKey(
            ambiguity: out _,
            basis: basis,
            key: out var key,
            overlay: target
        )) {
            return ((JsonArray)target.DeepClone());
        }

        // A keyed reconstruction replays surviving basis rows in basis order, then appends new keys in target order.
        // When the target's own order is anything else (rows were reordered), no keyed delta can express it — fall
        // back to the wholesale marker form.
        var reconstructed = new List<JsonNode?>();

        foreach (var basisRow in basis) {
            var basisKey = ((JsonObject)basisRow!)[propertyName: key];

            if (IndexOfRowKey(
                key: key,
                list: target,
                value: basisKey
            ) >= 0) {
                reconstructed.Add(item: basisKey);
            }
        }

        foreach (var targetRow in target) {
            var targetKey = ((JsonObject)targetRow!)[propertyName: key];

            if (IndexOfRowKey(
                key: key,
                list: basis,
                value: targetKey
            ) < 0) {
                reconstructed.Add(item: targetKey);
            }
        }

        var orderHolds = (reconstructed.Count == target.Count);

        for (var index = 0; (orderHolds && (index < target.Count)); index++) {
            orderHolds = JsonNode.DeepEquals(
                node1: reconstructed[index: index],
                node2: ((JsonObject)target[index: index]!)[propertyName: key]
            );
        }

        if (!orderHolds) {
            var wholesale = new JsonArray { new JsonObject { [propertyName: ReplaceMemberName] = true } };

            foreach (var row in target) {
                wholesale.Add(value: row?.DeepClone());
            }

            return wholesale;
        }

        var delta = new JsonArray();

        foreach (var targetRow in target) {
            var targetObject = ((JsonObject)targetRow!);
            var targetKey = targetObject[propertyName: key];
            var basisIndex = IndexOfRowKey(
                key: key,
                list: basis,
                value: targetKey
            );

            if (basisIndex < 0) {
                delta.Add(value: targetObject.DeepClone());

                continue;
            }

            var basisObject = ((JsonObject)basis[index: basisIndex]!);

            if (JsonNode.DeepEquals(
                node1: basisObject,
                node2: targetObject
            )) {
                continue;
            }

            if (TypeDiscriminatorsDiffer(
                basis: basisObject,
                overlay: targetObject
            )) {
                delta.Add(value: targetObject.DeepClone());

                continue;
            }

            var rowDelta = DiffObject(
                basis: basisObject,
                target: targetObject
            );

            rowDelta[propertyName: key] = targetKey?.DeepClone();
            delta.Add(value: rowDelta);
        }

        foreach (var basisRow in basis) {
            var basisObject = ((JsonObject)basisRow!);
            var basisKey = basisObject[propertyName: key];

            if (IndexOfRowKey(
                key: key,
                list: target,
                value: basisKey
            ) < 0) {
                delta.Add(value: new JsonObject {
                    [propertyName: key] = basisKey?.DeepClone(),
                    [propertyName: DropMemberName] = true,
                });
            }
        }

        return delta;
    }
    private static JsonObject DiffObject(JsonObject basis, JsonObject target) {
        var delta = new JsonObject();

        foreach (var (name, value) in target) {
            var basisHas = basis.TryGetPropertyValue(
                jsonNode: out var basisValue,
                propertyName: name
            );

            if (!basisHas) {
                delta[propertyName: name] = value?.DeepClone();

                continue;
            }

            if (JsonNode.DeepEquals(
                node1: basisValue,
                node2: value
            )) {
                continue;
            }

            if (
                (basisValue is JsonObject basisObject) &&
                (value is JsonObject targetObject) &&
                !TypeDiscriminatorsDiffer(
                basis: basisObject,
                overlay: targetObject
            )
            ) {
                delta[propertyName: name] = DiffObject(
                    basis: basisObject,
                    target: targetObject
                );

                continue;
            }

            if (
                (basisValue is JsonArray basisList) &&
                (value is JsonArray targetList)
            ) {
                delta[propertyName: name] = DiffList(
                    basis: basisList,
                    target: targetList
                );

                continue;
            }

            delta[propertyName: name] = value?.DeepClone();
        }

        foreach (var (name, _) in basis) {
            if (!target.ContainsKey(propertyName: name)) {
                delta[propertyName: name] = null;
            }
        }

        return delta;
    }
    private static bool HasDuplicateKey(JsonArray list, string key, out string duplicate) {
        for (var outer = 0; (outer < list.Count); outer++) {
            var outerKey = ((JsonObject)list[index: outer]!)[propertyName: key];

            for (var inner = (outer + 1); (inner < list.Count); inner++) {
                if (JsonNode.DeepEquals(
                    node1: outerKey,
                    node2: ((JsonObject)list[index: inner]!)[propertyName: key]
                )) {
                    duplicate = $"{key} {DescribeKey(value: outerKey)}";

                    return true;
                }
            }
        }

        duplicate = string.Empty;

        return false;
    }
    private static int IndexOfRowKey(JsonArray list, string key, JsonNode? value) {
        for (var index = 0; (index < list.Count); index++) {
            if (
                (list[index: index] is JsonObject row) &&
                JsonNode.DeepEquals(
                node1: row[propertyName: key],
                node2: value
            )
            ) {
                return index;
            }
        }

        return -1;
    }
    private static bool IsReplaceMarker(JsonNode? node) {
        return (
            (node is JsonObject marker) &&
            (marker.Count == 1) &&
            IsTrue(node: marker[propertyName: ReplaceMemberName])
        );
    }
    // Backing-agnostic: an authored file's values are JsonElement-backed while Diff-built markers are CLR-bool-backed,
    // and GetValue<JsonElement> throws on the latter.
    private static bool IsTrue(JsonNode? node) {
        return (
            (node is JsonValue value) &&
            value.TryGetValue<bool>(value: out var parsed) &&
            parsed
        );
    }
    private static bool ListCarriesKey(JsonArray list, string key) {
        if (list.Count == 0) {
            return false;
        }

        foreach (var row in list) {
            if (row is not JsonObject rowObject) {
                return false;
            }

            if (rowObject[propertyName: key] is not JsonValue keyValue) {
                return false;
            }

            if (
                !keyValue.TryGetValue<string>(value: out _) &&
                !keyValue.TryGetValue<double>(value: out _)
            ) {
                return false;
            }
        }

        return true;
    }
    private static JsonArray MergeList(JsonArray basis, JsonArray overlay, string path) {
        if (
            (overlay.Count > 0) &&
            IsReplaceMarker(node: overlay[index: 0])
        ) {
            var replaced = new JsonArray();

            for (var index = 1; (index < overlay.Count); index++) {
                RefuseRowMarkers(
                    node: overlay[index: index],
                    path: $"{path}[{index}]",
                    context: "a '$replace'-marked list replaces wholesale; its rows carry no further compose vocabulary"
                );
                replaced.Add(value: overlay[index: index]?.DeepClone());
            }

            return replaced;
        }

        if (!TryFindRowKey(
            ambiguity: out var ambiguity,
            basis: basis,
            key: out var key,
            overlay: overlay
        )) {
            if (ambiguity.Length > 0) {
                throw new JsonException(message: $"{path} {ambiguity}");
            }

            for (var index = 0; (index < overlay.Count); index++) {
                RefuseRowMarkers(
                    node: overlay[index: index],
                    path: $"{path}[{index}]",
                    context: "the list's rows share no identity key (id/name/index on every row of both lists), so it replaces wholesale"
                );
            }

            return ((JsonArray)overlay.DeepClone());
        }

        var merged = new JsonArray();
        var consumed = new bool[overlay.Count];

        foreach (var basisRow in basis) {
            var basisObject = ((JsonObject)basisRow!);
            var basisKey = basisObject[propertyName: key];
            var overlayIndex = IndexOfRowKey(
                key: key,
                list: overlay,
                value: basisKey
            );

            if (overlayIndex < 0) {
                merged.Add(value: basisObject.DeepClone());

                continue;
            }

            consumed[overlayIndex] = true;

            var overlayRow = ((JsonObject)overlay[index: overlayIndex]!);

            if (overlayRow.ContainsKey(propertyName: DropMemberName)) {
                RequireTombstoneShape(
                    row: overlayRow,
                    key: key,
                    path: $"{path}[{key}={DescribeKey(value: basisKey)}]"
                );

                continue;
            }

            if (TypeDiscriminatorsDiffer(
                basis: basisObject,
                overlay: overlayRow
            )) {
                merged.Add(value: overlayRow.DeepClone());
            } else {
                var mergedRow = ((JsonObject)basisObject.DeepClone());

                MergeObject(
                    target: mergedRow,
                    overlay: overlayRow,
                    path: $"{path}[{key}={DescribeKey(value: basisKey)}]"
                );
                merged.Add(value: mergedRow);
            }
        }

        for (var index = 0; (index < overlay.Count); index++) {
            if (consumed[index]) {
                continue;
            }

            var appended = ((JsonObject)overlay[index: index]!);

            if (appended.ContainsKey(propertyName: DropMemberName)) {
                throw new JsonException(message: $"{path}[{index}] is a tombstone for {key} {DescribeKey(value: appended[propertyName: key])}, which names no basis row — a tombstone that drops nothing is stale authoring; remove it.");
            }

            RefuseRowMarkers(
                context: "an appended row is new content, not compose vocabulary",
                node: appended,
                path: $"{path}[{index}]"
            );
            merged.Add(value: appended.DeepClone());
        }

        return merged;
    }
    private static void MergeObject(JsonObject target, JsonObject overlay, string path) {
        foreach (var (name, value) in overlay) {
            if (
                string.Equals(
                a: name,
                b: DropMemberName,
                comparisonType: StringComparison.Ordinal
            ) ||
                string.Equals(
                a: name,
                b: ReplaceMemberName,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                throw new JsonException(message: $"{path} carries '{name}' — it is compose-time row vocabulary, meaningful only on a row inside a keyed list ('$drop') or as a list's leading marker ('$replace').");
            }

            if (value is null) {
                target.Remove(propertyName: name);

                continue;
            }

            var memberPath = $"{path}.{name}";

            if (
                (target[propertyName: name] is JsonObject basisObject) &&
                (value is JsonObject overlayObject)
            ) {
                if (TypeDiscriminatorsDiffer(
                    basis: basisObject,
                    overlay: overlayObject
                )) {
                    target[propertyName: name] = overlayObject.DeepClone();
                } else {
                    MergeObject(
                        overlay: overlayObject,
                        path: memberPath,
                        target: basisObject
                    );
                }

                continue;
            }

            if (
                (target[propertyName: name] is JsonArray basisList) &&
                (value is JsonArray overlayList)
            ) {
                target[propertyName: name] = MergeList(
                    basis: basisList,
                    overlay: overlayList,
                    path: memberPath
                );

                continue;
            }

            target[propertyName: name] = value.DeepClone();
        }
    }
    private static void RefuseRowMarkers(JsonNode? node, string path, string context) {
        if (node is not JsonObject row) {
            return;
        }

        if (row.ContainsKey(propertyName: DropMemberName)) {
            throw new JsonException(message: $"{path} carries '{DropMemberName}', but {context}.");
        }

        if (row.ContainsKey(propertyName: ReplaceMemberName)) {
            throw new JsonException(message: $"{path} carries '{ReplaceMemberName}', but {context}.");
        }
    }
    private static void RequireTombstoneShape(JsonObject row, string key, string path) {
        if (!IsTrue(node: row[propertyName: DropMemberName])) {
            throw new JsonException(message: $"{path} carries '{DropMemberName}' with a value other than true — a tombstone is exactly {{\"{key}\": …, \"{DropMemberName}\": true}}.");
        }

        foreach (var (name, _) in row) {
            if (
                !string.Equals(
                a: name,
                b: key,
                comparisonType: StringComparison.Ordinal
            ) &&
                !string.Equals(
                a: name,
                b: DropMemberName,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                throw new JsonException(message: $"{path} is a tombstone carrying member '{name}' — a tombstone carries only its identity key and '{DropMemberName}'; content on a dropped row is content lost silently.");
            }
        }
    }
    private static bool TryFindRowKey(JsonArray basis, JsonArray overlay, out string key, out string ambiguity) {
        key = string.Empty;
        ambiguity = string.Empty;

        foreach (var candidate in RowKeyPrecedence) {
            if (
                !ListCarriesKey(
                key: candidate,
                list: basis
            ) ||
                !ListCarriesKey(
                key: candidate,
                list: overlay
            )
            ) {
                continue;
            }

            if (HasDuplicateKey(
                duplicate: out var basisDuplicate,
                key: candidate,
                list: basis
            )) {
                ambiguity = $"cannot merge by '{candidate}': the basis list carries it more than once ({basisDuplicate}).";

                return false;
            }

            if (HasDuplicateKey(
                duplicate: out var overlayDuplicate,
                key: candidate,
                list: overlay
            )) {
                ambiguity = $"cannot merge by '{candidate}': the derived list carries it more than once ({overlayDuplicate}).";

                return false;
            }

            key = candidate;

            return true;
        }

        return false;
    }
    private static bool TypeDiscriminatorsDiffer(JsonObject basis, JsonObject overlay) {
        return (
            (basis[propertyName: "$type"] is { } basisType) &&
            (overlay[propertyName: "$type"] is { } overlayType) &&
            !JsonNode.DeepEquals(
            node1: basisType,
            node2: overlayType
        )
        );
    }

    /// <summary>Computes the minimal delta tree whose <see cref="TryMerge"/> over <paramref name="basis"/> reproduces
    /// <paramref name="target"/> — member-wise for objects, by identity key (tombstones and appends) for keyed lists,
    /// an explicit <c>null</c> for a removed member, and a <c>$replace</c>-marked wholesale list where a keyed
    /// reconstruction cannot express the target's row order. Pure and refusal-free: an inexpressible shape degrades
    /// to a wholesale form, never an error. The caller owns the round-trip proof (see the type remarks).</summary>
    /// <param name="basis">The basis document's tree.</param>
    /// <param name="target">The tree the delta must reproduce over <paramref name="basis"/>.</param>
    /// <returns>The delta tree — empty when <paramref name="target"/> already equals <paramref name="basis"/>.</returns>
    public static JsonObject Diff(JsonObject basis, JsonObject target) {
        ArgumentNullException.ThrowIfNull(argument: basis);
        ArgumentNullException.ThrowIfNull(argument: target);

        return DiffObject(
            basis: basis,
            target: target
        );
    }
    /// <summary>Merges <paramref name="overlay"/> (the derived document's tree, its <c>basis</c> member already
    /// removed) over <paramref name="basis"/> under the rules in the type remarks, returning the composed tree.
    /// Neither input is mutated.</summary>
    /// <param name="basis">The basis document's tree.</param>
    /// <param name="overlay">The derived document's tree.</param>
    /// <param name="composed">The composed tree on success; <see langword="null"/> on refusal.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the merge composed.</returns>
    public static bool TryMerge(JsonObject basis, JsonObject overlay, out JsonObject? composed, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: basis);
        ArgumentNullException.ThrowIfNull(argument: overlay);

        try {
            var target = ((JsonObject)basis.DeepClone());

            MergeObject(
                overlay: overlay,
                path: "$",
                target: target
            );

            composed = target;
            reason = string.Empty;

            return true;
        } catch (JsonException exception) {
            composed = null;
            reason = exception.Message;

            return false;
        }
    }
    /// <summary>Folds <paramref name="imports"/> — each already the FULL recursively composed tree of one imported
    /// fragment, in authored list order — into one layer, refusing a genuine authorship collision between two
    /// imports unless <paramref name="restated"/> (the importing file's own body, basis/imports members already
    /// stripped) also declares the same path, row, or list: the explicit resolution the type remarks describe.
    /// Two imports agreeing on a value at the same path (most commonly because they share a common ancestor
    /// somewhere in their own basis/import graphs) never collide — only a genuine disagreement, which can only arise
    /// from each side's own authored content actually diverging, does. An object member merges member-wise
    /// (recursing when both sides carry an object, so disjoint nested members from two imports combine rather than
    /// colliding on their shared parent); a row list keyed by the settled identity vocabulary unions by row (a key
    /// only one side carries appends; the same key on both sides collides exactly like a leaf, checked against
    /// <paramref name="restated"/>'s own row); any other shared list or scalar collides wholesale.</summary>
    /// <param name="imports">Each import's display name (for the refusal message) paired with its fully composed
    /// tree, in authored order.</param>
    /// <param name="restated">The importing file's own body — the sole exemption from a sibling collision.</param>
    /// <param name="composed">The folded layer on success; <see langword="null"/> on refusal.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when every import folded without an unresolved collision.</returns>
    public static bool TryMergeImports(IReadOnlyList<(string Name, JsonObject Tree)> imports, JsonObject restated, out JsonObject? composed, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: imports);
        ArgumentNullException.ThrowIfNull(argument: restated);

        var target = new JsonObject();
        var owners = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        try {
            foreach (var (name, tree) in imports) {
                MergeSiblingObject(
                    overlay: tree,
                    overlayName: name,
                    owners: owners,
                    path: "$",
                    restated: restated,
                    target: target
                );
            }

            composed = target;
            reason = string.Empty;

            return true;
        } catch (JsonException exception) {
            composed = null;
            reason = exception.Message;

            return false;
        }
    }
    private static JsonArray MergeSiblingArray(JsonArray existing, JsonArray overlay, JsonNode? restated, string overlayName, Dictionary<string, string> owners, string path) {
        if (!TryFindRowKey(
            ambiguity: out var ambiguity,
            basis: existing,
            key: out var key,
            overlay: overlay
        )) {
            if (ambiguity.Length > 0) {
                throw new JsonException(message: $"{path} {ambiguity}");
            }

            if (JsonNode.DeepEquals(
                node1: existing,
                node2: overlay
            )) {
                return existing;
            }

            if (restated is not JsonArray) {
                var earlier = owners.GetValueOrDefault(
                    defaultValue: "an earlier import",
                    key: path
                );

                throw new JsonException(message: $"{path}: '{earlier}' and '{overlayName}' both author this list, and it cannot merge by row identity — restate the whole list in the importing file to resolve the collision.");
            }

            return existing;
        }

        var merged = ((JsonArray)existing.DeepClone());
        var restatedArray = (restated as JsonArray);

        foreach (var row in overlay) {
            var rowObject = ((JsonObject)row!);
            var rowKey = rowObject[propertyName: key];
            var existingIndex = IndexOfRowKey(
                key: key,
                list: merged,
                value: rowKey
            );

            if (existingIndex < 0) {
                merged.Add(value: rowObject.DeepClone());
                owners[$"{path}[{DescribeKey(value: rowKey)}]"] = overlayName;

                continue;
            }

            if (JsonNode.DeepEquals(
                node1: merged[index: existingIndex],
                node2: rowObject
            )) {
                continue;
            }

            var restatesRow = (
                (restatedArray is not null) &&
                (IndexOfRowKey(
                key: key,
                list: restatedArray,
                value: rowKey
            ) >= 0)
            );

            if (!restatesRow) {
                var earlier = owners.GetValueOrDefault(
                    defaultValue: "an earlier import",
                    key: $"{path}[{DescribeKey(value: rowKey)}]"
                );

                throw new JsonException(message: $"{path}[{key}={DescribeKey(value: rowKey)}]: '{earlier}' and '{overlayName}' both author this row — restate it in the importing file to resolve the collision.");
            }
        }

        return merged;
    }
    private static void MergeSiblingObject(JsonObject target, JsonObject overlay, JsonNode? restated, string overlayName, Dictionary<string, string> owners, string path) {
        foreach (var (name, value) in overlay) {
            var memberPath = $"{path}.{name}";
            var childRestated = ((restated as JsonObject)?[name]);

            if (!target.TryGetPropertyValue(
                jsonNode: out var existing,
                propertyName: name
            )) {
                target[propertyName: name] = value?.DeepClone();
                RecordFreshOwnership(
                    node: target[propertyName: name],
                    owners: owners,
                    overlayName: overlayName,
                    path: memberPath
                );

                continue;
            }

            if (
                (existing is JsonObject existingObject) &&
                (value is JsonObject overlayObject) &&
                !TypeDiscriminatorsDiffer(
                basis: existingObject,
                overlay: overlayObject
            )
            ) {
                MergeSiblingObject(
                    overlay: overlayObject,
                    overlayName: overlayName,
                    owners: owners,
                    path: memberPath,
                    restated: childRestated,
                    target: existingObject
                );

                continue;
            }

            if (
                (existing is JsonArray existingArray) &&
                (value is JsonArray overlayArray)
            ) {
                target[propertyName: name] = MergeSiblingArray(
                    existing: existingArray,
                    overlay: overlayArray,
                    overlayName: overlayName,
                    owners: owners,
                    path: memberPath,
                    restated: childRestated
                );

                continue;
            }

            if (JsonNode.DeepEquals(
                node1: existing,
                node2: value
            )) {
                continue;
            }

            if (childRestated is null) {
                var earlier = owners.GetValueOrDefault(
                    defaultValue: "an earlier import",
                    key: memberPath
                );

                throw new JsonException(message: $"{memberPath}: '{earlier}' and '{overlayName}' both author this member — restate it in the importing file to resolve the collision.");
            }
        }
    }
    // Registers ownership at EVERY path reachable inside a freshly-added subtree, not just its own top-level path —
    // a later import's collision may land on a nested leaf ("$.metadata.title") this one never touched directly,
    // and the refusal must still name the ORIGINAL contributor rather than falling back to a generic description.
    private static void RecordFreshOwnership(JsonNode? node, string path, string overlayName, Dictionary<string, string> owners) {
        owners[path] = overlayName;

        if (node is JsonObject nested) {
            foreach (var (name, value) in nested) {
                RecordFreshOwnership(
                    node: value,
                    overlayName: overlayName,
                    owners: owners,
                    path: $"{path}.{name}"
                );
            }

            return;
        }

        if (
            (node is JsonArray array) &&
            TryFindSingleListRowKey(
            key: out var key,
            list: array
        )
        ) {
            foreach (var row in array) {
                if (row is JsonObject rowObject) {
                    owners[$"{path}[{DescribeKey(value: rowObject[propertyName: key])}]"] = overlayName;
                }
            }
        }
    }
    private static bool TryFindSingleListRowKey(JsonArray list, out string key) {
        foreach (var candidate in RowKeyPrecedence) {
            if (ListCarriesKey(
                key: candidate,
                list: list
            )) {
                key = candidate;

                return true;
            }
        }

        key = string.Empty;

        return false;
    }
}
