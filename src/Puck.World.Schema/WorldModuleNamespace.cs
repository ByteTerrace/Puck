using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Puck.World;

/// <summary>
/// Composes an imported fragment under an alias (<see cref="WorldImport.As"/>): every name the fragment declares —
/// each <see cref="WorldNameRole.Declares"/> site the <see cref="WorldNameRegistry"/> lists — becomes
/// <c>&lt;alias&gt;_&lt;name&gt;</c>, and every other registered site that spells one of those names is rewritten to
/// match: a bare name position, a reserved <c>$</c> channel's colon segments, a cell key's <c>$cell:</c>/<c>cell:</c>
/// spelling, an infix expression's state reads and topology arguments, a postfix token's row, and a
/// <c>state.&lt;row&gt;</c> binding. A name the fragment does not declare is left as written, so a fragment may
/// still address its host's rows by name. Runs on the fragment's composed raw JSON tree before the strict parse, in
/// the same pass that strips <c>basis</c>/<c>imports</c>.
/// </summary>
public static class WorldModuleNamespace {
    private static readonly JsonSerializerOptions Options = WorldJsonContext.Default.Options;

    /// <summary>Prefixes every name <paramref name="module"/> declares with <paramref name="alias"/> and rewrites
    /// the module's references to match, in place.</summary>
    /// <param name="module">The fragment's composed tree; mutated.</param>
    /// <param name="alias">The alias, admissible under <see cref="WorldImport.TryValidateAlias"/>.</param>
    /// <param name="reason">The one-line refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the alias was admissible and the rewrite applied.</returns>
    public static bool TryApply(JsonObject module, string alias, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: module);

        if (!WorldImport.TryValidateAlias(alias: alias, reason: out reason)) {
            return false;
        }

        var declared = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        Visit(node: module, type: typeof(WorldDefinition), visitor: (parent, name, value, field) => {
            if ((field.Role == WorldNameRole.Declares) && (value is JsonValue leaf) && leaf.TryGetValue<string>(value: out var text) && (text.Length > 0)) {
                declared[text] = $"{alias}{WorldNameRegistry.AliasSeparator}{text}";
            }
        });

        if (declared.Count == 0) {
            reason = string.Empty;

            return true;
        }

        var rewriter = new Rewriter(declared: declared);

        Visit(node: module, type: typeof(WorldDefinition), visitor: (parent, name, value, field) => {
            switch (value) {
                case JsonArray list when (field.Role == WorldNameRole.Names):
                    for (var index = 0; (index < list.Count); index++) {
                        if ((list[index: index] is JsonValue element) && element.TryGetValue<string>(value: out var item)) {
                            list[index: index] = rewriter.Rewrite(text: item, role: field.Role);
                        }
                    }

                    break;
                case JsonValue leaf when leaf.TryGetValue<string>(value: out var text):
                    parent[propertyName: name] = rewriter.Rewrite(text: text, role: field.Role);

                    break;
            }
        });

        reason = string.Empty;

        return true;
    }
    /// <summary>Rewrites one value of a registered role against a declared-name map — the unit
    /// <see cref="TryApply"/> applies at every site, exposed so a law can hold each spelling on its own.</summary>
    /// <param name="text">The authored value.</param>
    /// <param name="role">How the value carries names.</param>
    /// <param name="declared">Each declared name paired with its prefixed spelling.</param>
    /// <returns>The rewritten value; <paramref name="text"/> unchanged when it spells no declared name.</returns>
    public static string Rewrite(string text, WorldNameRole role, IReadOnlyDictionary<string, string> declared) {
        ArgumentNullException.ThrowIfNull(argument: text);
        ArgumentNullException.ThrowIfNull(argument: declared);

        return new Rewriter(declared: declared).Rewrite(text: text, role: role);
    }

    /// <summary>Walks a raw tree type-directed, calling <paramref name="visitor"/> at every registered name-bearing
    /// site with the holding object, the member's JSON name, the value, and the registration: each present member
    /// is resolved against the registry by its C# member, a <c>$type</c> discriminator selects the arm, and the two
    /// converter-backed shapes (an expression's token object, a reaction scalar's row object) are followed by
    /// hand.</summary>
    /// <param name="node">The tree, or the subtree to walk.</param>
    /// <param name="type">The model type <paramref name="node"/> holds.</param>
    /// <param name="visitor">Called once per registered site, in document order.</param>
    public static void Visit(JsonNode? node, Type type, Action<JsonObject, string, JsonNode, WorldNameField> visitor) {
        type = (Nullable.GetUnderlyingType(nullableType: type) ?? type);

        if (node is null) {
            return;
        }

        if (type == typeof(ValueExpression)) {
            if (node is JsonObject) {
                Visit(node: node, type: typeof(ValueExpressionTokens), visitor: visitor);
            }

            return;
        }

        if (type == typeof(WorldLatticeScalar)) {
            if ((node is JsonObject scalar) && scalar.TryGetPropertyValue(propertyName: "row", jsonNode: out var row) && (row is not null) && WorldNameRegistry.TryFind(declaringType: typeof(WorldLatticeScalar), member: nameof(WorldLatticeScalar.Row), field: out var scalarField)) {
                visitor(scalar, "row", row, scalarField);
            }

            return;
        }

        if (type.IsPrimitive || type.IsEnum || (type == typeof(string)) || (type == typeof(decimal))) {
            return;
        }

        JsonTypeInfo typeInfo;

        try {
            typeInfo = Options.GetTypeInfo(type: type);
        } catch (Exception exception) when ((exception is NotSupportedException or InvalidOperationException)) {
            return;
        }

        if (typeof(StateRow).IsAssignableFrom(c: type) && (node is JsonObject rowObject)) {
            foreach (var (jsonName, declaringType, member, propertyType) in WorldNameRegistry.ReflectedRowMembers(type: type)) {
                if (!rowObject.TryGetPropertyValue(propertyName: jsonName, jsonNode: out var value) || (value is null)) {
                    continue;
                }

                if (WorldNameRegistry.TryResolve(declaringType: declaringType, member: member, propertyType: propertyType, field: out var field)) {
                    visitor(rowObject, jsonName, value, field);
                }

                Visit(node: value, type: propertyType, visitor: visitor);
            }

            return;
        }

        switch (typeInfo.Kind) {
            case JsonTypeInfoKind.Object when (node is JsonObject obj):
                if ((typeInfo.PolymorphismOptions is { } polymorphism) && obj.TryGetPropertyValue(propertyName: "$type", jsonNode: out var discriminatorNode) && (discriminatorNode is JsonValue discriminatorValue) && discriminatorValue.TryGetValue<string>(value: out var discriminator)) {
                    foreach (var derived in polymorphism.DerivedTypes) {
                        if ((derived.TypeDiscriminator is string arm) && string.Equals(a: arm, b: discriminator, comparisonType: StringComparison.Ordinal) && (derived.DerivedType != type)) {
                            Visit(node: node, type: derived.DerivedType, visitor: visitor);

                            return;
                        }
                    }
                }

                foreach (var property in typeInfo.Properties) {
                    if (property.IsExtensionData || (property.Get is null) || (property.Set is null) || !obj.TryGetPropertyValue(propertyName: property.Name, jsonNode: out var value) || (value is null)) {
                        continue;
                    }

                    var (declaringType, member) = WorldNameRegistry.ResolveMember(property: property);

                    if (WorldNameRegistry.TryResolve(declaringType: declaringType, member: member, propertyType: property.PropertyType, field: out var field)) {
                        visitor(obj, property.Name, value, field);
                    }

                    Visit(node: value, type: property.PropertyType, visitor: visitor);
                }

                break;
            case JsonTypeInfoKind.Enumerable when (node is JsonArray list):
                foreach (var element in list) {
                    Visit(node: element, type: typeInfo.ElementType!, visitor: visitor);
                }

                break;
            case JsonTypeInfoKind.Dictionary when (node is JsonObject entries):
                foreach (var entry in entries) {
                    Visit(node: entry.Value, type: typeInfo.ElementType!, visitor: visitor);
                }

                break;
        }
    }

    private sealed class Rewriter(IReadOnlyDictionary<string, string> declared) {
        private const string BindingPrefix = "state.";

        private string Map(string name) =>
            (declared.TryGetValue(key: name, value: out var prefixed) ? prefixed : name);

        public string Rewrite(string text, WorldNameRole role) => role switch {
            WorldNameRole.Declares => Map(name: text),
            WorldNameRole.Names => (text.StartsWith(value: '$') ? RewriteReserved(name: text) : Map(name: text)),
            WorldNameRole.Key => RewriteKey(key: text),
            WorldNameRole.Expression => RewriteExpression(text: text),
            WorldNameRole.Binding => RewriteBinding(token: text),
            WorldNameRole.Template => RewriteTemplate(template: text),
            _ => text,
        };

        // A literal key is local to its row and stays; a reserved spelling ($cell:, $zone:, $pair:, $expr:, $zones[)
        // or a body-reference spelling (cell:<row>:<key>, argmax:<row>) carries names in its segments.
        private string RewriteKey(string key) {
            if (key.StartsWith(value: RuleFacts.ExpressionKeyPrefix, comparisonType: StringComparison.Ordinal)) {
                return $"{RuleFacts.ExpressionKeyPrefix}{RewriteExpression(text: key[RuleFacts.ExpressionKeyPrefix.Length..])}";
            }

            if (key.StartsWith(value: '$') || key.Contains(value: ':')) {
                return RewriteReserved(name: key);
            }

            return key;
        }
        // Colon segments outside brackets are names; a bracketed span is a live-zone index, a cell key in its own
        // right. A $bind: read names a rule-local binding, never a row.
        private string RewriteReserved(string name) {
            if (name.StartsWith(value: RuleFacts.BindPrefix, comparisonType: StringComparison.Ordinal)) {
                return name;
            }

            var output = new StringBuilder(capacity: name.Length);
            var segment = new StringBuilder();
            var index = 0;

            while (index < name.Length) {
                var character = name[index];

                if (character == '[') {
                    var close = MatchingBracket(text: name, open: index);

                    if (close < 0) {
                        _ = segment.Append(value: name, startIndex: index, count: (name.Length - index));

                        break;
                    }

                    _ = output.Append(value: Map(name: segment.ToString()));
                    _ = segment.Clear();
                    _ = output.Append(value: '[').Append(value: RewriteBracket(inner: name[(index + 1)..close])).Append(value: ']');
                    index = (close + 1);

                    continue;
                }

                if (character == ':') {
                    _ = output.Append(value: Map(name: segment.ToString())).Append(value: ':');
                    _ = segment.Clear();
                    index++;

                    continue;
                }

                _ = segment.Append(value: character);
                index++;
            }

            _ = output.Append(value: Map(name: segment.ToString()));

            return output.ToString();
        }
        // The text between a name's brackets: a bare or backquoted name is a cell key and stays; a reserved token
        // rewrites as a key; anything else is an expression.
        private string RewriteBracket(string inner) {
            if (!ExpressionSpelling.TryParseKey(text: inner, key: out var key, error: out _)) {
                return inner;
            }

            if (key.StartsWith(value: RuleFacts.ExpressionKeyPrefix, comparisonType: StringComparison.Ordinal) || key.StartsWith(value: RuleFacts.CellKeyPrefix, comparisonType: StringComparison.Ordinal)) {
                return RewriteExpression(text: inner);
            }

            if (key.StartsWith(value: '$')) {
                return RewriteReserved(name: inner.Trim());
            }

            return inner;
        }
        private string RewriteExpression(string text) {
            var output = new StringBuilder(capacity: text.Length);
            var index = 0;

            while (index < text.Length) {
                var character = text[index];

                if (character == '`') {
                    var close = text.IndexOf(value: '`', startIndex: (index + 1));

                    if (close < 0) {
                        _ = output.Append(value: text, startIndex: index, count: (text.Length - index));

                        break;
                    }

                    _ = output.Append(value: '`').Append(value: Map(name: text[(index + 1)..close])).Append(value: '`');
                    index = (close + 1);

                    continue;
                }

                if (IsNameStart(character: character)) {
                    var end = NameEnd(text: text, start: index);
                    var name = text[index..end];

                    _ = output.Append(value: (name.StartsWith(value: '$') ? RewriteReserved(name: name) : Map(name: name)));
                    index = end;

                    if ((index < text.Length) && (text[index] == '[')) {
                        var close = MatchingBracket(text: text, open: index);

                        if (close < 0) {
                            continue;
                        }

                        _ = output.Append(value: '[').Append(value: RewriteBracket(inner: text[(index + 1)..close])).Append(value: ']');
                        index = (close + 1);
                    }

                    continue;
                }

                if (char.IsAsciiDigit(c: character)) {
                    var end = index;

                    while ((end < text.Length) && (char.IsAsciiLetterOrDigit(c: text[end]) || (text[end] == '.'))) {
                        end++;
                    }

                    _ = output.Append(value: text, startIndex: index, count: (end - index));
                    index = end;

                    continue;
                }

                _ = output.Append(value: character);
                index++;
            }

            return output.ToString();
        }
        private string RewriteBinding(string token) {
            if (!token.StartsWith(value: BindingPrefix, comparisonType: StringComparison.Ordinal)) {
                return token;
            }

            var rest = token[BindingPrefix.Length..];
            var dot = rest.IndexOf(value: '.');
            var row = ((dot < 0) ? rest : rest[..dot]);
            var tail = ((dot < 0) ? string.Empty : rest[dot..]);

            return $"{BindingPrefix}{Map(name: row)}{tail}";
        }
        private string RewriteTemplate(string template) {
            var output = new StringBuilder(capacity: template.Length);
            var index = 0;

            while (index < template.Length) {
                var character = template[index];

                if ((character == '{') && ((index + 1) < template.Length) && (template[index + 1] == '{')) {
                    _ = output.Append(value: "{{");
                    index += 2;

                    continue;
                }

                if (character == '{') {
                    var close = template.IndexOf(value: '}', startIndex: index);

                    if (close < 0) {
                        _ = output.Append(value: template, startIndex: index, count: (template.Length - index));

                        break;
                    }

                    _ = output.Append(value: '{').Append(value: RewriteBinding(token: template[(index + 1)..close])).Append(value: '}');
                    index = (close + 1);

                    continue;
                }

                _ = output.Append(value: character);
                index++;
            }

            return output.ToString();
        }

        // The span of one bare name in the infix grammar: letters, digits, '_', '$', '.'; a reserved name also takes
        // ':' before a name character or a signed digit segment, and a "$zones[" index span whole.
        private static int NameEnd(string text, int start) {
            var reserved = (text[start] == '$');
            var index = (start + 1);

            while (index < text.Length) {
                var character = text[index];

                if (IsNamePart(character: character)) {
                    index++;

                    continue;
                }

                if (reserved && (character == ':') && ((index + 1) < text.Length) && (IsNamePart(character: text[index + 1]) || IsSignedSegment(text: text, index: (index + 1)))) {
                    index++;

                    continue;
                }

                if (reserved && (character == '-') && (text[index - 1] == ':') && ((index + 1) < text.Length) && char.IsAsciiDigit(c: text[index + 1])) {
                    index++;

                    continue;
                }

                if (reserved && (character == '[') && EndsWithLiveZonePrefix(text: text, bracket: index)) {
                    var close = MatchingBracket(text: text, open: index);

                    if (close < 0) {
                        break;
                    }

                    index = (close + 1);

                    continue;
                }

                break;
            }

            return index;
        }
        private static bool EndsWithLiveZonePrefix(string text, int bracket) {
            var prefix = (RuleFacts.LiveZonePrefix.Length - 1);

            return ((bracket >= prefix) && (string.CompareOrdinal(strA: text, indexA: (bracket - prefix), strB: RuleFacts.LiveZonePrefix, indexB: 0, length: prefix) == 0));
        }
        private static bool IsSignedSegment(string text, int index) =>
            (((index + 1) < text.Length) && (text[index] == '-') && char.IsAsciiDigit(c: text[index + 1]));
        private static bool IsNameStart(char character) =>
            (char.IsLetter(c: character) || (character == '_') || (character == '$'));
        private static bool IsNamePart(char character) =>
            (char.IsLetterOrDigit(c: character) || (character == '_') || (character == '$') || (character == '.'));
        private static int MatchingBracket(string text, int open) {
            var depth = 0;

            for (var index = open; (index < text.Length); index++) {
                if (text[index] == '[') {
                    depth++;
                } else if ((text[index] == ']') && (--depth == 0)) {
                    return index;
                }
            }

            return -1;
        }
    }
}
