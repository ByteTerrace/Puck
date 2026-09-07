using System.Text.Json.Nodes;

namespace Puck.World;

public static partial class WorldSchema {
    // Rewrites a section's own "./common.schema.json#/$defs/X" pointers to the bundle-local "#/$defs/X" form, and
    // hoists every titled shape left inlined ANYWHERE — a section, or a common.schema.json def's own content, since
    // that split's threshold (HoistMinimumLength, a use count of one) leaves a small or singly-used shape (e.g. a
    // three-value enum) embedded in its containing def rather than promoted — into a fresh bundle def keyed by its
    // title. Post-order: a parent's own hoist test runs on its already-rewritten children, so a child def is
    // created (or matched) before any parent that might itself also be titled. <paramref name="hoistSelf"/> is
    // false only for the top call over a common.schema.json def's own content — that node already owns its def's
    // key; only ITS children are candidates for a fresh nested def.
    private static JsonNode HoistBundleTitles(JsonNode node, JsonObject defs, Dictionary<string, string> defContentByTitle, Dictionary<string, bool> nullableDefs, bool hoistSelf = true) {
        if (node is JsonObject obj) {
            // A node carrying its OWN "$id" opens a fresh base-URI scope (a whole other document schema — e.g.
            // puck.creation.v1 — embedded inline, self-contained): a bare "#/$defs/X" pointer written inside it
            // resolves against THAT $id, never the bundle root, so hoisting anything out of it (or pulling the
            // island itself into the bundle's own $defs) would mint a $ref nothing can resolve. The split's own
            // common.schema.json hoisting already leaves such an island alone; the bundle matches that here,
            // copying it through untouched.
            if (obj.ContainsKey(propertyName: "$id")) {
                return obj.DeepClone()!;
            }

            if (IsCommonFileRef(
                name: out var commonName,
                obj: obj
            )) {
                return ((obj.Count == 1)
                    ? new JsonObject { ["$ref"] = $"#/$defs/{commonName}" }
                    : RewrapReferenceSiteSiblings(
                        obj: obj,
                        title: commonName,
                        nullableDefs: nullableDefs
                    ));
            }

            // A common.schema.json def can itself reference ANOTHER common def by the SAME bare "#/$defs/X" form
            // its own internal content already used before bundling (FinalizeRefs' insideCommon:true spelling) —
            // untouched by the rewrite above (which matches only the cross-file "./common.schema.json#/..." form
            // a SECTION uses), so it needs the identical anyOf-wrap for the same reason, when it carries a sibling
            // that would trigger it.
            if (IsLocalDefsRef(
                name: out var bareTarget,
                obj: obj
            )) {
                return ((obj.Count == 1)
                    ? obj.DeepClone()!
                    : RewrapReferenceSiteSiblings(
                        obj: obj,
                        title: bareTarget,
                        nullableDefs: nullableDefs
                    ));
            }

            var newObj = new JsonObject();

            foreach (var (key, value) in obj) {
                newObj[key] = ((value is not null)
                    ? HoistBundleTitles(
                        node: value,
                        defs: defs,
                        defContentByTitle: defContentByTitle,
                        nullableDefs: nullableDefs
                    )
                    : null
                );
            }

            // Unlike the split's own common.schema.json hoist (IsHoistCandidate, gated to an object/enum/union
            // shape worth a $ref), the bundle hoists ANY titled node, scalar wrappers included (BindableColor,
            // CellName, ...): json-schema-to-typescript names an inline node from its own "title" too, so a
            // scalar type reused at many sites without a $ref would mint one numbered duplicate per site
            // (BindableColor, BindableColor1, BindableColor2, ...) instead of the one shared alias the class
            // doc's OUTCOME calls for.
            return (
                hoistSelf &&
                (newObj["title"] is JsonValue titleValue) &&
                titleValue.TryGetValue<string>(value: out var title)
            )
                ? HoistOrReferenceTitledNode(
                    obj: newObj,
                    title: title,
                    defs: defs,
                    defContentByTitle: defContentByTitle,
                    nullableDefs: nullableDefs
                )
                : newObj;
        }

        if (node is JsonArray arr) {
            var newArr = new JsonArray();

            foreach (var value in arr) {
                newArr.Add(item: ((value is not null)
                    ? HoistBundleTitles(
                        node: value,
                        defs: defs,
                        defContentByTitle: defContentByTitle,
                        nullableDefs: nullableDefs
                    )
                    : null));
            }

            return newArr;
        }

        return node.DeepClone()!;
    }
    // Turns a titled, un-hoisted node into a $ref against defs[title] — minting that def on first sight, matching
    // an already-minted def when the content agrees under CanonicalDefText (blind to nullability at any depth, so
    // "Foo" and "Foo?" reaching the same title share one def rather than minting "FooNullable"), and refusing when
    // it does not: two distinct shapes cannot share one name in a bundle whose $defs are addressed BY that name.
    // The STORED def always drops its own top-level null admission (see nullableDefs's own remark); every site —
    // this occurrence's own, and every later reference the title's own nullableDefs entry now carries forward —
    // wraps in "anyOf: [$ref, null]" whenever nullable, so nothing a caller once could send stops validating.
    private static JsonNode HoistOrReferenceTitledNode(JsonObject obj, string title, JsonObject defs, Dictionary<string, string> defContentByTitle, Dictionary<string, bool> nullableDefs) {
        var description = obj["description"]?.DeepClone();
        var hasDefault = obj.TryGetPropertyValue(
            propertyName: "default",
            jsonNode: out var defaultValue
        );
        var nullable = AllowsNull(node: obj);
        var body = ((JsonObject)obj.DeepClone()!);

        body.Remove(propertyName: "description");
        body.Remove(propertyName: "default");
        StripNullInPlace(obj: body);

        var canonical = CanonicalDefText(obj: body);

        if (defContentByTitle.TryGetValue(
            key: title,
            value: out var existing
        )) {
            if (!string.Equals(
                a: existing,
                b: canonical,
                comparisonType: StringComparison.Ordinal
            )) {
                throw new InvalidOperationException(message: $"schema: title '{title}' names two different shapes in the bundle — rename one of the source CLR types so they no longer share a FriendlyTypeName.");
            }

            nullableDefs[title] = (nullableDefs.GetValueOrDefault(key: title) || nullable);
        } else {
            defs[title] = body;
            defContentByTitle[title] = canonical;
            nullableDefs[title] = nullable;
        }

        var reference = BuildReferenceSite(
            title: title,
            nullable: nullable
        );

        if (description is not null) {
            reference["description"] = description;
        }

        if (hasDefault) {
            reference["default"] = defaultValue?.DeepClone();
        }

        return reference;
    }
    // A reference site is always "anyOf: [{$ref}]" — a single-arm union when non-nullable, a two-arm one with a
    // "null" type when nullable — never a bare "$ref". json-schema-to-typescript resolves a $ref correctly no
    // matter what rides beside the ENCLOSING anyOf, but treats a bare "$ref" carrying ANY sibling keyword
    // ("description", "default") as a distinct schema to inline and name afresh — see HoistBundleTitles' own
    // remark at its common-file-ref rewrite for the observed behavior this works around. A stored def's OWN
    // top-level type never keeps a "null" admission (see nullableDefs) — folding it into "anyOf: [X, null]"
    // instead of "type: [X, null]" ALSO sidesteps a second, unrelated json-schema-to-typescript quirk: given
    // "type"/"required"/"anyOf" together (how a $type-discriminated union's own def is shaped) AND a "null" in
    // that "type", it decomposes the def into an intersection of two further-numbered helper exports instead of
    // the one clean union alias a def with a plain non-null "type" compiles to.
    private static JsonObject BuildReferenceSite(string title, bool nullable) {
        var arms = new JsonArray(new JsonObject { ["$ref"] = $"#/$defs/{title}" });

        if (nullable) {
            arms.Add(item: new JsonObject { ["type"] = "null" });
        }

        return new JsonObject { ["anyOf"] = arms };
    }
    private static bool IsNullArm(JsonNode? value) =>
        ((value is null) ||
        IsNullToken(value: value) ||
        ((value is JsonObject arm) &&
        (arm["type"] is JsonValue armType) &&
        armType.TryGetValue<string>(value: out var armTypeName) &&
        string.Equals(
            a: armTypeName,
            b: "null",
            comparisonType: StringComparison.Ordinal
        )));
    private static bool IsNullToken(JsonNode? value) =>
        ((value is JsonValue token) &&
        token.TryGetValue<string>(value: out var text) &&
        string.Equals(
            a: text,
            b: "null",
            comparisonType: StringComparison.Ordinal
        ));
    // Removes every way AllowsNull recognizes a "null" admission — a "null" member of a "type" array (collapsed
    // back to a bare scalar when exactly one type survives), a null member of "enum", and a null-typed arm of
    // "anyOf"/"oneOf" — leaving the shape a nullable site's non-null body actually shares with its non-nullable
    // sibling.
    private static void StripNullInPlace(JsonObject obj) {
        if (
            (obj["type"] is JsonValue scalarType) &&
            scalarType.TryGetValue<string>(value: out var typeName) &&
            string.Equals(
            a: typeName,
            b: "null",
            comparisonType: StringComparison.Ordinal
        )
        ) {
            obj.Remove(propertyName: "type");
        } else if (obj["type"] is JsonArray typeArray) {
            var kept = typeArray.Where(predicate: value => !IsNullToken(value: value)).Select(selector: value => value!.DeepClone()).ToList();

            typeArray.Clear();

            if (kept.Count == 0) {
                obj.Remove(propertyName: "type");
            } else if (kept.Count == 1) {
                obj["type"] = kept[0];
            } else {
                foreach (var value in kept) {
                    typeArray.Add(item: value);
                }

                obj["type"] = typeArray;
            }
        }

        if (obj["enum"] is JsonArray enumArray) {
            var kept = enumArray.Where(predicate: value => (value is not null)).Select(selector: value => value!.DeepClone()).ToList();

            enumArray.Clear();

            foreach (var value in kept) {
                enumArray.Add(item: value);
            }
        }

        foreach (var key in new[] { "anyOf", "oneOf" }) {
            if (obj[key] is not JsonArray unionArray) {
                continue;
            }

            var kept = unionArray.Where(predicate: value => !IsNullArm(value: value)).Select(selector: value => value!.DeepClone()).ToList();

            unionArray.Clear();

            foreach (var value in kept) {
                unionArray.Add(item: value);
            }
        }
    }
    // A def's own "description" reads however the occurrence that happened to define it was documented, and
    // nullability can legitimately vary occurrence to occurrence — the exporter's nullable-oblivious handling
    // reaches the same property through different reference chains, and the split's own OWN pre-existing
    // convention already bakes "null" into a def's top type when every occurrence it saw was nullable. Neither is
    // part of what makes two candidate bodies "the same shape" for bundling purposes: only the STORED def (the
    // first occurrence reached) keeps its own description and its own nullability, everywhere in its tree; this
    // text is the comparison basis alone, blind to both.
    private static string CanonicalDefText(JsonObject obj) =>
        CompactSerialize(node: CanonicalForComparison(node: obj));
    private static JsonNode CanonicalForComparison(JsonNode node) {
        if (node is JsonObject obj) {
            var working = ((JsonObject)obj.DeepClone()!);

            StripNullInPlace(obj: working);

            // A nullable-reference wrapper ("anyOf": [{"$ref"}, {"type":"null"}]) strips down to a single
            // remaining "$ref" arm above — collapsed the rest of the way to a bare "$ref", equal to how a
            // non-nullable site refers to the very same def.
            if (
                (working["anyOf"] is JsonArray singleArm) &&
                (singleArm.Count == 1) &&
                (singleArm[0] is JsonObject onlyArm) &&
                (onlyArm.Count == 1) &&
                (onlyArm["$ref"] is JsonValue refValue)
            ) {
                working.Remove(propertyName: "anyOf");
                working["$ref"] = refValue.DeepClone();
            }

            var result = new JsonObject();

            foreach (var (key, value) in working) {
                if (key is "description" or "default") {
                    continue;
                }

                if (value is not null) {
                    result[key] = CanonicalForComparison(node: value);
                }
            }

            return result;
        }

        if (node is JsonArray arr) {
            var result = new JsonArray();

            foreach (var value in arr) {
                if (value is not null) {
                    result.Add(item: CanonicalForComparison(node: value));
                }
            }

            return result;
        }

        return node.DeepClone()!;
    }
    private static bool IsCommonFileRef(JsonObject obj, out string name) {
        const string Prefix = $"./{CommonDefsFileName}#/$defs/";

        if (ContainsRefKey(obj: obj)) {
            var value = ((JsonValue)obj["$ref"]!).GetValue<string>();

            if (value.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: Prefix
            )) {
                name = value[Prefix.Length..];

                return true;
            }
        }

        name = string.Empty;

        return false;
    }
    private static bool IsLocalDefsRef(JsonObject obj, out string name) {
        const string Prefix = "#/$defs/";

        if (ContainsRefKey(obj: obj)) {
            var value = ((JsonValue)obj["$ref"]!).GetValue<string>();

            if (value.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: Prefix
            )) {
                name = value[Prefix.Length..];

                return true;
            }
        }

        name = string.Empty;

        return false;
    }
    // A $ref that carries no sibling keyword needs no wrap — a bare "{"$ref": X}" already resolves cleanly (see
    // BuildReferenceSite's own remark); only a sibling ("description", "default", ...) triggers
    // json-schema-to-typescript's re-inline-and-rename behavior, so only that case pays the wrap's extra nesting.
    private static JsonNode RewrapReferenceSiteSiblings(JsonObject obj, string title, Dictionary<string, bool> nullableDefs) {
        // A bare local/cross-file $ref site never carries its own "type" (Transform stops at an already-$ref
        // node — see its own remark), so nullability here comes from the TARGET def's own original admission
        // (see nullableDefs), not this node's own (always-absent) shape.
        var rewritten = BuildReferenceSite(
            title: title,
            nullable: (nullableDefs.GetValueOrDefault(key: title) || AllowsNull(node: obj))
        );

        foreach (var (key, value) in obj) {
            if (!string.Equals(
                a: key,
                b: "$ref",
                comparisonType: StringComparison.Ordinal
            )) {
                rewritten[key] = value?.DeepClone();
            }
        }

        return rewritten;
    }
    /// <summary>Composes a <see cref="SplitSchema"/> into the single-file equivalent json-schema-to-typescript
    /// reads: every shared shape lives once under the bundle's own <c>$defs</c> (seeded from
    /// <see cref="SplitSchema.Common"/>, whose internal pointers already use the bare <c>#/$defs/X</c> form), a
    /// section's own cross-file pointer is rewritten to match, and every OTHER titled shape a section left
    /// inlined — used at one site, or independently regenerated at each (a <c>$type</c> union arm) — is hoisted
    /// into a fresh def keyed by <see cref="StampTitle"/>'s own name (see <see cref="HoistBundleTitles"/>). Two
    /// occurrences reaching the same title always resolve to one def; content that disagrees under a shared title
    /// is a generator defect this method refuses by name rather than silently duplicating or merging.</summary>
    public static JsonObject Bundle(SplitSchema split) {
        var root = ((JsonObject)split.Root.DeepClone()!);

        StampBundleCommit(root: root);

        var sectionsByName = split.Sections.ToDictionary(
            keySelector: s => s.Name,
            elementSelector: s => s.Node,
            comparer: StringComparer.Ordinal
        );
        var propertiesObject = ((JsonObject)root["properties"]!);
        var defs = new JsonObject();
        var defContentByTitle = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var nullableDefs = new Dictionary<string, bool>(comparer: StringComparer.Ordinal);
        var commonDefs = ((JsonObject)split.Common["$defs"]!);

        // Recorded up front, over every common.schema.json key's own RAW (unprocessed) top-level shape, before
        // any cross-def rewriting starts: a def's own reference to another can appear before that other def's
        // own turn in the loop below, and RewrapReferenceSiteSiblings needs every title's nullability resolved
        // the first time anything might ask for it, not just after its own def has been visited.
        foreach (var (name, defNode) in commonDefs) {
            nullableDefs[name] = AllowsNull(node: defNode!);
        }

        foreach (var (name, defNode) in commonDefs) {
            var clone = ((JsonObject)defNode!.DeepClone()!);

            // A def's own title reflects the type StampTitle stamped it from, oblivious to which of possibly
            // several defs sharing that base type (a nullable/non-nullable pair ChooseDefName disambiguated as
            // "Foo"/"FooNullable") this particular entry is — normalized here to the one name that has to hold:
            // the def's own key. Its OWN children still walk through the same bundle-only hoist a section's
            // content does (hoistSelf: false so the def keeps its assigned key instead of becoming a $ref to
            // itself) — the split's own HoistMinimumLength/use-count threshold can leave a small or singly-used
            // titled shape (e.g. a three-value enum) embedded here that the bundle names on its own.
            clone["title"] = name;
            StripNullInPlace(obj: clone);

            var hoisted = ((JsonObject)HoistBundleTitles(
                node: clone,
                defs: defs,
                defContentByTitle: defContentByTitle,
                nullableDefs: nullableDefs,
                hoistSelf: false
            ));
            var canonical = CanonicalDefText(obj: hoisted);

            if (defContentByTitle.TryGetValue(
                key: name,
                value: out var existing
            )) {
                if (!string.Equals(
                    a: existing,
                    b: canonical,
                    comparisonType: StringComparison.Ordinal
                )) {
                    throw new InvalidOperationException(message: $"schema: title '{name}' names two different shapes in the bundle — rename one of the source CLR types so they no longer share a FriendlyTypeName.");
                }
            } else {
                defs[name] = hoisted;
                defContentByTitle[name] = canonical;
            }
        }

        foreach (var name in propertiesObject.Select(selector: kv => kv.Key).ToList()) {
            propertiesObject.Remove(propertyName: name);

            var sectionContent = ((JsonNode)sectionsByName[name].DeepClone()!);

            propertiesObject[name] = HoistBundleTitles(
                node: sectionContent,
                defs: defs,
                defContentByTitle: defContentByTitle,
                nullableDefs: nullableDefs
            );
        }

        root["$defs"] = defs;

        return root;
    }
}
