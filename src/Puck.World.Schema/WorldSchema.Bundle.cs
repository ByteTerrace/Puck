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
                return ((obj.Count == 1) && !nullableDefs.GetValueOrDefault(key: commonName)
                    ? new JsonObject { ["$ref"] = $"#/$defs/{commonName}" }
                    : RewrapReferenceSiteSiblings(
                        obj: obj,
                        title: commonName,
                        defs: defs,
                        defContentByTitle: defContentByTitle,
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
                return ((obj.Count == 1) && !nullableDefs.GetValueOrDefault(key: bareTarget)
                    ? obj.DeepClone()!
                    : RewrapReferenceSiteSiblings(
                        obj: obj,
                        title: bareTarget,
                        defs: defs,
                        defContentByTitle: defContentByTitle,
                        nullableDefs: nullableDefs
                    ));
            }

            var nullable = AcceptsBundleNull(node: obj, referenceNullability: name => nullableDefs.GetValueOrDefault(key: name));
            var newObj = new JsonObject();

            foreach (var (key, value) in obj) {
                newObj[key] = RewriteSchemaKeyword(
                    key: key,
                    value: value,
                    rewrite: child => HoistBundleTitles(
                        node: child,
                        defs: defs,
                        defContentByTitle: defContentByTitle,
                        nullableDefs: nullableDefs
                    )
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
                    nullable: nullable,
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
    // Only schema-valued keywords recurse. enum/const/default/examples and extension annotations hold JSON
    // instance data: a literal object's "title" or "$ref" must never become a schema declaration or pointer.
    private static JsonNode? RewriteSchemaKeyword(string key, JsonNode? value, Func<JsonNode, JsonNode> rewrite) {
        if (value is null) {
            return null;
        }

        if ((key is "properties" or "patternProperties" or "$defs" or "definitions" or "dependentSchemas") && (value is JsonObject map)) {
            var result = new JsonObject();

            foreach (var (name, schema) in map) {
                result[name] = ((schema is null) ? null : rewrite(arg: schema));
            }

            return result;
        }

        if (key is "items" or "additionalItems" or "contains" or "additionalProperties" or "unevaluatedProperties" or "unevaluatedItems" or "propertyNames" or "not" or "if" or "then" or "else" or "allOf" or "anyOf" or "oneOf" or "prefixItems" or "contentSchema") {
            return rewrite(arg: value);
        }

        return value.DeepClone();
    }
    // Share equal non-null bodies, preserving descriptions/defaults and null admission at each occurrence.
    // Nested constraints remain part of identity; a different body receives an explicit VariantN definition.
    // nullableDefs describes the ORIGINAL common definitions and is never changed by an inline occurrence.
    private static JsonNode HoistOrReferenceTitledNode(JsonObject obj, string title, bool nullable, JsonObject defs, Dictionary<string, string> defContentByTitle, Dictionary<string, bool> nullableDefs) {
        var description = obj["description"]?.DeepClone();
        var hasDefault = obj.TryGetPropertyValue(
            propertyName: "default",
            jsonNode: out var defaultValue
        );
        var body = ((JsonObject)obj.DeepClone()!);

        body.Remove(propertyName: "description");
        body.Remove(propertyName: "default");
        StripNullInPlace(obj: body);

        var canonical = CanonicalDefText(obj: body);

        // A CLR type can be exported with different nested nullability at different sites. Share only equal
        // constraints; an explicit variant keeps both sites faithful instead of letting traversal order decide.
        var baseTitle = title;
        var variant = 2;

        while (defContentByTitle.TryGetValue(key: title, value: out var candidate)
            ? !string.Equals(a: candidate, b: canonical, comparisonType: StringComparison.Ordinal)
            : ((title != baseTitle) && nullableDefs.ContainsKey(key: title))) {
            title = $"{baseTitle}Variant{variant++}";
        }

        body["title"] = title;

        if (!defContentByTitle.ContainsKey(key: title)) {
            defs[title] = body;
            defContentByTitle[title] = canonical;
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
    // Evaluate only the null instance. Object/array/string/number-specific keywords cannot constrain it;
    // type, literal equality, references, and applicators can. A nullable type array alone is insufficient:
    // an enum or an anyOf of object-only arms can still reject null.
    private static bool AcceptsBundleNull(JsonNode node, Func<string, bool> referenceNullability) {
        if (node is JsonValue boolean && boolean.TryGetValue<bool>(value: out var allowed)) {
            return allowed;
        }
        if (node is not JsonObject obj) {
            return false;
        }
        if ((IsCommonFileRef(obj: obj, name: out var target) || IsLocalDefsRef(obj: obj, name: out target)) && !referenceNullability(arg: target)) {
            return false;
        }
        if (obj["type"] is { } type && !IsNullToken(value: type) && !((type is JsonArray types) && types.Any(predicate: IsNullToken))) {
            return false;
        }
        if ((obj["enum"] is JsonArray values) && !values.Any(predicate: value => value is null)) {
            return false;
        }
        if (obj.TryGetPropertyValue(propertyName: "const", jsonNode: out var constant) && (constant is not null)) {
            return false;
        }
        foreach (var key in new[] { "allOf", "anyOf", "oneOf" }) {
            if (obj[key] is not JsonArray arms) {
                continue;
            }
            var matches = arms.Count(predicate: arm => (arm is not null) && AcceptsBundleNull(node: arm, referenceNullability: referenceNullability));
            if ((key == "allOf" && matches != arms.Count) || (key == "anyOf" && matches == 0) || (key == "oneOf" && matches != 1)) {
                return false;
            }
        }
        if (obj["not"] is { } negated && AcceptsBundleNull(node: negated, referenceNullability: referenceNullability)) {
            return false;
        }
        if (obj["if"] is { } condition) {
            var branch = AcceptsBundleNull(node: condition, referenceNullability: referenceNullability) ? "then" : "else";
            if (obj[branch] is { } constraint && !AcceptsBundleNull(node: constraint, referenceNullability: referenceNullability)) {
                return false;
            }
        }
        return true;
    }
    // Keep exactly the non-null instances of a schema. Null-only schemas become unsatisfiable bodies; nested
    // property constraints are untouched. Each site's original null admission was computed before this rewrite.
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
            // The non-null body of a null-only type accepts nothing, never the unconstrained {} schema.
            obj["not"] = new JsonObject();
        } else if (obj["type"] is JsonArray typeArray) {
            var kept = typeArray.Where(predicate: value => !IsNullToken(value: value)).Select(selector: value => value!.DeepClone()).ToList();

            typeArray.Clear();

            if (kept.Count == 0) {
                obj.Remove(propertyName: "type");
                obj["not"] = new JsonObject();
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
            if (kept.Count == 0) {
                obj.Remove(propertyName: "enum");
                obj["not"] = new JsonObject();
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
            if (kept.Count == 0) {
                obj.Remove(propertyName: key);
                obj["not"] = new JsonObject();
            }
        }

        // Removing a null arm from a union without an explicit type can leave a body that still accepts null
        // (e.g. oneOf: [null, {}]). Every stored target is non-null; the reference site alone owns its admission.
        if (AcceptsBundleNull(node: obj, referenceNullability: static _ => false)) {
            var nullType = new JsonObject { ["type"] = "null" };
            obj["not"] = (obj["not"] is { } negated)
                ? new JsonObject { ["anyOf"] = new JsonArray(negated.DeepClone(), nullType) }
                : nullType;
        }
    }
    // Compare schema constraints, excluding annotations. Top-level null admission already lives at each site;
    // nested nullability and literal enum/const values must still compare exactly.
    private static string CanonicalDefText(JsonObject obj) =>
        CompactSerialize(node: CanonicalForComparison(node: obj));
    private static JsonNode CanonicalForComparison(JsonNode node) {
        if (node is JsonObject obj) {
            var working = ((JsonObject)obj.DeepClone()!);

            // A single-reference union and a bare reference impose identical constraints. A two-arm nullable
            // wrapper is deliberately retained: it is different from a non-nullable reference.
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
                if (key is "description" or "default" or "title") {
                    continue;
                }

                result[key] = RewriteSchemaKeyword(key: key, value: value, rewrite: CanonicalForComparison);
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
    // Wrap references with annotations for the TypeScript compiler, and every nullable target because its
    // stored body excludes null. A bare reference is sufficient only for an unannotated, non-nullable target.
    private static JsonNode RewrapReferenceSiteSiblings(JsonObject obj, string title, JsonObject defs, Dictionary<string, string> defContentByTitle, Dictionary<string, bool> nullableDefs) {
        // Generated references normally carry only annotations; extension schemas may also constrain the
        // target with sibling keywords. Both the original target and those siblings must admit null.
        var rewritten = BuildReferenceSite(
            title: title,
            nullable: AcceptsBundleNull(node: obj, referenceNullability: name => nullableDefs.GetValueOrDefault(key: name))
        );

        // $ref siblings are conjunctive. Preserve an existing anyOf rather than overwriting our reference arm.
        if (obj.ContainsKey(propertyName: "anyOf")) {
            rewritten = new JsonObject { ["allOf"] = new JsonArray(rewritten) };
        }

        foreach (var (key, value) in obj) {
            if (!string.Equals(
                a: key,
                b: "$ref",
                comparisonType: StringComparison.Ordinal
            )) {
                var child = RewriteSchemaKeyword(key: key, value: value, rewrite: schema => HoistBundleTitles(node: schema, defs: defs, defContentByTitle: defContentByTitle, nullableDefs: nullableDefs));
                if ((key == "allOf") && (rewritten[key] is JsonArray existing) && (child is JsonArray additional)) {
                    foreach (var arm in additional) {
                        existing.Add(item: arm?.DeepClone());
                    }
                } else {
                    rewritten[key] = child;
                }
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
    /// occurrences with equal constraints share one def; differing nested constraints receive named
    /// <c>VariantN</c> definitions. Each occurrence retains its original null admission and annotations.</summary>
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
        var active = new HashSet<string>(comparer: StringComparer.Ordinal);
        bool ResolveNullability(string name) {
            if (nullableDefs.TryGetValue(key: name, value: out var nullable)) {
                return nullable;
            }
            if (!active.Add(item: name)) {
                throw new InvalidOperationException(message: $"schema: unguarded reference cycle at '{name}'.");
            }
            var result = AcceptsBundleNull(node: commonDefs[name]!, referenceNullability: ResolveNullability);
            active.Remove(item: name);
            nullableDefs[name] = result;
            return result;
        }
        foreach (var (name, _) in commonDefs) {
            ResolveNullability(name: name);
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
