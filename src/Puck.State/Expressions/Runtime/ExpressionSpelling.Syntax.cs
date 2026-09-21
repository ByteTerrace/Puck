using System.Text;

namespace Puck.State;

public static partial class ExpressionSpelling {
    /// <summary>An externally parsed atom inside an operand. Its contents are owned by the source language.</summary>
    /// <param name="Start">The atom's first character in the operand.</param>
    /// <param name="Length">The atom's character length.</param>
    public readonly record struct SourceAtomSpan(int Start, int Length);

    /// <summary>A name before bindings, channels, fields, and rows have been resolved.</summary>
    /// <param name="Name">The decoded name.</param>
    /// <param name="Quoted">Whether backquotes force a literal name.</param>
    public sealed record SourceName(string Name, bool Quoted = false) : SourceNode;

    /// <summary>A string argument to an operand call.</summary>
    /// <param name="Value">The decoded string.</param>
    public sealed record SourceString(string Value) : SourceNode;

    /// <summary>A reference to an atom parsed by the source language.</summary>
    /// <param name="Index">The index in the supplied atom span list.</param>
    public sealed record SourceAtom(int Index) : SourceNode;

    /// <summary>An explicit group. In key position parentheses distinguish a live expression from a literal key.</summary>
    /// <param name="Value">The grouped expression.</param>
    public sealed record SourceGroup(SyntaxNode Value) : SourceNode;

    /// <summary>An indexed reference whose target and index retain their syntax.</summary>
    /// <param name="Target">The indexed reference.</param>
    /// <param name="Index">The index or cell key.</param>
    public sealed record SourceIndex(SyntaxNode Target, SyntaxNode Index) : SourceNode;

    /// <summary>A member reference, resolved by the vocabulary rather than by the lexer.</summary>
    /// <param name="Target">The enum, record, binding, or row.</param>
    /// <param name="Member">The named member.</param>
    public sealed record SourceAccess(SyntaxNode Target, string Member) : SourceNode;

    /// <summary>A call argument retaining its optional parameter name.</summary>
    /// <param name="Name">The parameter name, or null for a positional argument.</param>
    /// <param name="Value">The argument syntax.</param>
    public sealed record SourceArgument(string? Name, SyntaxNode Value);

    /// <summary>A call before its vocabulary has resolved the operation and argument roles.</summary>
    /// <param name="Name">The call name.</param>
    /// <param name="Arguments">The arguments in source order.</param>
    public sealed record SourceCall(string Name, IReadOnlyList<SourceArgument> Arguments) : SourceNode;

    /// <summary>A family fold's bound member and body.</summary>
    /// <param name="Binder">The member name bound inside the body.</param>
    /// <param name="Body">The fold body.</param>
    public sealed record SourceLambda(string Binder, SyntaxNode Body) : SourceNode;

    /// <summary>Syntax requiring vocabulary resolution before it can emit runtime instructions.</summary>
    public abstract record SourceNode : SyntaxNode {
        internal override int Level => PrimaryLevel;
        internal override void Emit(List<Instruction> into, ProgramBuilder builder) =>
            throw new InvalidOperationException(message: "Source syntax must be resolved before instruction emission.");
        internal override void PrintBare(StringBuilder into) => PrintSourceNode(into: into, node: this);
    }

    /// <summary>Parses source operands without deciding whether names denote constants, rows, or fields.</summary>
    /// <remarks>Uses the same lexer, precedence, nesting bounds, and arithmetic nodes as runtime expressions.
    /// Atoms are supplied as spans rather than replaced with sentinel text, so their contents never become syntax.</remarks>
    /// <param name="text">The operand source.</param>
    /// <param name="atoms">Nonoverlapping atom spans in ascending source order.</param>
    /// <param name="syntax">The parsed tree, or null on refusal.</param>
    /// <param name="error">The refusal with its source position, or empty on success.</param>
    /// <returns>Whether the source is syntactically valid.</returns>
    public static bool TryParseSyntax(string? text, IReadOnlyList<SourceAtomSpan> atoms, out SyntaxNode? syntax, out string error) {
        ArgumentNullException.ThrowIfNull(atoms);
        syntax = null;
        if (string.IsNullOrWhiteSpace(value: text)) {
            error = "is empty";
            return false;
        }
        if (text.Length > MaxLength) {
            error = $"is {text.Length} characters long; at most {MaxLength} are admitted";
            return false;
        }
        var end = 0;
        foreach (var atom in atoms) {
            if ((atom.Start < end) || (atom.Length <= 0) || (atom.Start > (text.Length - atom.Length))) {
                error = "has an invalid or overlapping atom span";
                return false;
            }
            end = (atom.Start + atom.Length);
        }
        try {
            var parser = new Parser(text: text, atoms: atoms, sourceSyntax: true);
            syntax = parser.ParseExpression();
            parser.ExpectEnd();
            error = string.Empty;
            return true;
        } catch (SyntaxException failure) {
            syntax = null;
            error = failure.Message;
            return false;
        }
    }

    private static void PrintSourceNode(SourceNode node, StringBuilder into) {
        switch (node) {
            case SourceGroup group:
                into.Append(value: '(');
                group.Value.Print(into: into, parentLevel: 0, rightOperand: false);
                into.Append(value: ')');
                break;
            case SourceName name:
                into.Append(value: (name.Quoted ? $"`{name.Name}`" : name.Name));
                break;
            case SourceString text:
                into.Append(value: '"').Append(value: text.Value.Replace(comparisonType: StringComparison.Ordinal, newValue: "\\\\", oldValue: "\\").Replace(comparisonType: StringComparison.Ordinal, newValue: "\\\"", oldValue: "\"")).Append(value: '"');
                break;
            case SourceIndex index:
                index.Target.Print(into: into, parentLevel: PrimaryLevel, rightOperand: false);
                into.Append(value: '[');
                index.Index.Print(into: into, parentLevel: 0, rightOperand: false);
                into.Append(value: ']');
                break;
            case SourceAccess access:
                access.Target.Print(into: into, parentLevel: PrimaryLevel, rightOperand: false);
                into.Append(value: '.').Append(value: access.Member);
                break;
            case SourceCall call:
                into.Append(value: call.Name).Append(value: '(');
                for (var i = 0; (i < call.Arguments.Count); i++) {
                    if (i > 0) { into.Append(value: ", "); }
                    var argument = call.Arguments[i];
                    if (argument.Name is { } name) { into.Append(value: name).Append(value: ": "); }
                    argument.Value.Print(into: into, parentLevel: 0, rightOperand: false);
                }
                into.Append(value: ')');
                break;
            case SourceLambda lambda:
                into.Append(value: lambda.Binder).Append(value: " -> ");
                lambda.Body.Print(into: into, parentLevel: 0, rightOperand: false);
                break;
            default:
                throw new InvalidOperationException(message: "An atom must be resolved by its source language before printing.");
        }
    }

    private sealed partial class Parser {
        private int m_sourceAtomIndex;

        private bool TryReadSourceAtom() {
            var low = 0;
            var high = ((atoms?.Count ?? 0) - 1);
            while (low <= high) {
                var middle = (low + ((high - low) / 2));
                var span = atoms![middle];
                if (span.Start < m_position) { low = (middle + 1); } else if (span.Start > m_position) { high = (middle - 1); } else {
                    m_sourceAtomIndex = middle;
                    m_position += span.Length;
                    m_kind = Lexeme.Atom;
                    m_value = string.Empty;
                    return true;
                }
            }
            return false;
        }

        private SyntaxNode ParseSourcePrimary() {
            Prime();
            var postfixDepth = 0;
            SyntaxNode node;
            switch (m_kind) {
                case Lexeme.Atom:
                    node = new SourceAtom(Index: m_sourceAtomIndex);
                    Advance();
                    break;
                case Lexeme.Number:
                    node = new Literal(Value: ParseNumber(lexeme: m_value, start: m_start));
                    Advance();
                    break;
                case Lexeme.String:
                    node = new SourceString(Value: m_value);
                    Advance();
                    break;
                case Lexeme.Name:
                    var name = m_value;
                    var quoted = m_quoted;
                    Advance();
                    if (!quoted && Accept(punctuation: "(")) {
                        node = ParseSourceCall(name: name);
                    } else if (!quoted && !name.StartsWith(value: '$') && name.Contains(value: '.')) {
                        var parts = name.Split('.');
                        node = new SourceName(parts[0]);
                        for (var i = 1; (i < parts.Length); i++) {
                            if (parts[i].Length == 0) { throw Fail(message: "expected a member name after '.'"); }
                            Descend();
                            postfixDepth++;
                            node = new SourceAccess(node, parts[i]);
                        }
                    } else {
                        node = new SourceName(Name: name, Quoted: quoted);
                    }
                    break;
                case Lexeme.Punctuation when (m_value == "("):
                    Advance();
                    node = new SourceGroup(Value: ParseExpression());
                    Expect(punctuation: ")");
                    break;
                default:
                    throw Fail(message: $"expected a value but found '{m_value}'");
            }
            while (true) {
                if (Accept(punctuation: "[")) {
                    Descend();
                    postfixDepth++;
                    var index = ParseExpression();
                    Expect(punctuation: "]");
                    node = new SourceIndex(Index: index, Target: node);
                } else if (Accept(punctuation: ".")) {
                    Descend();
                    postfixDepth++;
                    Prime();
                    if ((m_kind != Lexeme.Name) || m_quoted || m_value.Contains(value: '.')) {
                        throw Fail(message: "expected one member name after '.'");
                    }
                    node = new SourceAccess(Member: m_value, Target: node);
                    Advance();
                } else {
                    m_depth -= postfixDepth;
                    return node;
                }
            }
        }

        private SyntaxNode ParseSourceCall(string name) {
            var arguments = new List<SourceArgument>();
            if (Accept(punctuation: ")")) { return new SourceCall(Arguments: arguments, Name: name); }
            do {
                Prime();
                string? argumentName = null;
                if ((m_kind == Lexeme.Name) && !m_quoted && IsBinderName(name: m_value)) {
                    var saved = (m_position, m_kind, m_value, m_quoted, m_start, m_primed, m_sourceAtomIndex);
                    var candidate = m_value;
                    Advance();
                    if (Accept(punctuation: ":")) {
                        argumentName = candidate;
                    } else if (Accept(punctuation: "->") || Accept(punctuation: "=>")) {
                        arguments.Add(item: new SourceArgument(Name: null, Value: new SourceLambda(Binder: candidate, Body: ParseExpression())));
                        continue;
                    } else {
                        (m_position, m_kind, m_value, m_quoted, m_start, m_primed, m_sourceAtomIndex) = saved;
                    }
                }
                arguments.Add(item: new SourceArgument(Name: argumentName, Value: ParseExpression()));
            } while (Accept(punctuation: ","));
            Expect(punctuation: ")");
            return new SourceCall(Arguments: arguments, Name: name);
        }
    }
}
