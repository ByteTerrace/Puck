using System.Globalization;
using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    // An inherited enum is one the document's basis declares: its members read as they read in the basis, and the
    // composed document, not this one, carries it into `state.enums`.
    internal sealed record EnumDefinition(string Name, IReadOnlyList<string> Members, bool Inherited = false);
    internal sealed record RecordFieldDefinition(string Name, string TypeName, IReadOnlyList<StateModifierNode> Modifiers, ExpressionNode? Default, SourceSpan Span);
    internal sealed record RecordDefinition(string Name, IReadOnlyList<RecordFieldDefinition> Fields);

    // The bare-member constants an enum declaration bound, each to its declaration, so a later declaration can tell a
    // member it replaces from a let or module parameter it would shadow. Keyed by the constant's own node, so it is
    // right in every scope that shares the binding.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ExpressionNode, EnumDeclarationNode> EnumMemberConstants = new();

    internal static Dictionary<string, EnumDefinition> GetOrCreateEnums(DocumentScope scope) =>
        GetOrCreateScopeDictionary<EnumDefinition>(key: "WorldEnums", scope: scope);
    internal static Dictionary<string, RecordDefinition> GetOrCreateRecords(DocumentScope scope) =>
        GetOrCreateScopeDictionary<RecordDefinition>(key: "WorldRecords", scope: scope);
    internal static Dictionary<string, OperandExpressionNode> GetOrCreateDerivedState(DocumentScope scope) =>
        GetOrCreateScopeDictionary<OperandExpressionNode>(key: "WorldDerivedState", scope: scope);
    internal static Dictionary<string, string> GetOrCreateRecordPools(DocumentScope scope) =>
        GetOrCreateScopeDictionary<string>(key: "WorldRecordPools", scope: scope);
    internal static void IndexTypesAndDerivedState(IEnumerable<StatementNode> statements, DocumentScope scope) {
        var enums = GetOrCreateEnums(scope: scope);
        var records = GetOrCreateRecords(scope: scope);
        var derived = GetOrCreateDerivedState(scope: scope);
        var recordPools = GetOrCreateRecordPools(scope: scope);

        foreach (var stmt in statements) {
            switch (stmt) {
                case EnumDeclarationNode enumNode:
                    RegisterEnum(
                        enumNode: enumNode,
                        inherited: false,
                        scope: scope
                    );
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

    // Binds an enum's members: each qualified member, and each bare member no other name in the scope spells.
    private static void RegisterEnum(EnumDeclarationNode enumNode, DocumentScope scope, bool inherited) {
        GetOrCreateEnums(scope: scope)[enumNode.Name] = new EnumDefinition(Name: enumNode.Name, Members: [.. enumNode.Members.Select(selector: static member => member.Name)], Inherited: inherited);
        for (var i = 0; (i < enumNode.Members.Count); i++) {
            var member = enumNode.Members[i].Name;

            scope.Constants[QualifiedName.Parse(text: enumNode.Name).Append(member: member).ToString()] = new LiteralExpressionNode(Value: ((long)i));

            // A bare member reads as its ordinal, so it cannot share a spelling with a let or a module parameter. Two
            // enums may share a member, and each reads it qualified; the bare spelling then names no one ordinal, so
            // every bare read of it is refused rather than one enum's winning.
            if (scope.Constants.TryGetValue(key: member, value: out var existing)) {
                if (!EnumMemberConstants.TryGetValue(key: existing, value: out var owner)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.EnumMemberShadowsConstant,
                        message: $"enum '{enumNode.Name}'{(inherited ? " (declared by the basis)" : string.Empty)} member '{member}' is spelled like a let or module parameter in the same scope, and a bare member reads as its ordinal; rename one, or read the member as '{enumNode.Name}.{member}'",
                        span: enumNode.Members[i].Span
                    );

                    continue;
                }
                if (!ReferenceEquals(objA: owner, objB: enumNode)) {
                    EnumMemberConstants.AddOrUpdate(
                        key: scope.BindRefused(
                            code: PuckDiagnosticCodes.EnumMemberShadowsConstant,
                            message: $"'{member}' is a member of both enum '{owner.Name}' and enum '{enumNode.Name}', so it names no one ordinal bare; read it as '{owner.Name}.{member}' or '{enumNode.Name}.{member}'",
                            name: member
                        ),
                        value: enumNode
                    );

                    continue;
                }
            }

            var ordinal = new LiteralExpressionNode(Value: ((long)i));

            EnumMemberConstants.AddOrUpdate(key: ordinal, value: enumNode);
            scope.Constants[member] = ordinal;
        }
    }

    // Binds the enums a document's basis declares before the document's own declarations, so a member the document
    // writes reads as the basis reads it (`slot left: Element = Air`). An enum the document declares itself is its
    // own, and a basis member spelled like one of the document's names is refused at the `basis` line, the only
    // place in this source the member comes from.
    internal static void IndexInheritedEnums(IReadOnlyList<EnumDefinition> enums, DocumentNode document, DocumentScope scope) {
        if (enums.Count == 0) {
            return;
        }

        var declared = new HashSet<string>(comparer: StringComparer.Ordinal);

        CollectEnumNames(declared: declared, statements: document.Statements);

        var span = document.BasisSpan;

        foreach (var inherited in enums) {
            if (declared.Contains(item: inherited.Name)) {
                continue;
            }

            RegisterEnum(
                enumNode: new EnumDeclarationNode(
                    Column: span.Column,
                    Length: span.Length,
                    Line: span.Line,
                    Members: [.. inherited.Members.Select(selector: member => new EnumMemberNode(Column: span.Column, Length: span.Length, Line: span.Line, Name: member, Offset: span.Offset))],
                    Name: inherited.Name,
                    Offset: span.Offset
                ),
                inherited: true,
                scope: scope
            );
        }

        static void CollectEnumNames(IEnumerable<StatementNode> statements, HashSet<string> declared) {
            foreach (var statement in statements) {
                switch (statement) {
                    case EnumDeclarationNode enumNode:
                        _ = declared.Add(item: enumNode.Name);
                        break;
                    case BlockNode block when ((block.Identifier == "state") || (block.Identifier == "world")):
                        CollectEnumNames(declared: declared, statements: block.Statements);
                        break;
                }
            }
        }
    }
    // A field naming an enum no enum this document can see declares keeps the name, since only the composed document
    // can say whether it holds one, so whole-document validation refuses one the composed section does not hold
    // (PUCK119, at the field): the field's `enum` member is registered at the field for that refusal to land on.
    internal static JsonObject LowerRecordDeclaration(RecordDeclarationNode declaration, JsonObject state, DocumentScope scope, string? pointer) {
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

            if (field.TypeName is not ("Int" or "Bool" or "Fixed" or "Text" or "Vector")) {
                fieldObj["enum"] = field.TypeName;
                MaterializeRuntimeEnum(name: field.TypeName, state: state, scope: scope);
                if (pointer is not null) {
                    scope.SourceMap?.Register(jsonPointer: $"{pointer}/fields/{fields.Count}/enum", span: field.Span);
                }
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
                            rowName: QualifiedName.Parse(text: declaration.Name).Append(member: field.Name).ToString(),
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

    // A state row naming an enum carries the enum into `state.enums` through the one call a record field uses. A
    // name this document does not declare may be one its basis carries into `state.enums`, which only the composed
    // document can say, so whole-document validation refuses one the composed section does not hold (PUCK119, at the
    // row's `enum` member).
    private static void ResolveRowEnum(JsonObject row, StatementNode statement, JsonObject state, DocumentScope scope, string pointer) {
        if ((row["enum"] is not JsonValue value) || !value.TryGetValue<string>(value: out var name)) {
            return;
        }
        if (GetOrCreateEnums(scope: scope).ContainsKey(key: name)) {
            MaterializeRuntimeEnum(name: name, scope: scope, state: state);

            return;
        }

        // The fault is the name, so it is reported at the text that writes it: the explicit row's `enum:` member, or
        // the name a `: Enum` annotation writes.
        var span = statement switch {
            BlockNode block when (block.Statements.OfType<PropertyNode>().FirstOrDefault(predicate: static property => (property.Name == "enum")) is { } property) => property.Span,
            StateSlotDeclarationNode { EnumSpan.Length: > 0 } slot => slot.EnumSpan,
            StateTableDeclarationNode { EnumSpan.Length: > 0 } table => table.EnumSpan,
            StateGridDeclarationNode { EnumSpan.Length: > 0 } grid => grid.EnumSpan,
            _ => statement.Span,
        };

        var rowName = (row["name"]?.ToString() ?? string.Empty);

        if (GetOrCreateRecords(scope: scope).ContainsKey(key: name)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationKindAnnotated,
                message: $"state row '{rowName}' names record '{name}', and a row's cells are drawn from an enum; instantiate the record with 'pool {rowName} of {name} capacity(...)' instead",
                span: span
            );
            row.Remove(propertyName: "enum");

            return;
        }

        scope.SourceMap?.Register(
            jsonPointer: $"{pointer}/enum",
            span: span
        );
    }

    // Every enum a root document declares is its vocabulary, carried into `state.enums` whether or not one of its own
    // rows names it, so a document composed over it can name the enum's members. A module declares vocabulary for the
    // roots that import it, and carries none of its own.
    internal static void MaterializeDeclaredEnums(JsonObject root, DocumentScope scope) {
        var state = ((root["state"] as JsonObject) ?? new JsonObject());

        foreach (var declared in GetOrCreateEnums(scope: scope).Values) {
            MaterializeRuntimeEnum(name: declared.Name, scope: scope, state: state);
        }
        if ((state.Count > 0) && (root["state"] is null)) {
            root["state"] = state;
        }
    }

    // Carries an enum this document declares into `state.enums`; an enum it does not declare, or one its basis
    // declares, is the composed document's to carry.
    private static void MaterializeRuntimeEnum(string name, JsonObject state, DocumentScope scope) {
        if (!GetOrCreateEnums(scope: scope).TryGetValue(key: name, value: out var source) || source.Inherited) {
            return;
        }

        var enums = ((state["enums"] as JsonArray) ?? new JsonArray());

        state["enums"] = enums;
        if (enums.Any(predicate: node => (node?["name"]?.ToString() == name))) {
            return;
        }

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
