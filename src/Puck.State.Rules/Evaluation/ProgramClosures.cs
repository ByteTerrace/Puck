using System.Globalization;
using System.Runtime.CompilerServices;

using Puck.Maths;

namespace Puck.State.Rules;

// How one evaluation stands after a node answered. A node that answers a value leaves None, or Marked when that value
// is an absence some consumer may still answer (isAbsent, ??, not, a truth test, an address). Every other case stops
// the program it sits in: Absent when an operator consumed an absence, Domain and Forever for the two faults. A body
// (a call's subprogram, a fold's member body, a short-circuit operand) is a program of its own, so the node that runs
// one reads its stop as the body's answer and stops its own program only as the postfix walk did.
internal enum Halt : byte {
    None,
    Marked,
    Absent,
    Domain,
    Forever,
}

// The values a call hands its body, on the machine stack of the node that calls it.
[InlineArray(length: RuleExpressions.MaxArguments)]
internal struct ArgumentBlock {
    private long m_first;
}

// What one program's nodes share while it runs: its call's arguments, the fold member bound for it, and how it
// stands. A call gives its body a context of its own; every other body shares its caller's. It holds no reference, so
// a call's context is left uninitialized until its fields are written.
internal struct ProgramContext {
    public ArgumentBlock Arguments;
    public int ArgumentCount;
    public long Member;
    public bool HasMember;
    public Halt Halt;

    public static ProgramContext Start() {
        Unsafe.SkipInit(value: out ProgramContext context);
        context.ArgumentCount = 0;
        context.Member = 0L;
        context.HasMember = false;
        context.Halt = Halt.None;

        return context;
    }

    // The answer of a body whose root answered `value`, leaving the context standing for the next body.
    public Outcome Close(long value) {
        var halt = Halt;

        Halt = Halt.None;

        return halt switch {
            Halt.None => Outcome.Finite(raw: value),
            Halt.Marked or Halt.Absent => Outcome.Absent,
            Halt.Forever => Outcome.Faulted(why: ExpressionFault.Forever),
            _ => Outcome.Faulted(why: ExpressionFault.Domain),
        };
    }
}

/// <summary>One node of a compiled closure tree: what one token of a postfix program, or one conjunct of a gate,
/// does once its operands are nodes of their own. Each kind of token is its own sealed node, chosen when the tree is
/// built, so evaluation dispatches on nothing a token carries.</summary>
internal abstract class ProgramNode {
    // How many nodes deep one evaluation of this node recurses at most, through every body it runs: the builder sets
    // it before the tree is published, and the machine stack an evaluation takes is proportional to it.
    internal int Depth { get; set; } = 1;

    // Answers the node's value, or stops the program through `context.Halt` and answers zero.
    internal abstract long Run(ref EvaluationFrame frame, ref ProgramContext context);

    // Runs one operand of an operator. A stop is the operator's at once; an absent value is remembered so the operands
    // after it still run, in postfix order, before the operator answers the absence.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    protected static bool Operand(ProgramNode node, ref EvaluationFrame frame, ref ProgramContext context, ref bool absent, out long value) {
        value = node.Run(frame: ref frame, context: ref context);

        switch (context.Halt) {
            case Halt.None:
                return true;
            case Halt.Marked:
                context.Halt = Halt.None;
                absent = true;

                return true;
            default:
                return false;
        }
    }
    // Runs a body for its truth: a nonzero number holds, zero or an absence does not. A fault stops the caller.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    protected static bool Truth(ProgramNode body, ref EvaluationFrame frame, ref ProgramContext context, out bool truth) {
        var answered = body.Run(frame: ref frame, context: ref context);

        switch (context.Halt) {
            case Halt.None:
                truth = (answered != 0L);

                return true;
            case Halt.Marked:
            case Halt.Absent:
                context.Halt = Halt.None;
                truth = false;

                return true;
            default:
                truth = false;

                return false;
        }
    }
    // Stops the program: an operator that consumed an absence answers the program's absence.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    protected static long Stop(ref ProgramContext context, Halt halt) {
        context.Halt = halt;

        return 0L;
    }
}

/// <summary>Builds the closure tree of a compiled program or gate. A program's tokens and a gate's tokens lower to
/// one family of nodes over one context, so a gate is a program whose leaves are comparisons.</summary>
internal static class ProgramClosures {
    /// <summary>Builds the tree a token program evaluates as in one kind.</summary>
    /// <param name="tokens">The postfix tokens.</param>
    /// <param name="kind">The kind the program evaluates in.</param>
    /// <param name="persistent">Whether bodies resolve through their own programs' kept trees; a tree built for one
    /// evaluation of tokens a caller rewrites builds its bodies alongside it.</param>
    /// <returns>The root node.</returns>
    internal static ProgramNode Build(ReadOnlySpan<CompiledExpressionToken> tokens, CellKind kind, bool persistent) => new Builder(persistent: persistent).Program(kind: kind, tokens: tokens);
    /// <summary>Builds the tree a gate decides as.</summary>
    /// <param name="gate">The postfix gate.</param>
    /// <returns>The root node, answering one for open and zero for closed, and stopping on a faulted conjunct.</returns>
    internal static ProgramNode BuildGate(ReadOnlySpan<GateToken> gate) => new Builder(persistent: true).Gate(gate: gate);

    /// <summary>The deepest a program's tree may recurse, through every call and fold body it reaches: a node is one
    /// frame of machine stack (a call's frame carries its arguments), so this bounds an evaluation's stack well inside
    /// any thread's. A chain of binary operations is one node, so only an expression nested as deep as its token
    /// ceiling inside calls nested as deep as theirs comes near it; a program deeper than this faults
    /// (<see cref="ExpressionFault.Domain"/>) wherever and on whichever thread it is evaluated.</summary>
    internal const int MaxDepth = 2048;

    private sealed class Builder(bool persistent) {
        private readonly Dictionary<(CompiledExpressionToken[], CellKind), ProgramNode>? m_bodies = (persistent ? null : []);

        // Pushes a node built over its operands (and the body it runs), recording how deep it recurses.
        private static void Add(List<ProgramNode> stack, ProgramNode node, ReadOnlySpan<ProgramNode> operands = default, ProgramNode? body = null) {
            var deepest = (body?.Depth ?? 0);

            if (node is ChainNode chain) {
                operands = chain.Operands;
            }
            foreach (var operand in operands) {
                deepest = Math.Max(val1: deepest, val2: operand.Depth);
            }

            node.Depth = (deepest + 1);
            stack.Add(item: node);
        }

        public ProgramNode Program(ReadOnlySpan<CompiledExpressionToken> tokens, CellKind kind) {
            var root = Unbounded(kind: kind, tokens: tokens);

            return ((root.Depth > MaxDepth)
                ? new FaultNode(children: [])
                : root);
        }

        private ProgramNode Unbounded(ReadOnlySpan<CompiledExpressionToken> tokens, CellKind kind) {
            var stack = new List<ProgramNode>(capacity: tokens.Length);

            foreach (ref readonly var token in tokens) {
                var carrier = (token.CarrierKind ?? kind);

                switch (token.Step) {
                    case ExpressionStep.Constant:
                        Add(node: new ConstantNode(value: token.Constant), stack: stack);

                        continue;
                    case ExpressionStep.Argument:
                        Add(node: ((((ulong)token.Constant) < RuleExpressions.MaxArguments)
                            ? new ArgumentNode(slot: ((int)token.Constant))
                            : new FaultNode(children: [])), stack: stack);

                        continue;
                    case ExpressionStep.Member:
                        Add(node: new MemberNode(), stack: stack);

                        continue;
                    case ExpressionStep.Read:
                        Add(node: new ReadNode(carrier: carrier, operand: token.Operand!), stack: stack);

                        continue;
                    case ExpressionStep.ReadAddressed: {
                            var feeds = Pop(count: token.Consumes, stack: stack);

                            Add(node: new ReadAddressedNode(carrier: carrier, feeds: feeds, operand: token.Operand!), operands: feeds, stack: stack);
                        }

                        continue;
                    case ExpressionStep.IsAbsent: {
                            var value = Pop(stack: stack);

                            Add(node: new IsAbsentNode(value: value), operands: [value], stack: stack);
                        }

                        continue;
                    case ExpressionStep.NotBody: {
                            var body = Body(body: token.Call!.Body, kind: kind);

                            Add(body: body, node: new BodyTruthNode(body: body, negated: true), stack: stack);
                        }

                        continue;
                    case ExpressionStep.Not: {
                            var value = Pop(stack: stack);

                            // `not` over a negated body is the body's truth: the shape a short-circuit's left operand takes.
                            if (value is BodyTruthNode { Negated: true } negated) {
                                Add(body: negated.Body, node: new BodyTruthNode(body: negated.Body, negated: false), stack: stack);
                            } else {
                                Add(node: new NotNode(value: value), operands: [value], stack: stack);
                            }

                            continue;
                        }
                    case ExpressionStep.And:
                    case ExpressionStep.Or: {
                            var body = Body(body: token.Call!.Body, kind: kind);
                            var left = Pop(stack: stack);

                            Add(body: body, node: new ShortCircuitNode(body: body, isAnd: (token.Step == ExpressionStep.And), left: left), operands: [left], stack: stack);
                        }

                        continue;
                    case ExpressionStep.Coalesce: {
                            var fallback = Pop(stack: stack);

                            var value = Pop(stack: stack);

                            Add(node: new CoalesceNode(fallback: fallback, value: value), operands: [value, fallback], stack: stack);

                            continue;
                        }
                    case ExpressionStep.Fold: {
                            var body = Body(body: token.Fold!.Body, kind: token.Fold.MemberKind);

                            Add(body: body, node: new FoldNode(body: body, fold: token.Fold), stack: stack);
                        }

                        continue;
                    case ExpressionStep.Call: {
                            var call = token.Call!;
                            var arguments = Pop(count: call.Arity, stack: stack);

                            if (call.Arity > RuleExpressions.MaxArguments) {
                                Add(node: new FaultNode(children: arguments), operands: arguments, stack: stack);
                            } else {
                                var body = Body(body: call.Body, kind: kind);

                                Add(body: body, node: new CallNode(arguments: arguments, body: body), operands: arguments, stack: stack);
                            }

                            continue;
                        }
                }

                var operands = Pop(count: token.Consumes, stack: stack);

                Add(node: Operator(carrier: carrier, operands: operands, token: in token), operands: operands, stack: stack);
            }

            if (stack.Count == 1) {
                return stack[0];
            }

            var leftover = new FaultNode(children: [.. stack]);

            Add(node: leftover, operands: stack.ToArray(), stack: []);

            return leftover;
        }

        public ProgramNode Gate(ReadOnlySpan<GateToken> gate) {
            var stack = new List<(ProgramNode Node, sbyte StopWhen)>(capacity: gate.Length);

            foreach (ref readonly var token in gate) {
                switch (token.Op) {
                    case GateOp.ShortCircuit: {
                            // The conjunct just finished decides its group once it answers the skip's truth.
                            var last = stack[^1];

                            stack[^1] = (last.Node, (sbyte)(token.SkipWhen ? 1 : 0));

                            continue;
                        }
                    case GateOp.Not:
                        stack[^1] = (new GateNotNode(value: stack[^1].Node), -1);

                        continue;
                    case GateOp.All:
                    case GateOp.Any: {
                            var start = (stack.Count - token.Arity);

                            if (start < 0) {
                                throw new InvalidOperationException(message: "A gate group consumes more conjuncts than precede it.");
                            }

                            var children = new ProgramNode[token.Arity];
                            var stops = new sbyte[token.Arity];

                            for (var index = 0; (index < token.Arity); index++) {
                                (children[index], stops[index]) = stack[(start + index)];
                            }

                            stack.RemoveRange(count: token.Arity, index: start);
                            stack.Add(item: (new GateGroupNode(children: children, isAll: (token.Op == GateOp.All), stops: stops), -1));

                            continue;
                        }
                }

                stack.Add(item: (Compare(token: in token), -1));
            }

            if (stack.Count == 1) {
                return stack[0].Node;
            }

            var leftover = new ProgramNode[stack.Count];

            for (var index = 0; (index < leftover.Length); index++) {
                leftover[index] = stack[index].Node;
            }

            return new GateSequenceNode(children: leftover);
        }

        // A call's subprogram, a fold's member body or a short-circuit operand: a kept tree of the body's own program,
        // or one built beside a tree that is not kept, shared by every site that reaches it so a chain of calls that
        // reaches one body twice at each level builds it once.
        private ProgramNode Body(CompiledProgram body, CellKind kind) {
            if (m_bodies is null) {
                return body.Root(kind: kind);
            }
            if (!m_bodies.TryGetValue(key: (body.Tokens, kind), value: out var node)) {
                node = Program(kind: kind, tokens: body.Tokens);
                m_bodies[(body.Tokens, kind)] = node;
            }

            return node;
        }

        private ProgramNode Compare(in GateToken token) {
            if (token.ValueKind == CellKind.Text) {
                return new GateTextCompareNode(token: token);
            }

            var left = Source(kind: token.ValueKind, source: token.LeftSource);
            var right = Source(kind: token.ValueKind, source: token.RightSource);

            // A side compared against a literal (`cell == 3`, the shape a sweep's gate takes most) reads one fact and
            // compares raw values through the comparison's kernel; Q48.16 orders as its raw bits do.
            if (right is LiteralSource literal) {
                return token.Comparison switch {
                    ExpressionOp.Equal => new GateLiteralCompareNode<EqualKernel>(left: left, literal: literal.Fact, token: token),
                    ExpressionOp.NotEqual => new GateLiteralCompareNode<NotEqualKernel>(left: left, literal: literal.Fact, token: token),
                    ExpressionOp.Less => new GateLiteralCompareNode<LessKernel>(left: left, literal: literal.Fact, token: token),
                    ExpressionOp.LessOrEqual => new GateLiteralCompareNode<LessOrEqualKernel>(left: left, literal: literal.Fact, token: token),
                    ExpressionOp.Greater => new GateLiteralCompareNode<GreaterKernel>(left: left, literal: literal.Fact, token: token),
                    ExpressionOp.GreaterOrEqual => new GateLiteralCompareNode<GreaterOrEqualKernel>(left: left, literal: literal.Fact, token: token),
                    _ => new GateCompareNode(left: left, right: right, token: token),
                };
            }

            return new GateCompareNode(left: left, right: right, token: token);
        }
        private static ValueSourceNode Source(in CompiledValueSource source, CellKind kind) {
            if (source.Operand is { } operand) {
                return ((operand.Addresses.IsEmpty && (operand is not IWordFoldOperand))
                    ? new PlainOperandSource(operand: operand)
                    : new AddressedOperandSource(operand: operand));
            }
            if (source.Expression is { } expression) {
                return new ExpressionSource(
                    computedKind: RuleEvaluation.ComputedKind(kind: kind, source: in source),
                    kind: kind,
                    program: expression
                );
            }

            return new LiteralSource(fact: Fact.Finite(kind: kind, value: source.RawValue));
        }

        private static ProgramNode Operator(in CompiledExpressionToken token, CellKind carrier, ProgramNode[] operands) {
            switch (token.Step) {
                case ExpressionStep.Clamp:
                    return new ClampNode(operands: Arity(count: 3, operands: operands));
                case ExpressionStep.Select:
                    return new SelectNode(operands: Arity(count: 3, operands: operands));
                case ExpressionStep.BitField:
                    return new BitFieldNode(operands: Arity(count: 3, operands: operands));
                case ExpressionStep.BitInsert:
                    return new BitInsertNode(operands: Arity(count: 4, operands: operands));
                case ExpressionStep.BoardShift:
                case ExpressionStep.BoardRay:
                case ExpressionStep.BoardImage:
                    return new BoardNode(
                        query: ((BoardNeighbourQuery)token.Board!),
                        step: token.Step,
                        value: Arity(count: 1, operands: operands)[0]
                    );
                case ExpressionStep.BoardSlide:
                    Arity(count: 2, operands: operands);

                    return new BoardSlideNode(
                        occupied: operands[1],
                        query: ((BoardNeighbourQuery)token.Board!),
                        sliders: operands[0]
                    );
                case ExpressionStep.Function:
                    if (operands.Length > RuleExpressions.MaxArguments) {
                        throw new InvalidOperationException(message: $"A function takes at most {RuleExpressions.MaxArguments} arguments; '{token.Operation}' takes {operands.Length}.");
                    }

                    return (ExpressionArithmetic.FunctionAdmits(operation: token.Operation, kind: carrier)
                        ? new FunctionNode(carrier: carrier, operands: operands, operation: token.Operation)
                        : new FaultNode(children: operands));
                case ExpressionStep.Unary:
                    return new UnaryNode(carrier: carrier, operation: token.Operation, value: Arity(count: 1, operands: operands)[0]);
            }

            Arity(count: 2, operands: operands);

            return Binary(carrier: carrier, left: operands[0], operation: token.Operation, right: operands[1]);
        }
        // The binary operators a sweep meets most answer through kernels of their own, each the operation's one
        // definition in ExpressionArithmetic; the rest through its one dispatch.
        // A binary operation whose left operand is another binary operation extends that one into a chain, which runs
        // the operands and operations in the postfix order without a frame for each; the rest stand alone.
        private static ProgramNode Binary(ExpressionOp operation, CellKind carrier, ProgramNode left, ProgramNode right) {
            var applied = Operation(carrier: carrier, operation: operation);

            if (left is ChainNode chain) {
                return chain.Then(operation: applied, right: right);
            }
            if (left is IBinaryNode binary) {
                return new ChainNode(first: binary.Left, operations: [binary.Operation, applied], rights: [binary.Right, right]);
            }

            return applied.Node(left: left, right: right);
        }
        // The binary operators a sweep meets most answer through kernels of their own, each the operation's one
        // definition in ExpressionArithmetic; the rest through its one dispatch.
        private static BinaryOperation Operation(ExpressionOp operation, CellKind carrier) {
            if (carrier is not (CellKind.Int or CellKind.Fixed)) {
                return new KernelOperation<ArithmeticKernel>(kernel: new ArithmeticKernel(carrier: carrier, operation: operation));
            }

            return operation switch {
                ExpressionOp.Add => KernelOperation<AddKernel>.Instance,
                ExpressionOp.Subtract => KernelOperation<SubtractKernel>.Instance,
                ExpressionOp.Equal => KernelOperation<EqualKernel>.Instance,
                ExpressionOp.NotEqual => KernelOperation<NotEqualKernel>.Instance,
                ExpressionOp.Less => KernelOperation<LessKernel>.Instance,
                ExpressionOp.LessOrEqual => KernelOperation<LessOrEqualKernel>.Instance,
                ExpressionOp.Greater => KernelOperation<GreaterKernel>.Instance,
                ExpressionOp.GreaterOrEqual => KernelOperation<GreaterOrEqualKernel>.Instance,
                _ => new KernelOperation<ArithmeticKernel>(kernel: new ArithmeticKernel(carrier: carrier, operation: operation)),
            };
        }
        private static ProgramNode[] Arity(int count, ProgramNode[] operands) => ((operands.Length == count)
            ? operands
            : throw new InvalidOperationException(message: $"An operator token consumes {operands.Length} values where its operation takes {count}."));

        private static ProgramNode Pop(List<ProgramNode> stack) => Pop(count: 1, stack: stack)[0];
        private static ProgramNode[] Pop(List<ProgramNode> stack, int count) {
            if (count == 0) {
                return [];
            }

            var start = (stack.Count - count);

            if (start < 0) {
                throw new InvalidOperationException(message: "A compiled program consumes more values than precede the token.");
            }

            var popped = stack.GetRange(count: count, index: start).ToArray();

            stack.RemoveRange(count: count, index: start);

            return popped;
        }
    }
}

internal sealed class ConstantNode(long value) : ProgramNode {
    public long Value { get; } = value;

    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) => Value;
}
internal sealed class ArgumentNode(int slot) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) => ((slot < context.ArgumentCount)
        ? context.Arguments[slot]
        : Stop(context: ref context, halt: Halt.Domain));
}
internal sealed class MemberNode : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) => (context.HasMember
        ? context.Member
        : Stop(context: ref context, halt: Halt.Domain));
}
// Runs its operands in order, then faults: a program that leaves other than one value, an argument slot no call can
// hand, an operation the carrier does not admit, or a call wider than a call's frame.
internal sealed class FaultNode(ProgramNode[] children) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var absent = false;

        foreach (var child in children) {
            if (!Operand(absent: ref absent, context: ref context, frame: ref frame, node: child, value: out _)) {
                return 0L;
            }
        }

        return Stop(context: ref context, halt: Halt.Domain);
    }
}
internal static class FactHalts {
    // A read's fact as a node's answer: a fault stops the program, an absence is a marked value.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static long Answer(in Fact fact, CellKind carrier, ref ProgramContext context) {
        switch (fact.Outcome.Case) {
            case FaultCase.Finite:
                return fact.ToRaw(kind: carrier);
            case FaultCase.Absent:
                context.Halt = Halt.Marked;

                return 0L;
            case FaultCase.Forever:
                context.Halt = Halt.Forever;

                return 0L;
            default:
                context.Halt = Halt.Domain;

                return 0L;
        }
    }
}
internal sealed class ReadNode(IRuleOperand operand, CellKind carrier) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        if (!frame.IsLive) {
            return Stop(context: ref context, halt: Halt.Domain);
        }

        var fact = operand.Read(
            frame: ref frame,
            keys: default
        );

        return FactHalts.Answer(carrier: carrier, context: ref context, fact: in fact);
    }
}
internal sealed class ReadAddressedNode(IRuleOperand operand, CellKind carrier, ProgramNode[] feeds) : ProgramNode {
    [SkipLocalsInit]
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        Unsafe.SkipInit(value: out FeedFrame feedFrame);
        Span<AddressFeed> fed = feedFrame;

        for (var slot = 0; (slot < feeds.Length); slot++) {
            var value = feeds[slot].Run(frame: ref frame, context: ref context);
            var absent = false;

            switch (context.Halt) {
                case Halt.None:
                    break;
                case Halt.Marked:
                    context.Halt = Halt.None;
                    absent = true;

                    break;
                default:
                    return 0L;
            }

            fed[slot] = new AddressFeed(Absent: absent, Value: value);
        }
        if (!frame.IsLive) {
            return Stop(context: ref context, halt: Halt.Domain);
        }

        var fact = RuleExpressions.ReadAddressed(
            feeds: fed[..feeds.Length],
            frame: ref frame,
            operand: operand
        );

        return FactHalts.Answer(carrier: carrier, context: ref context, fact: in fact);
    }

    [InlineArray(length: RuleExpressions.MaxAddresses)]
    private struct FeedFrame {
        private AddressFeed m_first;
    }
}
internal sealed class IsAbsentNode(ProgramNode value) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        _ = value.Run(frame: ref frame, context: ref context);

        switch (context.Halt) {
            case Halt.None:
                return 0L;
            case Halt.Marked:
                context.Halt = Halt.None;

                return 1L;
            default:
                return 0L;
        }
    }
}
internal sealed class NotNode(ProgramNode value) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var answered = value.Run(frame: ref frame, context: ref context);

        switch (context.Halt) {
            case Halt.None:
                return ((answered == 0L) ? 1L : 0L);
            case Halt.Marked:
                context.Halt = Halt.None;

                return 1L;
            default:
                return 0L;
        }
    }
}
// The truth of a body, or its negation: a body that answered a nonzero number holds, one that answered zero or an
// absence does not, and a fault is the program's.
internal sealed class BodyTruthNode(ProgramNode body, bool negated) : ProgramNode {
    public ProgramNode Body => body;
    public bool Negated => negated;

    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) => (Truth(body: body, context: ref context, frame: ref frame, truth: out var truth)
        ? ((truth != negated) ? 1L : 0L)
        : 0L);
}
// `and` runs its right body only when the truth on its left holds, `or` only when it fails; an absent right is false.
internal sealed class ShortCircuitNode(ProgramNode left, ProgramNode body, bool isAnd) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var answered = left.Run(frame: ref frame, context: ref context);
        bool truth;

        switch (context.Halt) {
            case Halt.None:
                truth = (answered != 0L);

                break;
            case Halt.Marked:
                context.Halt = Halt.None;
                truth = false;

                break;
            default:
                return 0L;
        }
        if ((isAnd == truth) && !Truth(body: body, context: ref context, frame: ref frame, truth: out truth)) {
            return 0L;
        }

        return (truth ? 1L : 0L);
    }
}
// Both sides run, as the postfix order had them; the fallback, with its own absence, stands in for an absent value.
internal sealed class CoalesceNode(ProgramNode value, ProgramNode fallback) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var answered = value.Run(frame: ref frame, context: ref context);
        var valueAbsent = false;

        switch (context.Halt) {
            case Halt.None:
                break;
            case Halt.Marked:
                context.Halt = Halt.None;
                valueAbsent = true;

                break;
            default:
                return 0L;
        }

        var standIn = fallback.Run(frame: ref frame, context: ref context);

        switch (context.Halt) {
            case Halt.None:
            case Halt.Marked:
                break;
            default:
                return 0L;
        }
        if (valueAbsent) {
            return standIn;
        }

        context.Halt = Halt.None;

        return answered;
    }
}
// Every cell the family's member row holds is visited once, in the arena's own member order, so the reduction is
// deterministic. The member body runs in the caller's context with the member bound, and the binding the caller had
// is restored on every way out.
internal sealed class FoldNode(CompiledFold fold, ProgramNode body) : ProgramNode {
    private readonly ExpressionOp m_operation = fold.Operation;
    private readonly CellKind m_memberKind = fold.MemberKind;
    private readonly int m_rowOrdinal = fold.RowOrdinal;

    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        if (
            !frame.IsLive ||
            (m_rowOrdinal < 0)
        ) {
            return Stop(context: ref context, halt: Halt.Domain);
        }

        var outerMember = context.Member;
        var outerHasMember = context.HasMember;
        var answered = Reduce(context: ref context, frame: ref frame);

        context.Member = outerMember;
        context.HasMember = outerHasMember;

        return answered;
    }

    private long Reduce(ref EvaluationFrame frame, ref ProgramContext context) {
        var operation = m_operation;
        var accumulator = ((operation == ExpressionOp.All)
            ? 1L
            : 0L
        );
        var arena = frame.Arena;
        var time = frame.Time;
        var cursor = 0;
        var choosing = (operation is ExpressionOp.ArgMinimum or ExpressionOp.ArgMaximum);
        var chosen = false;
        var best = 0L;

        context.HasMember = true;
        while (arena.TryNextLiveNumber(
            cursor: ref cursor,
            key: out _,
            rowOrdinal: m_rowOrdinal,
            time: in time,
            value: out var member
        )) {
            context.Member = member;

            var applied = body.Run(frame: ref frame, context: ref context);

            switch (context.Halt) {
                case Halt.None:
                    break;
                case Halt.Marked:
                case Halt.Absent:
                    // An arg fold weighs only the members it can score: an absent score is no candidate, never a fault.
                    if (choosing) {
                        context.Halt = Halt.None;

                        continue;
                    }

                    return Stop(context: ref context, halt: Halt.Absent);
                default:
                    return 0L;
            }
            if (choosing) {
                // Every score is in the body's one kind, so its raw encodings order as the values do; a tie keeps the
                // member the row's order reached first.
                if (!chosen || ((operation == ExpressionOp.ArgMinimum)
                    ? (applied < best)
                    : (applied > best))) {
                    chosen = true;
                    best = applied;
                    accumulator = member;
                }

                continue;
            }

            switch (operation) {
                case ExpressionOp.All:
                    if (applied == 0L) {
                        return 0L;
                    }

                    break;
                case ExpressionOp.Any:
                    if (applied != 0L) {
                        return 1L;
                    }

                    break;
                case ExpressionOp.Count:
                    accumulator += ((applied != 0L)
                        ? 1L
                        : 0L
                    );

                    break;
                default:
                    if (!ExpressionArithmetic.TryBinary(
                        ExpressionOp.Add,
                        m_memberKind,
                        accumulator,
                        applied,
                        out accumulator
                    )) {
                        return Stop(context: ref context, halt: Halt.Domain);
                    }

                    break;
            }
        }

        return ((choosing && !chosen)
            ? Stop(context: ref context, halt: Halt.Absent)
            : accumulator);
    }
}
// A call hands its body the values of its operands in a context of its own; an absent operand is the program's
// absence, and the body's absence or fault is the program's too.
internal sealed class CallNode(ProgramNode[] arguments, ProgramNode body) : ProgramNode {
    [SkipLocalsInit]
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        Unsafe.SkipInit(value: out ProgramContext called);

        var absent = false;

        for (var slot = 0; (slot < arguments.Length); slot++) {
            if (!Operand(absent: ref absent, context: ref context, frame: ref frame, node: arguments[slot], value: out called.Arguments[slot])) {
                return 0L;
            }
        }
        if (absent) {
            return Stop(context: ref context, halt: Halt.Absent);
        }

        called.ArgumentCount = arguments.Length;
        called.Member = context.Member;
        called.HasMember = context.HasMember;
        called.Halt = Halt.None;

        var answered = body.Run(frame: ref frame, context: ref called);

        switch (called.Halt) {
            case Halt.None:
                return answered;
            case Halt.Marked:
            case Halt.Absent:
                return Stop(context: ref context, halt: Halt.Absent);
            default:
                return Stop(context: ref context, halt: called.Halt);
        }
    }
}

/// <summary>A binary operation's one definition, specialized into the node that applies it: a kernel is a struct, so
/// a node generic over it runs the operation with no dispatch.</summary>
internal interface IBinaryKernel {
    bool TryApply(long left, long right, out long value);
}
internal readonly struct AddKernel : IBinaryKernel {
    public bool TryApply(long left, long right, out long value) => ExpressionArithmetic.TryAdd(left: left, right: right, value: out value);
}
internal readonly struct SubtractKernel : IBinaryKernel {
    public bool TryApply(long left, long right, out long value) => ExpressionArithmetic.TrySubtract(left: left, right: right, value: out value);
}
// The six comparisons, each answering the Int truth of its order over raw values (Q48.16 orders as its raw bits do).
internal readonly struct EqualKernel : IBinaryKernel {
    public bool TryApply(long left, long right, out long value) => Kernels.Answer(holds: (left == right), value: out value);
}
internal readonly struct NotEqualKernel : IBinaryKernel {
    public bool TryApply(long left, long right, out long value) => Kernels.Answer(holds: (left != right), value: out value);
}
internal readonly struct LessKernel : IBinaryKernel {
    public bool TryApply(long left, long right, out long value) => Kernels.Answer(holds: (left < right), value: out value);
}
internal readonly struct LessOrEqualKernel : IBinaryKernel {
    public bool TryApply(long left, long right, out long value) => Kernels.Answer(holds: (left <= right), value: out value);
}
internal readonly struct GreaterKernel : IBinaryKernel {
    public bool TryApply(long left, long right, out long value) => Kernels.Answer(holds: (left > right), value: out value);
}
internal readonly struct GreaterOrEqualKernel : IBinaryKernel {
    public bool TryApply(long left, long right, out long value) => Kernels.Answer(holds: (left >= right), value: out value);
}
// Every other binary operation through ExpressionArithmetic's one dispatch, in the carrier the token reads.
// Data-dependent arithmetic refusal can occur thousands of times in a dense sample; the checked semantics are kept
// without an exception per evaluation.
internal readonly struct ArithmeticKernel(ExpressionOp operation, CellKind carrier) : IBinaryKernel {
    public bool TryApply(long left, long right, out long value) => ExpressionArithmetic.TryBinary(operation, carrier, left, right, out value);
}
internal static class Kernels {
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static bool Answer(bool holds, out long value) {
        value = ExpressionArithmetic.Truth(holds: holds);

        return true;
    }
}
// A binary operation as a value: the kernel a chain applies, and the node that applies it alone.
internal abstract class BinaryOperation {
    public abstract bool TryApply(long left, long right, out long value);
    public abstract ProgramNode Node(ProgramNode left, ProgramNode right);
}
internal sealed class KernelOperation<TKernel>(TKernel kernel) : BinaryOperation where TKernel : struct, IBinaryKernel {
    public static KernelOperation<TKernel> Instance { get; } = new(kernel: default);

    public override bool TryApply(long left, long right, out long value) => kernel.TryApply(left: left, right: right, value: out value);
    public override ProgramNode Node(ProgramNode left, ProgramNode right) => ((right is ConstantNode constant)
        ? new BinaryConstantNode<TKernel>(kernel: kernel, left: left, operation: this, right: constant)
        : new BinaryNode<TKernel>(kernel: kernel, left: left, operation: this, right: right));
}
// A node applying one binary operation to two operands, which a chain extends.
internal interface IBinaryNode {
    ProgramNode Left { get; }
    BinaryOperation Operation { get; }
    ProgramNode Right { get; }
}
internal sealed class BinaryNode<TKernel>(ProgramNode left, TKernel kernel, BinaryOperation operation, ProgramNode right) : ProgramNode, IBinaryNode where TKernel : struct, IBinaryKernel {
    public ProgramNode Left => left;
    public BinaryOperation Operation => operation;
    public ProgramNode Right => right;

    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var absent = false;

        if (
            !Operand(absent: ref absent, context: ref context, frame: ref frame, node: left, value: out var leftValue) ||
            !Operand(absent: ref absent, context: ref context, frame: ref frame, node: right, value: out var rightValue)
        ) {
            return 0L;
        }
        if (absent) {
            return Stop(context: ref context, halt: Halt.Absent);
        }

        return (kernel.TryApply(left: leftValue, right: rightValue, value: out var value)
            ? value
            : Stop(context: ref context, halt: Halt.Domain));
    }
}
internal sealed class BinaryConstantNode<TKernel>(ProgramNode left, TKernel kernel, BinaryOperation operation, ConstantNode right) : ProgramNode, IBinaryNode where TKernel : struct, IBinaryKernel {
    private readonly long m_right = right.Value;

    public ProgramNode Left => left;
    public BinaryOperation Operation => operation;
    public ProgramNode Right => right;

    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var answered = left.Run(frame: ref frame, context: ref context);

        switch (context.Halt) {
            case Halt.None:
                break;
            case Halt.Marked:
                return Stop(context: ref context, halt: Halt.Absent);
            default:
                return 0L;
        }

        return (kernel.TryApply(left: answered, right: m_right, value: out var value)
            ? value
            : Stop(context: ref context, halt: Halt.Domain));
    }
}
// Binary operations each over the result before it (`a + b - c * d` read left to right as the postfix walk ran it):
// the first operand, then each right operand and the operation that takes it. An absent operand is the program's
// absence once the operation that consumes it runs, and a refused operation stops the chain before the next operand.
internal sealed class ChainNode(ProgramNode first, BinaryOperation[] operations, ProgramNode[] rights) : ProgramNode {
    // Every operand the chain runs, first among them, which is what its depth reads.
    public ProgramNode[] Operands { get; } = [first, .. rights];

    public ChainNode Then(BinaryOperation operation, ProgramNode right) => new(first: first, operations: [.. operations, operation], rights: [.. rights, right]);

    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var absent = false;

        if (!Operand(absent: ref absent, context: ref context, frame: ref frame, node: first, value: out var accumulated)) {
            return 0L;
        }

        for (var index = 0; (index < rights.Length); index++) {
            if (!Operand(absent: ref absent, context: ref context, frame: ref frame, node: rights[index], value: out var right)) {
                return 0L;
            }
            if (absent) {
                return Stop(context: ref context, halt: Halt.Absent);
            }
            if (!operations[index].TryApply(left: accumulated, right: right, value: out accumulated)) {
                return Stop(context: ref context, halt: Halt.Domain);
            }
        }

        return accumulated;
    }
}
internal sealed class UnaryNode(ExpressionOp operation, CellKind carrier, ProgramNode value) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var answered = value.Run(frame: ref frame, context: ref context);

        switch (context.Halt) {
            case Halt.None:
                break;
            case Halt.Marked:
                return Stop(context: ref context, halt: Halt.Absent);
            default:
                return 0L;
        }

        return (ExpressionArithmetic.TryUnary(operation, carrier, answered, out var unary)
            ? unary
            : Stop(context: ref context, halt: Halt.Domain));
    }
}
internal sealed class ClampNode(ProgramNode[] operands) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var absent = false;

        if (
            !Operand(absent: ref absent, context: ref context, frame: ref frame, node: operands[0], value: out var input) ||
            !Operand(absent: ref absent, context: ref context, frame: ref frame, node: operands[1], value: out var minimum) ||
            !Operand(absent: ref absent, context: ref context, frame: ref frame, node: operands[2], value: out var maximum)
        ) {
            return 0L;
        }
        if (absent) {
            return Stop(context: ref context, halt: Halt.Absent);
        }

        return (ExpressionArithmetic.TryClamp(input, minimum, maximum, out var clamped)
            ? clamped
            : Stop(context: ref context, halt: Halt.Domain));
    }
}
internal sealed class SelectNode(ProgramNode[] operands) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var absent = false;

        if (
            !Operand(absent: ref absent, context: ref context, frame: ref frame, node: operands[0], value: out var condition) ||
            !Operand(absent: ref absent, context: ref context, frame: ref frame, node: operands[1], value: out var whenTrue) ||
            !Operand(absent: ref absent, context: ref context, frame: ref frame, node: operands[2], value: out var whenFalse)
        ) {
            return 0L;
        }
        if (absent) {
            return Stop(context: ref context, halt: Halt.Absent);
        }

        return ((condition != 0L)
            ? whenTrue
            : whenFalse);
    }
}
internal sealed class BitFieldNode(ProgramNode[] operands) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var absent = false;

        if (
            !Operand(absent: ref absent, context: ref context, frame: ref frame, node: operands[0], value: out var input) ||
            !Operand(absent: ref absent, context: ref context, frame: ref frame, node: operands[1], value: out var offset) ||
            !Operand(absent: ref absent, context: ref context, frame: ref frame, node: operands[2], value: out var width)
        ) {
            return 0L;
        }
        if (absent) {
            return Stop(context: ref context, halt: Halt.Absent);
        }

        return (ExpressionArithmetic.TryBitField(field: out var field, offset: offset, value: input, width: width)
            ? field
            : Stop(context: ref context, halt: Halt.Domain));
    }
}
internal sealed class BitInsertNode(ProgramNode[] operands) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var absent = false;

        if (
            !Operand(absent: ref absent, context: ref context, frame: ref frame, node: operands[0], value: out var input) ||
            !Operand(absent: ref absent, context: ref context, frame: ref frame, node: operands[1], value: out var field) ||
            !Operand(absent: ref absent, context: ref context, frame: ref frame, node: operands[2], value: out var offset) ||
            !Operand(absent: ref absent, context: ref context, frame: ref frame, node: operands[3], value: out var width)
        ) {
            return 0L;
        }
        if (absent) {
            return Stop(context: ref context, halt: Halt.Absent);
        }

        return (ExpressionArithmetic.TryBitInsert(field: field, inserted: out var inserted, offset: offset, value: input, width: width)
            ? inserted
            : Stop(context: ref context, halt: Halt.Domain));
    }
}
internal sealed class BoardNode(BoardNeighbourQuery query, ExpressionStep step, ProgramNode value) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var mask = value.Run(frame: ref frame, context: ref context);

        switch (context.Halt) {
            case Halt.None:
                break;
            case Halt.Marked:
                return Stop(context: ref context, halt: Halt.Absent);
            default:
                return 0L;
        }

        return step switch {
            ExpressionStep.BoardShift => BoardQueries.ShiftMask(query, mask),
            ExpressionStep.BoardRay => BoardQueries.RayMask(query, mask),
            _ => BoardQueries.ImageOfMask(query.Topology, query.Direction, mask),
        };
    }
}
// The cells sliders attack over an occupancy mask (BoardQueries.SlideMask): the sliders, then the occupied cells.
internal sealed class BoardSlideNode(BoardNeighbourQuery query, ProgramNode sliders, ProgramNode occupied) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var from = sliders.Run(frame: ref frame, context: ref context);

        if (context.Halt != Halt.None) {
            return ((context.Halt == Halt.Marked) ? Stop(context: ref context, halt: Halt.Absent) : 0L);
        }

        var blocked = occupied.Run(frame: ref frame, context: ref context);

        if (context.Halt != Halt.None) {
            return ((context.Halt == Halt.Marked) ? Stop(context: ref context, halt: Halt.Absent) : 0L);
        }

        return BoardQueries.SlideMask(occupied: blocked, query: query, sliders: from);
    }
}
internal sealed class FunctionNode(ExpressionOp operation, CellKind carrier, ProgramNode[] operands) : ProgramNode {
    [SkipLocalsInit]
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        Unsafe.SkipInit(value: out ArgumentBlock block);
        Span<long> values = block;
        var absent = false;

        for (var slot = 0; (slot < operands.Length); slot++) {
            if (!Operand(absent: ref absent, context: ref context, frame: ref frame, node: operands[slot], value: out values[slot])) {
                return 0L;
            }
        }
        if (absent) {
            return Stop(context: ref context, halt: Halt.Absent);
        }

        return (ExpressionArithmetic.TryValidatedFunction(arguments: values[..operands.Length], kind: carrier, operation: operation, value: out var applied)
            ? applied
            : Stop(context: ref context, halt: Halt.Domain));
    }
}

// A side of a gate comparison: a literal, an operand's fact, or what a program computes.
internal abstract class ValueSourceNode {
    public abstract bool IsExpression { get; }

    public abstract Fact Read(ref EvaluationFrame frame);
}
internal sealed class LiteralSource(Fact fact) : ValueSourceNode {
    public Fact Fact { get; } = fact;
    public override bool IsExpression => false;

    public override Fact Read(ref EvaluationFrame frame) => Fact;
}
internal sealed class PlainOperandSource(IRuleOperand operand) : ValueSourceNode {
    public override bool IsExpression => false;

    public override Fact Read(ref EvaluationFrame frame) => operand.Read(frame: ref frame, keys: default);
}
internal sealed class AddressedOperandSource(IRuleOperand operand) : ValueSourceNode {
    public override bool IsExpression => false;

    public override Fact Read(ref EvaluationFrame frame) => RuleOperandReads.Read(frame: ref frame, operand: operand);
}
internal sealed class ExpressionSource(CompiledProgram program, CellKind computedKind, CellKind kind) : ValueSourceNode {
    public override bool IsExpression => true;

    public override Fact Read(ref EvaluationFrame frame) => RuleEvaluation.LandProgram(
        kind: kind,
        outcome: RuleExpressions.Evaluate(frame: ref frame, kind: computedKind, program: program)
    );
}
// One comparison of a gate. Both sides are read, so an absence on one side never hides a fault on the other; an
// absent side holds no comparison; a faulted side stops the gate undecided once the conjunct is narrated.
internal sealed class GateCompareNode(GateToken token, ValueSourceNode left, ValueSourceNode right) : ProgramNode {
    private readonly ExpressionOp m_comparison = token.Comparison;

    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var leftFact = left.Read(frame: ref frame);
        var rightFact = right.Read(frame: ref frame);
        var leftCase = leftFact.Outcome.Case;
        var rightCase = rightFact.Outcome.Case;

        // An absent side names no cell, so no comparison holds against it — not even NotEqual: "the top card is not a
        // king" must not read as true of an empty pile. Absence converts to false at a gate.
        var holds = (
            (leftCase is not (FaultCase.Absent or FaultCase.Faulted)) &&
            (rightCase is not (FaultCase.Absent or FaultCase.Faulted)) &&
            m_comparison.Holds(
                expected: FixedQ4816.FromRawBits(value: (rightFact.Outcome.TryFinite(raw: out var rightRaw) ? rightRaw : 0L)),
                expectedIsForever: (rightCase == FaultCase.Forever),
                value: FixedQ4816.FromRawBits(value: (leftFact.Outcome.TryFinite(raw: out var leftRaw) ? leftRaw : 0L)),
                valueIsForever: (leftCase == FaultCase.Forever)
            )
        );

        if (frame.Trace is { } trace) {
            trace.Add(item: $"{token.Describe}: {GateNarration.Spell(fact: in leftFact, read: (!left.IsExpression || (leftCase == FaultCase.Finite)))} {m_comparison.Symbol()} {GateNarration.Spell(fact: in rightFact, read: (!right.IsExpression || (rightCase == FaultCase.Finite)))} -> {GateNarration.Spell(value: holds)}");
        }
        // A faulted comparison has no truth value to negate or combine.
        if ((leftCase == FaultCase.Faulted) || (rightCase == FaultCase.Faulted)) {
            return Stop(context: ref context, halt: Halt.Domain);
        }

        return (holds ? 1L : 0L);
    }
}
// A comparison whose right side is a literal: the one fact read on the left decides it, as the general comparison
// would, with the literal narrated as its own fact.
internal sealed class GateLiteralCompareNode<TKernel>(GateToken token, ValueSourceNode left, Fact literal) : ProgramNode where TKernel : struct, IBinaryKernel {
    private readonly long m_literal = (literal.Outcome.TryFinite(raw: out var raw) ? raw : 0L);
    // What the comparison answers when the left side is positive infinity: the literal is finite, so only the order of
    // the two decides it.
    private readonly bool m_foreverHolds = token.Comparison.Holds(
        expected: FixedQ4816.FromRawBits(value: (literal.Outcome.TryFinite(raw: out var expected) ? expected : 0L)),
        expectedIsForever: false,
        value: FixedQ4816.Zero,
        valueIsForever: true
    );

    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var leftFact = left.Read(frame: ref frame);
        var leftCase = leftFact.Outcome.Case;
        bool holds;

        if (leftFact.Outcome.TryFinite(raw: out var value)) {
            // A comparison kernel always answers.
            _ = default(TKernel).TryApply(left: value, right: m_literal, value: out var truth);
            holds = (truth != 0L);
        } else {
            holds = ((leftCase == FaultCase.Forever) && m_foreverHolds);
        }
        if (frame.Trace is { } trace) {
            trace.Add(item: $"{token.Describe}: {GateNarration.Spell(fact: in leftFact, read: (!left.IsExpression || (leftCase == FaultCase.Finite)))} {token.Comparison.Symbol()} {GateNarration.Spell(fact: in literal, read: true)} -> {GateNarration.Spell(value: holds)}");
        }

        return ((leftCase == FaultCase.Faulted)
            ? Stop(context: ref context, halt: Halt.Domain)
            : (holds ? 1L : 0L));
    }
}
internal sealed class GateTextCompareNode(GateToken token) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var reader = frame.Reader;
        var leftSource = token.LeftSource;
        var rightSource = token.RightSource;
        var leftResolved = ((leftSource.Text is { } leftLiteral) ? Resolved<string>.Named(value: leftLiteral) : RuleEvaluation.ReadText(reader: reader, source: leftSource.Operand));
        var rightResolved = ((rightSource.Text is { } rightLiteral) ? Resolved<string>.Named(value: rightLiteral) : RuleEvaluation.ReadText(reader: reader, source: rightSource.Operand));
        var leftNamed = leftResolved.TryNamed(value: out var leftValue);
        var rightNamed = rightResolved.TryNamed(value: out var rightValue);
        var leftText = (leftValue ?? string.Empty);
        var rightText = (rightValue ?? string.Empty);
        var equal = string.Equals(a: leftText, b: rightText, comparisonType: StringComparison.Ordinal);
        var holds = (leftNamed && rightNamed &&
            ((token.Comparison == ExpressionOp.Equal) ? equal : !equal));

        frame.Trace?.Add(item: $"{token.Describe}: \"{leftText}\" {token.Comparison.Symbol()} \"{rightText}\" -> {GateNarration.Spell(value: holds)}");
        if ((leftResolved.Case == FaultCase.Faulted) || (rightResolved.Case == FaultCase.Faulted)) {
            return Stop(context: ref context, halt: Halt.Domain);
        }

        return (holds ? 1L : 0L);
    }
}
internal sealed class GateNotNode(ProgramNode value) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var answered = value.Run(frame: ref frame, context: ref context);

        if (context.Halt != Halt.None) {
            return 0L;
        }

        var inverted = (answered == 0L);

        frame.Trace?.Add(item: $"not -> {GateNarration.Spell(value: inverted)}");

        return (inverted ? 1L : 0L);
    }
}
// A conjunction or disjunction of conjuncts, in order. A conjunct the compiler marked as deciding its group (the
// short-circuit it emitted after every conjunct but the last) ends the group once it answers that truth, unnarrated;
// a group that runs to its end narrates its answer.
internal sealed class GateGroupNode(ProgramNode[] children, sbyte[] stops, bool isAll) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        var result = isAll;

        for (var index = 0; (index < children.Length); index++) {
            var answered = children[index].Run(frame: ref frame, context: ref context);

            if (context.Halt != Halt.None) {
                return 0L;
            }

            var answer = (answered != 0L);

            if ((stops[index] >= 0) && (answer == (stops[index] == 1))) {
                return (answer ? 1L : 0L);
            }

            result = (isAll
                ? (result && answer)
                : (result || answer));
        }

        frame.Trace?.Add(item: $"{(isAll ? "all" : "any")} of {children.Length.ToString(provider: CultureInfo.InvariantCulture)} -> {GateNarration.Spell(value: result)}");

        return (result ? 1L : 0L);
    }
}
// Conjuncts no group consumed: each runs in order, and the gate is closed unless one faults.
internal sealed class GateSequenceNode(ProgramNode[] children) : ProgramNode {
    internal override long Run(ref EvaluationFrame frame, ref ProgramContext context) {
        foreach (var child in children) {
            _ = child.Run(frame: ref frame, context: ref context);

            if (context.Halt != Halt.None) {
                return 0L;
            }
        }

        return 0L;
    }
}
internal static class GateNarration {
    public static string Spell(bool value) => (value
        ? "true"
        : "false"
    );
    public static string Spell(in Fact fact, bool read) => (read
        ? RuleEvaluation.DescribeFact(fact: in fact)
        : "refused"
    );
}
