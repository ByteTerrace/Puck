using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Rewriting;
using Puck.World.Authoring;

namespace Puck.World.Transpiler.Lowering;

// Shade uses the shared parser, lexical bindings, template argument binding, function recursion guard and for
// expansion. This vocabulary only assigns meanings to stamp reads, runtime slots and phase output assignments.
internal sealed class WorldShadeLowering {
    private sealed record Bound(SdfShadeExpression Value, ExpressionNode Source, DocumentScope Scope) : ExpressionNode;
    private sealed record Runtime(string Parameter) : ExpressionNode;

    private readonly List<SdfShadeParameter> m_parameters = [];
    private readonly Dictionary<string, JsonNode?> m_values = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<(SdfShadePhase, string), SdfShadeExpression> m_outputs = [];
    private readonly HashSet<string> m_resolving = new(comparer: StringComparer.Ordinal);

    private int m_nextParameter;

    public static JsonNode? Lower(ExpressionNode expression, DocumentScope scope) {
        try {
            var lowering = new WorldShadeLowering();
            var name = ((expression is CallExpressionNode call) ? call.Name : "inline");

            lowering.Program(expression: expression, scope: scope);
            if (scope.Diagnostics.HasErrors) { return null; }
            var definition = new SdfShadeDefinition(name, lowering.m_parameters,
                lowering.m_outputs.OrderBy(keySelector: p => p.Key.Item1).ThenBy(p => p.Key.Item2, StringComparer.Ordinal)
                    .Select(selector: p => new SdfShadeOutput(p.Key.Item1, p.Key.Item2, p.Value)).ToArray());
            var program = SdfShadeProgram.Compile(definition: definition);

            return JsonSerializer.SerializeToNode(program.Definition, CreationJsonContext.Document.SdfShadeDefinition);
        } catch (SdfShadeException error) {
            scope.Diagnostics.ReportError(code: error.Code, message: error.Message, span: expression.Span);
            return null;
        } catch (OverflowException) {
            scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.ShadeCapacity, message: "Shade cost or structural arithmetic overflowed.", span: expression.Span);
            return null;
        } catch (JsonException error) {
            scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.ShadeOperand, message: error.Message, span: expression.Span);
            return null;
        }
    }

    private void Program(ExpressionNode expression, DocumentScope scope) {
        if ((expression is CallExpressionNode call) && scope.Templates.TryGetValue(key: call.Name, value: out var template) && template.IsShade) {
            DocumentLowering.ExpandFunction(call.Name, call.Span, scope, () => {
                DocumentLowering.ExpandTemplate(call, new JsonObject(), scope, (statement, _, invocation) => Phase(scope: invocation, statement: statement),
                    bindArgument: BindParameter);
                return true;
            }, false);
            return;
        }
        if (expression is ObjectExpressionNode obj) {
            foreach (var property in obj.Properties) {
                if (property.Value is not ObjectExpressionNode body) { throw new SdfShadeException(code: "PUCK126", message: "A shade phase is a block."); }
                Phase(new BlockNode(property.Name, null, null, body.Properties), scope);
            }
            return;
        }
        if ((expression is IdentifierExpressionNode id) && scope.TryGetExpression(id.Name, out var bound, out var lexical)) {
            Program(expression: bound!, scope: lexical); return;
        }
        throw new SdfShadeException(code: "PUCK126", message: "shade needs a shade template call or a block of surface, light and compose phases.");
    }
    private ExpressionNode BindParameter(TemplateParameterNode parameter, ExpressionNode value, DocumentScope caller) {
        value = RequireStructural(expression: value, scope: caller);
        if (parameter.IsStructural) { return value; }
        var data = DocumentLowering.EvaluateValue(value, caller);
        var name = $"p{m_nextParameter++}";
        var literal = Literal(data);

        m_values.Add(key: name, value: data);
        m_parameters.Add(item: new(name, literal.Type, literal.Bits!));
        return new Runtime(Parameter: name);
    }
    private void Phase(StatementNode statement, DocumentScope scope) {
        if (statement is LetNode) { return; }
        if ((statement is not BlockNode block) || !Enum.TryParse<SdfShadePhase>(block.Identifier, true, out var phase) || !Enum.IsDefined(value: phase)) {
            throw new SdfShadeException(code: "PUCK126", message: "A shade template contains surface, light and compose blocks.");
        }
        var constants = new Dictionary<string, ExpressionNode>(scope.Constants, StringComparer.Ordinal);
        var local = scope.WithConstants(constants);

        foreach (var item in block.Statements) { Statement(phase: phase, scope: local, statement: item); }
    }
    private void Statement(StatementNode statement, DocumentScope scope, SdfShadePhase phase) {
        switch (statement) {
            case LetNode let:
                var snapshot = scope.WithConstants(new(scope.Constants, StringComparer.Ordinal));
                scope.Constants[let.Name] = new Bound(Expression(let.Value, scope), let.Value, snapshot); break;
            case PropertyNode property:
                _ = SdfShadeProgram.OutputType(phase, property.Name);
                m_outputs[(phase, property.Name)] = Expression(property.Value, scope); break;
            case ForStatementNode loop:
                loop = loop with { Sequence = RequireStructural(expression: loop.Sequence, scope: scope) };
                foreach (var (item, iteration) in DocumentLowering.ExpandForStatements(loop: loop, scope: scope)) { Statement(phase: phase, scope: iteration, statement: item); }
                break;
            case ExpressionStatementNode { Expression: CallExpressionNode call }:
                DocumentLowering.ExpandFunction(call.Name, call.Span, scope, () => {
                    DocumentLowering.ExpandTemplate(call, new JsonObject(), scope, (item, _, invocation) => Statement(phase: phase, scope: invocation, statement: item));
                    return true;
                }, false);
                break;
            default: throw new SdfShadeException(code: "PUCK126", message: "Shade phases admit let, assignment, template calls and compile-time for only.");
        }
    }
    private SdfShadeExpression Expression(ExpressionNode expression, DocumentScope scope, SdfShadeType? expected = null) {
        using var budget = scope.Budget.Enter(span: expression.Span);

        switch (expression) {
            case Bound bound:
                if ((bound.Value is { Op: "parameter", Name: { } parameterName }) && (expected is { } parameterType)) { ParameterType(name: parameterName, type: parameterType); }
                return bound.Value;
            case Runtime parameter:
                if (expected is { } type) {
                    ParameterType(name: parameter.Parameter, type: type);
                }
                return new("parameter", Name: parameter.Parameter);
            case IdentifierExpressionNode id:
                if (scope.Locals.TryGetValue(key: id.Name, value: out var local)) { return Literal(expected: expected, node: local); }
                if (scope.TryGetExpression(id.Name, out var declared, out var lexical)) {
                    if (!m_resolving.Add(item: id.Name)) { throw new SdfShadeException(code: "PUCK128", message: $"Shade binding '{id.Name}' reads itself before definition."); }
                    try { return Expression(expected: expected, expression: declared!, scope: lexical); } finally { m_resolving.Remove(item: id.Name); }
                }
                return Read(name: id.Name);
            case MemberAccessExpressionNode member:
                if ((Qualified(node: member) is { } qualified) && SdfShadeOps.Inputs.Any(predicate: i => (i.Name == qualified))) { return Read(name: qualified); }
                if ((member.Member.Length == 1) && ("xyzw".IndexOf(value: member.Member[0]) is var component) && (component >= 0)) {
                    return new("component", [Expression(member.Target, scope)], Name: component.ToString(provider: CultureInfo.InvariantCulture));
                }
                return Read(name: (Qualified(node: member) ?? member.Member));
            case IndexExpressionNode index when (Qualified(node: index.Target) is "lanes.slot" or "lanes.world"):
                var selector = DocumentLowering.EvaluateValue(RequireStructural(expression: index.Index, scope: scope), scope);
                if (!int.TryParse(selector?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lane) || (lane is < 0 or > 3)) {
                    throw new SdfShadeException(code: "PUCK127", message: "A shade lane selector must be an integer from 0 to 3.");
                }
                return Read(name: $"{Qualified(node: index.Target)}{lane}");
            case LiteralExpressionNode or ColorExpressionNode:
                return Literal(DocumentLowering.EvaluateValue(expression, scope), expected);
            case ArrayExpressionNode array:
                return new($"float{array.Elements.Count}", array.Elements.Select(selector: a => Expression(a, scope)).ToArray());
            case BinaryExpressionNode binary:
                if (binary.Operator is ">" or ">=") {
                    return new(((binary.Operator == ">") ? "<" : "<="), [Expression(binary.Right, scope), Expression(binary.Left, scope)]);
                }
                if (binary.Operator == "!=") {
                    return new("select", [new("==", [Expression(binary.Left, scope), Expression(binary.Right, scope)]), Literal(JsonValue.Create(0)), Literal(JsonValue.Create(1))]);
                }
                return new(binary.Operator, [Expression(binary.Left, scope), Expression(binary.Right, scope)]);
            case UnaryExpressionNode { Operator: "-" } unary:
                return new("negate", [Expression(unary.Operand, scope)]);
            case ConditionalExpressionNode conditional:
                return new("select", [Expression(conditional.Condition, scope), Expression(conditional.WhenTrue, scope), Expression(conditional.WhenFalse, scope)]);
            case CallExpressionNode call when (DocumentLowering.FunctionNamed(name: call.Name, scope: scope) is { } function):
                var indices = DocumentLowering.BindFunctionArguments(call.Name, function, call.Arguments.Select(selector: a => a.Name).ToArray(), call.Span, scope);
                if (indices is null) { return Literal(JsonValue.Create(0)); }
                var bindings = new Dictionary<string, ExpressionNode>(scope.Constants, StringComparer.Ordinal);
                for (var i = 0; (i < indices.Length); i++) {
                    var argument = ((indices[i] >= 0) ? call.Arguments[indices[i]].Value : function.Parameters[i].DefaultValue!);

                    bindings[function.Parameters[i].Name] = new Bound(Expression(argument, scope), argument, scope);
                }
                return DocumentLowering.ExpandFunction(call.Name, call.Span, scope,
                    () => Expression(function.Body, scope.WithConstants(bindings)), Literal(JsonValue.Create(0)));
            case CallExpressionNode { Name: "uint" } cast when (cast.Arguments.Count == 1):
                return Literal(DocumentLowering.EvaluateValue(RequireStructural(expression: cast.Arguments[0].Value, scope: scope), scope), SdfShadeType.UInt);
            case CallExpressionNode { Name: "shape" } shape when (shape.Arguments.Count == 2):
                var descriptor = DocumentLowering.EvaluateValue(RequireStructural(expression: shape.Arguments[0].Value, scope: scope), scope);
                var parsed = (descriptor.Deserialize(jsonTypeInfo: CreationJsonContext.Document.SdfShadeShape)
                    ?? throw new SdfShadeException(code: "PUCK127", message: "shape needs a structural descriptor."));
                return new("shape", [Expression(shape.Arguments[1].Value, scope)], Shape: parsed);
            case CallExpressionNode call:
                return new(call.Name, call.Arguments.Select(selector: (a, i) => Expression(a.Value, scope,
                    (((call.Name is "lattice" or "lines") && (i == 1)) ? SdfShadeType.UInt : null))).ToArray());
            default: throw new SdfShadeException(code: "PUCK126", message: "Expression is outside the shade vocabulary.");
        }
    }
    private void ParameterType(string name, SdfShadeType type) {
        var slot = m_parameters.FindIndex(match: p => (p.Name == name));
        var literal = Literal(m_values[name], type);

        m_parameters[slot] = new(name, literal.Type, literal.Bits!);
    }
    private static string? Qualified(ExpressionNode node) => node switch {
        IdentifierExpressionNode id => id.Name,
        MemberAccessExpressionNode member when (Qualified(node: member.Target) is { } head) => $"{head}.{member.Member}",
        _ => null,
    };
    private static SdfShadeExpression Read(string name) {
        if (!SdfShadeOps.Inputs.Any(predicate: i => (i.Name == name))) {
            throw new SdfShadeException(code: (name.Contains(comparisonType: StringComparison.Ordinal, value: '.') ? "PUCK126" : "PUCK128"),
                message: $"Unknown shade input or undefined register '{name}'{DocumentNearestNames.Suggestion(name, SdfShadeOps.Inputs.Select(selector: i => i.Name))}.");
        }
        return new("input", Name: name);
    }
    private static SdfShadeExpression Literal(JsonNode? node, SdfShadeType? expected = null) {
        if ((node is JsonValue boolean) && boolean.TryGetValue<bool>(value: out var flag)) { node = JsonValue.Create((flag ? 1 : 0)); }
        if ((node is JsonValue text) && text.TryGetValue<string>(value: out var color) && HexColor.TryParse(rgb: out var rgb, value: color)) {
            return new("constant", Type: SdfShadeType.Float3,
                Bits: [BitConverter.SingleToUInt32Bits(value: rgb.X), BitConverter.SingleToUInt32Bits(value: rgb.Y), BitConverter.SingleToUInt32Bits(value: rgb.Z)]);
        }
        var values = ((node is JsonArray array) ? array.ToArray() : new[] { node });
        var type = (expected ?? ((SdfShadeType)values.Length));

        if (values.Length != SdfShadeOps.Width(type: type)) { throw new SdfShadeException(code: "PUCK127", message: "Shade literal has the wrong width."); }
        var bits = values.Select(selector: value => {
            if (!double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(d: number)) {
                throw new SdfShadeException(code: "PUCK127", message: "A shade value must be a finite number, vector or colour.");
            }
            if (!SdfShadeOps.IsUInt(type: type)) { return BitConverter.SingleToUInt32Bits(value: ((float)number)); }
            if ((number < 0) || (number > uint.MaxValue) || (Math.Truncate(d: number) != number)) { throw new SdfShadeException(code: "PUCK127", message: "A uint shade parameter must be a 32-bit unsigned integer."); }
            return ((uint)number);
        }).ToArray();

        return new("constant", Type: type, Bits: bits);
    }
    private static ExpressionNode RequireStructural(ExpressionNode expression, DocumentScope scope) => new Structural(scope: scope, visiting: []).Apply(expression: expression);

    // Reuse the language's syntax traversal and value evaluator. A let retains its lexical source for structural
    // evaluation, while the shade expression keeps binary32 folding and runtime slots for presentation arithmetic.
    private sealed class Structural(DocumentScope scope, HashSet<(DocumentScope, string)> visiting) : PuckSyntaxRewriter {
        public ExpressionNode Apply(ExpressionNode expression) => RewriteExpression(expression: expression);

        protected override ExpressionNode RewriteExpression(ExpressionNode expression) {
            if (expression is Runtime) { throw new SdfShadeException(code: "PUCK130", message: "A runtime parameter cannot shape a shade program; mark it structural."); }
            if (expression is Bound bound) { return new Structural(scope: bound.Scope, visiting: visiting).Apply(expression: bound.Source); }
            if (expression is IdentifierExpressionNode id) {
                if (scope.Locals.TryGetValue(key: id.Name, value: out var local)) { return Syntax(value: local); }
                if (scope.TryGetExpression(id.Name, out var source, out var lexical)) {
                    if (!visiting.Add(item: (lexical, id.Name))) { throw new SdfShadeException(code: "PUCK128", message: $"Structural binding '{id.Name}' reads itself."); }
                    try { return new Structural(scope: lexical, visiting: visiting).Apply(expression: source!); } finally { visiting.Remove(item: (lexical, id.Name)); }
                }
            }
            return base.RewriteExpression(expression: expression);
        }

        private static ExpressionNode Syntax(JsonNode? value) => value switch {
            JsonArray array => new ArrayExpressionNode(array.Select(selector: Syntax).ToArray()),
            JsonValue scalar when scalar.TryGetValue<string>(value: out var text) => new LiteralExpressionNode(text),
            JsonValue scalar when scalar.TryGetValue<bool>(value: out var boolean) => new LiteralExpressionNode(boolean),
            null => new LiteralExpressionNode(null),
            _ => new LiteralExpressionNode(decimal.Parse(value.ToString(), CultureInfo.InvariantCulture)),
        };
    }
}
