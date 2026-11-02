using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Syntax = Puck.State.ExpressionSpelling;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    // Binding walks parsed names and accesses. Computed atoms and quoted names are terminal values: neither is
    // ever sent back through symbol resolution, so a binding cannot inject an operator or capture another name.
    internal static string BindOperand(OperandExpressionNode operand, DocumentScope scope) =>
        new OperandBinder(scope: scope).Bind(operand: operand);

    private static ExpressionNode CompileTimeOperand(OperandExpressionNode operand, DocumentScope scope) =>
        new OperandBinder(scope: scope).CompileTime(operand: operand);
    // The operand with its value functions inlined (OperandBinder.Inline) and what its compile-time constants decide
    // folded (OperandBinder.Fold), or itself when neither changes it.
    private static OperandExpressionNode FoldOperand(OperandExpressionNode operand, DocumentScope scope) {
        if ((operand.Form != DocumentValueForm.Expression) || (operand.Syntax is not { } syntax)) {
            return operand;
        }
        var binder = new OperandBinder(scope: scope);
        var inlined = binder.Inline(node: syntax, operand: operand);
        var folded = binder.Fold(node: inlined, operand: operand);

        return (ReferenceEquals(objA: folded, objB: syntax)
            ? operand
            : PuckParser.RebuildOperand(atoms: [.. operand.Atoms.Select(selector: static atom => atom.Value), .. binder.InlinedAtoms], original: operand, syntax: folded));
    }

    private sealed class OperandBinder(DocumentScope scope) {
        private readonly StringBuilder m_text = new();
        private readonly List<InterpolatedStringNode> m_inlinedAtoms = [];

        // The atoms an inlined function body or default brought, numbered after the operand's own.
        internal IReadOnlyList<InterpolatedStringNode> InlinedAtoms => m_inlinedAtoms;

        // Splices each call of a value function (DocumentLowering.FunctionNamed) into the operand: its body with each
        // parameter replaced by its argument's syntax (or its default's), so the rule program is the expression written
        // out. Names the body reads that are not its parameters resolve where the call is, like any operand name.
        internal Syntax.SyntaxNode Inline(Syntax.SyntaxNode node, OperandExpressionNode operand) {
            return (CallsFunction(node: node) ? Splice(current: node) : node);

            Syntax.SyntaxNode Splice(Syntax.SyntaxNode current) => current switch {
                Syntax.SourceCall call when (DocumentLowering.FunctionNamed(name: call.Name, scope: scope) is { } function) => Expand(call: call, function: function),
                Syntax.SourceCall call => call with { Arguments = [.. call.Arguments.Select(selector: argument => argument with { Value = Splice(current: argument.Value) })] },
                Syntax.SourceGroup group => group with { Value = Splice(current: group.Value) },
                Syntax.SourceIndex index => index with { Target = Splice(current: index.Target), Index = Splice(current: index.Index) },
                Syntax.SourceAccess access => access with { Target = Splice(current: access.Target) },
                Syntax.SourceLambda lambda => lambda with { Body = Splice(current: lambda.Body) },
                Syntax.Unary unary => unary with { Operand = Splice(current: unary.Operand) },
                Syntax.Binary binary => binary with { Left = Splice(current: binary.Left), Right = Splice(current: binary.Right) },
                Syntax.Ternary ternary => new Syntax.Ternary(Condition: Splice(current: ternary.Condition), WhenTrue: Splice(current: ternary.WhenTrue), WhenFalse: Splice(current: ternary.WhenFalse)),
                _ => current,
            };
            Syntax.SyntaxNode Expand(Syntax.SourceCall call, FunctionExpressionNode function) {
                if (DocumentLowering.BindFunctionArguments(argumentNames: [.. call.Arguments.Select(selector: static argument => argument.Name)], function: function, name: call.Name, scope: scope, span: operand.Span) is not { } bound) {
                    return new Syntax.Literal(Value: 0m);
                }
                return DocumentLowering.ExpandFunction(
                    expand: () => {
                        var arguments = new Dictionary<string, Syntax.SyntaxNode>(comparer: StringComparer.Ordinal);

                        for (var at = 0; (at < bound.Length); at++) {
                            arguments[function.Parameters[at].Name] = ((bound[at] >= 0)
                                ? Splice(current: call.Arguments[bound[at]].Value)
                                : Convert(expression: function.Parameters[at].DefaultValue!));
                        }
                        return Splice(current: Substitute(arguments: arguments, node: Convert(expression: function.Body)));
                    },
                    name: call.Name,
                    refused: ((Syntax.SyntaxNode)new Syntax.Literal(Value: 0m)),
                    scope: scope,
                    span: operand.Span
                );
            }
            // The compile-time expression as operand syntax, its atoms numbered after those already spliced in.
            Syntax.SyntaxNode Convert(ExpressionNode expression) {
                var converted = PuckParser.CreateOperand(expression: expression, form: DocumentValueForm.Expression);

                if (converted.Syntax is not { } tree) {
                    Refuse(message: $"'{converted.Text}' {converted.SyntaxError}", span: operand.Span);
                    return new Syntax.Literal(Value: 0m);
                }
                var offset = (operand.Atoms.Count + m_inlinedAtoms.Count);

                m_inlinedAtoms.AddRange(collection: converted.Atoms.Select(selector: static atom => atom.Value));
                return Renumber(node: tree, offset: offset);
            }
        }

        private bool CallsFunction(Syntax.SyntaxNode node) => node switch {
            Syntax.SourceCall call => ((DocumentLowering.FunctionNamed(name: call.Name, scope: scope) is not null) || call.Arguments.Any(predicate: argument => CallsFunction(node: argument.Value))),
            Syntax.SourceGroup group => CallsFunction(node: group.Value),
            Syntax.SourceIndex index => (CallsFunction(node: index.Target) || CallsFunction(node: index.Index)),
            Syntax.SourceAccess access => CallsFunction(node: access.Target),
            Syntax.SourceLambda lambda => CallsFunction(node: lambda.Body),
            Syntax.Unary unary => CallsFunction(node: unary.Operand),
            Syntax.Binary binary => (CallsFunction(node: binary.Left) || CallsFunction(node: binary.Right)),
            Syntax.Ternary ternary => (CallsFunction(node: ternary.Condition) || CallsFunction(node: ternary.WhenTrue) || CallsFunction(node: ternary.WhenFalse)),
            _ => false,
        };
        // A parameter's every bare reading becomes its argument, grouped unless the argument is a primary, so an
        // argument's operators keep their own precedence; a lambda binder of the same name shadows it.
        private static Syntax.SyntaxNode Substitute(Syntax.SyntaxNode node, IReadOnlyDictionary<string, Syntax.SyntaxNode> arguments) => node switch {
            Syntax.SourceName { Quoted: false } name when arguments.TryGetValue(key: name.Name, value: out var argument) =>
                ((argument is Syntax.Literal or Syntax.SourceName or Syntax.SourceString or Syntax.SourceAtom or Syntax.SourceGroup or Syntax.SourceAccess or Syntax.SourceIndex or Syntax.SourceCall) ? argument : new Syntax.SourceGroup(Value: argument)),
            Syntax.SourceCall call => call with { Arguments = [.. call.Arguments.Select(selector: item => item with { Value = Substitute(arguments: arguments, node: item.Value) })] },
            Syntax.SourceGroup group => group with { Value = Substitute(arguments: arguments, node: group.Value) },
            Syntax.SourceIndex index => index with { Target = Substitute(arguments: arguments, node: index.Target), Index = Substitute(arguments: arguments, node: index.Index) },
            Syntax.SourceAccess access => access with { Target = Substitute(arguments: arguments, node: access.Target) },
            Syntax.SourceLambda lambda => lambda with { Body = Substitute(arguments: (arguments.ContainsKey(key: lambda.Binder) ? arguments.Where(predicate: pair => (pair.Key != lambda.Binder)).ToDictionary(comparer: StringComparer.Ordinal) : arguments), node: lambda.Body) },
            Syntax.Unary unary => unary with { Operand = Substitute(arguments: arguments, node: unary.Operand) },
            Syntax.Binary binary => binary with { Left = Substitute(arguments: arguments, node: binary.Left), Right = Substitute(arguments: arguments, node: binary.Right) },
            Syntax.Ternary ternary => new Syntax.Ternary(Condition: Substitute(arguments: arguments, node: ternary.Condition), WhenTrue: Substitute(arguments: arguments, node: ternary.WhenTrue), WhenFalse: Substitute(arguments: arguments, node: ternary.WhenFalse)),
            _ => node,
        };
        private static Syntax.SyntaxNode Renumber(Syntax.SyntaxNode node, int offset) => ((offset == 0) ? node : node switch {
            Syntax.SourceAtom atom => atom with { Index = (atom.Index + offset) },
            Syntax.SourceCall call => call with { Arguments = [.. call.Arguments.Select(selector: item => item with { Value = Renumber(node: item.Value, offset: offset) })] },
            Syntax.SourceGroup group => group with { Value = Renumber(node: group.Value, offset: offset) },
            Syntax.SourceIndex index => index with { Target = Renumber(node: index.Target, offset: offset), Index = Renumber(node: index.Index, offset: offset) },
            Syntax.SourceAccess access => access with { Target = Renumber(node: access.Target, offset: offset) },
            Syntax.SourceLambda lambda => lambda with { Body = Renumber(node: lambda.Body, offset: offset) },
            Syntax.Unary unary => unary with { Operand = Renumber(node: unary.Operand, offset: offset) },
            Syntax.Binary binary => binary with { Left = Renumber(node: binary.Left, offset: offset), Right = Renumber(node: binary.Right, offset: offset) },
            Syntax.Ternary ternary => new Syntax.Ternary(Condition: Renumber(node: ternary.Condition, offset: offset), WhenTrue: Renumber(node: ternary.WhenTrue, offset: offset), WhenFalse: Renumber(node: ternary.WhenFalse, offset: offset)),
            _ => node,
        });

        private HashSet<string>? m_derived;
        private string? m_binder;
        private int m_keyDepth;

        internal string Bind(OperandExpressionNode operand) {
            operand = FoldOperand(operand: operand, scope: scope);
            WriteOperand(operand: operand);
            var text = m_text.ToString();
            // A literal string can be the comparand of a text gate. Arithmetic still compiles through the
            // numeric expression reader below and refuses strings inside a numeric program.
            if (operand.Syntax is Syntax.SourceString) { return text; }

            if (operand.Form == DocumentValueForm.Expression) {
                if (Syntax.TryParse(error: out var error, program: out var program, text: text)) {
                    var resolved = ResolveEmbeddedOperands(program: program, expectedSpace: null, scope: scope, span: operand.Span);

                    if (Syntax.TryPrint(program: resolved, text: out var canonical)) { return canonical; }
                } else if (operand.Syntax is not Syntax.SourceCall { Name: "embed" or "vector" }) {
                    // Refused here, at its span, and written as a value the document's own pass reads without
                    // refusing it a second time (WorldExpressionJson.Lower).
                    Refuse(message: $"'{operand.Text}' {error}", span: operand.Span);

                    return "0";
                }
            }
            return text;
        }
        internal ExpressionNode CompileTime(OperandExpressionNode operand) => ((operand.Syntax is { } syntax)
            ? ToCompileTime(node: syntax, operand: operand) : RefuseCompileTime(operand: operand));
        // Folds what compile-time constants decide, so a spelling's program and price never depend on whether the
        // author wrote a constant or an expression of constants: a name bound to a number becomes that number, a
        // choice on a constant condition becomes its arm, and an operator over constants becomes its result where
        // the result is exact in the runtime's Q48.16 arithmetic. Calls, indexes and accesses keep their spelling,
        // since a name inside one may be a row or a key rather than a value.
        internal Syntax.SyntaxNode Fold(Syntax.SyntaxNode node, OperandExpressionNode operand) {
            switch (node) {
                case Syntax.SourceGroup group: {
                        var value = Fold(node: group.Value, operand: operand);

                        return ((value is Syntax.Literal) ? value : (ReferenceEquals(objA: value, objB: group.Value) ? group : group with { Value = value }));
                    }
                case Syntax.SourceName name when (Constant(name: name, operand: operand) is { } constant):
                    return new Syntax.Literal(Value: constant);
                case Syntax.SourceAccess access when (Constant(access: access) is { } constant):
                    return new Syntax.Literal(Value: constant);
                // A read through a compile-time object or array (a loop's record, a let's table) is the number it reads.
                case Syntax.SourceAccess or Syntax.SourceIndex when (HasBoundRoot(node: node) && (Number(value: DocumentLowering.LowerValue(ToCompileTime(node: node, operand: operand), scope)) is { } read)):
                    return new Syntax.Literal(Value: read);
                case Syntax.Unary unary: {
                        var value = Fold(node: unary.Operand, operand: operand);

                        if ((value is Syntax.Literal { Value: var number }) && IsExact(value: number)) {
                            switch (unary.Operator) {
                                case "-": return new Syntax.Literal(Value: -number);
                                case "not": return Truth(value: (number == 0m));
                            }
                        }
                        return (ReferenceEquals(objA: value, objB: unary.Operand) ? unary : unary with { Operand = value });
                    }
                case Syntax.Binary binary: {
                        var left = Fold(node: binary.Left, operand: operand);
                        var right = Fold(node: binary.Right, operand: operand);

                        if (FoldBinary(left: left, operation: binary.Operator, right: right) is { } result) { return result; }
                        return ((ReferenceEquals(objA: left, objB: binary.Left) && ReferenceEquals(objA: right, objB: binary.Right)) ? binary : binary with { Left = left, Right = right });
                    }
                case Syntax.Ternary ternary: {
                        var condition = Fold(node: ternary.Condition, operand: operand);

                        if ((condition is Syntax.Literal { Value: var number }) && IsExact(value: number)) {
                            return Fold(node: ((number != 0m) ? ternary.WhenTrue : ternary.WhenFalse), operand: operand);
                        }
                        var whenTrue = Fold(node: ternary.WhenTrue, operand: operand);
                        var whenFalse = Fold(node: ternary.WhenFalse, operand: operand);

                        return ((ReferenceEquals(objA: condition, objB: ternary.Condition) && ReferenceEquals(objA: whenTrue, objB: ternary.WhenTrue) && ReferenceEquals(objA: whenFalse, objB: ternary.WhenFalse))
                            ? ternary
                            : new Syntax.Ternary(Condition: condition, WhenFalse: whenFalse, WhenTrue: whenTrue));
                    }
                default:
                    return node;
            }
        }

        // The number a bare name stands for at compile time, resolved in WriteName's order: a runtime binder, a
        // pool binding or derived state is never a constant, and a let or loop binding is one when it holds a number.
        private decimal? Constant(Syntax.SourceName name, OperandExpressionNode operand) {
            if (name.Quoted || name.Name.StartsWith(value: '$') || (name.Name == m_binder) || GetPoolBindings(scope: scope).Contains(item: name.Name) ||
                GetOrCreateDerivedState(scope: scope).ContainsKey(key: name.Name)) {
                return null;
            }
            if (name.Name is "true" or "false") { return ((name.Name == "true") ? 1m : 0m); }
            if (!scope.TryLowerBinding(name.Name, out var bound) || scope.TryRefuseRead(name: name.Name, span: operand.Span)) {
                return null;
            }
            return Number(value: bound);
        }
        // The number a dotted read stands for, resolved in Write's order: a qualified binding, then an enum member.
        private decimal? Constant(Syntax.SourceAccess access) {
            if (HasRuntimeRoot(node: access)) { return null; }
            if ((QualifiedName.From(syntax: access)?.ToString() is { } qualified) && scope.TryLowerBinding(qualified, out var bound)) {
                return Number(value: bound);
            }
            if ((InstanceName(node: access) is null) && (access.Target is Syntax.SourceName { Quoted: false } enumName) &&
                GetOrCreateEnums(scope: scope).TryGetValue(key: enumName.Name, value: out var definition)) {
                for (var ordinal = 0; (ordinal < definition.Members.Count); ordinal++) {
                    if (definition.Members[ordinal] == access.Member) { return ordinal; }
                }
            }
            return null;
        }
        private static decimal? Number(JsonNode? value) {
            if (value is not JsonValue scalar) {
                return null;
            }
            // The number WriteValue would write for the binding, read back from the same text.
            var text = (scalar.TryGetValue<string>(value: out var written) ? written : scalar.ToJsonString());
            decimal? number = (scalar.TryGetValue<bool>(value: out var flag) ? (flag ? 1m : 0m)
                : (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null));

            return (((number is { } exact) && IsExact(value: exact)) ? exact : null);
        }

        internal static Syntax.Literal? FoldBinary(Syntax.SyntaxNode left, string operation, Syntax.SyntaxNode right) {
            if ((left is Syntax.Literal { Value: var a }) && IsExact(value: a)) {
                // A constant decides a short-circuit whatever the other side reads.
                if ((operation == "and") && (a == 0m)) { return Truth(value: false); }
                if ((operation == "or") && (a != 0m)) { return Truth(value: true); }
            }
            if ((left is not Syntax.Literal { Value: var x }) || (right is not Syntax.Literal { Value: var y }) || !IsExact(value: x) || !IsExact(value: y)) {
                return null;
            }
            decimal? result = operation switch {
                "==" => Bool(value: (x == y)),
                "!=" => Bool(value: (x != y)),
                "<" => Bool(value: (x < y)),
                "<=" => Bool(value: (x <= y)),
                ">" => Bool(value: (x > y)),
                ">=" => Bool(value: (x >= y)),
                "and" => Bool(value: ((x != 0m) && (y != 0m))),
                "or" => Bool(value: ((x != 0m) || (y != 0m))),
                "+" => (x + y),
                "-" => (x - y),
                // A product is exact in Q48.16 when one factor is whole.
                "*" when ((decimal.Truncate(d: x) == x) || (decimal.Truncate(d: y) == y)) => (x * y),
                _ => null,
            };

            return (((result is { } value) && IsExact(value: value)) ? new Syntax.Literal(Value: value) : null);

            static decimal Bool(bool value) => (value ? 1m : 0m);
        }

        private static Syntax.Literal Truth(bool value) => new(Value: (value ? 1m : 0m));
        // A decimal is exact in the runtime's arithmetic when it is a whole number of 2^-16 steps inside Q48.16's range.
        private static bool IsExact(decimal value) => ((Math.Abs(value: value) < 140_737_488_355_328m) && (decimal.Truncate(d: (value * 65_536m)) == (value * 65_536m)));
        private void Refuse(string message, SourceSpan span) => scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.OperandParse, message: message, span: span);
        private void WriteOperand(OperandExpressionNode operand) {
            using var evaluation = scope.Budget.Enter(span: operand.Span);

            if (operand.Syntax is not { } syntax) {
                if (!operand.SyntaxErrorReported) {
                    scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.OperandParse, message: $"'{operand.Text}' {operand.SyntaxError}", span: operand.Span);
                }
                m_text.Append(value: '0');
                return;
            }
            Write(syntax, operand.Form, operand);
        }
        private void Write(Syntax.SyntaxNode node, DocumentValueForm form, OperandExpressionNode operand) {
            if ((node is Syntax.SourceAccess) && !HasRuntimeRoot(node: node) && (QualifiedName.From(syntax: node)?.ToString() is { } qualified) && scope.TryLowerBinding(qualified, out var qualifiedValue)) {
                WriteValue(form: form, value: qualifiedValue, span: operand.Span);
                return;
            }
            if ((form != DocumentValueForm.Key) && (InstanceName(node: node) is { } instanceName)) {
                WriteValue(form: form, value: JsonValue.Create(value: instanceName), span: operand.Span);
                return;
            }
            if ((node is Syntax.SourceAccess or Syntax.SourceIndex) && HasBoundRoot(node: node)) {
                WriteValue(DocumentLowering.LowerValue(ToCompileTime(node: node, operand: operand), scope), form, operand.Span);
                return;
            }
            switch (node) {
                case Syntax.SourceGroup group:
                    m_text.Append(value: '(');
                    Write(group.Value, DocumentValueForm.Expression, operand);
                    m_text.Append(value: ')');
                    break;
                case Syntax.Literal literal:
                    m_text.Append(value: literal.Value.ToString(provider: CultureInfo.InvariantCulture));
                    break;
                case Syntax.SourceAtom atom:
                    WriteValue(DocumentLowering.LowerValue(operand.Atoms[atom.Index].Value, scope), form, operand.Span);
                    break;
                case Syntax.SourceString literal:
                    m_text.Append(value: '"').Append(value: literal.Value.Replace(comparisonType: StringComparison.Ordinal, newValue: "\\\\", oldValue: "\\")
                        .Replace(comparisonType: StringComparison.Ordinal, newValue: "\\\"", oldValue: "\"")).Append(value: '"');
                    break;
                case Syntax.SourceName name:
                    WriteName(form: form, name: name, operand: operand);
                    break;
                case Syntax.SourceAccess access:
                    WriteAccess(access: access, form: form, operand: operand);
                    break;
                case Syntax.SourceIndex index:
                    WriteIndex(index: index, operand: operand);
                    break;
                case Syntax.Unary unary:
                    if (unary.Operator == "not") { m_text.Append(value: '('); }
                    m_text.Append(value: unary.Operator).Append(value: '(');
                    Write(unary.Operand, DocumentValueForm.Expression, operand);
                    m_text.Append(value: ')');
                    if (unary.Operator == "not") { m_text.Append(value: ')'); }
                    break;
                case Syntax.Binary binary:
                    m_text.Append(value: '(');
                    Write(binary.Left, DocumentValueForm.Expression, operand);
                    m_text.Append(value: ' ').Append(value: binary.Operator).Append(value: ' ');
                    Write(binary.Right, DocumentValueForm.Expression, operand);
                    m_text.Append(value: ')');
                    break;
                case Syntax.Ternary ternary:
                    m_text.Append(value: '(');
                    Write(ternary.Condition, DocumentValueForm.Expression, operand);
                    m_text.Append(value: " ? ");
                    Write(ternary.WhenTrue, DocumentValueForm.Expression, operand);
                    m_text.Append(value: " : ");
                    Write(ternary.WhenFalse, DocumentValueForm.Expression, operand);
                    m_text.Append(value: ')');
                    break;
                case Syntax.SourceCall call:
                    WriteCall(call: call, operand: operand);
                    break;
                case Syntax.SourceLambda lambda:
                    var previous = m_binder;
                    m_binder = lambda.Binder;
                    m_text.Append(value: lambda.Binder).Append(value: " -> ");
                    Write(lambda.Body, DocumentValueForm.Expression, operand);
                    m_binder = previous;
                    break;
                default:
                    throw new InvalidOperationException(message: $"Unexpected operand syntax {node.GetType().Name}.");
            }
            if (m_text.Length > DocumentEvaluationBudget.TextLimit) {
                throw new DocumentEvaluationException("The bound operand exceeds the text limit.", operand.Span);
            }
        }
        private void WriteName(Syntax.SourceName name, DocumentValueForm form, OperandExpressionNode operand) {
            if (name.Quoted) {
                m_text.Append(value: Syntax.PrintName(name: name));
                return;
            }
            if ((form == DocumentValueForm.Key) || (name.Name == m_binder) || GetPoolBindings(scope: scope).Contains(item: name.Name) || name.Name.StartsWith(value: '$')) {
                m_text.Append(value: name.Name);
                return;
            }
            if (form != DocumentValueForm.Name) {
                if (name.Name is "true" or "false") {
                    m_text.Append(value: ((name.Name == "true") ? '1' : '0'));
                    return;
                }
                if (GetOrCreateDerivedState(scope: scope).TryGetValue(key: name.Name, value: out var derived)) {
                    m_derived ??= new HashSet<string>(comparer: StringComparer.Ordinal);
                    if (!m_derived.Add(item: name.Name)) {
                        scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.DerivedStateCycle,
                            message: $"Cyclic dependency detected in derived state '{name.Name}'", span: derived.Span);
                        m_text.Append(value: '0');
                        return;
                    }
                    m_text.Append(value: '(');
                    WriteOperand(operand: derived);
                    m_text.Append(value: ')');
                    m_derived.Remove(item: name.Name);
                    return;
                }
            }
            if (scope.TryLowerBinding(name.Name, out var bound) &&
                ((form != DocumentValueForm.Name) || ((bound is JsonValue named) && named.TryGetValue<string>(value: out _)))) {
                if (scope.TryRefuseRead(name: name.Name, span: operand.Span)) {
                    m_text.Append(value: '0');
                    return;
                }
                WriteValue(form: form, value: bound, span: operand.Span);
                return;
            }
            m_text.Append(value: name.Name);
        }
        private void WriteAccess(Syntax.SourceAccess access, DocumentValueForm form, OperandExpressionNode operand) {
            if ((form == DocumentValueForm.Expression) && (access.Target is Syntax.SourceName { Quoted: false } enumName) &&
                GetOrCreateEnums(scope: scope).TryGetValue(key: enumName.Name, value: out var definition)) {
                for (var index = 0; (index < definition.Members.Count); index++) {
                    if (definition.Members[index] == access.Member) {
                        m_text.Append(value: index.ToString(provider: CultureInfo.InvariantCulture));
                        return;
                    }
                }
                Refuse(message: $"Enum '{enumName.Name}' has no member '{access.Member}'", span: operand.Span);
                m_text.Append(value: '0');
                return;
            }
            if ((form is DocumentValueForm.Expression or DocumentValueForm.Key) && (access.Target is Syntax.SourceName row) &&
                !GetPoolBindings(scope: scope).Contains(item: row.Name)) {
                Write(form: DocumentValueForm.Name, node: row, operand: operand);
                m_text.Append(value: '[').Append(value: access.Member).Append(value: ']');
                return;
            }
            // A keyed read of a row a module instance declares: `alias.name.key`.
            if ((form is DocumentValueForm.Expression or DocumentValueForm.Key) && (access.Target is Syntax.SourceAtom)) {
                Write(access.Target, DocumentValueForm.Name, operand);
                m_text.Append(value: '[').Append(value: access.Member).Append(value: ']');
                return;
            }
            if ((form == DocumentValueForm.Expression) && (InstanceName(node: access.Target) is { } instanceRow)) {
                WriteValue(form: DocumentValueForm.Name, value: JsonValue.Create(value: instanceRow), span: operand.Span);
                m_text.Append(value: '[').Append(value: access.Member).Append(value: ']');
                return;
            }
            Write(access.Target, DocumentValueForm.Name, operand);
            m_text.Append(value: '.').Append(value: access.Member);
        }
        private void WriteIndex(Syntax.SourceIndex index, OperandExpressionNode operand) {
            var key = index.Index;

            if (key is Syntax.SourceGroup) {
                Refuse(message: "row[(expression)] is retired; use row[expression]. Brackets already evaluate their contents.", span: operand.Span);
            }
            while (key is Syntax.SourceGroup group) { key = group.Value; }
            if ((m_keyDepth > 0) && (key is Syntax.SourceName { Name: "$value" })) {
                Refuse(message: "slot[$value] inside brackets is retired; use the slot name directly: row[slot].", span: operand.Span);
                Write(index.Target, DocumentValueForm.Expression, operand);
                return;
            }
            if ((index.Target is Syntax.SourceName { Quoted: false } name) &&
                GetOrCreateStateFamilies(scope: scope).TryGetValue(key: name.Name, value: out var family) &&
                (key is Syntax.Literal literal) && (decimal.Truncate(d: literal.Value) == literal.Value)) {
                for (var i = 0; (i < family.Indices.Count); i++) {
                    if (family.Indices[i] == literal.Value) {
                        m_text.Append(value: Syntax.PrintName(
                            name: family.MemberNames[i],
                            quoted: true
                        ));
                        return;
                    }
                }
                scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.FamilyIndexOutOfBounds,
                    message: $"Index '{literal.Value}' names no member of family '{name.Name}', whose members are {string.Join(separator: ", ", values: family.Indices)}",
                    span: operand.Span);
            }
            Write(index.Target, DocumentValueForm.Name, operand);
            m_text.Append(value: '[');
            m_keyDepth++;
            // Brackets evaluate their contents. Literal and reserved keys already have a document spelling;
            // all other values go through the expression binder, including bare lexical names.
            if ((key is Syntax.SourceName { Quoted: false } boundName) && scope.TryLowerBinding(boundName.Name, out var boundKey)) {
                WriteValue(boundKey, DocumentValueForm.Key, operand.Span);
            } else if (key is Syntax.SourceAccess or Syntax.SourceIndex or Syntax.SourceCall) {
                Write(form: DocumentValueForm.Expression, node: key, operand: operand);
            } else if (key is not (Syntax.Literal or Syntax.SourceString or Syntax.SourceAtom or Syntax.SourceName { Quoted: true } or Syntax.SourceName { Name: ['$', ..] })) {
                m_text.Append(value: '(');
                Write(form: DocumentValueForm.Expression, node: key, operand: operand);
                m_text.Append(value: ')');
            } else {
                Write(form: DocumentValueForm.Key, node: key, operand: operand);
            }
            m_keyDepth--;
            m_text.Append(value: ']');
        }
        private void WriteCall(Syntax.SourceCall call, OperandExpressionNode operand) {
            if (call.Name is "compareState" or "compareValue") {
                Refuse(message: $"{call.Name}(...) is retired in source gates; write an ordinary comparison such as 'left == right'.", span: operand.Span);
                m_text.Append(value: '0');
                return;
            }
            if ((call is { Name: "maximum", Arguments: [var left, var right] }) &&
                BooleanSyntax(node: left.Value) && BooleanSyntax(node: right.Value)) {
                Refuse(message: "maximum() over Boolean expressions is retired; write 'left or right'.", span: operand.Span);
                m_text.Append(value: '0');
                return;
            }
            if (call.Name == "local") {
                Refuse(message: "local(name) is retired; read the local by its bare name: name.", span: operand.Span);
                m_text.Append(value: '0');
                return;
            }
            m_text.Append(value: call.Name).Append(value: '(');
            for (var index = 0; (index < call.Arguments.Count); index++) {
                if (index > 0) { m_text.Append(value: ", "); }
                var argument = call.Arguments[index];

                if (argument.Name is { } name) { m_text.Append(value: name).Append(value: ": "); }
                var form = ((((index == 0) && (call.Name is "all" or "any" or "count" or "sum")) ||
                    ((index > 0) && (call.Name is "boardShift" or "boardRay" or "boardImage")) ||
                    (argument.Name is "where" or "space")) ? DocumentValueForm.Name : DocumentValueForm.Expression);

                Write(argument.Value, form, operand);
            }
            m_text.Append(value: ')');
        }
        private static bool BooleanSyntax(Syntax.SyntaxNode node) => node switch {
            Syntax.SourceGroup group => BooleanSyntax(node: group.Value),
            Syntax.Binary { Operator: "==" or "!=" or "<" or "<=" or ">" or ">=" or "and" or "or" } => true,
            Syntax.Unary { Operator: "not" } => true,
            _ => false,
        };
        private void WriteValue(JsonNode? value, DocumentValueForm form, SourceSpan span) {
            if (value is JsonValue scalar) {
                if (scalar.TryGetValue<string>(value: out var text)) {
                    if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
                        m_text.Append(value: number.ToString(provider: CultureInfo.InvariantCulture));
                    } else if ((form == DocumentValueForm.Expression) && (Syntax.CurrentLocals?.Contains(item: text) == true)) {
                        // A computed name that names one of the rule's locals reads that local, as the same name
                        // written bare does; any other computed name is a row.
                        m_text.Append(value: text);
                    } else if (Syntax.IsSpelledName(name: text)) {
                        m_text.Append(value: Syntax.PrintName(
                            name: text,
                            quoted: true
                        ));
                    } else {
                        Refuse(message: "A computed operand name must be nonempty and cannot contain a backquote", span: span);
                        m_text.Append(value: '0');
                    }
                    return;
                }
                if (scalar.TryGetValue<bool>(value: out var flag)) {
                    m_text.Append(value: (flag ? '1' : '0'));
                    return;
                }
                m_text.Append(value: scalar.ToJsonString());
                return;
            }
            Refuse(message: $"A {form} operand requires one name or scalar value", span: span);
            m_text.Append(value: '0');
        }
        // The generated name an access names when its dotted spelling reads through a module instance of this scope
        // (DocumentScope.TryQualify), unless a runtime binding or lambda binder owns its head.
        private string? InstanceName(Syntax.SyntaxNode node) => (((node is Syntax.SourceAccess) && !HasRuntimeRoot(node: node) &&
            (QualifiedName.From(syntax: node)?.ToString() is { } dotted) && scope.TryQualify(qualified: out var name, reference: dotted)) ? name : null);
        private bool HasRuntimeRoot(Syntax.SyntaxNode node) {
            while (true) {
                switch (node) {
                    case Syntax.SourceAccess access: node = access.Target; break;
                    case Syntax.SourceIndex index: node = index.Target; break;
                    default:
                        return ((node is Syntax.SourceName { Quoted: false } name) &&
                        ((name.Name == m_binder) || GetPoolBindings(scope: scope).Contains(item: name.Name)));
                }
            }
        }
        private bool HasBoundRoot(Syntax.SyntaxNode node) {
            if (HasRuntimeRoot(node: node)) { return false; }
            // A binding a key or member is read through that holds a name (a template parameter or loop variable
            // holding "goSeat") names a row, so what follows it is that row's key or member, read or written as the
            // row spelled out would be; only a binding holding an object or array is read through at compile time.
            for (var whole = true; ; whole = false) {
                if ((QualifiedName.From(syntax: node)?.ToString() is { } qualified) && scope.TryLowerBinding(qualified, out var bound)) {
                    return (whole || !((bound is JsonValue named) && named.TryGetValue<string>(value: out _)));
                }
                switch (node) {
                    case Syntax.SourceAccess access: node = access.Target; break;
                    case Syntax.SourceIndex index: node = index.Target; break;
                    default: return false;
                }
            }
        }
        private ExpressionNode ToCompileTime(Syntax.SyntaxNode node, OperandExpressionNode operand) => node switch {
            Syntax.SourceGroup group => ToCompileTime(node: group.Value, operand: operand),
            Syntax.Literal literal => new LiteralExpressionNode(literal.Value, Offset: operand.Offset, Length: operand.Length, Line: operand.Line, Column: operand.Column),
            Syntax.SourceName { Quoted: false, Name: "true" } => new LiteralExpressionNode(true),
            Syntax.SourceName { Quoted: false, Name: "false" } => new LiteralExpressionNode(false),
            Syntax.SourceName { Quoted: true } name => new LiteralExpressionNode(name.Name),
            Syntax.SourceName name => new IdentifierExpressionNode(name.Name, Offset: operand.Offset, Length: operand.Length, Line: operand.Line, Column: operand.Column),
            Syntax.SourceString text => new LiteralExpressionNode(text.Value),
            Syntax.SourceAtom atom => operand.Atoms[atom.Index].Value,
            Syntax.SourceAccess access when ((QualifiedName.From(syntax: access)?.ToString() is { } qualified) && scope.TryLowerBinding(qualified, out _)) => new IdentifierExpressionNode(qualified),
            Syntax.SourceAccess access when (InstanceName(node: access) is { } instanceName) => new LiteralExpressionNode(instanceName),
            Syntax.SourceAccess access => new IndexExpressionNode(ToCompileTime(node: access.Target, operand: operand), new LiteralExpressionNode(access.Member)),
            Syntax.SourceIndex index => new IndexExpressionNode(ToCompileTime(node: index.Target, operand: operand), ToCompileTime(node: index.Index, operand: operand)),
            Syntax.Unary unary => new UnaryExpressionNode(unary.Operator, ToCompileTime(node: unary.Operand, operand: operand)),
            Syntax.Binary binary => new BinaryExpressionNode(ToCompileTime(node: binary.Left, operand: operand), binary.Operator, ToCompileTime(node: binary.Right, operand: operand)),
            Syntax.Ternary ternary => new ConditionalExpressionNode(ToCompileTime(node: ternary.Condition, operand: operand), ToCompileTime(node: ternary.WhenTrue, operand: operand), ToCompileTime(node: ternary.WhenFalse, operand: operand)),
            Syntax.SourceCall call => new CallExpressionNode(call.Name, [.. call.Arguments.Select(selector: argument => new ArgumentNode(argument.Name, ToCompileTime(node: argument.Value, operand: operand)))]),
            _ => RefuseCompileTime(operand: operand),
        };
        private LiteralExpressionNode RefuseCompileTime(OperandExpressionNode operand) {
            Refuse(message: "A compile-time access requires a compile-time index expression", span: operand.Span);
            return new LiteralExpressionNode(0m);
        }
    }
}
