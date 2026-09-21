using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Syntax = Puck.State.ExpressionSpelling;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    // Binding walks parsed names and accesses. Computed atoms and quoted names are terminal values: neither is
    // ever sent back through symbol resolution, so a binding cannot inject an operator or capture another name.
    private static string BindOperand(OperandExpressionNode operand, DocumentScope scope) =>
        new OperandBinder(scope: scope).Bind(operand: operand);

    private static ExpressionNode CompileTimeOperand(OperandExpressionNode operand, DocumentScope scope) =>
        new OperandBinder(scope: scope).CompileTime(operand: operand);

    private sealed class OperandBinder(DocumentScope scope) {
        private readonly StringBuilder m_text = new();
        private HashSet<string>? m_derived;
        private string? m_binder;

        internal string Bind(OperandExpressionNode operand) {
            WriteOperand(operand: operand);
            var text = m_text.ToString();
            if (operand.Form == DocumentValueForm.Expression) {
                if (Syntax.TryParse(error: out var error, program: out var program, text: text)) {
                    var resolved = ResolveEmbeddedOperands(program: program, expectedSpace: null, scope: scope, span: operand.Span);
                    if (Syntax.TryPrintSource(program: resolved, text: out var canonical)) { return canonical; }
                } else if (operand.Syntax is not Syntax.SourceCall { Name: "embed" or "vector" }) {
                    Refuse(message: $"'{operand.Text}' {error}", span: operand.Span);
                }
            }
            return text;
        }

        internal ExpressionNode CompileTime(OperandExpressionNode operand) => ((operand.Syntax is { } syntax)
            ? ToCompileTime(syntax, operand) : RefuseCompileTime(operand: operand));

        private void Refuse(string message, SourceSpan span) => scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.OperandParse, message: message, span: span);

        private void WriteOperand(OperandExpressionNode operand) {
            using var evaluation = scope.Budget.Enter(span: operand.Span);
            if (operand.Syntax is not { } syntax) {
                Refuse($"'{operand.Text}' {operand.SyntaxError}", operand.Span);
                m_text.Append(value: '0');
                return;
            }
            Write(syntax, operand.Form, operand);
        }

        private void Write(Syntax.SyntaxNode node, DocumentValueForm form, OperandExpressionNode operand) {
            if ((node is Syntax.SourceAccess) && !HasRuntimeRoot(node: node) && (QualifiedName(node: node) is { } qualified) && scope.TryLowerBinding(qualified, out var qualifiedValue)) {
                WriteValue(form: form, value: qualifiedValue, span: operand.Span);
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
                    m_text.Append(literal.Value.ToString(CultureInfo.InvariantCulture));
                    break;
                case Syntax.SourceAtom atom:
                    WriteValue(DocumentLowering.LowerValue(operand.Atoms[atom.Index].Value, scope), form, operand.Span);
                    break;
                case Syntax.SourceString literal:
                    m_text.Append(value: '"').Append(literal.Value.Replace("\\", "\\\\", StringComparison.Ordinal)
                        .Replace("\"", "\\\"", StringComparison.Ordinal)).Append(value: '"');
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
                    m_text.Append(value: unary.Operator).Append(value: '(');
                    Write(unary.Operand, DocumentValueForm.Expression, operand);
                    m_text.Append(value: ')');
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
                    m_text.Append(lambda.Binder).Append(value: " -> ");
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
                m_text.Append(value: '`').Append(name.Name).Append(value: '`');
                return;
            }
            if ((form == DocumentValueForm.Key) || (name.Name == m_binder) || GetPoolBindings(scope: scope).Contains(name.Name) || name.Name.StartsWith('$')) {
                m_text.Append(name.Name);
                return;
            }
            if (form != DocumentValueForm.Name) {
                if (name.Name is "true" or "false") {
                    m_text.Append(value: ((name.Name == "true") ? '1' : '0'));
                    return;
                }
                if (GetOrCreateDerivedState(scope: scope).TryGetValue(name.Name, out var derived)) {
                    m_derived ??= new HashSet<string>(comparer: StringComparer.Ordinal);
                    if (!m_derived.Add(name.Name)) {
                        scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.DerivedStateCycle,
                            message: $"Cyclic dependency detected in derived state '{name.Name}'", span: derived.Span);
                        m_text.Append(value: '0');
                        return;
                    }
                    m_text.Append(value: '(');
                    WriteOperand(operand: derived);
                    m_text.Append(value: ')');
                    m_derived.Remove(name.Name);
                    return;
                }
            }
            if (scope.TryLowerBinding(name.Name, out var bound) &&
                ((form != DocumentValueForm.Name) || ((bound is JsonValue named) && named.TryGetValue<string>(value: out _)))) {
                WriteValue(form: form, value: bound, span: operand.Span);
                return;
            }
            m_text.Append(name.Name);
        }

        private void WriteAccess(Syntax.SourceAccess access, DocumentValueForm form, OperandExpressionNode operand) {
            if ((form == DocumentValueForm.Expression) && (access.Target is Syntax.SourceName { Quoted: false } enumName) &&
                GetOrCreateEnums(scope: scope).TryGetValue(enumName.Name, out var definition)) {
                for (var index = 0; (index < definition.Members.Count); index++) {
                    if (definition.Members[index] == access.Member) {
                        m_text.Append(value: index.ToString(provider: CultureInfo.InvariantCulture));
                        return;
                    }
                }
                Refuse($"Enum '{enumName.Name}' has no member '{access.Member}'", operand.Span);
                m_text.Append(value: '0');
                return;
            }
            if ((form == DocumentValueForm.Expression) && (access.Target is Syntax.SourceName row) &&
                !GetPoolBindings(scope: scope).Contains(row.Name)) {
                Write(row, DocumentValueForm.Name, operand);
                m_text.Append(value: '[').Append(access.Member).Append(value: ']');
                return;
            }
            Write(access.Target, DocumentValueForm.Name, operand);
            m_text.Append(value: '.').Append(access.Member);
        }

        private void WriteIndex(Syntax.SourceIndex index, OperandExpressionNode operand) {
            if ((index.Target is Syntax.SourceName { Quoted: false } name) &&
                GetOrCreateStateFamilies(scope: scope).TryGetValue(name.Name, out var family) &&
                (index.Index is Syntax.Literal literal) && (decimal.Truncate(d: literal.Value) == literal.Value)) {
                for (var i = 0; (i < family.Indices.Count); i++) {
                    if (family.Indices[i] == literal.Value) {
                        m_text.Append(value: '`').Append(value: family.MemberNames[i]).Append(value: '`');
                        return;
                    }
                }
                scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.FamilyIndexOutOfBounds,
                    message: $"Index '{literal.Value}' names no member of family '{name.Name}', whose members are {string.Join(separator: ", ", values: family.Indices)}",
                    span: operand.Span);
            }
            Write(index.Target, DocumentValueForm.Name, operand);
            m_text.Append(value: '[');
            Write(index.Index, DocumentValueForm.Key, operand);
            m_text.Append(value: ']');
        }

        private void WriteCall(Syntax.SourceCall call, OperandExpressionNode operand) {
            m_text.Append(call.Name).Append(value: '(');
            for (var index = 0; (index < call.Arguments.Count); index++) {
                if (index > 0) { m_text.Append(value: ", "); }
                var argument = call.Arguments[index];
                if (argument.Name is { } name) { m_text.Append(name).Append(value: ": "); }
                var form = ((((index == 0) && (call.Name is "all" or "any" or "count" or "sum")) ||
                    ((index > 0) && (call.Name is "boardShift" or "boardFill" or "boardImage")) ||
                    (argument.Name is "where" or "space")) ? DocumentValueForm.Name : DocumentValueForm.Expression);
                Write(argument.Value, form, operand);
            }
            m_text.Append(value: ')');
        }

        private void WriteValue(JsonNode? value, DocumentValueForm form, SourceSpan span) {
            if (value is JsonValue scalar) {
                if (scalar.TryGetValue<string>(value: out var text)) {
                    if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
                        m_text.Append(value: number.ToString(provider: CultureInfo.InvariantCulture));
                    } else if (!text.Contains(value: '`') && (text.Length > 0)) {
                        m_text.Append(value: '`').Append(value: text).Append(value: '`');
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

        private static string? QualifiedName(Syntax.SyntaxNode node) => node switch {
            Syntax.SourceName { Quoted: false } name => name.Name,
            Syntax.SourceAccess access when (QualifiedName(access.Target) is { } prefix) => $"{prefix}.{access.Member}",
            _ => null,
        };

        private bool HasRuntimeRoot(Syntax.SyntaxNode node) {
            while (true) {
                switch (node) {
                    case Syntax.SourceAccess access: node = access.Target; break;
                    case Syntax.SourceIndex index: node = index.Target; break;
                    default:
                        return ((node is Syntax.SourceName { Quoted: false } name) &&
                        ((name.Name == m_binder) || GetPoolBindings(scope: scope).Contains(name.Name)));
                }
            }
        }

        private bool HasBoundRoot(Syntax.SyntaxNode node) {
            if (HasRuntimeRoot(node: node)) { return false; }
            while (true) {
                if ((QualifiedName(node: node) is { } qualified) && scope.TryLowerBinding(qualified, out _)) { return true; }
                switch (node) {
                    case Syntax.SourceAccess access: node = access.Target; break;
                    case Syntax.SourceIndex index: node = index.Target; break;
                    default: return ((node is Syntax.SourceName { Quoted: false } name) && scope.TryLowerBinding(name.Name, out _));
                }
            }
        }

        private ExpressionNode ToCompileTime(Syntax.SyntaxNode node, OperandExpressionNode operand) => node switch {
            Syntax.SourceGroup group => ToCompileTime(group.Value, operand),
            Syntax.Literal literal => new LiteralExpressionNode(literal.Value, Offset: operand.Offset, Length: operand.Length, Line: operand.Line, Column: operand.Column),
            Syntax.SourceName { Quoted: false, Name: "true" } => new LiteralExpressionNode(true),
            Syntax.SourceName { Quoted: false, Name: "false" } => new LiteralExpressionNode(false),
            Syntax.SourceName { Quoted: true } name => new LiteralExpressionNode(name.Name),
            Syntax.SourceName name => new IdentifierExpressionNode(name.Name),
            Syntax.SourceString text => new LiteralExpressionNode(text.Value),
            Syntax.SourceAtom atom => operand.Atoms[atom.Index].Value,
            Syntax.SourceAccess access when ((QualifiedName(access) is { } qualified) && scope.TryLowerBinding(qualified, out _)) => new IdentifierExpressionNode(qualified),
            Syntax.SourceAccess access => new IndexExpressionNode(ToCompileTime(access.Target, operand), new LiteralExpressionNode(access.Member)),
            Syntax.SourceIndex index => new IndexExpressionNode(ToCompileTime(index.Target, operand), ToCompileTime(index.Index, operand)),
            Syntax.Unary unary => new UnaryExpressionNode(unary.Operator, ToCompileTime(unary.Operand, operand)),
            Syntax.Binary binary => new BinaryExpressionNode(ToCompileTime(binary.Left, operand), binary.Operator, ToCompileTime(binary.Right, operand)),
            Syntax.SourceCall call => new CallExpressionNode(call.Name, [.. call.Arguments.Select(argument => new ArgumentNode(argument.Name, ToCompileTime(argument.Value, operand)))]),
            _ => RefuseCompileTime(operand: operand),
        };

        private LiteralExpressionNode RefuseCompileTime(OperandExpressionNode operand) {
            Refuse("A compile-time access requires a compile-time index expression", operand.Span);
            return new LiteralExpressionNode(0m);
        }
    }
}
