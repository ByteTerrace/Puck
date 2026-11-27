using System.Text.Json.Nodes;
using Puck.Assets.Documents;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Syntax = Puck.State.ExpressionSpelling;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    private sealed partial class OperandBinder {
        // Writes out what the operand computes at compile time but reads state through: a fold over a compile-time
        // list (ListFolds) becomes one term per element, each the fold's body with the element in place of its binder,
        // and `let name = value in body` becomes the body with the value in place of the name. The result is the
        // expression an author writes out by hand, so it is priced, folded and refused as that expression is.
        //
        // An element or a compile-time value stands in the body as the syntax a compile-time binding of it is written
        // as (ElementSyntax): a number as its literal, a name or text as an atom holding it, a record or list only
        // through a read of one of its members. An interpolated string that reads the name is lowered with the name
        // bound and stands as the atom of its text. Nothing the result holds refers to a binding the expansion made,
        // so FoldOperand's result binds the same under any binder of the scope. A tree with neither a let nor a fold
        // over a compile-time list (a row's `count(pile)` included) comes back as the same object.
        internal Syntax.SyntaxNode Expand(Syntax.SyntaxNode node, OperandExpressionNode operand) => node switch {
            Syntax.SourceLet let => ExpandLet(let: let, operand: operand),
            Syntax.SourceCall call when (TryExpandFold(call: call, operand: operand) is { } unrolled) => unrolled,
            _ => Syntax.MapSourceChildren(map: child => Expand(node: child, operand: operand), node: node),
        };

        private static bool FoldShaped(Syntax.SourceCall call) => ListFolds.Shaped(
            arguments: call.Arguments.Count,
            name: call.Name,
            secondIsLambda: ((call.Arguments.Count > 1) && (call.Arguments[1].Value is Syntax.SourceLambda))
        );
        // A fold of ListFolds' shape whose list the compiler knows, unrolled; null for any other call, which a rule's
        // own fold over a row's cells (`count(pile)`, `any(row, x -> x > 0)`) is.
        private Syntax.SyntaxNode? TryExpandFold(Syntax.SourceCall call, OperandExpressionNode operand) {
            var arguments = call.Arguments;

            if (!FoldShaped(call: call) ||
                arguments.Any(predicate: static argument => (argument.Name is not null))) {
                return null;
            }
            if (CompileTimeList(node: arguments[0].Value, operand: operand) is not { } items) {
                if (call.Name != "first") {
                    return null;
                }
                Refuse(message: DiagnosticText.Compose(
                    fix: "Write the list as a literal, a 'let', a template parameter or a builtin such as range(1, 5)",
                    problem: "first(list, condition, otherwise) reads a list the compiler knows, and this one is not known at compile time"
                ), span: operand.Span);
                return new Syntax.Literal(Value: 0m);
            }
            var first = (call.Name == "first");
            var term = (Syntax.SourceLambda)arguments[1].Value;
            var value = ((first && (arguments.Count == 4)) ? (arguments[2].Value as Syntax.SourceLambda) : null);

            if (first && (((arguments.Count == 4) && (value is null)) || (arguments[^1].Value is Syntax.SourceLambda))) {
                Refuse(message: DiagnosticText.Compose(
                    fix: "Write the value as a lambda and the otherwise as a value, as in first(seats, s => seat == s, s => score(s), 0)",
                    problem: "first(list, condition, value, otherwise) takes its value as a lambda over the element and ends with the value when no condition holds"
                ), span: operand.Span);
                return new Syntax.Literal(Value: 0m);
            }
            scope.Budget.Collection(count: items.Count, span: operand.Span);
            var terms = new Syntax.SyntaxNode[items.Count];
            var values = (first ? new Syntax.SyntaxNode[items.Count] : null);

            for (var index = 0; (index < items.Count); index++) {
                terms[index] = Expand(node: BindName(name: term.Binder, node: term.Body, operand: operand, value: items[index]), operand: operand);
                if (values is not null) {
                    values[index] = Expand(node: ((value is null) ? ElementSyntax(operand: operand, value: items[index]) : BindName(name: value.Binder, node: value.Body, operand: operand, value: items[index])), operand: operand);
                }
            }
            var otherwise = (first ? Expand(node: arguments[^1].Value, operand: operand) : null);

            if (ListFolds.Unroll(name: call.Name, otherwise: otherwise, refusal: out var refusal, spelling: OperandFoldSpelling.Instance, terms: terms, values: values) is { } unrolled) {
                return unrolled;
            }
            Refuse(message: DiagnosticText.Compose(fix: "Fold a list with at least one element", problem: refusal!), span: operand.Span);
            return new Syntax.Literal(Value: 0m);
        }
        // The list a fold's first argument stands for, when the compiler knows it: a list written out, a binding holding
        // a list, a collection builtin's result (range, map, concat and the rest), or a read of a list through a
        // compile-time record. Anything else (a row, a runtime name) is null.
        private JsonArray? CompileTimeList(Syntax.SyntaxNode node, OperandExpressionNode operand) {
            while (node is Syntax.SourceGroup group) { node = group.Value; }
            var known = node switch {
                Syntax.SourceList => true,
                Syntax.SourceName { Quoted: false } name => (!name.Name.StartsWith(value: '$') && !IsRuntimeName(name: name.Name) &&
                    scope.TryLowerBinding(name.Name, out var bound) && (bound is JsonArray)),
                Syntax.SourceCall call => DocumentBuiltins.Collections.ContainsKey(key: call.Name),
                Syntax.SourceAccess or Syntax.SourceIndex => HasBoundRoot(node: node),
                _ => false,
            };

            return (known ? (DocumentLowering.LowerValue(ToCompileTime(node: node, operand: operand), scope) as JsonArray) : null);
        }

        // `let name = value in body`. A value the compiler knows binds as a fold's element does; any other value is
        // written in place of the name, which it may be only where the hand-written expression would hold it once,
        // or where it is a plain read, so the program never evaluates the value more often than the author did.
        private Syntax.SyntaxNode ExpandLet(Syntax.SourceLet let, OperandExpressionNode operand) {
            var value = Expand(node: let.Value, operand: operand);

            if (IsCompileTime(node: value)) {
                return Expand(node: BindName(name: let.Name, node: let.Body, operand: operand, value: DocumentLowering.LowerValue(ToCompileTime(node: value, operand: operand), scope)), operand: operand);
            }
            var body = Expand(node: let.Body, operand: operand);

            if ((Reads(name: let.Name, node: body) > 1) && !IsPlainRead(node: value)) {
                Refuse(message: DiagnosticText.Compose(
                    fix: $"Declare it as a rule local ('local {let.Name} = …'), which is evaluated once, and read the local",
                    problem: $"The let '{let.Name}' holds a value that reads state, and its body reads it more than once, so writing the value in place would evaluate it each time"
                ), span: operand.Span);
                return new Syntax.Literal(Value: 0m);
            }
            return Substitute(arguments: new Dictionary<string, Syntax.SyntaxNode>(comparer: StringComparer.Ordinal) { [let.Name] = value }, node: body);
        }
        // How many times the body reads the name where it is not shadowed.
        private static int Reads(string name, Syntax.SyntaxNode node) => node switch {
            Syntax.SourceName { Quoted: false } read => ((read.Name == name) ? 1 : 0),
            Syntax.SourceLambda lambda when (lambda.Binder == name) => 0,
            Syntax.SourceLet let when (let.Name == name) => Reads(name: name, node: let.Value),
            _ => Syntax.SourceChildren(node: node).Sum(selector: child => Reads(name: name, node: child)),
        };
        // A name, or a name read by literal keys and members: what a program reads as one operand, which costs the same
        // read however many times it is written.
        private static bool IsPlainRead(Syntax.SyntaxNode node) => node switch {
            Syntax.SourceGroup group => IsPlainRead(node: group.Value),
            Syntax.Literal or Syntax.SourceName or Syntax.SourceAtom or Syntax.SourceString => true,
            Syntax.SourceAccess access => IsPlainRead(node: access.Target),
            Syntax.SourceIndex index => (IsPlainRead(node: index.Target) && (index.Index is Syntax.Literal or Syntax.SourceString or Syntax.SourceAtom or Syntax.SourceName)),
            _ => false,
        };
        // Whether the compiler knows the value: every name it reads is a compile-time binding, an enum member or a
        // literal name, and every call is a builtin over such values. A row read, a rule local, a reserved `$` name or
        // a call the rule evaluates makes it a value that reads state.
        private bool IsCompileTime(Syntax.SyntaxNode node) => node switch {
            Syntax.Literal or Syntax.SourceString or Syntax.SourceAtom => true,
            Syntax.SourceName { Quoted: true } => true,
            Syntax.SourceName { Name: "true" or "false" } => true,
            Syntax.SourceName name => (!name.Name.StartsWith(value: '$') && !IsRuntimeName(name: name.Name) &&
                !GetOrCreateDerivedState(scope: scope).ContainsKey(key: name.Name) && scope.TryLowerBinding(name.Name, out _)),
            Syntax.SourceAccess access => ((Constant(access: access) is not null) || HasBoundRoot(node: access)),
            Syntax.SourceIndex index => (HasBoundRoot(node: index) && IsCompileTime(node: index.Index)),
            Syntax.SourceGroup group => IsCompileTime(node: group.Value),
            Syntax.SourceList list => list.Items.All(predicate: IsCompileTime),
            Syntax.Unary unary => IsCompileTime(node: unary.Operand),
            Syntax.Binary binary => (IsCompileTime(node: binary.Left) && IsCompileTime(node: binary.Right)),
            Syntax.Ternary ternary => (IsCompileTime(node: ternary.Condition) && IsCompileTime(node: ternary.WhenTrue) && IsCompileTime(node: ternary.WhenFalse)),
            Syntax.SourceCall call => (DocumentBuiltins.Names.Contains(item: call.Name) && call.Arguments.All(predicate: argument => IsCompileTime(node: argument.Value))),
            _ => false,
        };

        // The body with the compile-time value in place of the name: a bare reading becomes ElementSyntax, a read
        // through a record or list the value holds reads at compile time as far as its keys are known, and an
        // interpolated string reading the name is lowered with the name bound. A lambda or let binding the same name
        // hides it.
        private Syntax.SyntaxNode BindName(string name, Syntax.SyntaxNode node, OperandExpressionNode operand, JsonNode? value) {
            return Rebind(current: node);

            Syntax.SyntaxNode Rebind(Syntax.SyntaxNode current) => current switch {
                Syntax.SourceName { Quoted: false } read when (read.Name == name) => ElementSyntax(operand: operand, value: value),
                Syntax.SourceAccess or Syntax.SourceIndex when ((value is JsonObject or JsonArray) && (Root(node: current) is Syntax.SourceName { Quoted: false } root) && (root.Name == name)) =>
                    Spelled(read: ReadThrough(node: current)),
                Syntax.SourceAtom atom when ReadsName(expression: AtomAt(index: atom.Index, operand: operand), name: name) =>
                    ElementSyntax(operand: operand, value: DocumentLowering.LowerValue(AtomAt(index: atom.Index, operand: operand), Bound(name: name, value: value))),
                Syntax.SourceLambda lambda when (lambda.Binder == name) => lambda,
                Syntax.SourceLet let when (let.Name == name) => let with { Value = Rebind(current: let.Value) },
                _ => Syntax.MapSourceChildren(map: Rebind, node: current),
            };
            // A chain of members and keys from the name, read at compile time while the value it reaches is a record
            // or a list and the key is known; from the first scalar on, the rest of the chain reads state through it
            // (a name held in a record is a row, and `[$each]` its runtime key).
            // A read answers its compile-time value, with no syntax, while the chain is known, and its syntax once it
            // reads state.
            (JsonNode? Value, Syntax.SyntaxNode? Syntax) ReadThrough(Syntax.SyntaxNode node) {
                switch (node) {
                    case Syntax.SourceName:
                        return (value, null);
                    case Syntax.SourceAccess access: {
                            var target = ReadThrough(node: access.Target);

                            if ((target.Syntax is null) && (target.Value is JsonObject or JsonArray)) {
                                return (ReadAt(target: target.Value, key: new LiteralExpressionNode(access.Member)), null);
                            }
                            return (null, access with { Target = Spelled(read: target) });
                        }
                    case Syntax.SourceIndex index: {
                            var target = ReadThrough(node: index.Target);
                            var key = Fold(node: Rebind(current: index.Index), operand: operand);

                            if ((target.Syntax is null) && (target.Value is JsonObject or JsonArray)) {
                                if (IsCompileTime(node: key)) {
                                    return (ReadAt(target: target.Value, key: ToCompileTime(node: key, operand: operand)), null);
                                }
                                Refuse(message: DiagnosticText.Compose(
                                    fix: "Index it with a literal, a 'let' or a loop variable, or fold the list",
                                    problem: $"A list or record '{name}' holds at compile time is indexed by a value the rule reads at run time"
                                ), span: operand.Span);
                            }
                            return (null, index with { Target = Spelled(read: target), Index = key });
                        }
                    default:
                        return (null, node);
                }
            }
            Syntax.SyntaxNode Spelled((JsonNode? Value, Syntax.SyntaxNode? Syntax) read) => (read.Syntax ?? ElementSyntax(operand: operand, value: read.Value));
            // The member or element a compile-time key reads, through the document's own index rules and refusals.
            JsonNode? ReadAt(JsonNode target, ExpressionNode key) {
                const string Held = "$held";

                return DocumentLowering.LowerValue(new IndexExpressionNode(new IdentifierExpressionNode(Held), key), Bound(name: Held, value: target));
            }
        }
        // The scope with one more compile-time binding, as a collection builtin binds its lambda's parameter.
        private DocumentScope Bound(string name, JsonNode? value) => scope.WithLocals(lambdaLocals: new Dictionary<string, JsonNode?>(dictionary: scope.Locals, comparer: StringComparer.Ordinal) {
            [name] = value,
        });
        // A compile-time value as the syntax a binding holding it is written as: a number (or a truth) as its literal,
        // a name or text as an atom holding it, which WriteValue writes as WriteName writes a bound name, and a list
        // as the list of its elements, which only a fold or a compile-time index may read.
        private Syntax.SyntaxNode ElementSyntax(OperandExpressionNode operand, JsonNode? value) {
            if (Number(value: value) is { } number) {
                return new Syntax.Literal(Value: number);
            }
            if ((value is JsonValue scalar) && scalar.TryGetValue<string>(value: out var text)) {
                m_inlinedAtoms.Add(item: new InterpolatedStringNode(Segments: [new InterpolationSegment.Literal(Text: text)]));
                return new Syntax.SourceAtom(Index: ((operand.Atoms.Count + m_inlinedAtoms.Count) - 1));
            }
            if (value is JsonArray list) {
                return new Syntax.SourceList(Items: [.. list.Select(selector: item => ElementSyntax(operand: operand, value: item))]);
            }
            Refuse(message: DiagnosticText.Compose(
                fix: "Read one of its members or elements, such as 'item.side' or 'item[0]'",
                problem: "A list, a record or an absent value stands where a rule value is one number, name or text"
            ), span: operand.Span);
            return new Syntax.Literal(Value: 0m);
        }
        // The interpolated string an atom index names: the operand's own, then those inlining and expansion added.
        private InterpolatedStringNode AtomAt(int index, OperandExpressionNode operand) => ((index < operand.Atoms.Count)
            ? operand.Atoms[index].Value
            : m_inlinedAtoms[(index - operand.Atoms.Count)]);
        private static Syntax.SyntaxNode Root(Syntax.SyntaxNode node) {
            while (true) {
                switch (node) {
                    case Syntax.SourceAccess access: node = access.Target; break;
                    case Syntax.SourceIndex index: node = index.Target; break;
                    default: return node;
                }
            }
        }
        // Whether an interpolated string's holes read the name.
        private static bool ReadsName(SyntaxNode expression, string name) => ((expression is IdentifierExpressionNode identifier) && (identifier.Name == name)) ||
            expression.Children().Any(predicate: child => ReadsName(expression: child, name: name));

        private sealed class OperandFoldSpelling : ListFoldSpelling<Syntax.SyntaxNode> {
            public static OperandFoldSpelling Instance { get; } = new();

            public override Syntax.SyntaxNode Number(decimal value) => new Syntax.Literal(Value: value);
            public override Syntax.SyntaxNode Binary(Syntax.SyntaxNode left, string symbol, Syntax.SyntaxNode right) => new Syntax.Binary(Left: left, Operator: symbol, Right: right);
            public override Syntax.SyntaxNode Call(string name, Syntax.SyntaxNode left, Syntax.SyntaxNode right) => new Syntax.SourceCall(Arguments: [new Syntax.SourceArgument(Name: null, Value: left), new Syntax.SourceArgument(Name: null, Value: right)], Name: name);
            public override Syntax.SyntaxNode Choice(Syntax.SyntaxNode condition, Syntax.SyntaxNode whenTrue, Syntax.SyntaxNode whenFalse) => new Syntax.Ternary(Condition: condition, WhenFalse: whenFalse, WhenTrue: whenTrue);
            public override bool IsTruth(Syntax.SyntaxNode term) => BooleanSyntax(node: term);
        }
    }
}
