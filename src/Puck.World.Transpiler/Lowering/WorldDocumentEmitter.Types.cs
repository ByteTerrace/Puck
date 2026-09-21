using System.Globalization;
using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    internal sealed record EnumDefinition(string Name, IReadOnlyList<string> Members);
    internal sealed record RecordFieldDefinition(string Name, string TypeName, IReadOnlyList<StateModifierNode> Modifiers, ExpressionNode? Default, SourceSpan Span);
    internal sealed record RecordDefinition(string Name, IReadOnlyList<RecordFieldDefinition> Fields);

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
    internal static Dictionary<string, OperandExpressionNode> GetOrCreateDerivedState(DocumentScope scope) {
        if (!scope.Annotations.TryGetValue(key: "WorldDerivedState", value: out var obj) || (obj is not Dictionary<string, OperandExpressionNode> dict)) {
            dict = new Dictionary<string, OperandExpressionNode>(comparer: StringComparer.Ordinal);
            scope.Annotations["WorldDerivedState"] = dict;
        }
        return dict;
    }
    internal static Dictionary<string, string> GetOrCreateRecordPools(DocumentScope scope) {
        if (!scope.Annotations.TryGetValue(key: "WorldRecordPools", value: out var obj) || (obj is not Dictionary<string, string> dict)) {
            dict = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
            scope.Annotations["WorldRecordPools"] = dict;
        }
        return dict;
    }
    internal static void IndexTypesAndDerivedState(IEnumerable<StatementNode> statements, DocumentScope scope) {
        var enums = GetOrCreateEnums(scope: scope);
        var records = GetOrCreateRecords(scope: scope);
        var derived = GetOrCreateDerivedState(scope: scope);
        var recordPools = GetOrCreateRecordPools(scope: scope);

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
                    var fieldsList = recordNode.Fields.Select(selector: f => new RecordFieldDefinition(
                        Default: f.Default,
                        Modifiers: f.Modifiers,
                        Name: f.Name,
                        TypeName: f.TypeName,
                        Span: f.Span
                    )).ToList();
                    records[recordNode.Name] = new RecordDefinition(Name: recordNode.Name, Fields: fieldsList);
                    break;

                case DerivedStateNode deriveNode:
                    derived[deriveNode.Name] = deriveNode.Expression;
                    break;

                case StatePoolDeclarationNode poolNode:
                    recordPools[poolNode.Name] = poolNode.RecordName;
                    break;

                case BlockNode block when ((block.Identifier == "state") || (block.Identifier == "world")):
                    IndexTypesAndDerivedState(statements: block.Statements, scope: scope);
                    break;

            }
        }
    }
    internal static JsonObject LowerRecordDeclaration(RecordDeclarationNode declaration, JsonObject state, DocumentScope scope) {
        var records = GetOrCreateRecords(scope: scope);
        var definition = records[declaration.Name];
        var fields = new JsonArray();

        foreach (var field in definition.Fields) {
            var kind = ((field.TypeName is "Bool" or "Fixed" or "Text" or "Vector")
                ? field.TypeName
                : "Int");
            var fieldObj = new JsonObject {
                ["name"] = field.Name,
                ["kind"] = kind,
            };

            if (GetOrCreateEnums(scope: scope).ContainsKey(key: field.TypeName)) {
                fieldObj["enum"] = field.TypeName;
                MaterializeRuntimeEnum(name: field.TypeName, state: state, scope: scope);
            } else if (field.TypeName is not ("Int" or "Bool" or "Fixed" or "Text" or "Vector")) {
                scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.StateDeclarationInvalidDefault, message: $"record '{declaration.Name}' field '{field.Name}' names unknown type or enum '{field.TypeName}'", span: field.Span);
            }
            if (field.Default is { } defaultExpr) {
                var loweredDefault = ((kind == "Vector")
                    ? DocumentLowering.LowerValue(expr: defaultExpr, scope: scope)
                    : LowerStateScalarValue(
                        context: $"record '{declaration.Name}' field '{field.Name}' default",
                        expr: defaultExpr,
                        kind: kind,
                        scope: scope
                    ));

                fieldObj["default"] = TaggedCellValue(kind: kind, value: loweredDefault!);
            }
            foreach (var modifier in field.Modifiers) {
                switch (modifier.Name) {
                    case "bounds":
                        LowerStateBoundsModifier(
                            modifier: modifier,
                            rowObj: fieldObj,
                            kind: kind,
                            rowName: $"{declaration.Name}.{field.Name}",
                            scope: scope
                        );
                        NormalizeRecordFixedBounds(field: fieldObj, kind: kind);
                        break;
                    case "space" when (modifier.Arguments.Count == 1):
                        fieldObj["space"] = DocumentLowering.LowerValue(expr: modifier.Arguments[0].Value, scope: scope);
                        break;
                    case "advance":
                        fieldObj["advance"] = LowerStateAdvanceModifier(
                            context: $"record field '{declaration.Name}.{field.Name}' advance",
                            modifier: modifier,
                            scope: scope
                        );
                        break;
                    default:
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationUnknownModifier,
                            message: $"record field '{declaration.Name}.{field.Name}' does not admit modifier '{modifier.Name}'",
                            span: modifier.Span
                        );
                        break;
                }
            }
            fields.Add(item: fieldObj);
        }

        return new JsonObject {
            ["name"] = declaration.Name,
            ["fields"] = fields,
        };
    }

    private static void MaterializeRuntimeEnum(string name, JsonObject state, DocumentScope scope) {
        var enums = ((state["enums"] as JsonArray) ?? new JsonArray());

        state["enums"] = enums;
        if (enums.Any(predicate: node => (node?["name"]?.ToString() == name))) {
            return;
        }
        var source = GetOrCreateEnums(scope: scope)[name];

        enums.Add(item: ((JsonNode)new JsonObject { ["name"] = name, ["members"] = new JsonArray([.. source.Members.Select(selector: static member => ((JsonNode)member))]) }));
    }
    private static void NormalizeRecordFixedBounds(JsonObject field, string kind) {
        if (kind != "Fixed") {
            return;
        }

        foreach (var member in ((string[])["min", "max"])) {
            if (field[member] is { } value) {
                field[member] = TaggedCellValue(kind: kind, value: value.DeepClone())["value"]!.DeepClone();
            }
        }
    }

    internal static JsonObject LowerPoolDeclaration(StatePoolDeclarationNode declaration, DocumentScope scope) {
        var records = GetOrCreateRecords(scope: scope);

        records.TryGetValue(key: declaration.RecordName, value: out var record);
        var capacity = 0;

        if (declaration.Capacity is { } capacityExpr) {
            var loweredCapacity = DocumentLowering.LowerValue(expr: capacityExpr, scope: scope);

            if ((loweredCapacity is JsonValue value) && value.TryGetValue<long>(value: out var count) && (count > 0) && (count <= int.MaxValue)) {
                capacity = ((int)count);
            } else {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                    message: $"pool '{declaration.Name}' capacity must be a positive whole number",
                    span: declaration.Span
                );
            }
        }

        var seeds = new JsonArray();

        if (declaration.Initializer is { } initializer) {
            var lowered = DocumentLowering.LowerValue(expr: initializer, scope: scope);

            if (lowered is JsonArray instances) {
                for (var slot = 0; (slot < instances.Count); slot++) {
                    if (instances[slot] is not JsonObject instance) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                            message: $"pool '{declaration.Name}' initializer entry {slot} must be an object",
                            span: declaration.Span
                        );
                        continue;
                    }
                    var values = new JsonArray();

                    foreach (var (field, value) in instance) {
                        var recordField = record?.Fields.FirstOrDefault(predicate: candidate => string.Equals(a: candidate.Name, b: field, comparisonType: StringComparison.Ordinal));

                        if ((record is not null) && (recordField is null)) {
                            scope.Diagnostics.ReportError(
                                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                                message: $"pool '{declaration.Name}' initializer names unknown field '{field}' of record '{declaration.RecordName}'",
                                span: declaration.Span
                            );
                            continue;
                        }
                        values.Add(item: new JsonObject {
                            ["field"] = field,
                            ["value"] = TaggedCellValue(
                                kind: ((recordField?.TypeName is "Bool" or "Fixed" or "Text" or "Vector") ? recordField.TypeName : "Int"),
                                value: (value?.DeepClone() ?? JsonValue.Create(0L)!)
                            ),
                        });
                    }
                    seeds.Add(item: new JsonObject { ["slot"] = slot, ["values"] = values });
                }
                if ((capacity > 0) && (instances.Count > capacity)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationCapacityTooSmall,
                        message: $"pool '{declaration.Name}' capacity {capacity} is smaller than its {instances.Count} static instances",
                        span: declaration.Span
                    );
                }
                if (capacity == 0) {
                    capacity = instances.Count;
                }
            }
        }

        if (capacity == 0) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                message: $"pool '{declaration.Name}' must declare a positive capacity",
                span: declaration.Span
            );
            capacity = 1;
        }

        return new JsonObject {
            ["name"] = declaration.Name,
            ["record"] = declaration.RecordName,
            ["capacity"] = capacity,
            ["initial"] = seeds,
        };
    }

    private static JsonObject TaggedCellValue(string kind, JsonNode value) {
        if ((kind == "Fixed") && (value is JsonValue fixedValue) && fixedValue.TryGetValue<string>(value: out var text) &&
            Puck.Maths.FixedQ4816.TryParse(text, CultureInfo.InvariantCulture, out var fixedNumber)) {
            value = JsonValue.Create(fixedNumber.Value)!;
        } else if ((kind == "Fixed") && (value is JsonValue numericValue) && numericValue.TryGetValue<double>(value: out var number)) {
            value = JsonValue.Create(Puck.Maths.FixedQ4816.FromDouble(value: number).Value)!;
        }
        return new JsonObject {
            ["kind"] = kind,
            ["value"] = value,
        };
    }

    internal static JsonObject LowerPairPoolDeclaration(StatePairPoolDeclarationNode declaration, DocumentScope scope) {
        var value = DocumentLowering.LowerValue(expr: declaration.MaxLive, scope: scope);

        if ((value is not JsonValue number) || !number.TryGetValue<long>(value: out var maxLive) || (maxLive <= 0) || (maxLive > int.MaxValue)) {
            scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.StateDeclarationInvalidDefault, message: $"pairPool '{declaration.Name}' maxLive must be a positive whole number", span: declaration.Span);
            maxLive = 1;
        }
        return new JsonObject {
            ["name"] = declaration.Name,
            ["record"] = declaration.RecordName,
            ["leftPool"] = declaration.LeftPool,
            ["rightPool"] = declaration.RightPool,
            ["maxLive"] = maxLive,
            ["directed"] = declaration.Directed,
            ["allowSelf"] = declaration.AllowSelf,
        };
    }

}
