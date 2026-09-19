using System.Text.RegularExpressions;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    internal sealed record EnumDefinition(string Name, IReadOnlyList<string> Members);
    internal sealed record RecordDefinition(string Name, IReadOnlyList<(string Name, string TypeName)> Fields);

    internal static Dictionary<string, EnumDefinition> GetOrCreateEnums(DocumentScope scope) {
        if (!scope.Annotations.TryGetValue(key: "WorldEnums", value: out var obj) || (obj is not Dictionary<string, EnumDefinition> dict)) {
            dict = new Dictionary<string, EnumDefinition>(comparer: StringComparer.Ordinal);
            scope.Annotations["WorldEnums"] = dict;
        }
        return dict;
    }
    internal static Dictionary<string, RecordDefinition> GetOrCreateRecords(DocumentScope scope) {
        if (!scope.Annotations.TryGetValue(key: "WorldRecords", value: out var obj) || (obj is not Dictionary<string, RecordDefinition> dict)) {
            dict = new Dictionary<string, RecordDefinition>(comparer: StringComparer.Ordinal);
            scope.Annotations["WorldRecords"] = dict;
        }
        return dict;
    }
    internal static Dictionary<string, (string ExprText, SourceSpan Span)> GetOrCreateDerivedState(DocumentScope scope) {
        if (!scope.Annotations.TryGetValue(key: "WorldDerivedState", value: out var obj) || (obj is not Dictionary<string, (string ExprText, SourceSpan Span)> dict)) {
            dict = new Dictionary<string, (string ExprText, SourceSpan Span)>(comparer: StringComparer.Ordinal);
            scope.Annotations["WorldDerivedState"] = dict;
        }
        return dict;
    }
    internal static Dictionary<string, string> GetOrCreateRecordTables(DocumentScope scope) {
        if (!scope.Annotations.TryGetValue(key: "WorldRecordTables", value: out var obj) || (obj is not Dictionary<string, string> dict)) {
            dict = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
            scope.Annotations["WorldRecordTables"] = dict;
        }
        return dict;
    }
    internal static void IndexTypesAndDerivedState(IEnumerable<StatementNode> statements, DocumentScope scope) {
        var enums = GetOrCreateEnums(scope: scope);
        var records = GetOrCreateRecords(scope: scope);
        var derived = GetOrCreateDerivedState(scope: scope);
        var recordTables = GetOrCreateRecordTables(scope: scope);

        foreach (var stmt in statements) {
            switch (stmt) {
                case EnumDeclarationNode enumNode:
                    enums[enumNode.Name] = new EnumDefinition(Name: enumNode.Name, Members: [.. enumNode.Members.Select(selector: static member => member.Name)]);
                    for (var i = 0; (i < enumNode.Members.Count); i++) {
                        var member = enumNode.Members[i].Name;

                        scope.Constants[$"{enumNode.Name}.{member}"] = new LiteralExpressionNode(Value: ((long)i));
                        scope.Constants[member] = new LiteralExpressionNode(Value: ((long)i));
                    }
                    break;

                case RecordDeclarationNode recordNode:
                    var fieldsList = recordNode.Fields.Select(selector: f => (f.Name, f.TypeName)).ToList();
                    records[recordNode.Name] = new RecordDefinition(Name: recordNode.Name, Fields: fieldsList);
                    break;

                case DerivedStateNode deriveNode:
                    derived[deriveNode.Name] = ((deriveNode.RawExpression ?? (deriveNode.Expression.ToString() ?? string.Empty)), deriveNode.Span);
                    break;

                case BlockNode block when ((block.Identifier == "state") || (block.Identifier == "world")):
                    IndexTypesAndDerivedState(statements: block.Statements, scope: scope);
                    break;

                case StateTableDeclarationNode table:
                    if (records.ContainsKey(key: table.Kind)) {
                        recordTables[table.Name] = table.Kind;
                    }
                    break;
            }
        }
    }
    internal static bool IsRecordType(string kind, DocumentScope scope) {
        var records = GetOrCreateRecords(scope: scope);

        return records.ContainsKey(key: kind);
    }
    internal static IEnumerable<StateTableDeclarationNode> ExpandRecordTable(StateTableDeclarationNode table, DocumentScope scope) {
        var records = GetOrCreateRecords(scope: scope);

        if (!records.TryGetValue(key: table.Kind, value: out var recDef)) {
            yield return table;
            yield break;
        }

        var enums = GetOrCreateEnums(scope: scope);
        var cells = new List<StateCellEntryNode>();

        if (table.FamilySize is LiteralExpressionNode { Value: long count }) {
            for (var i = 0; (i < count); i++) {
                cells.Add(item: new StateCellEntryNode(
                    Column: table.Column,
                    Key: i.ToString(),
                    Length: 0,
                    Line: table.Line,
                    Modifiers: [],
                    Offset: table.Offset,
                    Value: new LiteralExpressionNode(Value: 0L)
                ));
            }
        } else if (table.Cells.Count > 0) {
            cells.AddRange(collection: table.Cells);
        }

        foreach (var (fieldName, fieldType) in recDef.Fields) {
            var colName = $"{table.Name}_{fieldName}";
            var colKind = "Int";

            if (enums.ContainsKey(key: fieldType)) {
                colKind = "Int";
            } else if (fieldType is "Bool" or "Fixed" or "Text" or "Vector") {
                colKind = fieldType;
            }

            yield return new StateTableDeclarationNode(
                Cells: cells,
                Column: table.Column,
                FamilySize: null,
                Initializer: null,
                Kind: colKind,
                Length: table.Length,
                Line: table.Line,
                Modifiers: table.Modifiers,
                Name: colName,
                Offset: table.Offset
            );
        }
    }

    private static readonly Regex RecordFieldWithIndexRegex = new(
        options: RegexOptions.Compiled,
        pattern: @"\b([A-Za-z_][A-Za-z0-9_]*)\[([^\]]+)\]\.([A-Za-z_][A-Za-z0-9_]*)\b"
    );
    private static readonly Regex RecordFieldBareRegex = new(
        options: RegexOptions.Compiled,
        pattern: @"\b([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)\b(?!\s*[\(\[])"
    );

    internal static string ResolveRecordFieldAccessesInText(string text, DocumentScope scope) {
        if (string.IsNullOrEmpty(value: text)) {
            return text;
        }

        var recordTables = GetOrCreateRecordTables(scope: scope);

        if (recordTables.Count == 0) {
            return text;
        }

        var rewritten = RecordFieldWithIndexRegex.Replace(
            input: text,
            evaluator: m => {
                var tableName = m.Groups[1].Value;
                var indexPart = m.Groups[2].Value;
                var fieldName = m.Groups[3].Value;

                if (recordTables.ContainsKey(key: tableName)) {
                    return $"{tableName}_{fieldName}[{indexPart}]";
                }
                return m.Value;
            }
        );

        rewritten = RecordFieldBareRegex.Replace(
            input: rewritten,
            evaluator: m => {
                var tableName = m.Groups[1].Value;
                var fieldName = m.Groups[2].Value;

                if (recordTables.ContainsKey(key: tableName)) {
                    return $"{tableName}_{fieldName}";
                }
                return m.Value;
            }
        );

        return rewritten;
    }

    private static readonly Regex EnumQualifiedRegex = new(
        options: RegexOptions.Compiled,
        pattern: @"\b([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)\b"
    );

    internal static string ResolveEnumsInText(string text, DocumentScope scope) {
        if (string.IsNullOrEmpty(value: text)) {
            return text;
        }

        var enums = GetOrCreateEnums(scope: scope);

        if (enums.Count == 0) {
            return text;
        }

        return EnumQualifiedRegex.Replace(
            input: text,
            evaluator: m => {
                var enumName = m.Groups[1].Value;
                var memberName = m.Groups[2].Value;

                if (enums.TryGetValue(key: enumName, value: out var def)) {
                    for (var i = 0; (i < def.Members.Count); i++) {
                        if (string.Equals(a: def.Members[i], b: memberName, comparisonType: StringComparison.Ordinal)) {
                            return i.ToString();
                        }
                    }
                }
                return m.Value;
            }
        );
    }

    private static readonly Regex DerivedIdentifierRegex = new(
        options: RegexOptions.Compiled,
        pattern: @"(?<![\w$`.\[])[A-Za-z_][A-Za-z0-9_]*(?![\w(\[:`])"
    );

    internal static string ResolveDerivedStateInText(string text, DocumentScope scope, HashSet<string>? visiting = null) {
        if (string.IsNullOrEmpty(value: text)) {
            return text;
        }

        var derived = GetOrCreateDerivedState(scope: scope);

        if (derived.Count == 0) {
            return text;
        }

        visiting ??= new HashSet<string>(comparer: StringComparer.Ordinal);

        return DerivedIdentifierRegex.Replace(
            input: text,
            evaluator: m => {
                var name = m.Value;

                if (!derived.TryGetValue(key: name, value: out var def)) {
                    return name;
                }

                if (visiting.Contains(item: name)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.DerivedStateCycle,
                        message: $"Cyclic dependency detected in derived state '{name}'",
                        span: def.Span
                    );
                    return name;
                }

                visiting.Add(item: name);
                var expanded = ResolveDerivedStateInText(scope: scope, text: def.ExprText, visiting: visiting);

                visiting.Remove(item: name);

                return $"({expanded})";
            }
        );
    }

    private static readonly Regex CollectionAllRegex = new(
        options: RegexOptions.Compiled,
        pattern: @"\ball\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:->|=>)\s*([^)]+)\)"
    );
    private static readonly Regex CollectionAnyRegex = new(
        options: RegexOptions.Compiled,
        pattern: @"\bany\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:->|=>)\s*([^)]+)\)"
    );
    private static readonly Regex CollectionCountRegex = new(
        options: RegexOptions.Compiled,
        pattern: @"\bcount\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:->|=>)\s*([^)]+)\)"
    );
    private static readonly Regex CollectionSumRegex = new(
        options: RegexOptions.Compiled,
        pattern: @"\bsum\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:->|=>)\s*([^)]+)\)"
    );

    internal static string ResolveCollectionOperationsInText(string text, DocumentScope scope) {
        if (string.IsNullOrEmpty(value: text)) {
            return text;
        }

        var families = GetOrCreateStateFamilies(scope: scope);

        if (families.Count == 0) {
            return text;
        }

        var rewritten = CollectionAllRegex.Replace(
            input: text,
            evaluator: m => {
                var famName = m.Groups[1].Value;
                var varName = m.Groups[2].Value;
                var body = m.Groups[3].Value.Trim();

                if (!families.TryGetValue(key: famName, value: out var fam)) {
                    return m.Value;
                }

                var terms = Enumerable.Range(0, fam.Size)
                    .Select(selector: i => $"({Regex.Replace(input: body, pattern: $@"\b{varName}\b", replacement: $"{famName}{i}")})");

                return $"({string.Join(separator: " and ", values: terms)})";
            }
        );

        rewritten = CollectionAnyRegex.Replace(
            input: rewritten,
            evaluator: m => {
                var famName = m.Groups[1].Value;
                var varName = m.Groups[2].Value;
                var body = m.Groups[3].Value.Trim();

                if (!families.TryGetValue(key: famName, value: out var fam)) {
                    return m.Value;
                }

                var terms = Enumerable.Range(0, fam.Size)
                    .Select(selector: i => $"({Regex.Replace(input: body, pattern: $@"\b{varName}\b", replacement: $"{famName}{i}")})");

                return $"({string.Join(separator: " or ", values: terms)})";
            }
        );

        rewritten = CollectionCountRegex.Replace(
            input: rewritten,
            evaluator: m => {
                var famName = m.Groups[1].Value;
                var varName = m.Groups[2].Value;
                var body = m.Groups[3].Value.Trim();

                if (!families.TryGetValue(key: famName, value: out var fam)) {
                    return m.Value;
                }

                var terms = Enumerable.Range(0, fam.Size)
                    .Select(selector: i => $"({Regex.Replace(input: body, pattern: $@"\b{varName}\b", replacement: $"{famName}{i}")} ? 1 : 0)");

                return $"({string.Join(separator: " + ", values: terms)})";
            }
        );

        rewritten = CollectionSumRegex.Replace(
            input: rewritten,
            evaluator: m => {
                var famName = m.Groups[1].Value;
                var varName = m.Groups[2].Value;
                var body = m.Groups[3].Value.Trim();

                if (!families.TryGetValue(key: famName, value: out var fam)) {
                    return m.Value;
                }

                var terms = Enumerable.Range(0, fam.Size)
                    .Select(selector: i => $"({Regex.Replace(input: body, pattern: $@"\b{varName}\b", replacement: $"{famName}{i}")})");

                return $"({string.Join(separator: " + ", values: terms)})";
            }
        );

        return rewritten;
    }
}
