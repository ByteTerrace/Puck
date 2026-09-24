using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Lowering;

// Rule sugar carries parsed operand trees. Binding resolves names by position before the resulting runtime
// instructions classify compareState/compareValue and value/fromState/expression document members.
public static partial class WorldDocumentEmitter {
    private static void LowerRuleBlock(RuleBlockNode rule, JsonObject parent, DocumentScope scope, RuleScopeContext? scopeContext = null) {
        if (parent["rules"] is not JsonArray rulesArr) {
            rulesArr = [];
            parent["rules"] = rulesArr;
        }

        var ruleIdx = rulesArr.Count;
        var rulePointer = $"{scope.CurrentPointer}/rules/{ruleIdx}";

        scope.SourceMap?.Register(
            jsonPointer: rulePointer,
            span: rule.Span
        );
        var oldPointer = scope.CurrentPointer;

        scope.CurrentPointer = rulePointer;

        var written = (DocumentLowering.ResolveRuleName(
            rule: rule,
            scope: scope
        ) ?? string.Empty);

        // A bare identifier in the name position that is also a bound template parameter never reached Rename, so
        // the rule would take the parameter's own spelling and every instantiation would mint the same name.
        if (
            (rule.NameExpression is null) &&
            IsBoundTemplateParameter(
            name: written,
            scope: scope
        )
        ) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.RuleNameNotLiteral,
                message: $"Rule name '{written}' is template parameter '{written}', so every instantiation would mint the same name — write an interpolated name such as rule $\"{written}-{{{written}}}\" {{ }}, or move the rule to the template's top level where the parameter substitutes.",
                span: rule.Span
            );
        }

        RefuseReservedScopedName(
            name: written,
            prefix: scopeContext?.Prefix,
            scope: scope,
            span: rule.Span
        );

        var ruleName = PrefixedName(
            name: written,
            prefix: scopeContext?.Prefix,
            scope: scope
        );

        // A local is read by its bare name wherever the rule spells an expression, whichever statement declares
        // it, so the names are gathered before any of the rule's text is parsed.
        var localNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var inherited in (scopeContext?.Locals ?? [])) {
            if (inherited["name"]?.ToString() is { Length: > 0 } inheritedName) {
                _ = localNames.Add(item: inheritedName);
            }
        }
        // A compile-time `for` in the body is unrolled first, so the locals it declares are known by their resolved
        // names before any expression that reads them is bound.
        var body = DocumentLowering.ExpandBody(
            scope: scope,
            statements: rule.Statements
        ).ToList();

        foreach (var (statement, statementScope) in body) {
            if (statement is LocalStatementNode declared) {
                _ = localNames.Add(item: DocumentLowering.ResolveLocalName(local: declared, scope: statementScope));
            }
        }

        using var ruleLocals = ExpressionSpelling.WithLocals(locals: localNames);

        var obj = new JsonObject { ["name"] = ruleName };
        var prevRule = (scope.Annotations.TryGetValue(key: "CurrentRule", value: out var pr) ? pr : null);

        scope.Annotations["CurrentRule"] = obj;

        var effects = new JsonArray();
        JsonArray? locals = null;
        var poolBindings = GetPoolBindings(scope: scope);
        var addedPoolBinding = ((rule.PoolBinding is { } binding) && poolBindings.Add(item: binding));
        var bindingPools = GetPoolBindingPools(scope: scope);

        if ((rule.PoolForEach is { } pool) && (rule.PoolBinding is { } poolBinding)) {
            bindingPools[poolBinding] = pool;
            RefusePoolBindingRowCollision(alias: poolBinding, scope: scope, span: rule.Span);
            obj["poolForEach"] = new JsonObject { ["pool"] = pool, ["binding"] = poolBinding };
        }

        if (scopeContext?.Locals is { Count: > 0 } parentLocals) {
            locals = [];
            foreach (var l in parentLocals) {
                locals.AppendNode(item: l.DeepClone());
            }
        }

        foreach (var (stmt, stmtScope) in body) {
            switch (stmt) {
                case WhenStatementNode when1:
                    var ruleGate = LowerPredicate(
                        node: when1.Predicate,
                        ruleObj: obj,
                        scope: stmtScope
                    );

                    scope.SourceMap?.Register(
                        jsonPointer: $"{rulePointer}/gate",
                        span: when1.Span
                    );
                    obj["gate"] = CombineGates(existing: (obj["gate"] as JsonObject), newGate: ruleGate);
                    break;
                case LocalStatementNode local:
                    locals ??= [];
                    scope.SourceMap?.Register(
                        jsonPointer: $"{rulePointer}/locals/{locals.Count}",
                        span: local.Span
                    );
                    locals.AppendNode(item: LowerLocal(local: local, scope: stmtScope));
                    break;
                case DecisionBlockNode decision:
                    scope.SourceMap?.Register(
                        jsonPointer: $"{rulePointer}/decision",
                        span: decision.Span
                    );
                    obj["decision"] = LowerDecisionBlock(
                        decision: decision,
                        scope: stmtScope
                    );
                    break;
                case PropertyNode prop:
                    var loweredProp = LowerModelMember(holder: typeof(WorldRule), property: prop, scope: stmtScope);
                    DocumentLowering.AssignOrExtend(
                        obj,
                        prop.Name,
                        loweredProp
                    );
                    break;
                case EffectStatementNode or ExpressionStatementNode:
                    AppendEffect(
                        effects: effects,
                        pointer: $"{rulePointer}/effects",
                        ruleObj: obj,
                        scope: stmtScope,
                        statement: stmt
                    );
                    break;
            }
        }

        if (scopeContext?.Gate is not null) {
            obj["gate"] = CombineGates(existing: ((JsonObject)scopeContext.Gate.DeepClone()), newGate: (obj["gate"] as JsonObject));
        }

        if (scopeContext?.Properties is { Count: > 0 } parentProps) {
            foreach (var (k, v) in parentProps) {
                if (!obj.ContainsKey(propertyName: k)) {
                    obj[k] = v?.DeepClone();
                } else if ((k == "zones") && (v is JsonArray scopeZones) && (obj["zones"] is JsonArray ruleZones)) {
                    var existingSet = new HashSet<string>(comparer: StringComparer.Ordinal);

                    foreach (var item in ruleZones) {
                        if (item?.ToString() is { } s) {
                            existingSet.Add(item: s);
                        }
                    }
                    foreach (var item in scopeZones) {
                        if ((item?.ToString() is { } s) && existingSet.Add(item: s)) {
                            ruleZones.AppendNode(item: item.DeepClone());
                        }
                    }
                }
            }
        }

        // An authored `effects:`/`locals:` property is the whole array; the statement-built one only fills in when
        // no property named it, so neither spelling silently erases the other.
        if (
            (effects.Count > 0) ||
            !obj.ContainsKey(propertyName: "effects")
        ) {
            obj["effects"] = effects;
        }
        if (locals is not null) {
            obj["locals"] = locals;
        }

        scope.Annotations["CurrentRule"] = prevRule;
        if (addedPoolBinding) {
            _ = poolBindings.Remove(item: rule.PoolBinding!);
            _ = bindingPools.Remove(key: rule.PoolBinding!);
        }
        scope.CurrentPointer = oldPointer;
        rulesArr.AppendNode(item: obj);
    }

    // The model types a rule's own property lines, its decision's and an option's fill, so each line is written in
    // the one spelling its member takes.
    private static readonly Type? DecisionModel = WorldCallArguments.MemberType(member: "decision", owner: typeof(WorldRule));
    private static readonly Type? OptionModel = ((DecisionModel is null) ? null : WorldCallArguments.MemberType(member: "options", owner: DecisionModel));

    private static JsonNode? LowerModelMember(Type? holder, PropertyNode property, DocumentScope scope) => DocumentLowering.LowerMember(
        fieldKey: property.Name,
        holder: holder,
        holderName: null,
        memberName: property.Name,
        scope: scope,
        value: property.Value
    );
    private static JsonObject LowerLocal(LocalStatementNode local, DocumentScope scope) {
        var text = BindOperand(operand: local.Expression, scope: scope);

        return new JsonObject {
            ["name"] = DocumentLowering.ResolveLocalName(local: local, scope: scope),
            ["expression"] = DocumentExpression(text: text),
        };
    }

    // The text a document holds names a reserved channel and a rule local in full. An expression that reads one is
    // therefore written in the document's spelling; any other stays as the author wrote it.
    internal static string DocumentExpression(string text) {
        if (
            !ExpressionSpelling.TryParse(
                error: out _,
                program: out var program,
                text: text
            ) ||
            !ReadsReserved(program: program)
        ) {
            return text;
        }

        return (ExpressionSpelling.TryPrint(
            program: program,
            text: out var printed
        )
            ? printed
            : text
        );

        static bool ReadsReserved(ExpressionProgram program) => (
            AnyReserved(instructions: program.Instructions) ||
            program.Subprograms.Any(predicate: static subprogram => AnyReserved(instructions: subprogram.Instructions))
        );
        static bool AnyReserved(IReadOnlyList<Instruction> instructions) => instructions.Any(predicate: static instruction => (
            (instruction.Payload is InstructionPayload.State state) &&
            ((state.Name.Call is not null) || (state.Key?.Call is not null))
        ));
    }

    private static JsonObject LowerDecisionBlock(DecisionBlockNode decision, DocumentScope scope) {
        var decisionPointer = $"{scope.CurrentPointer}/decision";
        var obj = new JsonObject();
        var options = new JsonArray();
        decimal? periodSeconds = null;
        var mode = "HighestScore";
        var sawMode = false;
        var scoreKind = "Fixed";
        var sawScoreKind = false;
        var commitmentSeconds = 0M;
        var sawCommitmentSeconds = false;
        var incumbentBonus = 0M;
        var sawIncumbentBonus = false;
        var seed = 0L;
        var sawSeed = false;

        foreach (var stmt in decision.Statements) {
            switch (stmt) {
                case PropertyNode { Name: "periodSeconds" } p:
                    periodSeconds = ToDecimalNode(node: LowerExpression(
                        p.Value,
                        scope,
                        p.Name
                    ));
                    break;
                case PropertyNode { Name: "mode" } p:
                    mode = (LowerModelMember(holder: DecisionModel, property: p, scope: scope)?.ToString() ?? mode);
                    sawMode = true;
                    break;
                case PropertyNode { Name: "scoreKind" } p:
                    scoreKind = (LowerModelMember(holder: DecisionModel, property: p, scope: scope)?.ToString() ?? scoreKind);
                    sawScoreKind = true;
                    break;
                case PropertyNode { Name: "commitmentSeconds" } p:
                    commitmentSeconds = ToDecimalNode(node: LowerExpression(
                        p.Value,
                        scope,
                        p.Name
                    ));
                    sawCommitmentSeconds = true;
                    break;
                case PropertyNode { Name: "incumbentBonus" } p:
                    incumbentBonus = ToDecimalNode(node: LowerExpression(
                        p.Value,
                        scope,
                        p.Name
                    ));
                    sawIncumbentBonus = true;
                    break;
                case PropertyNode { Name: "seed" } p:
                    seed = ((long)ToDecimalNode(node: LowerExpression(
                        p.Value,
                        scope,
                        p.Name
                    )));
                    sawSeed = true;
                    break;
                case InterruptStatementNode interrupt:
                    scope.SourceMap?.Register(
                        jsonPointer: $"{decisionPointer}/interrupt",
                        span: interrupt.Span
                    );
                    obj["interrupt"] = LowerPredicate(
                        node: interrupt.Predicate,
                        scope: scope
                    );
                    break;
                case PropertyNode { Name: "interrupt" } p:
                    obj["interrupt"] = LowerModelMember(holder: DecisionModel, property: p, scope: scope);
                    break;
                case OnNoChoiceBlockNode onNoChoice: {
                        scope.SourceMap?.Register(
                            jsonPointer: $"{decisionPointer}/onNoChoice",
                            span: onNoChoice.Span
                        );
                        obj["onNoChoice"] = LowerEffectList(
                            pointer: $"{decisionPointer}/onNoChoice",
                            ruleObj: null,
                            scope: scope,
                            statements: onNoChoice.Effects
                        );
                        break;
                    }
                case OptionBlockNode option:
                    scope.SourceMap?.Register(
                        jsonPointer: $"{decisionPointer}/options/{options.Count}",
                        span: option.Span
                    );
                    options.AppendNode(item: LowerOptionBlock(
                        option: option,
                        pointer: $"{decisionPointer}/options/{options.Count}",
                        scope: scope
                    ));
                    break;
            }
        }

        if (periodSeconds is { } periodValue) {
            obj["periodSeconds"] = periodValue;
        }
        if (
            sawMode ||
            !string.Equals(
            a: mode,
            b: "HighestScore",
            comparisonType: StringComparison.Ordinal
        )
        ) {
            obj["mode"] = mode;
        }
        if (
            sawScoreKind ||
            !string.Equals(
            a: scoreKind,
            b: "Fixed",
            comparisonType: StringComparison.Ordinal
        )
        ) {
            obj["scoreKind"] = scoreKind;
        }
        if (
            sawCommitmentSeconds ||
            (commitmentSeconds != 0)
        ) {
            obj["commitmentSeconds"] = commitmentSeconds;
        }
        if (
            sawIncumbentBonus ||
            (incumbentBonus != 0)
        ) {
            obj["incumbentBonus"] = incumbentBonus;
        }
        if (
            sawSeed ||
            (seed != 0)
        ) {
            obj["seed"] = seed;
        }
        obj["options"] = options;
        return obj;
    }
    private static JsonObject LowerOptionBlock(OptionBlockNode option, DocumentScope scope, string pointer) {
        var obj = new JsonObject { ["name"] = option.Name };
        var effects = new JsonArray();

        foreach (var stmt in option.Statements) {
            switch (stmt) {
                case WhenStatementNode when1:
                    obj["gate"] = LowerPredicate(
                        node: when1.Predicate,
                        scope: scope
                    );
                    break;
                case ScoreStatementNode score:
                    obj["score"] = DocumentExpression(text: BindOperand(operand: score.Expression, scope: scope));
                    break;
                case PropertyNode p:
                    obj[p.Name] = LowerModelMember(holder: OptionModel, property: p, scope: scope);
                    break;
                case EffectStatementNode or ExpressionStatementNode or ForStatementNode:
                    AppendEffect(
                        effects: effects,
                        pointer: $"{pointer}/effects",
                        ruleObj: null,
                        scope: scope,
                        statement: stmt
                    );
                    break;
            }
        }

        obj["effects"] = effects;
        return obj;
    }
    private static decimal ToDecimalNode(JsonNode? node) => node switch {
        JsonValue v when v.TryGetValue<decimal>(value: out var d) => d,
        JsonValue v when v.TryGetValue<long>(value: out var l) => l,
        JsonValue v when v.TryGetValue<double>(value: out var db) => DecimalValues.FromDouble(value: db),
        _ => 0m,
    };
    // ---- Gate lowering (§1) --------------------------------------------------------------------------------

    private static JsonObject LowerPredicate(PredicateNode node, DocumentScope scope, JsonObject? ruleObj = null) => node switch {
        ComparisonPredicateNode cmp => LowerComparison(
            cmp: cmp,
            ruleObj: ruleObj,
            scope: scope
        ),
        AndPredicateNode and => LowerPredicateList(
            "all",
            "predicates",
            and.Operands,
            scope,
            ruleObj
        ),
        OrPredicateNode or => LowerPredicateList(
            "any",
            "predicates",
            or.Operands,
            scope,
            ruleObj
        ),
        NotPredicateNode not => new JsonObject {
            ["$type"] = "not",
            ["predicate"] = LowerPredicate(
                node: not.Operand,
                ruleObj: ruleObj,
                scope: scope
            ),
        },
        CallPredicateNode call => RefuseCallGate(
            call: call,
            scope: scope
        ),
        _ => throw new InvalidOperationException(message: $"unrecognized predicate node '{node.GetType()}'"),
    };
    // A world gate is a comparison, or and/or/not over comparisons - there is no arm for a named test. The vacuous
    // `all` keeps the shape well-formed for whatever else the emitter is midway through building; the reported error
    // is what stops the compile.
    private static JsonObject RefuseCallGate(CallPredicateNode call, DocumentScope scope) {
        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.UnsupportedCallGate,
            message: $"'{call.Call.Name}(...)' is not a puck.world.definition.v1 gate - a world gate compares two operands",
            span: call.Span
        );

        return new JsonObject { ["$type"] = "all", ["predicates"] = new JsonArray() };
    }
    private static JsonObject LowerPredicateList(string discriminator, string propertyName, IReadOnlyList<PredicateNode> operands, DocumentScope scope, JsonObject? ruleObj = null) {
        var arr = new JsonArray();

        foreach (var operand in operands) {
            arr.AppendNode(item: LowerPredicate(
                node: operand,
                ruleObj: ruleObj,
                scope: scope
            ));
        }
        return new JsonObject { ["$type"] = discriminator, [propertyName] = arr };
    }
    private static JsonObject LowerComparison(ComparisonPredicateNode cmp, DocumentScope scope, JsonObject? ruleObj = null) {
        // A gate operand is resolved against the `let` bindings and loop locals in scope BEFORE it is classified.
        // Without this a bare name can only ever read as a state row, so a bound named `wellFloor` silently became
        // a read of a row by that name and the author had to write the number out with a comment naming what it
        // meant. A binding shadows a state row of the same name, which is the same precedence the cartridge
        // vocabulary's operands already use.
        var authoredLeftText = cmp.Left.Text;
        var authoredRightText = cmp.Right.Text;
        var leftText = BindOperand(operand: cmp.Left, scope: scope);
        var rightText = BindOperand(operand: cmp.Right, scope: scope);

        if (!ExpressionComparisons.TryParseSymbol(
            cmp.Comparator,
            out var parsedComparison
        )) {
            throw new InvalidOperationException(message: $"'{cmp.Comparator}' is not a DSL comparison operator");
        }
        var comparison = Enum.GetName(value: parsedComparison)!;

        if (
            TryTypedPoolFieldKind(kind: out _, scope: scope, text: authoredLeftText) ||
            TryTypedPoolFieldKind(kind: out _, scope: scope, text: authoredRightText)
        ) {
            return new JsonObject {
                ["$type"] = "compareValue",
                ["comparison"] = comparison,
                ["left"] = DocumentPoolFieldExpression(scope: scope, text: leftText),
                ["right"] = DocumentPoolFieldExpression(scope: scope, text: rightText),
            };
        }

        // An explicit `: Kind`/`as Kind` suffix always forces compareValue, even for two simple single-token
        // operands — CompareState carries no Kind field at all, so an authored kind annotation on it is meaningless;
        // forcing compareValue is the only reading that keeps the annotation.
        if (cmp.Kind is not null) {
            return new JsonObject {
                ["$type"] = "compareValue",
                ["comparison"] = comparison,
                ["kind"] = cmp.Kind,
                ["left"] = DocumentExpression(text: leftText),
                ["right"] = DocumentExpression(text: rightText),
            };
        }

        switch (ClassifyBareComparison(
            leftText: leftText,
            leftToken: out var leftToken,
            rightText: rightText,
            rightToken: out var rightToken
        )) {
            case BareComparisonShape.StateAgainstConstant:
                return NewCompareState(
                    (((InstructionPayload.State)leftToken!.Payload!)).Name.Spelling,
                    (((InstructionPayload.State)leftToken!.Payload!)).Key?.Spelling,
                    comparison,
                    value: (((InstructionPayload.Constant)rightToken!.Payload!)).Value
                );
            case BareComparisonShape.StateAgainstState:
                return NewCompareState(
                    (((InstructionPayload.State)leftToken!.Payload!)).Name.Spelling,
                    (((InstructionPayload.State)leftToken!.Payload!)).Key?.Spelling,
                    comparison,
                    comparandState: (((InstructionPayload.State)rightToken!.Payload!)).Name.Spelling,
                    comparandKey: (((InstructionPayload.State)rightToken!.Payload!)).Key?.Spelling
                );
            case BareComparisonShape.ConstantAgainstState:
                // Operands swapped so the live State read is always the CompareState subject — the decompiler never
                // emits this constant-first spelling, only the compiler tolerates it.
                return NewCompareState(
                    (((InstructionPayload.State)rightToken!.Payload!)).Name.Spelling,
                    (((InstructionPayload.State)rightToken!.Payload!)).Key?.Spelling,
                    Enum.GetName(value: parsedComparison.Flip())!,
                    value: (((InstructionPayload.Constant)leftToken!.Payload!)).Value
                );
            default:
                return new JsonObject {
                    ["$type"] = "compareValue",
                    ["comparison"] = comparison,
                    ["left"] = DocumentExpression(text: leftText),
                    ["right"] = DocumentExpression(text: rightText),
                };
        }
    }
    // `binding.field`: a field of a record a lexical pool binding in scope holds.
    private static bool TryPoolBindingField(string text, DocumentScope scope, out string binding, out string field) {
        var name = QualifiedName.Parse(text: text);

        (binding, field) = (name.Head, name.Last);

        return (
            (name.Segments.Count == 2) &&
            name.IsWellFormed &&
            GetPoolBindings(scope: scope).Contains(item: binding) &&
            IdentifierSpelling.IsName(text: field)
        );
    }
    private static bool TryTypedPoolFieldKind(string text, DocumentScope scope, out string kind) {
        kind = "Fixed";
        string? pool = null;
        string? fieldName = null;

        if (TryPoolBindingField(binding: out var binding, field: out var bound, scope: scope, text: text)) {
            _ = GetPoolBindingPools(scope: scope).TryGetValue(key: binding, value: out pool);
            fieldName = bound;
        } else if (TryStaticPoolField(reference: out var staticField, scope: scope, text: text)) {
            pool = staticField.PoolField!.Pool;
            fieldName = staticField.PoolField.Field;
        }
        if (
            (pool is null) ||
            !GetOrCreateRecordPools(scope: scope).TryGetValue(key: pool, value: out var recordName) ||
            !GetOrCreateRecords(scope: scope).TryGetValue(key: recordName, value: out var record)
        ) {
            return false;
        }
        var field = record.Fields.FirstOrDefault(predicate: candidate => string.Equals(a: candidate.Name, b: fieldName, comparisonType: StringComparison.Ordinal));

        if (field is null) {
            return false;
        }
        kind = ((field.TypeName is "Bool" or "Fixed" or "Text" or "Vector")
            ? field.TypeName
            : "Int"
        );
        return true;
    }
    private static JsonNode DocumentPoolFieldExpression(string text, DocumentScope scope) {
        StateChannelRef? reference = null;

        if (TryPoolBindingField(binding: out var binding, field: out var field, scope: scope, text: text)) {
            reference = StateChannelRef.OfBindingField(binding: binding, field: field);
        } else if (TryStaticPoolField(reference: out var typed, scope: scope, text: text)) {
            reference = typed;
        }
        return ((reference is null) ? DocumentExpression(text: text) : ExpressionProgramJsonConverter.ToNode(program: new ExpressionProgram(Instructions: [Instruction.Operand(name: reference)])));
    }
    private static bool TryStaticPoolField(string text, DocumentScope scope, out StateChannelRef reference) {
        reference = null!;
        var open = text.IndexOf(value: '[');
        var close = text.IndexOf(value: ']');
        var dot = text.IndexOf(value: '.', startIndex: Math.Max(val1: 0, val2: close));

        if ((open <= 0) || (close <= open) || (dot != (close + 1)) || !int.TryParse(provider: System.Globalization.CultureInfo.InvariantCulture, result: out var slot, s: text[(open + 1)..close], style: System.Globalization.NumberStyles.Integer) || !GetOrCreateRecordPools(scope: scope).ContainsKey(key: text[..open])) {
            return false;
        }
        reference = StateChannelRef.OfStaticPoolField(pool: text[..open], slot: slot, field: text[(dot + 1)..]);
        return true;
    }

    /// <summary>Which node an unannotated <c>left cmp right</c> comparison lowers to.</summary>
    internal enum BareComparisonShape {
        /// <summary>A <c>compareValue</c> node carrying both operand texts verbatim.</summary>
        CompareValue,

        /// <summary>A <c>compareState</c> node reading the left operand's row against a literal.</summary>
        StateAgainstConstant,

        /// <summary>A <c>compareState</c> node reading the left operand's row against the right operand's row.</summary>
        StateAgainstState,

        /// <summary>A <c>compareState</c> node built from the FLIPPED comparison, the right operand's row as
        /// subject and the left operand's literal as comparand.</summary>
        ConstantAgainstState,
    }

    /// <summary>Classifies what a comparison with no <c>: Kind</c> annotation lowers to.</summary>
    /// <remarks>The decompiler's <c>FormatCompareValue</c> calls this to decide whether a <c>compareValue</c>
    /// node's bare <c>left cmp right</c> text would re-lower to <c>compareState</c>, which is when it must print
    /// the kind annotation even for the default kind.</remarks>
    /// <param name="leftText">The left operand's verbatim source text.</param>
    /// <param name="rightText">The right operand's verbatim source text.</param>
    /// <param name="leftToken">The left operand's single token, when it has exactly one; otherwise
    /// <see langword="null"/>.</param>
    /// <param name="rightToken">The right operand's single token, when it has exactly one; otherwise
    /// <see langword="null"/>.</param>
    /// <returns>The shape the comparison lowers to.</returns>
    internal static BareComparisonShape ClassifyBareComparison(string leftText, string rightText, out Instruction? leftToken, out Instruction? rightToken) {
        ExpressionSpelling.TryParse(
            error: out _,
            program: out var leftProgram,
            text: leftText
        );
        ExpressionSpelling.TryParse(
            error: out _,
            program: out var rightProgram,
            text: rightText
        );
        leftToken = ((leftProgram.Instructions.Count == 1)
            ? leftProgram.Instructions[0]
            : null
        );
        rightToken = ((rightProgram.Instructions.Count == 1)
            ? rightProgram.Instructions[0]
            : null
        );

        return (leftToken?.Payload, rightToken?.Payload) switch {
            (InstructionPayload.State, InstructionPayload.Constant) => BareComparisonShape.StateAgainstConstant,
            (InstructionPayload.State, InstructionPayload.State) => BareComparisonShape.StateAgainstState,
            (InstructionPayload.Constant, InstructionPayload.State) => BareComparisonShape.ConstantAgainstState,
            _ => BareComparisonShape.CompareValue,
        };
    }

    private static JsonObject NewCompareState(string state, string? key, string comparison, decimal? value = null, string? comparandState = null, string? comparandKey = null) {
        var obj = new JsonObject { ["$type"] = "compareState", ["state"] = state, ["comparison"] = comparison };

        if (key is not null) {
            obj["key"] = key;
        }
        if (value is { } v) {
            obj["value"] = v;
        }
        if (comparandState is not null) {
            obj["comparandState"] = comparandState;
        }
        if (comparandKey is not null) {
            obj["comparandKey"] = comparandKey;
        }
        return obj;
    }
    // ---- Effect statement lowering (§2) ---------------------------------------------------------------------

    // Null means "refused, and the refusal is already reported": a caller drops it rather than writing a null into
    // an effects array.
    // Lowers one statement of an effect list onto the end of `effects`, whose own pointer is `pointer`, and maps
    // the element to its span. A compile-time `for` lands the effects it produces, each under its iteration's
    // bindings, so the list carries the unrolled effects and never the loop.
    private static void AppendEffect(JsonArray effects, StatementNode statement, DocumentScope scope, string pointer, JsonObject? ruleObj) {
        foreach (var (produced, iteration) in DocumentLowering.ExpandBody(
            scope: scope,
            statements: [statement]
        )) {
            var element = $"{pointer}/{effects.Count}";

            if (LowerEffectStatement(
                pointer: element,
                ruleObj: ruleObj,
                scope: iteration,
                stmt: produced
            ) is { } lowered) {
                scope.SourceMap?.Register(
                    jsonPointer: element,
                    span: produced.Span
                );
                effects.AppendNode(item: lowered);
            }
        }
    }
    private static JsonArray LowerEffectList(IReadOnlyList<StatementNode> statements, DocumentScope scope, string pointer, JsonObject? ruleObj) {
        var effects = new JsonArray();

        foreach (var statement in statements) {
            AppendEffect(
                effects: effects,
                pointer: pointer,
                ruleObj: ruleObj,
                scope: scope,
                statement: statement
            );
        }

        return effects;
    }
    private static JsonNode? LowerEffectStatement(StatementNode stmt, DocumentScope scope, string pointer, JsonObject? ruleObj = null) => stmt switch {
        SetCellStatementNode s => LowerCellEffect(
            discriminator: "setState",
            target: s.Target,
            rhs: s.Rhs,
            allowText: true,
            ruleObj: ruleObj,
            scope: scope
        ),
        AddCellStatementNode a => LowerCellEffect(
            discriminator: "addState",
            target: a.Target,
            rhs: a.Rhs,
            allowText: false,
            ruleObj: ruleObj,
            scope: scope
        ),
        PushStatementNode push => LowerPush(push: push, ruleObj: ruleObj, scope: scope),
        RemoveCellStatementNode remove => LowerRowOnlyEffect(
            discriminator: "removeStateCell",
            ruleObj: ruleObj,
            scope: scope,
            target: remove.Target
        ),
        ScheduleStatementNode schedule => LowerSchedule(ruleObj: ruleObj, schedule: schedule, scope: scope),
        TransformStatementNode transform => LowerTransformStatement(scope: scope, transform: transform),
        DrawStatementNode draw => new JsonObject {
            ["$type"] = "transformState",
            ["transform"] = new JsonObject {
                ["$type"] = "transfer",
                ["from"] = BindRowReference(scope: scope, span: draw.Span, text: draw.From),
                ["to"] = BindRowReference(scope: scope, span: draw.Span, text: draw.To),
                ["selector"] = "First",
            },
        },
        DealStatementNode deal => new JsonObject {
            ["$type"] = "transformState",
            ["transform"] = new JsonObject {
                ["$type"] = "transfer",
                ["from"] = BindRowReference(scope: scope, span: deal.Span, text: deal.From),
                ["to"] = BindRowReference(scope: scope, span: deal.Span, text: deal.To),
                ["selector"] = "First",
                ["count"] = deal.Count,
            },
        },
        ShuffleStatementNode shuffle => new JsonObject {
            ["$type"] = "transformState",
            ["transform"] = new JsonObject {
                ["$type"] = "shuffle",
                ["row"] = BindRowReference(scope: scope, span: shuffle.Span, text: shuffle.Row),
                ["draw"] = ((shuffle.Draw is not null) ? BindRowReference(scope: scope, span: shuffle.Span, text: shuffle.Draw) : null),
            },
        },
        TransactionStatementNode transaction => LowerTransaction(
            pointer: pointer,
            ruleObj: ruleObj,
            scope: scope,
            transaction: transaction
        ),
        ExpressionStatementNode { Expression: CallExpressionNode call } => DocumentLowering.At(
            scope: scope,
            context: typeof(ActionEffect),
            lower: () => LowerExpression(call, scope)
        )!,
        CompoundAssignStatementNode compound => RefuseCompoundAssignment(
            compound: compound,
            scope: scope
        ),
        IfStatementNode ifStmt => LowerIf(
            ifStmt: ifStmt,
            pointer: pointer,
            ruleObj: ruleObj,
            scope: scope
        ),
        RepeatStatementNode => RefuseControlFlow(
            alternative: "a rule already runs once per matching subject - use 'forEach'",
            keyword: "repeat",
            scope: scope,
            stmt: stmt
        ),
        BreakStatementNode => RefuseControlFlow(
            alternative: "there is no loop in a puck.world.definition.v1 rule to leave",
            keyword: "break",
            scope: scope,
            stmt: stmt
        ),
        ClaimStatementNode claim => LowerPoolClaim(claim: claim, pointer: pointer, ruleObj: ruleObj, scope: scope),
        ClaimPairStatementNode claim => LowerPairPoolClaim(claim: claim, pointer: pointer, ruleObj: ruleObj, scope: scope),
        ReleaseStatementNode release => new JsonObject { ["$type"] = "release", ["binding"] = release.Alias },
        PoolForEachStatementNode each => LowerPoolForEach(each: each, pointer: pointer, ruleObj: ruleObj, scope: scope),
        _ => throw new InvalidOperationException(message: $"unrecognized effect statement '{stmt.GetType()}'"),
    };
    private static JsonArray LowerPoolScope(
        string alias,
        IReadOnlyList<StatementNode> body,
        string pointer,
        string? pool,
        JsonObject? ruleObj,
        DocumentScope scope,
        SourceSpan span
    ) {
        var effects = new JsonArray();
        var bindings = GetPoolBindings(scope: scope);

        RefusePoolBindingRowCollision(alias: alias, scope: scope, span: span);
        var added = bindings.Add(item: alias);
        Dictionary<string, string>? bindingPools = null;

        if (pool is not null) {
            bindingPools = GetPoolBindingPools(scope: scope);
            bindingPools[alias] = pool;
        }

        try {
            foreach (var nested in body) {
                AppendEffect(
                    effects: effects,
                    pointer: $"{pointer}/effects",
                    ruleObj: ruleObj,
                    scope: scope,
                    statement: nested
                );
            }
        } finally {
            if (added) {
                bindings.Remove(item: alias);
                bindingPools?.Remove(key: alias);
            }
        }

        return effects;
    }
    private static JsonObject LowerPoolClaim(ClaimStatementNode claim, DocumentScope scope, string pointer, JsonObject? ruleObj) => new() {
        ["$type"] = "claim",
        ["pool"] = claim.Pool,
        ["binding"] = claim.Alias,
        ["effects"] = LowerPoolScope(
            alias: claim.Alias,
            body: claim.Body,
            pointer: pointer,
            pool: claim.Pool,
            ruleObj: ruleObj,
            scope: scope,
            span: claim.Span
        ),
    };
    private static JsonObject LowerPoolForEach(PoolForEachStatementNode each, DocumentScope scope, string pointer, JsonObject? ruleObj) => new() {
        ["$type"] = "forEachPool",
        ["pool"] = each.Pool,
        ["binding"] = each.Alias,
        ["effects"] = LowerPoolScope(
            alias: each.Alias,
            body: each.Body,
            pointer: pointer,
            pool: each.Pool,
            ruleObj: ruleObj,
            scope: scope,
            span: each.Span
        ),
    };
    private static JsonObject LowerPairPoolClaim(ClaimPairStatementNode claim, DocumentScope scope, string pointer, JsonObject? ruleObj) => new() {
        ["$type"] = "claimPair",
        ["pool"] = claim.Pool,
        ["left"] = claim.Left,
        ["right"] = claim.Right,
        ["binding"] = claim.Alias,
        ["effects"] = LowerPoolScope(
            alias: claim.Alias,
            body: claim.Body,
            pointer: pointer,
            pool: null,
            ruleObj: ruleObj,
            scope: scope,
            span: claim.Span
        ),
    };
    private static HashSet<string> GetPoolBindings(DocumentScope scope) {
        if (!scope.Annotations.TryGetValue(key: "WorldPoolBindings", value: out var value) || (value is not HashSet<string> bindings)) {
            bindings = new HashSet<string>(comparer: StringComparer.Ordinal);
            scope.Annotations["WorldPoolBindings"] = bindings;
        }
        return bindings;
    }
    private static Dictionary<string, string> GetPoolBindingPools(DocumentScope scope) =>
        GetOrCreateScopeDictionary<string>(key: "WorldPoolBindingPools", scope: scope);
    private static void RefusePoolBindingRowCollision(string alias, DocumentScope scope, SourceSpan span) {
        if (
            !scope.Annotations.TryGetValue(key: "WorldDocumentRoot", value: out var rootNode) ||
            (rootNode is not JsonObject root) ||
            (root["state"] is not JsonObject state) ||
            (state["world"] is not JsonArray rows) ||
            !rows.OfType<JsonObject>().Any(predicate: row => string.Equals(a: row["name"]?.ToString(), b: alias, comparisonType: StringComparison.Ordinal))
        ) {
            return;
        }
        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.StateDeclarationBehaviorConflict,
            message: $"pool binding '{alias}' conflicts with a state row of the same name; choose a distinct binding so '{alias}[key]' is unambiguous",
            span: span
        );
    }
    // puck.world.definition.v1 carries two assignment effects, setState and addState; every other operator belongs in the
    // expression on the right, where the state engine evaluates it.
    private static JsonNode? RefuseCompoundAssignment(CompoundAssignStatementNode compound, DocumentScope scope) {
        var target = ((compound.Target.Key is null)
            ? compound.Target.Name
            : $"{compound.Target.Name}[{compound.Target.Key}]"
        );

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.UnsupportedAssignmentOperator,
            message: $"puck.world.definition.v1 has no '{compound.Operator}=' effect - write it as '{target} = {target} {compound.Operator} ...'",
            span: compound.Span
        );

        return null;
    }
    // `if Gate { ... } [else { ... }]` lowers to ActionEffect.If, reusing the same predicate lowering `when` uses
    // for its own condition. An `else if` chain is already one nested IfStatementNode per level (the parser's own
    // shape), so recursing through LowerEffectStatement nests it the same way with no separate case. `repeat` and
    // `break` still have nothing to lower onto in a straight-line rule body.
    private static JsonObject LowerIf(IfStatementNode ifStmt, DocumentScope scope, string pointer, JsonObject? ruleObj = null) {
        var thenArr = LowerEffectList(
            pointer: $"{pointer}/then",
            ruleObj: ruleObj,
            scope: scope,
            statements: ifStmt.Then
        );
        var obj = new JsonObject {
            ["$type"] = "if",
            ["condition"] = LowerPredicate(
                node: ifStmt.Condition,
                ruleObj: ruleObj,
                scope: scope
            ),
            ["then"] = thenArr,
        };

        if (ifStmt.Else is { } elseStatements) {
            obj["else"] = LowerEffectList(
                pointer: $"{pointer}/else",
                ruleObj: ruleObj,
                scope: scope,
                statements: elseStatements
            );
        }
        return obj;
    }
    // puck.world.definition.v1 rule effects are a straight line: the rule's own gate decides whether the whole body runs,
    // and there is no branch or loop for one to lower onto. The language still parses control flow, because another
    // document vocabulary (a cartridge's rules) carries it natively.
    private static JsonNode? RefuseControlFlow(StatementNode stmt, string keyword, string alternative, DocumentScope scope) {
        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.UnsupportedControlFlow,
            message: $"a puck.world.definition.v1 rule body is straight-line, so '{keyword}' has nothing to lower onto - {alternative}",
            span: stmt.Span
        );

        return null;
    }
    private static JsonObject LowerCellEffect(string discriminator, RowRefNode target, RhsNode rhs, bool allowText, DocumentScope scope, JsonObject? ruleObj = null) {
        var resolved = ResolveEffectTarget(scope: scope, target: target);
        var obj = new JsonObject { ["$type"] = discriminator, ["state"] = resolved.State };

        if (resolved.Key is not null) {
            obj["key"] = resolved.Key;
        }

        var rootObj = ((scope.Annotations.TryGetValue(key: "WorldDocumentRoot", value: out var rObj) && (rObj is JsonObject ro)) ? ro : null);
        var targetRowSpace = FindRowSpace(rootObj: rootObj, rowName: resolved.Row);
        var isVectorRow = (targetRowSpace is not null);

        if (isVectorRow && (discriminator != "setState")) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted,
                message: $"Vector row '{resolved.Row}' admits only assignment ('=' / setState), not '{discriminator}'.",
                span: target.Span
            );
        }

        ApplyRhs(
            allowText: allowText,
            isVectorRow: isVectorRow,
            obj: obj,
            rhs: rhs,
            ruleObj: ruleObj,
            scope: scope,
            targetRowSpace: targetRowSpace
        );
        return obj;
    }
    // An assignment target's key is a key like any read's: a bare word is that key, and anything else is an
    // expression whose compile-time bindings bind here, exactly as they do in a read of the same cell.
    private static string BindTargetKey(string key, DocumentScope scope) {
        // A key never holds a dot, so a dotted key over a pool binding is the field it reads, as it is in a read.
        if ((QualifiedName.Parse(text: key) is { IsQualified: true, Head.Length: > 0 } dotted) && GetPoolBindings(scope: scope).Contains(item: dotted.Head)) {
            key = (RuleFacts.ExpressionKeyPrefix + key);
        }
        if (!key.StartsWith(comparisonType: StringComparison.Ordinal, value: RuleFacts.ExpressionKeyPrefix)) {
            return key;
        }

        var bound = BindOperand(
            operand: Puck.Transpiler.Parsing.PuckParser.CreateOperand(
                form: DocumentValueForm.Expression,
                text: key[RuleFacts.ExpressionKeyPrefix.Length..]
            ),
            scope: scope
        );

        // The key is read again after the rule's locals leave scope, so a local keeps its explicit spelling here
        // rather than the bare name the source dialect gives it.
        return (RuleFacts.ExpressionKeyPrefix + ((ExpressionSpelling.TryParse(error: out _, program: out var program, text: bound) && ExpressionSpelling.TryPrint(program: program, text: out var printed))
            ? printed
            : bound));
    }
    // `allowText` gates whether an RhsTextNode's Text lands on the object — AddState carries no Text field at all;
    // a string RHS reaching it is already PUCK009 from the parser, and best-effort lowering here emits nothing for
    // the field the destination cannot carry rather than shaping JSON that violates the target's own record.
    private static void ApplyRhs(JsonObject obj, RhsNode rhs, bool allowText, bool isVectorRow, string? targetRowSpace, DocumentScope scope, JsonObject? ruleObj = null) {
        switch (rhs) {
            case RhsTextNode text:
                if (isVectorRow) {
                    if (TryResolveEmbeddedText(text.Text, targetRowSpace, scope, text.Span, out var b64)) {
                        obj["vector"] = b64;
                    }
                } else if (allowText) {
                    obj["text"] = text.Text;
                }
                break;
            case RhsSecondsNode seconds:
                if (isVectorRow) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.VectorLiteralMisplaced,
                        message: "Seconds literal is not admitted on Vector row.",
                        span: seconds.Span
                    );
                } else {
                    obj["valueSeconds"] = seconds.Seconds;
                }
                break;
            case RhsOperandNode operand:
                ApplyOperandRhs(
                    isVectorRow: isVectorRow,
                    obj: obj,
                    ruleObj: ruleObj,
                    scope: scope,
                    span: operand.Span,
                    targetRowSpace: targetRowSpace,
                    text: BindOperand(operand: operand.Expression, scope: scope)
                );
                break;
        }
    }
    // A single unkeyed state read (`row`, no brackets) lowers to a bare `fromState`; a single KEYED read
    // (`row[key]`) always lowers to a verbatim `expression`, never decomposed into `fromState`+`fromKey`, even
    // though both spellings are runtime-equivalent — confirmed against `first-witness` (puck.world.json), which
    // ships `{"$type":"setState","expression":"houndIdentity[$each]",...}` for exactly this RHS shape (§2.3).
    // `fromState`+`fromKey` together stay reachable through call-form for an author who wants that exact wire
    // shape.
    private static void ApplyOperandRhs(JsonObject obj, string text, bool isVectorRow = false, string? targetRowSpace = null, DocumentScope? scope = null, SourceSpan span = default, JsonObject? ruleObj = null) {
        if (scope is not null) {
            if ((DocumentPoolFieldExpression(scope: scope, text: text) is JsonObject typed) && TryTypedPoolFieldKind(kind: out _, scope: scope, text: text)) {
                var typedProgram = ExpressionProgramJsonConverter.FromNode(node: typed);
                var typedState = ((InstructionPayload.State)typedProgram.Instructions[0].Payload!).Name;

                obj["fromState"] = StateChannelRefJsonConverter.ToNode(value: typedState);
                return;
            }
        }

        if (isVectorRow && (scope is not null)) {
            if (ExpressionSpelling.TryParseVector(error: out var parseErr, text: text, token: out var vecOp)) {
                if (vecOp is VectorOperand.Embed emb) {
                    var sp = (emb.Space ?? targetRowSpace);

                    if (TryResolveEmbeddedText(emb.Text, sp, scope, span, out var b64)) {
                        obj["vector"] = b64;
                        return;
                    }
                    return;
                }
                if (vecOp is VectorOperand.Literal lit) {
                    if (TryAdmitVectorLiteral(scope: scope, space: targetRowSpace, span: span, text: lit.Value)) {
                        obj["vector"] = lit.Value;
                    }
                    return;
                }
            } else if (text.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "vector(") || text.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "embed(")) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.VectorLiteralInvalid,
                    message: $"vector or embed literal is invalid: {parseErr}",
                    span: span
                );
                return;
            }
        } else if (!isVectorRow && (scope is not null)) {
            if (text.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "embed(") || text.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "vector(")) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.VectorLiteralMisplaced,
                    message: "embed or vector literal appears where no vector operand or value is admitted.",
                    span: span
                );
                return;
            }
        }

        ExpressionSpelling.TryParse(
            error: out _,
            program: out var parsed,
            text: text
        );
        if (
            (parsed.Instructions.Count == 1) &&
            (parsed.Instructions[0] is { Payload: InstructionPayload.Constant constant })
        ) {
            if (isVectorRow && (scope is not null)) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.VectorLiteralMisplaced,
                    message: "Constant value is not admitted on Vector row.",
                    span: span
                );
            } else {
                obj["value"] = constant.Value;
            }
        } else if (
            (parsed.Instructions.Count == 1) &&
            (parsed.Instructions[0] is { Payload: InstructionPayload.State { Key: null } state })
        ) {
            obj["fromState"] = state.Name.Spelling;
        } else {
            obj["expression"] = DocumentExpression(text: text);
        }
    }

    // Calls, sugar and classified block members bind the same operand tree. Quoted names and computed atoms
    // are terminal values, so binding never interprets their contents as further source syntax.
    internal static JsonNode? LowerOperandArgument(OperandExpressionNode operand, DocumentScope scope) {
        if ((operand.Form == DocumentValueForm.Name) &&
            (operand.Syntax is ExpressionSpelling.SourceName { Quoted: false } name) &&
            scope.TryLowerBinding(name.Name, out var bound) && (bound is JsonArray names)) {
            return names.DeepClone();
        }
        var text = BindOperand(operand: operand, scope: scope);

        switch (operand.Form) {
            case DocumentValueForm.Key:
                if (ExpressionSpelling.TryParseKey(error: out var keyError, key: out var key, text: text)) {
                    return JsonValue.Create(value: key);
                }

                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.OperandParse,
                    message: $"'{operand.Text}' {keyError}",
                    span: operand.Span
                );

                return JsonValue.Create(value: text);

            case DocumentValueForm.Name:
                if (TryPoolBindingField(binding: out var binding, field: out var bindingField, scope: scope, text: text)) {
                    return StateChannelRefJsonConverter.ToNode(value: StateChannelRef.OfBindingField(binding: binding, field: bindingField));
                }
                if (TryStaticPoolField(reference: out var field, scope: scope, text: text)) {
                    return StateChannelRefJsonConverter.ToNode(value: field);
                }

                return JsonValue.Create(value: DocumentReference(text: text));

            case DocumentValueForm.Expression:
                return DocumentPoolFieldExpression(scope: scope, text: ResolveBooleanLiteral(text: text));

            default:
                return JsonValue.Create(value: operand.Text);
        }
    }
    // A StateChannelRef member has one structural representation even when a binding or interpolation computed
    // its spelling. Dots in a name position denote pool fields, never ordinary row names. Keys and expressions
    // retain their separate grammars; their dotted reads need not mean a pool field.
    internal static JsonNode? NormalizeChannelReference(JsonNode? value) {
        if (value is JsonArray array) {
            for (var index = 0; (index < array.Count); index++) {
                var item = array[index];
                var normalized = NormalizeChannelReference(value: item);

                if (!ReferenceEquals(objA: item, objB: normalized)) {
                    array[index] = normalized;
                }
            }
            return array;
        }
        if (
            (value is JsonValue scalar) && scalar.TryGetValue<string>(value: out var text) &&
            text.Contains(value: '.') &&
            ExpressionSpelling.TryParse(error: out _, program: out var program, text: text) &&
            (program.Instructions is [{ Payload: InstructionPayload.State { Name.PoolField: not null, Key: null } state }]) &&
            (program.Subprograms.Count == 0)
        ) {
            return StateChannelRefJsonConverter.ToNode(value: state.Name);
        }

        return value;
    }

    // A reference is one operand. Its document spelling is what the operand grammar prints for it; text that is not
    // one operand (a live zone selector) is already in the document's spelling.
    private static string DocumentReference(string text) {
        using var noLocals = ExpressionSpelling.WithLocals(locals: System.Collections.Frozen.FrozenSet<string>.Empty);

        if ((text.Length > 2) && (text[0] == '`') && (text[^1] == '`') && (text.IndexOf(startIndex: 1, value: '`') == (text.Length - 1))) {
            return text[1..^1];
        }

        return ((
            ExpressionSpelling.TryParse(error: out _, program: out var program, text: text) &&
            (program.Instructions.Count == 1) &&
            (program.Subprograms.Count == 0) &&
            (program.Instructions[0].Payload is InstructionPayload.State { Name.Call: not null }) &&
            ExpressionSpelling.TryPrint(program: program, text: out var printed)
        )
            ? printed
            : text
        );
    }
    private static string ResolveBooleanLiteral(string text) => text.Trim() switch {
        "true" => "1",
        "false" => "0",
        _ => text,
    };
    private static JsonObject LowerPush(PushStatementNode push, DocumentScope scope, JsonObject? ruleObj = null) {
        var resolvedName = BindRowReference(scope: scope, span: push.Span, text: push.RowName);
        var obj = new JsonObject { ["$type"] = "pushState", ["state"] = resolvedName };
        // PushState carries no Text/Key/ValueSeconds field; ApplyRhs's text/seconds arms would shape JSON it
        // cannot carry, so only the classified-operand arm applies here (the parser already refuses the others).
        if (push.Rhs is RhsOperandNode operand) {
            ApplyOperandRhs(
                obj: obj,
                ruleObj: ruleObj,
                scope: scope,
                text: BindOperand(operand: operand.Expression, scope: scope)
            );
        }
        return obj;
    }
    private static JsonObject LowerRowOnlyEffect(string discriminator, RowRefNode target, DocumentScope scope, JsonObject? ruleObj = null) {
        var resolved = ResolveEffectTarget(scope: scope, target: target);
        var obj = new JsonObject { ["$type"] = discriminator, ["state"] = resolved.State };

        if (resolved.Key is not null) {
            obj["key"] = resolved.Key;
        }
        return obj;
    }
    private static JsonObject LowerSchedule(ScheduleStatementNode schedule, DocumentScope scope, JsonObject? ruleObj = null) {
        var resolved = ResolveEffectTarget(scope: scope, target: schedule.Target);
        var obj = new JsonObject {
            ["$type"] = "scheduleState",
            ["state"] = resolved.State,
            ["delaySeconds"] = schedule.DelaySeconds,
        };

        if (resolved.Key is not null) {
            obj["key"] = resolved.Key;
        }
        return obj;
    }

    // The cell an effect writes. State is the document's spelling of the row: a pool field's structural reference,
    // or the name a declared family's member or a live row position resolves to. Row is that row as a name, for
    // reading what the document declares about it; Key is the bound cell key, when the target has one.
    private readonly record struct EffectTarget(JsonNode State, string Row, string? Key);

    // Every effect that addresses one cell resolves its target here, so an assignment, a removal and a schedule
    // agree on what a target spelling means and refuse the same targets.
    private static EffectTarget ResolveEffectTarget(RowRefNode target, DocumentScope scope) {
        // A computed key is spelled first, exactly as the same atom in a read is, and the target is that key: a name
        // the bare form cannot carry comes back backquoted, and the key is the name inside the quotes.
        if (target.KeyAtom is { } atom) {
            var spelled = Puck.Transpiler.Lowering.DocumentLowering.SpliceAtoms(
                operand: Puck.Transpiler.Parsing.PuckParser.CreateOperand(
                    expression: atom,
                    form: DocumentValueForm.Expression
                ),
                scope: scope
            );

            target = (target with {
                Key = (((spelled.Length >= 2) && (spelled[0] == '`') && (spelled[^1] == '`'))
                    ? spelled[1..^1]
                    : spelled
                ),
                KeyAtom = null,
            });
        }

        var written = ((target.Key is null) ? target.Name : $"{target.Name}[{target.Key}]");

        if (GetOrCreateDerivedState(scope: scope).ContainsKey(key: target.Name)) {
            scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.InvalidValue,
                message: $"Derived state '{target.Name}' is read-only; assign to its source row", span: target.Span);
        }
        if ((target.Key is not null) && GetPoolBindings(scope: scope).Contains(item: target.Name)) {
            return new EffectTarget(
                Key: null,
                Row: QualifiedName.Parse(text: target.Name).Append(member: target.Key).ToString(),
                State: StateChannelRefJsonConverter.ToNode(value: StateChannelRef.OfBindingField(binding: target.Name, field: target.Key))
            );
        }
        // A row a module instance of this scope declares, written `alias.name`.
        if (target.FieldAccess && (target.Key is { } member) && scope.TryQualify(qualified: out var instanceRow, reference: QualifiedName.Parse(text: target.Name).Append(member: member).ToString())) {
            return new EffectTarget(Key: null, Row: instanceRow, State: JsonValue.Create(value: instanceRow));
        }
        if (target.FieldAccess && (target.Key?.Contains(value: '.') == true)) {
            scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.OperandParse,
                message: $"'{target.Name}.{target.Key}' names no module instance's row, and a typed lexical pool-field read admits exactly one dot", span: target.Span);
            return new EffectTarget(Key: null, Row: target.Name, State: JsonValue.Create(value: target.Name));
        }
        if (TryStaticPoolField(reference: out var staticField, scope: scope, text: written)) {
            return new EffectTarget(Key: null, Row: written, State: StateChannelRefJsonConverter.ToNode(value: staticField));
        }

        var resolved = BindRowReference(scope: scope, span: target.Span, text: written);
        var row = target.Name;
        var key = target.Key;

        // A live row position stays one spelling, whatever it resolved from: the rule compiler selects the row from
        // it and the remainder, if any, is the cell key inside that row.
        if (TryLiveRowHead(head: out var liveHead, key: out var liveKey, scope: scope, text: resolved)) {
            row = liveHead;
            key = liveKey;
        } else if (resolved != written) {
            var open = resolved.IndexOf(value: '[');
            var close = resolved.LastIndexOf(value: ']');

            row = ((open < 0) ? resolved : resolved[..open]);
            key = ((open < 0) ? null : ((close > open) ? resolved[(open + 1)..close] : resolved[(open + 1)..]));
        }
        if ((row.Length >= 2) && (row[0] == '`') && (row[^1] == '`')) {
            row = row[1..^1];
        }

        return new EffectTarget(
            Key: ((key is null) ? null : BindTargetKey(key: key, scope: scope)),
            Row: row,
            State: JsonValue.Create(value: row)
        );
    }
    private static JsonObject LowerTransaction(TransactionStatementNode transaction, DocumentScope scope, string pointer, JsonObject? ruleObj = null) {
        var obj = new JsonObject {
            ["$type"] = "transaction",
            ["effects"] = LowerEffectList(
                pointer: $"{pointer}/effects",
                ruleObj: ruleObj,
                scope: scope,
                statements: transaction.MainEffects
            ),
        };

        if (transaction.OnFailureEffects is { } onFailure) {
            // The `onFailure` block's own node is the array: its refusals name the array, never one element.
            scope.SourceMap?.Register(
                jsonPointer: $"{pointer}/onFailure",
                span: (transaction.OnFailureSpan ?? transaction.Span)
            );
            obj["onFailure"] = LowerEffectList(
                pointer: $"{pointer}/onFailure",
                ruleObj: ruleObj,
                scope: scope,
                statements: onFailure
            );
        }
        return obj;
    }
    // A name is a template parameter's own identifier only where that parameter is live: a document may bind the
    // same spelling as an ordinary `let`, and a rule named after one of those is the author's literal choice.
    private static bool IsBoundTemplateParameter(string name, DocumentScope scope) {
        if (
            string.IsNullOrEmpty(value: name) ||
            !scope.Constants.ContainsKey(key: name)
        ) {
            return false;
        }

        foreach (var (_, template) in scope.Templates) {
            foreach (var parameter in template.Parameters) {
                if (string.Equals(
                    parameter.Name,
                    name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    return true;
                }
            }
        }
        return false;
    }
}
