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
using Puck.Maths;

namespace Puck.World.Transpiler.Lowering;

// Shade uses the shared parser, lexical bindings, template argument binding, function recursion guard and for
// expansion. This vocabulary only assigns meanings to stamp reads, runtime slots and phase output assignments.
internal sealed class WorldShadeLowering {
    // Both stand in a tree as leaves: a bound source is lowered under the scope it carries, never where it stands.
    // A value lowered lazily where it is read, as if at the place it was bound: `Expanding` holds the functions being
    // expanded at that place (DocumentLowering.ExpandAsBound).
    private sealed record Bound(ExpressionNode Source, DocumentScope Scope) : ExpressionNode {
        public Dictionary<SdfShadeType, SdfShadeExpression> Values { get; } = [];
        public string[] Expanding { get; } = DocumentLowering.ExpandingFunctions(scope: Scope);

        public override IReadOnlyList<SyntaxNode> Children() => [];
        public override SyntaxNode MapChildren(ISyntaxMap map) => this;
    }
    private sealed record Runtime(string Parameter) : ExpressionNode {
        public override IReadOnlyList<SyntaxNode> Children() => [];
        public override SyntaxNode MapChildren(ISyntaxMap map) => this;
    }
    // A gradient's height function's parameter: the point the compiler differentiates the height along.
    private sealed record Variable : ExpressionNode {
        public override IReadOnlyList<SyntaxNode> Children() => [];
        public override SyntaxNode MapChildren(ISyntaxMap map) => this;
    }

    private readonly List<SdfShadeParameter> m_parameters = [];
    private readonly Dictionary<string, JsonNode?> m_values = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<(string Name, SdfShadeType Type), string> m_typedParameters = [];
    private readonly Dictionary<(SdfShadePhase, string), SdfShadeExpression> m_outputs = [];
    private readonly HashSet<ExpressionNode> m_resolving = new(ReferenceEqualityComparer.Instance);
    private readonly List<string> m_names = [];

    private int m_nextParameter;
    // A composed program (`shade: [a, b]`) lowers its elements in order into one program. While a later element lowers,
    // this holds the surface outputs every earlier element wrote, which its surface phase reads as its material
    // (ReadComposed); null for the first element and for a program that composes nothing.
    private Dictionary<string, SdfShadeExpression>? m_inherited;
    // The phase whose block is lowering, or null outside a phase block (a template's top-level let).
    private SdfShadePhase? m_phase;

    // One compilation's programs: every program its `shade` values lowered, under its identity (ShadeReference.KeyOf),
    // and each lowered program expression's identity in this compilation (CacheKeyOf) with the program it lowered to. A
    // use lowers to its program's key, so a palette row a let or a template repeats (every exhibit's plinth) lowers its
    // program once, holds it once in the document's `shades` table (Programs) and costs one key a use.
    private sealed class Compilation {
        public Dictionary<string, JsonNode> Programs { get; } = new(comparer: StringComparer.Ordinal);
        public Dictionary<string, string> Lowered { get; } = new(comparer: StringComparer.Ordinal);
        public Dictionary<TemplateNode, int> Templates { get; } = new(comparer: ReferenceEqualityComparer.Instance);
    }
    [SideTable(reason: "collects the shade programs one compilation lowers, keyed by its budget")]
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DocumentEvaluationBudget, Compilation> Compilations = new();

    /// <summary>Returns the programs a compilation's <c>shade</c> values lowered, each under its key, in the order they
    /// were first lowered; empty when it lowered none. The nodes are the compilation's own: a caller copies them.</summary>
    internal static IReadOnlyDictionary<string, JsonNode> ProgramsOf(DocumentEvaluationBudget budget) => (Compilations.TryGetValue(key: budget, value: out var compilation)
        ? compilation.Programs : new Dictionary<string, JsonNode>());

    public static JsonNode? Lower(ExpressionNode expression, DocumentScope scope) {
        // A use written as a key is the document's own spelling of a use: it names a row of the root `shades` table, which
        // the canonical walk (WorldDocumentMembers) refuses in a whole document that does not hold it, and which every
        // door that loads the world checks is that program's identity (WorldShadeTable).
        if (expression is LiteralExpressionNode { Value: string { Length: > 0 } written }) {
            return scope.Budget.Adopt(value: JsonValue.Create(value: written), span: expression.Span);
        }
        var compilation = Compilations.GetValue(key: scope.Budget, createValueCallback: static _ => new());
        var cacheKey = CacheKeyOf(expression: expression, scope: scope, compilation: compilation);

        if ((cacheKey is not null) && compilation.Lowered.TryGetValue(key: cacheKey, value: out var known)) {
            return scope.Budget.Adopt(value: JsonValue.Create(value: known), span: expression.Span);
        }
        try {
            var lowering = new WorldShadeLowering();

            lowering.Program(expression: expression, scope: scope);
            if (scope.Diagnostics.HasErrors) { return null; }
            var name = ((lowering.m_names.Count > 0) ? string.Join(separator: '+', values: lowering.m_names) : "inline");
            var definition = new SdfShadeDefinition(name, lowering.m_parameters,
                lowering.m_outputs.OrderBy(keySelector: p => p.Key.Item1).ThenBy(p => p.Key.Item2, StringComparer.Ordinal)
                    .Select(selector: p => new SdfShadeOutput(p.Key.Item1, p.Key.Item2, p.Value)).ToArray());
            var program = SdfShadeProgram.Compile(definition: definition);
            var key = ShadeReference.KeyOf(definition: program.Definition);

            if (!compilation.Programs.ContainsKey(key: key)) {
                compilation.Programs[key] = scope.Budget.Adopt(value: JsonSerializer.SerializeToNode(program.Definition, CreationJsonContext.Document.SdfShadeDefinition), span: expression.Span)!;
            }
            if (cacheKey is not null) { compilation.Lowered[cacheKey] = key; }
            return scope.Budget.Adopt(value: JsonValue.Create(value: key), span: expression.Span);
        } catch (SdfShadeException error) {
            scope.Diagnostics.ReportError(code: error.Code, message: error.Message, span: expression.Span);
            return null;
        } catch (OverflowException) {
            scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.ShadeCapacity, message: DiagnosticText.Compose(fix: "Simplify the program: use fewer or smaller structural loops, or fewer operations", problem: "The shade program is too large: its cost or its structural arithmetic overflowed"), span: expression.Span);
            return null;
        } catch (JsonException error) {
            scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.ShadeOperand, message: error.Message, span: expression.Span);
            return null;
        }
    }

    // A program expression's identity in this compilation, everything its lowering reads (Program): a shade template
    // call's template and each argument's name and value (BindParameter evaluates every argument as data or a structural
    // constant), a program read as a value by that value, a binding by what it binds, and a list by its elements in
    // order. Null for a block of phases, and for anything whose values do not evaluate cleanly, which lowers on its own.
    private static string? CacheKeyOf(ExpressionNode expression, DocumentScope scope, Compilation compilation) {
        var reported = scope.Diagnostics.Count;
        var text = new System.Text.StringBuilder();

        try {
            return (Append(expression: expression, scope: scope) && (scope.Diagnostics.Count == reported)) ? text.ToString() : null;
        } catch (DocumentEvaluationException) {
            return null;
        }

        bool Append(ExpressionNode expression, DocumentScope scope) {
            switch (expression) {
                case ArrayExpressionNode composed:
                    text.Append(value: '[');
                    foreach (var element in composed.Elements) {
                        if (!Append(expression: element, scope: scope)) { return false; }
                        text.Append(value: ',');
                    }
                    text.Append(value: ']');
                    return true;
                case CallExpressionNode call when scope.Templates.TryGetValue(key: call.Name, value: out var template) && template.IsShade:
                    if (!compilation.Templates.TryGetValue(key: template, value: out var ordinal)) { compilation.Templates[template] = ordinal = compilation.Templates.Count; }
                    text.Append(value: 'T').Append(value: ordinal).Append(value: '(');
                    foreach (var argument in call.Arguments) {
                        // A function argument (a relief's height) is code, not a value: such a call lowers on its own.
                        if (Function(expression: argument.Value, scope: scope) is not null) { return false; }
                        text.Append(value: argument.Name).Append(value: '=')
                            .Append(value: (DocumentLowering.EvaluateValue(expr: argument.Value, scope: scope)?.ToJsonString() ?? "null")).Append(value: ';');
                    }
                    text.Append(value: ')');
                    return true;
                case IdentifierExpressionNode id when scope.TryGetExpression(id.Name, out var bound, out var lexical):
                    return Append(expression: bound!, scope: lexical);
                case IdentifierExpressionNode or MemberAccessExpressionNode or IndexExpressionNode:
                    text.Append(value: '=').Append(value: (DocumentLowering.EvaluateValue(expression, scope)?.ToJsonString() ?? "null"));
                    return true;
                default:
                    return false;
            }
        }
    }
    // A program is a shade template call, a block of phases, a binding of either, an admitted program held as a value (a
    // palette row's `shade`, read as `base.shade`), or a list of programs composed in order. In a composition each
    // element's outputs replace the same outputs of the elements before it, and its surface phase reads what they wrote
    // as its material (`material.albedo`, `surface.normal`), so a surface treatment layers over another and a light
    // response (cel) joins a surface treatment (terrain) as one program. An absent element (a row with no `shade`) adds
    // nothing.
    private void Program(ExpressionNode expression, DocumentScope scope, bool optional = false) {
        if (expression is ArrayExpressionNode composed) {
            var enclosing = m_inherited;

            foreach (var element in composed.Elements) {
                m_inherited = m_outputs.Where(predicate: o => (o.Key.Item1 == SdfShadePhase.Surface))
                    .ToDictionary(keySelector: o => o.Key.Item2, elementSelector: o => o.Value, comparer: StringComparer.Ordinal);
                Program(expression: element, scope: scope, optional: true);
            }
            m_inherited = enclosing;
            return;
        }
        if ((expression is CallExpressionNode call) && scope.Templates.TryGetValue(key: call.Name, value: out var template) && template.IsShade) {
            m_names.Add(item: call.Name);
            DocumentLowering.ExpandFunction(call.Name, call.Span, scope, () => {
                DocumentLowering.ExpandTemplate(call, new JsonObject(), scope, (statement, _, invocation) => Phase(scope: invocation, statement: statement),
                    bindArgument: BindParameter);
                return true;
            }, false);
            return;
        }
        if (expression is ObjectExpressionNode obj) {
            m_names.Add(item: "inline");
            foreach (var property in obj.Properties) {
                if (property.Value is not ObjectExpressionNode body) { throw new SdfShadeException(code: "PUCK126", message: DiagnosticText.Compose(example: "'shade { surface { ... } }'", fix: "Write each phase ('surface', 'light', 'compose') as a block", problem: "A shade phase is written as something other than a block")); }
                Phase(new BlockNode(property.Name, null, null, body.Properties), scope);
            }
            return;
        }
        if ((expression is IdentifierExpressionNode id) && scope.TryGetExpression(id.Name, out var bound, out var lexical)) {
            Program(expression: bound!, scope: lexical, optional: optional); return;
        }
        if (expression is IdentifierExpressionNode or MemberAccessExpressionNode or IndexExpressionNode) {
            switch (DocumentLowering.EvaluateValue(expression, scope)) {
                case JsonObject admitted:
                    Import(definition: (admitted.Deserialize(jsonTypeInfo: CreationJsonContext.Document.SdfShadeDefinition)
                        ?? throw new SdfShadeException(code: "PUCK126", message: DiagnosticText.Compose(fix: "Use a row's 'shade', which names an admitted program", problem: "A value used as a shade program is not one"))));
                    return;
                // An admitted program is held by its key: a row's `shade` this compilation lowered names its program.
                case JsonValue named when named.TryGetValue<string>(value: out var key):
                    Import(definition: ((Compilations.TryGetValue(key: scope.Budget, value: out var compilation) && compilation.Programs.TryGetValue(key: key, value: out var held))
                        ? held.Deserialize(jsonTypeInfo: CreationJsonContext.Document.SdfShadeDefinition)!
                        : throw new SdfShadeException(code: "PUCK126", message: DiagnosticText.Compose(fix: "Check that the row it reads from declares a 'shade' in this compilation", problem: $"The shade value '{key}' names no program this compilation lowered"))));
                    return;
                case null when optional:
                    return;
            }
        }
        throw new SdfShadeException(code: "PUCK126",
            message: DiagnosticText.Compose(example: "'shade: terrain(...)' or 'shade { surface { ... } }'", fix: "Write a call to a shade template, a block of 'surface', 'light' and 'compose' phases, an admitted program, or a list of them", problem: "This 'shade' is none of the forms a shade takes"));
    }
    // Lowers an admitted program's canonical definition into this one: each named value expands into the expression it
    // names (shared, so the compiler still lowers it once), its parameters take fresh slots, and its surface reads see
    // what earlier elements of a composition wrote, as a template's do.
    private void Import(SdfShadeDefinition definition) {
        var renamed = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var values = new Dictionary<(SdfShadePhase, string), SdfShadeExpression>();

        m_names.Add(item: definition.Name);
        foreach (var parameter in definition.Parameters) {
            var name = $"p{m_parameters.Count}";

            renamed.Add(key: parameter.Name, value: name);
            m_parameters.Add(item: parameter with { Name = name });
        }
        foreach (var value in (definition.Values ?? [])) { values[(value.Phase, value.Name)] = Expand(expression: value.Value, phase: value.Phase); }
        foreach (var output in definition.Outputs) { m_outputs[(output.Phase, output.Name)] = Expand(expression: output.Value, phase: output.Phase); }

        SdfShadeExpression Expand(SdfShadeExpression expression, SdfShadePhase phase) => expression.Op switch {
            "value" => (values.GetValueOrDefault(key: (phase, (expression.Name ?? "")))
                ?? throw new SdfShadeException(code: "PUCK128", message: DiagnosticText.Compose(fix: $"Declare it with 'let {expression.Name} = ...' earlier in the phase, or check the spelling", problem: $"The shade value '{expression.Name}' is not defined in the {phase} phase"))),
            "parameter" => expression with {
                Name = (renamed.GetValueOrDefault(key: (expression.Name ?? ""))
                    ?? throw new SdfShadeException(code: "PUCK128", message: DiagnosticText.Compose(fix: "Declare the parameter in the shade template's parameter list, or check the spelling", problem: $"The shade parameter '{expression.Name}' is not defined"))),
            },
            "input" => (ReadComposed(name: (expression.Name ?? ""), phase: phase) ?? expression),
            _ => ((expression.Arguments is { Count: > 0 } arguments)
                ? expression with { Arguments = arguments.Select(selector: a => Expand(expression: a, phase: phase)).ToArray() } : expression),
        };
    }
    // What a composed element's surface phase reads for a material member or the surface normal: the output an earlier
    // element wrote, or null where none did (the material's own value). Only the surface phase layers this way; the light
    // and compose phases read the composed surface through `surface.albedo` and its siblings.
    private SdfShadeExpression? ReadComposed(string name, SdfShadePhase? phase) {
        if ((m_inherited is not { } prior) || (phase != SdfShadePhase.Surface)) { return null; }
        var member = (name.StartsWith(value: "material.", comparisonType: StringComparison.Ordinal) ? name["material.".Length..]
            : ((name == "surface.normal") ? "normal" : null));

        return ((member is not null) && prior.TryGetValue(key: member, value: out var written)) ? written : null;
    }
    private ExpressionNode BindParameter(TemplateParameterNode parameter, ExpressionNode value, DocumentScope caller) {
        // A function passed to a template (the height a relief differentiates) is bound as written, in the caller's scope.
        if (Function(expression: value, scope: caller) is not null) { return new Bound(value, caller); }
        value = RequireStructural(expression: value, scope: caller);
        if (parameter.IsStructural) {
            // A structural argument read out of a record or a list (`pen.uneven`, `rows[2]`) enters as its value: inside
            // a phase a member access names a stamp member, never the caller's data.
            return ((value is MemberAccessExpressionNode or IndexExpressionNode || ((value is IdentifierExpressionNode named) && named.Name.Contains(value: '.')))
                ? Structural.Syntax(value: DocumentLowering.EvaluateValue(value, caller))
                : value);
        }
        var data = DocumentLowering.EvaluateValue(value, caller);
        var name = $"p{m_nextParameter++}";
        _ = Literal(data);

        m_values.Add(key: name, value: data);
        return new Runtime(Parameter: name);
    }
    private void Phase(StatementNode statement, DocumentScope scope) {
        if (statement is LetNode let) { BindLet(let, scope); return; }
        if ((statement is not BlockNode block) || !Enum.TryParse<SdfShadePhase>(block.Identifier, true, out var phase) || !Enum.IsDefined(value: phase)) {
            throw new SdfShadeException(code: "PUCK126", message: DiagnosticText.Compose(fix: "Keep a shade template's body to 'surface', 'light', 'compose' and 'ink' blocks", problem: "A shade template holds a statement that is not one of those blocks"));
        }
        var constants = new Dictionary<string, ExpressionNode>(scope.Constants, StringComparer.Ordinal);
        var local = scope.WithConstants(constants);

        m_phase = phase;
        try {
            foreach (var item in block.Statements) { Statement(phase: phase, scope: local, statement: item); }
        } finally { m_phase = null; }
    }
    private void Statement(StatementNode statement, DocumentScope scope, SdfShadePhase phase) {
        switch (statement) {
            case LetNode let:
                BindLet(let, scope); break;
            case PropertyNode property:
                m_outputs[(phase, property.Name)] = Expression(property.Value, scope, SdfShadeProgram.OutputType(phase, property.Name)); break;
            case ForStatementNode loop:
                loop = loop with { Sequence = RequireStructural(expression: loop.Sequence, scope: scope) };
                foreach (var (item, iteration) in DocumentLowering.ExpandForStatements(bindLets: false, loop: loop, scope: scope)) { Statement(phase: phase, scope: iteration, statement: item); }
                break;
            case ExpressionStatementNode { Expression: CallExpressionNode call }:
                DocumentLowering.ExpandFunction(call.Name, call.Span, scope, () => {
                    DocumentLowering.ExpandTemplate(call, new JsonObject(), scope, (item, _, invocation) => Statement(phase: phase, scope: invocation, statement: item));
                    return true;
                }, false);
                break;
            default: throw new SdfShadeException(code: "PUCK126", message: DiagnosticText.Compose(fix: "Use only 'let', an assignment, a template call or a compile-time 'for' inside a shade phase", problem: "A shade phase holds a statement it does not admit"));
        }
    }
    private void BindLet(LetNode let, DocumentScope scope) {
        var snapshot = scope.WithConstants(new(scope.Constants, StringComparer.Ordinal)).WithLocals(new(scope.Locals, StringComparer.Ordinal));
        var bound = new Bound(let.Value, snapshot);
        bound.Values.Add(0, Expression(let.Value, snapshot));
        scope.Constants[let.Name] = bound;
    }
    private SdfShadeExpression Expression(ExpressionNode expression, DocumentScope scope, SdfShadeType? expected = null) {
        using var budget = scope.Budget.Enter(span: expression.Span);

        switch (expression) {
            case Bound bound:
                var expectedKey = expected ?? 0;
                if ((expected is null || !SdfShadeOps.IsUInt(expected.Value)) && bound.Values.TryGetValue(0, out var inferred)) { return inferred; }
                if (!bound.Values.TryGetValue(expectedKey, out var value)) {
                    value = DocumentLowering.ExpandAsBound(scope: bound.Scope, atBinding: bound.Expanding, expand: () => Expression(bound.Source, bound.Scope, expected));
                    bound.Values.Add(expectedKey, value);
                }
                return value;
            case Runtime parameter:
                var literal = Literal(m_values[parameter.Parameter], expected);
                var key = (parameter.Parameter, literal.Type);
                if (!m_typedParameters.TryGetValue(key, out var typedName)) {
                    typedName = $"p{m_parameters.Count}";
                    m_typedParameters.Add(key, typedName);
                    m_parameters.Add(new(typedName, literal.Type, literal.Bits!));
                }
                return new("parameter", Name: typedName);
            case IdentifierExpressionNode id:
                if (id.Name.Length > 2 && id.Name[^2] == '.' && "xyzw".IndexOf(id.Name[^1]) is var channel && channel >= 0) {
                    var parent = id.Name[..^2];
                    var target = scope.TryGetExpression(parent, out var selected, out var selectedScope)
                        ? Expression(selected!, selectedScope) : Read(parent);
                    return new("component", [target], Name: channel.ToString(CultureInfo.InvariantCulture));
                }
                if (scope.Locals.TryGetValue(key: id.Name, value: out var local)) { return Literal(expected: expected, node: local); }
                if (scope.TryGetExpression(id.Name, out var declared, out var lexical)) {
                    if (!m_resolving.Add(item: declared!)) { throw new SdfShadeException(code: "PUCK128", message: DiagnosticText.Compose(fix: "Define the binding from other values, or rename the earlier one it reads", problem: $"The shade binding '{id.Name}' reads itself before it is defined")); }
                    try { return Expression(expected: expected, expression: declared!, scope: lexical); } finally { m_resolving.Remove(item: declared!); }
                }
                // The language's named constants (`pi`) are the numbers a document reads them as; a binding of the name hides one.
                if (DocumentBuiltins.NamedConstants.TryGetValue(key: id.Name, value: out var named)) { return Literal(expected: expected, node: JsonValue.Create(value: named)); }
                return Read(name: id.Name);
            case MemberAccessExpressionNode member:
                if ((Qualified(node: member) is { } qualified) && SdfShadeOps.Inputs.Any(predicate: i => (i.Name == qualified))) { return Read(name: qualified); }
                if ((member.Target is IndexExpressionNode or MemberAccessExpressionNode) && TryStructuralLiteral(expression: member, scope: scope, expected: expected, literal: out var record)) { return record; }
                if ((member.Member.Length == 1) && ("xyzw".IndexOf(value: member.Member[0]) is var component) && (component >= 0)) {
                    return new("component", [Expression(member.Target, scope)], Name: component.ToString(provider: CultureInfo.InvariantCulture));
                }
                return Read(name: (Qualified(node: member) ?? member.Member));
            case IndexExpressionNode index when (Qualified(node: index.Target) is "lanes.slot" or "lanes.world"):
                var selector = DocumentLowering.EvaluateValue(RequireStructural(expression: index.Index, scope: scope), scope);
                if (!int.TryParse(selector?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lane) || (lane is < 0 or > 3)) {
                    throw new SdfShadeException(code: "PUCK127", message: DiagnosticText.Compose(example: "'lanes.slot[2]'", fix: "Index with a whole number from 0 to 3 that is known when the program is compiled", problem: "A shade lane selector is not a whole number from 0 to 3"));
                }
                return Read(name: $"{Qualified(node: index.Target)}{lane}");
            case IndexExpressionNode structuralIndex when TryStructuralLiteral(expression: structuralIndex, scope: scope, expected: expected, literal: out var item):
                return item;
            case CallExpressionNode { Name: "reduce" } fold when ((fold.Arguments.Count == 3) && (fold.Arguments[2].Value is LambdaExpressionNode { Parameters.Count: 2 } step)):
                return Reduce(fold: fold, step: step, scope: scope, expected: expected);
            case LiteralExpressionNode or ColorExpressionNode:
                return Literal(DocumentLowering.EvaluateValue(expression, scope), expected);
            case ArrayExpressionNode array:
                if (expected == SdfShadeType.UInt3) { return Literal(DocumentLowering.EvaluateValue(RequireStructural(array, scope), scope), expected); }
                return new($"float{array.Elements.Count}", array.Elements.Select(selector: a => Expression(a, scope)).ToArray());
            case BinaryExpressionNode binary:
                if (binary.Operator is ">" or ">=") {
                    return new(((binary.Operator == ">") ? "<" : "<="), [Expression(binary.Right, scope), Expression(binary.Left, scope)]);
                }
                if (binary.Operator == "!=") {
                    return new("select", [new("==", [Expression(binary.Left, scope), Expression(binary.Right, scope)]), Literal(JsonValue.Create(0)), Literal(JsonValue.Create(1))]);
                }
                var operandType = expected is { } integer && SdfShadeOps.IsUInt(integer) ? expected : null;
                return new(binary.Operator, [Expression(binary.Left, scope, operandType), Expression(binary.Right, scope, operandType)]);
            case UnaryExpressionNode { Operator: "-" } unary:
                return new("negate", [Expression(unary.Operand, scope)]);
            case ConditionalExpressionNode conditional:
                return new("select", [Expression(conditional.Condition, scope), Expression(conditional.WhenTrue, scope, expected), Expression(conditional.WhenFalse, scope, expected)]);
            case CallExpressionNode call when (DocumentLowering.FunctionNamed(name: call.Name, scope: scope) is { } function):
                var indices = DocumentLowering.BindFunctionArguments(call.Name, function, call.Arguments.Select(selector: a => a.Name).ToArray(), call.Span, scope);
                if (indices is null) { return Literal(JsonValue.Create(0)); }
                var bindings = new Dictionary<string, ExpressionNode>(scope.Constants, StringComparer.Ordinal);
                for (var i = 0; (i < indices.Length); i++) {
                    var argument = ((indices[i] >= 0) ? call.Arguments[indices[i]].Value : function.Parameters[i].DefaultValue!);

                    bindings[function.Parameters[i].Name] = new Bound(argument, scope);
                }
                return DocumentLowering.ExpandFunction(call.Name, call.Span, scope,
                    () => Expression(function.Body, scope.WithConstants(bindings), expected), Literal(JsonValue.Create(0)));
            // The document language's `fraction` and `clamp`, which the table has no op for, lower to the ops that make them
            // (`x - floor(x)`, `min(max(x, lo), hi)`), so a function an author wrote by hand and the name are one program.
            case CallExpressionNode fraction when ((DocumentBuiltins.Canonical(name: fraction.Name) == "fraction") && (fraction.Arguments.Count == 1)):
                var fractionOf = Expression(fraction.Arguments[0].Value, scope);
                return new("-", [fractionOf, new("floor", [fractionOf])]);
            case CallExpressionNode { Name: "clamp", Arguments.Count: 3 } clamp:
                return new("min", [new("max", [Expression(clamp.Arguments[0].Value, scope), Expression(clamp.Arguments[1].Value, scope)]), Expression(clamp.Arguments[2].Value, scope)]);
            case CallExpressionNode { Name: "gradient" } gradient when (gradient.Arguments.Count == 2):
                return Gradient(point: gradient.Arguments[0].Value, height: gradient.Arguments[1].Value, scope: scope);
            case Variable:
                return new("variable");
            case CallExpressionNode { Name: "uint" } cast when (cast.Arguments.Count == 1):
                return Literal(DocumentLowering.EvaluateValue(RequireStructural(expression: cast.Arguments[0].Value, scope: scope), scope), SdfShadeType.UInt);
            case CallExpressionNode { Name: "shape" } shape when (shape.Arguments.Count == 2):
                var descriptor = DocumentLowering.EvaluateValue(RequireStructural(expression: shape.Arguments[0].Value, scope: scope), scope);
                var parsed = (descriptor.Deserialize(jsonTypeInfo: CreationJsonContext.Document.SdfShadeShape)
                    ?? throw new SdfShadeException(code: "PUCK127", message: DiagnosticText.Compose(fix: "Pass a descriptor that the compiler can evaluate, such as a constant", problem: "A 'shape' call is given a descriptor that is not structural")));
                return new("shape", [Expression(shape.Arguments[1].Value, scope)], Shape: parsed);
            case CallExpressionNode call:
                return new(call.Name, call.Arguments.Select(selector: (a, i) => Expression(a.Value, scope,
                    call.Name == "hash" ? SdfShadeType.UInt3 :
                    ((((call.Name is "lattice" or "latticeGradient" or "lines" or "cells" or "cellBorder" or "cellBorderGradient") && (i == 1)) || call.Name == "xor") ? SdfShadeType.UInt :
                    call.Name == "select" && i > 0 ? expected : null))).ToArray());
            default: throw new SdfShadeException(code: "PUCK126", message: DiagnosticText.Compose(fix: "Rewrite it with the shade operations, names and numbers a shade program admits", problem: "This expression uses something a shade program has no word for"));
        }
    }
    // `gradient(point, q => height)`: the exact gradient of the height function at the point, which the shade compiler
    // differentiates (SdfShadeProgram's Differentiation). The function's parameter reads as the point it is
    // differentiated along; everything else it reads is held at its value there.
    private SdfShadeExpression Gradient(ExpressionNode point, ExpressionNode height, DocumentScope scope) {
        var (function, lexical) = (Function(expression: height, scope: scope)
            ?? throw new SdfShadeException(code: "PUCK126", message: DiagnosticText.Compose(example: "'gradient(surface.position, q => shadeFbm(q * 4, uint(7)))'", fix: "Pass the height as a function of one point", problem: "A gradient's height is not a function of the point")));

        if (function.Parameters.Count != 1) {
            throw new SdfShadeException(code: "PUCK126", message: DiagnosticText.Compose(example: "'q => shadeFbm(q, seed)'", fix: "Write the height as a function of one point", problem: $"A gradient's height function takes {function.Parameters.Count} parameters"));
        }
        var constants = new Dictionary<string, ExpressionNode>(lexical.Constants, StringComparer.Ordinal) { [function.Parameters[0]] = new Variable() };

        return new("gradient", [Expression(point, scope), Expression(function.Body, lexical.WithConstants(constants), SdfShadeType.Float)]);
    }
    // The function an expression names, with the scope it was written in: a lambda, or a binding or argument that holds
    // one; null for anything else.
    private static (LambdaExpressionNode Function, DocumentScope Scope)? Function(ExpressionNode expression, DocumentScope scope) {
        for (var hops = 0; (hops < 64); hops++) {
            switch (expression) {
                case LambdaExpressionNode function: return (function, scope);
                case Bound bound: (expression, scope) = (bound.Source, bound.Scope); break;
                case IdentifierExpressionNode id when !scope.Locals.ContainsKey(key: id.Name) && scope.TryGetExpression(id.Name, out var declared, out var lexical) && (declared is not null):
                    (expression, scope) = (declared, lexical); break;
                default: return null;
            }
        }
        return null;
    }
    // A compile-time reduce over a structural array unrolls into one expression: the running value is the shade
    // expression folded so far and each item enters as its value, so `reduce(range(0, octaves), ...)` lowers to the
    // program the hand-unrolled sum would (ergonomic is optimal).
    private SdfShadeExpression Reduce(CallExpressionNode fold, LambdaExpressionNode step, DocumentScope scope, SdfShadeType? expected) {
        var items = (DocumentLowering.EvaluateValue(RequireStructural(expression: fold.Arguments[0].Value, scope: scope), scope) as JsonArray)
            ?? throw new SdfShadeException(code: "PUCK127", message: DiagnosticText.Compose(example: "'reduce(range(0, octaves), 0, (sum, i) => ...)'", fix: "Reduce over an array the compiler knows, such as a range of structural numbers", problem: "A shade 'reduce' runs over something that is not an array known when the program is compiled"));
        var running = Expression(fold.Arguments[1].Value, scope, expected);

        foreach (var value in items) {
            var held = new Bound(fold.Arguments[1].Value, scope);
            held.Values.Add(0, running);
            var constants = new Dictionary<string, ExpressionNode>(scope.Constants, StringComparer.Ordinal) {
                [step.Parameters[0]] = held,
                [step.Parameters[1]] = Structural.Syntax(value: value),
            };
            running = Expression(step.Body, scope.WithConstants(constants), expected);
        }
        return running;
    }
    // Reads a member or element of compile-time data (`stops[i].color`, `size[0]`) as the literal it evaluates to, or
    // answers false when the expression reads anything the program supplies at run time.
    private bool TryStructuralLiteral(ExpressionNode expression, DocumentScope scope, SdfShadeType? expected, out SdfShadeExpression literal) {
        literal = null!;
        if (Qualified(node: expression) is { } name && SdfShadeOps.Inputs.Any(predicate: i => i.Name.StartsWith(value: name, comparisonType: StringComparison.Ordinal))) { return false; }
        JsonNode? value;
        try { value = DocumentLowering.EvaluateValue(RequireStructural(expression: expression, scope: scope), scope); }
        catch (SdfShadeException) { return false; }
        catch (DocumentEvaluationException) { return false; }
        if (value is not (JsonValue or JsonArray)) { return false; }
        literal = Literal(value, expected);
        return true;
    }
    private static string? Qualified(ExpressionNode node) => node switch {
        IdentifierExpressionNode id => id.Name,
        MemberAccessExpressionNode member when (Qualified(node: member.Target) is { } head) => $"{head}.{member.Member}",
        _ => null,
    };
    private SdfShadeExpression Read(string name) {
        if (ReadComposed(name: name, phase: m_phase) is { } written) { return written; }
        if (!SdfShadeOps.Inputs.Any(predicate: i => (i.Name == name))) {
            throw new SdfShadeException(code: (name.Contains(comparisonType: StringComparison.Ordinal, value: '.') ? "PUCK126" : "PUCK128"),
                message: $"Unknown shade input or undefined register '{name}'{DocumentNearestNames.Suggestion(name, SdfShadeOps.Inputs.Select(selector: i => i.Name))}.");
        }
        return new("input", Name: name);
    }
    private static SdfShadeExpression Literal(JsonNode? node, SdfShadeType? expected = null) {
        if ((node is JsonValue boolean) && boolean.TryGetValue<bool>(value: out var flag)) { node = JsonValue.Create((flag ? 1 : 0)); }
        // A colour literal in a shading program is light or reflectance: the display colour it spells, decoded.
        if ((node is JsonValue text) && text.TryGetValue<string>(value: out var color) && HexColor.TryParse(color: out var display, value: color)) {
            var rgb = display.ToScene().Linear;

            return new("constant", Type: SdfShadeType.Float3,
                Bits: [BitConverter.SingleToUInt32Bits(value: rgb.X), BitConverter.SingleToUInt32Bits(value: rgb.Y), BitConverter.SingleToUInt32Bits(value: rgb.Z)]);
        }
        var values = ((node is JsonArray array) ? array.ToArray() : new[] { node });
        var type = (expected ?? ((SdfShadeType)values.Length));

        if (values.Length != SdfShadeOps.Width(type: type)) { throw new SdfShadeException(code: "PUCK127", message: DiagnosticText.Compose(fix: "Write as many components as the type takes: one for a number, three for a vector, four for a colour with alpha", problem: "A shade literal has the wrong number of components for its type")); }
        var bits = values.Select(selector: value => {
            if (!double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(d: number)) {
                throw new SdfShadeException(code: "PUCK127", message: DiagnosticText.Compose(fix: "Write an ordinary number, a vector of numbers or a colour", problem: "A shade value is not a finite number, vector or colour"));
            }
            if (!SdfShadeOps.IsUInt(type: type)) { return BitConverter.SingleToUInt32Bits(value: ((float)number)); }
            if ((number < 0) || (number > uint.MaxValue) || (Math.Truncate(d: number) != number)) { throw new SdfShadeException(code: "PUCK127", message: DiagnosticText.Compose(fix: "Write a whole number from 0 to 4294967295", problem: "A 'uint' shade parameter is not a 32-bit unsigned integer")); }
            return ((uint)number);
        }).ToArray();

        return new("constant", Type: type, Bits: bits);
    }
    private static ExpressionNode RequireStructural(ExpressionNode expression, DocumentScope scope) => new Structural(scope: scope, visiting: new(ReferenceEqualityComparer.Instance)).Apply(expression: expression);

    // Reuse the language's syntax traversal and value evaluator. A let retains its lexical source for structural
    // evaluation, while the shade expression keeps binary32 folding and runtime slots for presentation arithmetic.
    private sealed class Structural(DocumentScope scope, HashSet<ExpressionNode> visiting) : PuckSyntaxRewriter {
        public ExpressionNode Apply(ExpressionNode expression) => RewriteExpression(expression: expression);

        protected override ExpressionNode RewriteExpression(ExpressionNode expression) {
            if (expression is Variable) { throw new SdfShadeException(code: "PUCK130", message: DiagnosticText.Compose(fix: "Use the height's point only in the height's arithmetic", problem: "A gradient's point is used where it would shape the program (a loop range, a shape descriptor or a lane selector)")); }
            if (expression is Runtime) { throw new SdfShadeException(code: "PUCK130", message: DiagnosticText.Compose(fix: "Mark the parameter 'structural' in the template's parameter list, so the compiler knows its value", problem: "A runtime parameter is used where it would shape the program (a loop range, a shape descriptor or a lane selector)")); }
            if (expression is Bound bound) { return new Structural(scope: bound.Scope, visiting: visiting).Apply(expression: bound.Source); }
            if (expression is IdentifierExpressionNode id) {
                if (scope.Locals.TryGetValue(key: id.Name, value: out var local)) { return Syntax(value: local); }
                if (scope.TryGetExpression(id.Name, out var source, out var lexical)) {
                    if (!visiting.Add(item: source!)) { throw new SdfShadeException(code: "PUCK128", message: DiagnosticText.Compose(fix: "Define the binding from other values", problem: $"The structural binding '{id.Name}' reads itself")); }
                    try { return new Structural(scope: lexical, visiting: visiting).Apply(expression: source!); } finally { visiting.Remove(item: source!); }
                }
            }
            return base.RewriteExpression(expression: expression);
        }

        internal static ExpressionNode Syntax(JsonNode? value) => value switch {
            JsonArray array => new ArrayExpressionNode(array.Select(selector: Syntax).ToArray()),
            JsonObject record => new ObjectExpressionNode(record.Select(selector: p => new PropertyNode(p.Key, Syntax(value: p.Value))).ToArray()),
            JsonValue scalar when scalar.TryGetValue<string>(value: out var text) => new LiteralExpressionNode(text),
            JsonValue scalar when scalar.TryGetValue<bool>(value: out var boolean) => new LiteralExpressionNode(boolean),
            null => new LiteralExpressionNode(null),
            _ => new LiteralExpressionNode(decimal.Parse(value.ToString(), CultureInfo.InvariantCulture)),
        };
    }
}
