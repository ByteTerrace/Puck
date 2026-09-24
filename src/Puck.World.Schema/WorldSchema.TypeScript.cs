using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Puck.World;

public static partial class WorldSchema {
    /// <summary>The name <see cref="ToTypeScript"/> gives the bundle root's own type.</summary>
    public const string TypeScriptRootName = "WorldDefinition";

    // Keywords that shape a TypeScript type, and the annotations and value constraints TypeScript cannot express and
    // the emitter reads past. Anything else refuses by name, so a schema that starts using a new keyword fails the
    // generator rather than silently widening a type to unknown.
    private static readonly HashSet<string> TypeScriptKnownKeywords = new(comparer: StringComparer.Ordinal) {
        "$comment", "$defs", "$id", "$ref", "$schema", "additionalProperties", "allOf", "anyOf", "const", "default",
        "description", "else", "enum", "format", "if", "items", "maxItems", "maxLength", "maximum", "minItems",
        "minLength", "minimum", "not", "oneOf", "pattern", "patternProperties", "properties", "required", "then",
        "title", "type", "x-enumDescriptions", "x-puck",
    };

    [GeneratedRegex(pattern: "\\A[A-Za-z_$][A-Za-z0-9_$]*\\z")]
    private static partial Regex TypeScriptIdentifier();

    /// <summary>Emits the TypeScript declarations of a bundled schema (<see cref="Bundle"/>): one
    /// <c>export type</c> named <see cref="TypeScriptRootName"/> for the root, then one per <c>$defs</c> entry under its
    /// key, in ordinal key order, each carrying its <c>description</c> as a doc comment.
    /// <para>The mapping follows JSON Schema's meaning. <c>anyOf</c> and <c>oneOf</c> are unions, <c>allOf</c> an
    /// intersection of the members that constrain a shape, <c>enum</c> and <c>const</c> literal types, <c>type</c> the
    /// primitive or object each name admits (<c>integer</c> is <c>number</c>), and an array of a fixed length
    /// (<c>minItems</c> equal to <c>maxItems</c>) a tuple. An object's property is optional unless <c>required</c>
    /// names it, and the object is open (<c>[k: string]: unknown</c>) unless <c>additionalProperties</c> is
    /// <see langword="false"/>. Conditional (<c>if</c>/<c>then</c>/<c>else</c>), negated (<c>not</c>) and value
    /// constraints (bounds, lengths, patterns) have no TypeScript form and are read past; a schema with no shape
    /// keyword is <c>unknown</c>.</para></summary>
    /// <param name="bundle">The bundle. Every <c>$ref</c> must name one of its own <c>$defs</c> as
    /// <c>#/$defs/&lt;key&gt;</c>.</param>
    /// <returns>The file text: LF newlines and one trailing newline, identical for an unchanged bundle.</returns>
    /// <exception cref="InvalidOperationException">A <c>$ref</c> names no <c>$defs</c> entry, a <c>$defs</c> key is not
    /// a TypeScript identifier or collides with <see cref="TypeScriptRootName"/>, or a node carries a keyword the
    /// emitter does not know.</exception>
    public static string ToTypeScript(JsonObject bundle) {
        ArgumentNullException.ThrowIfNull(argument: bundle);

        var defs = ((bundle["$defs"] as JsonObject) ?? []);
        var identity = (bundle["x-puck"] as JsonObject);
        var text = new StringBuilder();

        foreach (var (name, _) in defs) {
            if (
                !TypeScriptIdentifier().IsMatch(input: name) ||
                string.Equals(
                    a: name,
                    b: TypeScriptRootName,
                    comparisonType: StringComparison.Ordinal
                )
            ) {
                throw new InvalidOperationException(message: $"schema: $defs key '{name}' cannot name a TypeScript type.");
            }
        }

        text.Append(value: "// GENERATED FILE — do not hand-edit.\n");
        text.Append(value: "// Written by `puck schema` from the puck.world.definition.v1 schema bundle; `puck schema --check` fails when it\n");
        text.Append(value: "// no longer matches the schema.\n");
        text.Append(value: $"// Source bundle: schemaVersion={(((string?)identity?["schemaVersion"]) ?? "unknown")} generator={(((string?)identity?["generator"]) ?? "unknown")}\n");
        AppendTypeScriptDeclaration(
            defs: defs,
            name: TypeScriptRootName,
            node: bundle,
            path: "#",
            text: text
        );

        foreach (var name in defs.Select(selector: static pair => pair.Key).Order(comparer: StringComparer.Ordinal)) {
            AppendTypeScriptDeclaration(
                defs: defs,
                name: name,
                node: defs[name],
                path: $"#/$defs/{name}",
                text: text
            );
        }

        return text.ToString();
    }

    private static void AppendTypeScriptDeclaration(StringBuilder text, JsonObject defs, string name, JsonNode? node, string path) {
        text.Append(value: '\n');
        AppendTypeScriptDocumentation(
            indent: string.Empty,
            node: node,
            text: text
        );
        text.Append(value: $"export type {name} = {TypeScriptOf(defs: defs, indent: string.Empty, node: node, path: path).Text};\n");
    }
    private static void AppendTypeScriptDocumentation(StringBuilder text, string indent, JsonNode? node) {
        if (
            (node is not JsonObject obj) ||
            (obj["description"] is not JsonValue value) ||
            !value.TryGetValue<string>(value: out var description) ||
            string.IsNullOrWhiteSpace(value: description)
        ) {
            return;
        }

        text.Append(value: $"{indent}/**\n");

        foreach (var line in description.Replace(
            newValue: "\n",
            oldValue: "\r\n"
        ).Split(separator: '\n')) {
            var escaped = line.Replace(
                newValue: "*\\/",
                oldValue: "*/"
            ).TrimEnd();

            text.Append(value: ((escaped.Length == 0)
                ? $"{indent} *\n"
                : $"{indent} * {escaped}\n"
            ));
        }

        text.Append(value: $"{indent} */\n");
    }
    private static TypeScriptType TypeScriptArray(JsonObject defs, JsonObject obj, string indent, string path) {
        var element = ((obj["items"] is { } items)
            ? TypeScriptOf(
                defs: defs,
                indent: indent,
                node: items,
                path: $"{path}/items"
            )
            : TypeScriptType.Unknown
        );

        if (
            (obj["minItems"] is JsonValue minimum) &&
            (obj["maxItems"] is JsonValue maximum) &&
            minimum.TryGetValue<int>(value: out var minimumCount) &&
            maximum.TryGetValue<int>(value: out var maximumCount) &&
            (minimumCount == maximumCount)
        ) {
            return TypeScriptType.Primary(text: $"[{string.Join(
                separator: ", ",
                values: Enumerable.Repeat(
                    count: minimumCount,
                    element: element.Text
                )
            )}]");
        }

        return TypeScriptType.Primary(text: $"{element.Parenthesized(below: TypeScriptPrecedence.Primary)}[]");
    }
    private static TypeScriptType TypeScriptLiteral(JsonNode? value, string path) => value switch {
        null => TypeScriptType.Primary(text: "null"),
        JsonValue scalar when scalar.TryGetValue<string>(value: out var text) => TypeScriptType.Primary(text: TypeScriptString(value: text)),
        JsonValue scalar when scalar.TryGetValue<bool>(value: out var flag) => TypeScriptType.Primary(text: (flag
            ? "true"
            : "false"
        )),
        JsonValue scalar when (scalar.GetValueKind() == JsonValueKind.Number) => TypeScriptType.Primary(text: scalar.ToJsonString()),
        _ => throw new InvalidOperationException(message: $"schema: {path} names a literal TypeScript cannot spell ({value.ToJsonString()})."),
    };
    private static TypeScriptType TypeScriptObject(JsonObject defs, JsonObject obj, string indent, string path) {
        var inner = $"{indent}  ";
        var members = new StringBuilder();
        var properties = (obj["properties"] as JsonObject);
        var required = ((obj["required"] as JsonArray)?.Select(selector: static item => ((string?)item)).OfType<string>().ToHashSet(comparer: StringComparer.Ordinal) ?? []);

        foreach (var (name, schema) in (properties ?? [])) {
            AppendTypeScriptDocumentation(
                indent: inner,
                node: schema,
                text: members
            );

            var key = (TypeScriptIdentifier().IsMatch(input: name)
                ? name
                : TypeScriptString(value: name)
            );
            var optional = (required.Contains(item: name)
                ? string.Empty
                : "?"
            );

            members.Append(value: $"{inner}{key}{optional}: {TypeScriptOf(defs: defs, indent: inner, node: schema, path: $"{path}/properties/{name}").Text};\n");
        }

        var additional = obj["additionalProperties"];
        var closed = (
            (additional is JsonValue additionalValue) &&
            additionalValue.TryGetValue<bool>(value: out var admitsAdditional) &&
            !admitsAdditional &&
            (obj["patternProperties"] is null)
        );

        if (!closed) {
            var indexType = ((
                (additional is JsonObject) &&
                ((properties?.Count ?? 0) == 0) &&
                (obj["patternProperties"] is null)
            )
                ? TypeScriptOf(
                    defs: defs,
                    indent: inner,
                    node: additional,
                    path: $"{path}/additionalProperties"
                ).Text
                : "unknown"
            );

            members.Append(value: $"{inner}[k: string]: {indexType};\n");
        }

        return TypeScriptType.Primary(text: ((members.Length == 0)
            ? "Record<string, never>"
            : $"{{\n{members}{indent}}}"
        ));
    }

    // A JavaScript string literal may not hold either raw: both end a line in ECMAScript source.
    private const char LineSeparator = ((char)0x2028);
    private const char ParagraphSeparator = ((char)0x2029);

    private static string TypeScriptString(string value) {
        var text = new StringBuilder(capacity: (value.Length + 2));

        text.Append(value: '"');

        foreach (var character in value) {
            text.Append(value: character switch {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                (< ' ') or LineSeparator or ParagraphSeparator => $"\\u{((int)character).ToString(format: "x4", provider: CultureInfo.InvariantCulture)}",
                _ => character.ToString(),
            });
        }

        text.Append(value: '"');

        return text.ToString();
    }
    // The TypeScript type one schema node admits; see ToTypeScript's remarks for the mapping.
    private static TypeScriptType TypeScriptOf(JsonObject defs, string indent, JsonNode? node, string path) {
        if ((node is JsonValue boolean) && boolean.TryGetValue<bool>(value: out var admitsAll)) {
            return (admitsAll
                ? TypeScriptType.Unknown
                : TypeScriptType.Primary(text: "never")
            );
        }

        if (node is not JsonObject obj) {
            throw new InvalidOperationException(message: $"schema: {path} is not a schema object.");
        }

        foreach (var (keyword, _) in obj) {
            if (!TypeScriptKnownKeywords.Contains(item: keyword)) {
                throw new InvalidOperationException(message: $"schema: {path} carries '{keyword}', a keyword the TypeScript emitter does not map.");
            }
        }

        var names = obj["type"] switch {
            null => [],
            JsonArray array => array.Select(selector: static item => ((string?)item)).OfType<string>().ToList(),
            var single => [((string?)single)!],
        };

        if (
            (names.Count == 0) &&
            ((obj["properties"] is not null) || (obj["additionalProperties"] is not null) || (obj["patternProperties"] is not null))
        ) {
            names.Add(item: "object");
        }

        if (
            (names.Count == 0) &&
            (obj["items"] is not null)
        ) {
            names.Add(item: "array");
        }

        var parts = new List<TypeScriptType>();

        if (obj["$ref"] is { } reference) {
            var target = ((string?)reference);
            const string Prefix = "#/$defs/";

            if (
                (target is null) ||
                !target.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: Prefix
                ) ||
                !defs.ContainsKey(propertyName: target[Prefix.Length..])
            ) {
                throw new InvalidOperationException(message: $"schema: {path} references '{target}', which names no $defs entry.");
            }

            parts.Add(item: TypeScriptType.Primary(text: target[Prefix.Length..]));
        }

        if (obj["enum"] is JsonArray values) {
            parts.Add(item: TypeScriptType.Union(members: values.Select(selector: value => TypeScriptLiteral(
                path: path,
                value: value
            ))));
        } else if (obj.TryGetPropertyValue(
            jsonNode: out var constant,
            propertyName: "const"
        )) {
            parts.Add(item: TypeScriptLiteral(
                path: path,
                value: constant
            ));
        } else if ((obj["anyOf"] ?? obj["oneOf"]) is JsonArray arms) {
            var keyword = ((obj["anyOf"] is not null)
                ? "anyOf"
                : "oneOf"
            );
            var armTypes = arms.Select(selector: (arm, index) => TypeScriptOf(
                defs: defs,
                indent: indent,
                node: arm,
                path: $"{path}/{keyword}/{index}"
            )).ToList();

            // Beside a union, "type" contributes only what the arms cannot say for themselves: an object whose own
            // properties every arm shares (an intersection below), and a null admission.
            if (names.Contains(item: "null")) {
                armTypes.Add(item: TypeScriptType.Primary(text: "null"));
            }

            parts.Add(item: TypeScriptType.Union(members: armTypes));

            if (obj["properties"] is not null) {
                parts.Add(item: TypeScriptObject(
                    defs: defs,
                    indent: indent,
                    obj: obj,
                    path: path
                ));
            }
        } else if (names.Count > 0) {
            parts.Add(item: TypeScriptType.Union(members: names.Select(selector: name => name switch {
                "array" => TypeScriptArray(
                    defs: defs,
                    indent: indent,
                    obj: obj,
                    path: path
                ),
                "boolean" => TypeScriptType.Primary(text: "boolean"),
                "integer" or "number" => TypeScriptType.Primary(text: "number"),
                "null" => TypeScriptType.Primary(text: "null"),
                "object" => TypeScriptObject(
                    defs: defs,
                    indent: indent,
                    obj: obj,
                    path: path
                ),
                "string" => TypeScriptType.Primary(text: "string"),
                _ => throw new InvalidOperationException(message: $"schema: {path} declares type '{name}', which JSON Schema does not define."),
            })));
        }

        if (obj["allOf"] is JsonArray members) {
            parts.AddRange(collection: members.Select(selector: (member, index) => TypeScriptOf(
                defs: defs,
                indent: indent,
                node: member,
                path: $"{path}/allOf/{index}"
            )).Where(predicate: static member => !member.IsUnknown));
        }

        return TypeScriptType.Intersection(members: parts);
    }

    private enum TypeScriptPrecedence {
        Primary,
        Intersection,
        Union,
    }
    // One emitted type and how tightly it binds; a union keeps its members so a union of unions flattens and
    // repeats (a nullable arm inside a nullable union) collapse.
    private sealed record TypeScriptType(string Text, TypeScriptPrecedence Precedence, IReadOnlyList<string> Members) {
        public static TypeScriptType Unknown { get; } = Primary(text: "unknown");
        public bool IsUnknown => string.Equals(
            a: Text,
            b: "unknown",
            comparisonType: StringComparison.Ordinal
        );

        public static TypeScriptType Intersection(IReadOnlyList<TypeScriptType> members) => members.Count switch {
            0 => Unknown,
            1 => members[0],
            _ => new TypeScriptType(
                Members: [],
                Precedence: TypeScriptPrecedence.Intersection,
                Text: string.Join(
                    separator: " & ",
                    values: members.Select(selector: static member => member.Parenthesized(below: TypeScriptPrecedence.Intersection))
                )
            ),
        };
        public string Parenthesized(TypeScriptPrecedence below) => ((Precedence > below)
            ? $"({Text})"
            : Text
        );
        public static TypeScriptType Primary(string text) => new(
            Members: [text],
            Precedence: TypeScriptPrecedence.Primary,
            Text: text
        );
        public static TypeScriptType Union(IEnumerable<TypeScriptType> members) {
            var flattened = new List<string>();

            foreach (var member in members) {
                foreach (var text in ((member.Precedence == TypeScriptPrecedence.Union)
                    ? member.Members
                    : [member.Parenthesized(below: TypeScriptPrecedence.Union)]
                )) {
                    if (!flattened.Contains(item: text)) {
                        flattened.Add(item: text);
                    }
                }
            }

            // A nullable arm's null reads once, at the end, however many arms admitted it.
            if (flattened.Remove(item: "null")) {
                flattened.Add(item: "null");
            }

            return (flattened.Contains(item: "unknown")
                ? Unknown
                : (flattened.Count switch {
                    0 => Primary(text: "never"),
                    1 => Primary(text: flattened[0]),
                    _ => new TypeScriptType(
                        Members: flattened,
                        Precedence: TypeScriptPrecedence.Union,
                        Text: string.Join(
                            separator: " | ",
                            values: flattened
                        )
                    ),
                })
            );
        }
    }
}
