using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.World.Transpiler.Vocabulary;

namespace Puck.World.Transpiler.Decompiler;

// A rule group and the rules it claims print as ONE construct — `stabilize name { rule … }` or
// `workflow name { step … }` — so the `rules` array prints only what no group claims. Every claim the sugar cannot
// spell back (a member whose name does not carry its group's prefix, a staged trigger, a fixpoint trigger that is
// not a negation) sends both sections through the generic value path instead, which carries them unchanged.
public static partial class WorldDecompiler {
    // A name the `stabilize`/`workflow`/`step` header can carry bare; anything else is quoted, and the sugar
    // refuses a name no quoting can spell back.
    internal static bool IsBareName(string name) {
        if ((name.Length == 0) || !(char.IsLetter(c: name[0]) || (name[0] == '_'))) {
            return false;
        }

        foreach (var character in name) {
            if (!(char.IsLetterOrDigit(c: character) || (character == '_'))) {
                return false;
            }
        }

        return true;
    }
    internal static string QuotedName(string name) => (IsBareName(name: name)
        ? name
        : $"\"{EscapeString(s: name)}\""
    );
    // A header name is one line of source, quoted or bare, so a name carrying a line break has no spelling at all
    // and sends its whole section down the generic value path.
    internal static bool IsSpellableName(string name) => !name.AsSpan().ContainsAny(value0: '\r', value1: '\n');

    // A group node has two spellings, so what a group may carry is the union of their descriptions.
    private static readonly HashSet<string> RuleGroupKnownKeys = [
        .. WorldConstructs.NodeKeysOf(enclosing: null, keyword: "stabilize"),
        .. WorldConstructs.NodeKeysOf(enclosing: null, keyword: "workflow"),
    ];

    /// <summary>Determines whether every declared group, and every rule it claims, can be written back as
    /// <c>stabilize</c>/<c>workflow</c> source.</summary>
    /// <param name="groups">The document's <c>ruleGroups</c> array.</param>
    /// <param name="rules">The document's <c>rules</c> array, or <see langword="null"/> when it declares none.</param>
    /// <param name="claimed">The rule names the groups claim, keyed by rule name, on success.</param>
    /// <returns><see langword="true"/> when the whole pair prints as sugar.</returns>
    private static bool CanSugarRuleGroups(JsonArray groups, JsonArray? rules, out Dictionary<string, JsonObject> claimed) {
        claimed = new Dictionary<string, JsonObject>(comparer: StringComparer.Ordinal);

        var byName = new Dictionary<string, JsonObject>(comparer: StringComparer.Ordinal);

        foreach (var item in (rules ?? [])) {
            if (
                (item is JsonObject rule) &&
                (rule["name"]?.ToString() is { Length: > 0 } name)
            ) {
                byName[name] = rule;
            }
        }

        foreach (var item in groups) {
            if (
                (item is not JsonObject group) ||
                (group["name"]?.ToString() is not { Length: > 0 } groupName) ||
                !IsSpellableName(name: groupName) ||
                (group["steps"] is not JsonArray steps) ||
                (steps.Count == 0)
            ) {
                return false;
            }

            foreach (var (key, _) in group) {
                if (!RuleGroupKnownKeys.Contains(item: key)) {
                    return false;
                }
            }

            var shape = group["shape"]?.ToString();

            if (shape == "Staged") {
                // A workflow authors no trigger and no pass ceiling; either one means this document was not written
                // as one.
                if ((group["trigger"] is not null) || (group["passes"] is not null)) {
                    return false;
                }
            } else if (shape == "Fixpoint") {
                if (
                    (group["trigger"] is { } trigger) &&
                    ((trigger is not JsonObject negation) ||
                    (negation["$type"]?.ToString() != "not") ||
                    (negation["predicate"] is not JsonObject inner) ||
                    !IsPredicateSafeForGateSugar(node: inner))
                ) {
                    return false;
                }
            } else {
                return false;
            }

            foreach (var step in steps) {
                if (
                    (step is not JsonObject stepObj) ||
                    (stepObj["rule"]?.ToString() is not { Length: > 0 } member)
                ) {
                    return false;
                }

                foreach (var (key, _) in stepObj) {
                    if ((key != "rule") && (key != "onRefusal")) {
                        return false;
                    }
                }

                if (
                    !member.StartsWith(comparisonType: StringComparison.Ordinal, value: $"{groupName}_") ||
                    (member.Length == (groupName.Length + 1)) ||
                    !byName.TryGetValue(key: member, value: out var rule) ||
                    !claimed.TryAdd(key: member, value: rule)
                ) {
                    return false;
                }

                if ((shape == "Staged") && (stepObj["onRefusal"]?.ToString() is { } policy) && (policy != "Stall") && (policy != "Skip")) {
                    return false;
                }
            }
        }

        return true;
    }
    private static void DecompileRuleGroupsBlock(StringBuilder sb, JsonArray groups, IReadOnlyDictionary<string, JsonObject> claimed, int indentLevel) {
        var first = true;

        foreach (var item in groups) {
            if (item is not JsonObject group) {
                continue;
            }
            if (!first) {
                sb.AppendLine();
            }

            first = false;

            AppendRuleGroupBlock(
                claimed: claimed,
                group: group,
                indentLevel: indentLevel,
                sb: sb
            );
        }
    }
    private static void AppendRuleGroupBlock(StringBuilder sb, JsonObject group, IReadOnlyDictionary<string, JsonObject> claimed, int indentLevel) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );
        var name = (group["name"]?.ToString() ?? "");
        var staged = (group["shape"]?.ToString() == "Staged");
        var header = new StringBuilder();

        _ = header.Append(value: indent)
            .Append(value: (staged
                ? "workflow "
                : "stabilize "))
            .Append(value: QuotedName(name: name));

        if (group["undo"] is { } undo) {
            _ = header.Append(value: " undo(").Append(value: FormatUndo(indentLevel: indentLevel, node: undo)).Append(value: ')');
        }

        if (!staged) {
            if (group["passes"]?.ToString() is { Length: > 0 } passes) {
                _ = header.Append(value: $" maxPasses({passes})");
            }
            if ((group["trigger"] is JsonObject trigger) && (trigger["predicate"] is JsonObject predicate)) {
                _ = header.Append(value: " until ")
                    .Append(value: FormatPredicate(node: predicate));
            }
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{header} {{"
        );

        var firstStep = true;

        foreach (var step in ((JsonArray)group["steps"]!)) {
            if (
                (step is not JsonObject stepObj) ||
                (stepObj["rule"]?.ToString() is not { } member) ||
                !claimed.TryGetValue(key: member, value: out var rule)
            ) {
                continue;
            }
            if (!firstStep) {
                sb.AppendLine();
            }

            firstStep = false;

            var local = member[(name.Length + 1)..];

            if (staged) {
                var stepBlock = new StringBuilder();

                using (ExpressionSpelling.WithLocals(locals: RuleLocalNames(rule: rule))) {
                    stepBlock.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"{new string(c: ' ', count: ((indentLevel + 1) * 4))}step {QuotedName(name: local)}{((stepObj["onRefusal"]?.ToString() == "Skip") ? " skip" : "")} {{"
                    );
                    AppendRuleBody(
                        indentLevel: (indentLevel + 2),
                        rule: rule,
                        sb: stepBlock
                    );
                    stepBlock.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"{new string(c: ' ', count: ((indentLevel + 1) * 4))}}}"
                    );

                    sb.Append(value: Respell(text: stepBlock.ToString()));
                }
            } else {
                AppendRuleBlock(
                    indentLevel: (indentLevel + 1),
                    rule: RenamedRule(
                        name: local,
                        rule: rule
                    ),
                    sb: sb
                );
            }
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}}}"
        );
    }
    private static string FormatUndo(JsonNode node, int indentLevel) {
        if (
            (node is JsonObject undo) &&
            (undo["rows"] is JsonArray rows) &&
            (undo["depth"] is { } depth)
        ) {
            return $"{{ rows {FormatValue(indentLevel: indentLevel, node: rows)} depth: {FormatValue(indentLevel: indentLevel, node: depth)} }}";
        }

        return FormatValue(indentLevel: indentLevel, node: node);
    }
    private static JsonObject RenamedRule(JsonObject rule, string name) {
        var renamed = ((JsonObject)rule.DeepClone());

        renamed["name"] = JsonValue.Create(value: name);

        return renamed;
    }
}
