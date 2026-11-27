using Puck.Maths;
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
    /// <summary>An explicit expression group.</summary>
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
    /// <summary>A list written out, <c>[a, b, c]</c>: a compile-time value the source language folds away (a fold over
    /// a compile-time list unrolls into one term per element), never a runtime value.</summary>
    /// <param name="Items">The elements in written order.</param>
    public sealed record SourceList(IReadOnlyList<SyntaxNode> Items) : SourceNode;
    /// <summary>A name bound for one expression, <c>let name = value in body</c>: the source language resolves it
    /// before the program is emitted, so the program holds the body with the value in place of the name.</summary>
    /// <param name="Name">The name the body reads.</param>
    /// <param name="Value">The bound value.</param>
    /// <param name="Body">The expression the whole stands for.</param>
    public sealed record SourceLet(string Name, SyntaxNode Value, SyntaxNode Body) : SourceNode {
        // The body reaches as far right as it can, so inside an operator the whole is grouped.
        internal override int Level => 0;
    }
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
    public static bool TryParseSyntax(string? text, SourceAtomSpan[] atoms, out SyntaxNode? syntax, out string error) {
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

    /// <summary>Returns a source tree node's direct children in printed order: a call's argument values, an index's
    /// target then key, a let's value then body, a choice's condition then arms.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The children; empty for a literal, a name, a string or an atom.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is <see langword="null"/>.</exception>
    public static IEnumerable<SyntaxNode> SourceChildren(SyntaxNode node) {
        ArgumentNullException.ThrowIfNull(node);

        return node switch {
            SourceGroup group => [group.Value],
            SourceIndex index => [index.Target, index.Index],
            SourceAccess access => [access.Target],
            SourceCall call => call.Arguments.Select(selector: static argument => argument.Value),
            SourceLambda lambda => [lambda.Body],
            SourceList list => list.Items,
            SourceLet let => [let.Value, let.Body],
            Unary unary => [unary.Operand],
            Binary binary => [binary.Left, binary.Right],
            Ternary ternary => [ternary.Condition, ternary.WhenTrue, ternary.WhenFalse],
            _ => [],
        };
    }
    /// <summary>Returns a source tree node with each direct child replaced by <paramref name="map"/>'s answer, the
    /// children visited in printed order (<see cref="SourceChildren"/>), so a walk that numbers atoms as it goes
    /// numbers them as the printed text holds them.</summary>
    /// <param name="node">The node.</param>
    /// <param name="map">The replacement for one child.</param>
    /// <returns>The rebuilt node; <paramref name="node"/> itself when every answer is the child it was given.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> or <paramref name="map"/> is
    /// <see langword="null"/>.</exception>
    public static SyntaxNode MapSourceChildren(SyntaxNode node, Func<SyntaxNode, SyntaxNode> map) {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(map);

        switch (node) {
            case SourceGroup group: {
                    var value = map(arg: group.Value);

                    return (ReferenceEquals(objA: value, objB: group.Value) ? group : (group with { Value = value }));
                }
            case SourceIndex index: {
                    var target = map(arg: index.Target);
                    var key = map(arg: index.Index);

                    return ((ReferenceEquals(objA: target, objB: index.Target) && ReferenceEquals(objA: key, objB: index.Index)) ? index : new SourceIndex(Index: key, Target: target));
                }
            case SourceAccess access: {
                    var target = map(arg: access.Target);

                    return (ReferenceEquals(objA: target, objB: access.Target) ? access : (access with { Target = target }));
                }
            case SourceCall call: {
                    SourceArgument[]? arguments = null;

                    for (var at = 0; (at < call.Arguments.Count); at++) {
                        var argument = call.Arguments[at];
                        var value = map(arg: argument.Value);

                        if (!ReferenceEquals(objA: value, objB: argument.Value)) {
                            arguments ??= [.. call.Arguments];
                            arguments[at] = (argument with { Value = value });
                        }
                    }
                    return ((arguments is null) ? call : (call with { Arguments = arguments }));
                }
            case SourceLambda lambda: {
                    var body = map(arg: lambda.Body);

                    return (ReferenceEquals(objA: body, objB: lambda.Body) ? lambda : (lambda with { Body = body }));
                }
            case SourceList list: {
                    SyntaxNode[]? items = null;

                    for (var at = 0; (at < list.Items.Count); at++) {
                        var item = map(arg: list.Items[at]);

                        if (!ReferenceEquals(objA: item, objB: list.Items[at])) {
                            items ??= [.. list.Items];
                            items[at] = item;
                        }
                    }
                    return ((items is null) ? list : (list with { Items = items }));
                }
            case SourceLet let: {
                    var value = map(arg: let.Value);
                    var body = map(arg: let.Body);

                    return ((ReferenceEquals(objA: value, objB: let.Value) && ReferenceEquals(objA: body, objB: let.Body)) ? let : (let with { Value = value, Body = body }));
                }
            case Unary unary: {
                    var operand = map(arg: unary.Operand);

                    return (ReferenceEquals(objA: operand, objB: unary.Operand) ? unary : (unary with { Operand = operand }));
                }
            case Binary binary: {
                    var left = map(arg: binary.Left);
                    var right = map(arg: binary.Right);

                    return ((ReferenceEquals(objA: left, objB: binary.Left) && ReferenceEquals(objA: right, objB: binary.Right)) ? binary : (binary with { Left = left, Right = right }));
                }
            case Ternary ternary: {
                    var condition = map(arg: ternary.Condition);
                    var whenTrue = map(arg: ternary.WhenTrue);
                    var whenFalse = map(arg: ternary.WhenFalse);

                    return ((ReferenceEquals(objA: condition, objB: ternary.Condition) && ReferenceEquals(objA: whenTrue, objB: ternary.WhenTrue) && ReferenceEquals(objA: whenFalse, objB: ternary.WhenFalse))
                        ? ternary
                        : new Ternary(Condition: condition, WhenFalse: whenFalse, WhenTrue: whenTrue));
                }
            default:
                return node;
        }
    }

    /// <summary>Returns a source tree with each atom's index replaced by <paramref name="map"/>'s answer, the atoms
    /// visited in printed order.</summary>
    /// <param name="node">The tree.</param>
    /// <param name="map">The new index for an atom's index.</param>
    /// <returns>The renumbered tree.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> or <paramref name="map"/> is
    /// <see langword="null"/>.</exception>
    public static SyntaxNode MapSourceAtoms(SyntaxNode node, Func<int, int> map) {
        ArgumentNullException.ThrowIfNull(map);

        return ((node is SourceAtom atom) ? (atom with { Index = map(arg: atom.Index) }) : MapSourceChildren(map: child => MapSourceAtoms(map: map, node: child), node: node));
    }

    private static void PrintSourceNode(SourceNode node, StringBuilder into) {
        switch (node) {
            case SourceGroup group:
                into.Append(value: '(');
                group.Value.Print(into: into, parentLevel: 0, rightOperand: false);
                into.Append(value: ')');
                break;
            case SourceName name:
                into.Append(value: PrintName(name: name));
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
            case SourceList list:
                into.Append(value: '[');
                for (var i = 0; (i < list.Items.Count); i++) {
                    if (i > 0) { into.Append(value: ", "); }
                    list.Items[i].Print(into: into, parentLevel: 0, rightOperand: false);
                }
                into.Append(value: ']');
                break;
            case SourceLet let:
                into.Append(value: "let ").Append(value: let.Name).Append(value: " = ");
                let.Value.Print(into: into, parentLevel: 0, rightOperand: false);
                into.Append(value: " in ");
                let.Body.Print(into: into, parentLevel: 0, rightOperand: false);
                break;
            default:
                throw new InvalidOperationException(message: "An atom must be resolved by its source language before printing.");
        }
    }

    private sealed partial class Parser {
        private int m_sourceAtomIndex;

        private bool TryReadSourceAtom() {
            var candidates = (atoms ?? []);
            var index = PartitionPoint.Of(span: candidates, state: m_position, isBefore: static (at, atom) => (atom.Start < at));

            if ((index == candidates.Length) || (candidates[index].Start != m_position)) { return false; }

            m_sourceAtomIndex = index;
            m_position += candidates[index].Length;
            m_kind = Lexeme.Atom;
            m_value = string.Empty;
            return true;
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
                case Lexeme.Punctuation when (m_value == "["):
                    Advance();
                    Descend();
                    var items = new List<SyntaxNode>();

                    if (!Accept(punctuation: "]")) {
                        do {
                            items.Add(item: ParseExpression());
                        } while (Accept(punctuation: ","));
                        Expect(punctuation: "]");
                    }
                    m_depth--;
                    node = new SourceList(Items: items);
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
        // `let name = value in body`, read only where `let` is followed by a binder name and a lone `=`, so a row
        // named `let` still reads as a row everywhere else.
        private SourceLet? TryParseSourceLet() {
            Prime();
            if ((m_kind != Lexeme.Name) || m_quoted || (m_value != "let")) { return null; }
            var saved = (m_position, m_kind, m_value, m_quoted, m_start, m_primed, m_sourceAtomIndex);

            Advance();
            var name = m_value;
            var equals = m_position;

            while ((equals < text.Length) && char.IsWhiteSpace(c: text[equals])) { equals++; }
            if ((m_kind != Lexeme.Name) || m_quoted || !IsBinderName(name: name) || (equals >= text.Length) || (text[equals] != '=') ||
                (((equals + 1) < text.Length) && (text[(equals + 1)] is '=' or '>'))) {
                (m_position, m_kind, m_value, m_quoted, m_start, m_primed, m_sourceAtomIndex) = saved;
                return null;
            }
            m_position = (equals + 1);
            Advance();
            var value = ParseExpression();

            Prime();
            if ((m_kind != Lexeme.Name) || m_quoted || (m_value != "in")) {
                throw Fail(message: $"a let reads 'let {name} = value in expression'; expected 'in'{Found()}");
            }
            Advance();
            return new SourceLet(Body: ParseExpression(), Name: name, Value: value);
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
