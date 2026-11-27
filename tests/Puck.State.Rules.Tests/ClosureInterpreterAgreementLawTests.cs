using System.Globalization;

using Puck.Maths;

using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>The closure trees answer what the token interpreter answered: every program of a corpus that reaches each
/// token step (reads, computed addresses, absences answered and consumed, short circuits, folds, calls, faults) and
/// every gate of a seeded set of comparison trees, over seeded states, give equal outcomes, verdicts and traces.</summary>
public sealed class ClosureInterpreterAgreementLawTests {
    private const int States = 96;
    private const int GatesPerState = 48;

    private static readonly (string Text, CellKind Kind)[] Corpus = [
        ("a + b", CellKind.Int),
        ("a - b * c", CellKind.Int),
        ("a / b", CellKind.Int),
        ("a % b", CellKind.Int),
        ("(a + 1) * (b - 2)", CellKind.Int),
        ("minimum(a, b) + maximum(b, c)", CellKind.Int),
        ("clamp(a, b, c)", CellKind.Int),
        ("a < b ? a : b", CellKind.Int),
        ("c ? a : b", CellKind.Int),
        ("a == b", CellKind.Int),
        ("a != b", CellKind.Int),
        ("a <= b", CellKind.Int),
        ("a >= 3", CellKind.Int),
        ("3 > a", CellKind.Int),
        ("-a", CellKind.Int),
        ("absolute(a)", CellKind.Int),
        ("sign(a - b)", CellKind.Int),
        ("a & b", CellKind.Int),
        ("a | c", CellKind.Int),
        ("a ^ b", CellKind.Int),
        ("a << 3", CellKind.Int),
        ("a << b", CellKind.Int),
        ("a >> 1", CellKind.Int),
        ("bitField(a, 2, 4)", CellKind.Int),
        ("bitField(a, b, c)", CellKind.Int),
        ("bitInsert(a, b, 4, 8)", CellKind.Int),
        ("setBitCount(a)", CellKind.Int),
        ("rotateLeft(a, 3)", CellKind.Int),
        ("a > 0 and b > 0", CellKind.Int),
        ("a > 0 or b / c > 1", CellKind.Int),
        ("a > 0 and b / c > 1", CellKind.Int),
        ("not (a > b)", CellKind.Int),
        ("not a", CellKind.Int),
        ("a and b", CellKind.Int),
        ("not (a / c > 0) or b == 1", CellKind.Int),
        ("isAbsent(pile[0])", CellKind.Int),
        ("pile[0] ?? 9", CellKind.Int),
        ("pile[2] ?? a", CellKind.Int),
        ("scores[a] ?? -1", CellKind.Int),
        ("scores[a + b] + 1", CellKind.Int),
        ("isAbsent(scores[a])", CellKind.Int),
        ("count(scores, s -> s > 1)", CellKind.Int),
        ("sum(scores, s -> s * a)", CellKind.Int),
        ("all(scores, s -> s > 0)", CellKind.Int),
        ("any(scores, s -> s < b)", CellKind.Int),
        ("argmin(scores, s -> s)", CellKind.Int),
        ("argmax(scores, s -> s - a)", CellKind.Int),
        ("count(scores, s -> s > 1) + minimum(scores[0], 4)", CellKind.Int),
        ("sum(scores, s -> s / a)", CellKind.Int),
        ("greatestCommonDivisor(a, b)", CellKind.Int),
        ("power(a, 3)", CellKind.Int),
        ("factorial(c)", CellKind.Int),
        ("hash(a, b)", CellKind.Int),
        ("a * 4611686018427387904", CellKind.Int),
        ("a / (b - b)", CellKind.Int),
        ("f * g", CellKind.Fixed),
        ("f / g", CellKind.Fixed),
        ("f + 0.5", CellKind.Fixed),
        ("squareRoot(absolute(f))", CellKind.Fixed),
        ("floor(f)", CellKind.Fixed),
        ("f < g ? f : g", CellKind.Fixed),
        ("f - g * 2", CellKind.Fixed),
    ];

    [Fact]
    public void ClosureTreesAnswerWhatTheInterpreterAnswered() {
        var random = Pcg32XshRr.Create(state: 0x5EED_C105_0000_0001UL, stream: 7UL);
        var compared = 0;
        var gates = 0;

        for (var sample = 0; (sample < States); sample++) {
            var (host, context) = Arrange(random: ref random);

            foreach (var (text, kind) in Corpus) {
                compared += Agree(context: context, host: host, kind: kind, label: text, tokens: Compile(context: context, kind: kind, program: ExpressionProgram.Parse(text: text)));
            }
            foreach (var (label, program) in BuiltPrograms()) {
                compared += Agree(context: context, host: host, kind: CellKind.Int, label: label, tokens: Compile(context: context, kind: CellKind.Int, program: program));
            }
            for (var index = 0; (index < GatesPerState); index++) {
                var predicate = RandomPredicate(depth: 0, random: ref random);
                CompiledRule compiled;

                try {
                    compiled = RuleCompiler.Compile(new Rule(RulesFixture.Name(value: "agree"), [], Gate: predicate), context, requireEffects: false);
                } catch (RuleException) {
                    continue;
                }

                var closureTrace = new List<string>();
                var interpretedTrace = new List<string>();
                var closureFrame = new EvaluationFrame(reader: host);
                var interpretedFrame = new EvaluationFrame(reader: host);
                var closure = RuleEvaluation.Decide(ref closureFrame, compiled.Gate, closureTrace);
                var interpreted = RuleEvaluation.DecideInterpreted(ref interpretedFrame, compiled.Gate.Tokens, interpretedTrace);

                Assert.True(condition: (closure == interpreted), userMessage: $"{predicate}: closure {closure}, interpreter {interpreted}");
                Assert.Equal(expected: interpretedTrace, actual: closureTrace);
                gates++;
            }
        }

        Assert.True(condition: (compared > (States * Corpus.Length)), userMessage: compared.ToString(provider: CultureInfo.InvariantCulture));
        Assert.True(condition: (gates > (States * (GatesPerState / 2))), userMessage: gates.ToString(provider: CultureInfo.InvariantCulture));
    }

    // Each path a program can reach: the kept tree through the program, the frame a firing evaluates in, and a tree
    // built for one evaluation of a span.
    private static int Agree(ArenaEffectHost host, RuleCompileContext context, CellKind kind, string label, CompiledExpressionToken[]? tokens) {
        _ = context;

        if (tokens is null) {
            return 0;
        }

        var expected = RuleExpressions.EvaluateInterpreted(kind: kind, program: tokens, reader: host);
        var program = new CompiledProgram(tokens: tokens);
        var frame = new EvaluationFrame(reader: host);

        Assert.True(condition: (RuleExpressions.Evaluate(frame: ref frame, kind: kind, program: program) == expected), userMessage: $"{label}: live");
        Assert.True(condition: (RuleExpressions.EvaluateDetached(kind: kind, program: program, reader: host) == expected), userMessage: $"{label}: kept {RuleExpressions.EvaluateDetached(kind: kind, program: program, reader: host)} vs {expected}");
        Assert.True(condition: (RuleExpressions.EvaluateDetached(kind: kind, program: new ReadOnlySpan<CompiledExpressionToken>(array: tokens), reader: host) == expected), userMessage: $"{label}: span");

        return 1;
    }
    private static CompiledExpressionToken[]? Compile(RuleCompileContext context, ExpressionProgram program, CellKind kind) {
        try {
            return RuleCompiler.CompileExpression(context: context, expression: program, kind: kind, ruleName: "agree", verb: "agree");
        } catch (RuleException) {
            return null;
        } catch (FormatException) {
            return null;
        }
    }

    // Programs the text cannot spell: an absent zone end consumed, answered and coalesced, and calls into shared
    // subprograms, one of which folds with its caller's argument.
    private static IEnumerable<(string Label, ExpressionProgram Program)> BuiltPrograms() {
        var absent = Instruction.Operand(key: "$zone:hand:first", name: "pile");

        yield return ("zone end consumed", new ExpressionProgram(Instructions: [absent, Instruction.Constant(value: 1m), Instruction.Of(operation: ExpressionOp.Add)]));
        yield return ("zone end answered", new ExpressionProgram(Instructions: [absent, Instruction.Of(operation: ExpressionOp.IsAbsent)]));
        yield return ("zone end coalesced", new ExpressionProgram(Instructions: [absent, Instruction.Constant(value: 9m), Instruction.Of(operation: ExpressionOp.Coalesce)]));
        yield return ("zone end coalesced absent", new ExpressionProgram(Instructions: [absent, absent, Instruction.Of(operation: ExpressionOp.Coalesce), Instruction.Of(operation: ExpressionOp.IsAbsent)]));
        yield return ("zone end negated", new ExpressionProgram(Instructions: [absent, Instruction.Of(operation: ExpressionOp.Not)]));
        yield return ("call", new ExpressionProgram(Instructions: [
            Instruction.Operand(name: "a"),
            Instruction.Operand(name: "b"),
            Instruction.Call(subprogram: 0),
        ]) {
            Subprograms = [new Subprogram(
                Arity: 2,
                Instructions: [
                    Instruction.Argument(index: 0),
                    Instruction.Argument(index: 1),
                    Instruction.Of(operation: ExpressionOp.Divide),
                ],
                Name: "over"
            )],
        });
        yield return ("call with absent operand", new ExpressionProgram(Instructions: [
            absent,
            Instruction.Operand(name: "b"),
            Instruction.Call(subprogram: 0),
        ]) {
            Subprograms = [new Subprogram(
                Arity: 2,
                Instructions: [
                    Instruction.Argument(index: 0),
                    Instruction.Argument(index: 1),
                    Instruction.Of(operation: ExpressionOp.Subtract),
                ],
                Name: "minus"
            )],
        });
        yield return ("fold in call", new ExpressionProgram(Instructions: [
            Instruction.Operand(name: "c"),
            Instruction.Call(subprogram: 0),
        ]) {
            Subprograms = [
                new Subprogram(
                    Arity: 1,
                    Instructions: [Instruction.Fold(binder: "s", family: "scores", operation: ExpressionOp.Sum, subprogram: 1)],
                    Name: "shifted"
                ),
                new Subprogram(
                    Arity: 0,
                    Instructions: [
                        Instruction.Of(operation: ExpressionOp.Member),
                        Instruction.Argument(index: 0),
                        Instruction.Of(operation: ExpressionOp.Multiply),
                    ],
                    Name: "body"
                ),
            ],
        });
        yield return ("call in fold", new ExpressionProgram(Instructions: [Instruction.Fold(
            binder: "s",
            family: "scores",
            operation: ExpressionOp.Count,
            subprogram: 0
        )]) {
            Subprograms = [
                new Subprogram(
                    Arity: 0,
                    Instructions: [Instruction.Of(operation: ExpressionOp.Member), Instruction.Call(subprogram: 1)],
                    Name: "body"
                ),
                new Subprogram(
                    Arity: 1,
                    Instructions: [Instruction.Argument(index: 0), Instruction.Constant(value: 0m), Instruction.Of(operation: ExpressionOp.Greater)],
                    Name: "positive"
                ),
            ],
        });
    }

    private static ActionPredicate RandomPredicate(int depth, ref Pcg32XshRr random) {
        var shape = ((depth >= 3) ? 0U : Below(random: ref random, count: 6U));

        switch (shape) {
            case 3:
            case 4: {
                    var count = ((int)(1U + Below(random: ref random, count: 3U)));
                    var children = new ActionPredicate[count];

                    for (var index = 0; (index < count); index++) {
                        children[index] = RandomPredicate(depth: (depth + 1), random: ref random);
                    }

                    return ((shape == 3) ? new ActionPredicate.All(Predicates: children) : new ActionPredicate.Any(Predicates: children));
                }
            case 5:
                return new ActionPredicate.Not(Predicate: RandomPredicate(depth: (depth + 1), random: ref random));
        }

        ExpressionOp[] comparisons = [ExpressionOp.Equal, ExpressionOp.NotEqual, ExpressionOp.Less, ExpressionOp.LessOrEqual, ExpressionOp.Greater, ExpressionOp.GreaterOrEqual];
        var comparison = comparisons[Below(random: ref random, count: ((uint)comparisons.Length))];
        string[] slots = ["a", "b", "c"];

        return Below(random: ref random, count: 3U) switch {
            0U => new ActionPredicate.CompareState(slots[Below(random: ref random, count: 3U)], comparison, Value: (((decimal)Below(random: ref random, count: 9U)) - 3m)),
            1U => new ActionPredicate.CompareState(slots[Below(random: ref random, count: 3U)], comparison, ComparandState: slots[Below(random: ref random, count: 3U)]),
            _ => new ActionPredicate.CompareValue(
                Comparison: comparison,
                Left: ExpressionProgram.Parse(text: Corpus[Below(random: ref random, count: 50U)].Text),
                Right: ExpressionProgram.Parse(text: Corpus[Below(random: ref random, count: 50U)].Text)
            ),
        };
    }

    private static uint Below(ref Pcg32XshRr random, uint count) => random.NextUInt32(minimum: 0U, maximum: (count - 1U));
    private static (ArenaEffectHost Host, RuleCompileContext Context) Arrange(ref Pcg32XshRr random) {
        long Small(ref Pcg32XshRr r) => (((long)Below(random: ref r, count: 12U)) - 4L);
        long Wide(ref Pcg32XshRr r) => ((Below(random: ref r, count: 8U) == 0U) ? (1L << 62) : Small(ref r));

        StateRow Slot(string name, CellValue value, CellKind kind) => new(RulesFixture.Name(value: name), kind, Cells: [new StateCell(Key: StateRow.SlotKey, Value: value)]);
        StateCell Keyed(int index, long value) => new(RulesFixture.Name(value: index.ToString(provider: CultureInfo.InvariantCulture)), CellValue.Int(value: value));

        var a = Wide(ref random);
        var b = Small(ref random);
        var c = Small(ref random);
        var f = FixedQ4816.FromRawBits(value: (((long)random.NextUInt32()) - (1L << 31))).Value;
        var g = FixedQ4816.FromRawBits(value: (((long)Below(random: ref random, count: (1U << 20))) - (1L << 19))).Value;
        var scoreCount = ((int)Below(random: ref random, count: 5U));
        var scores = new StateCell[scoreCount];

        for (var index = 0; (index < scoreCount); index++) {
            scores[index] = Keyed(index: index, value: Small(ref random));
        }

        var pileCount = ((int)Below(random: ref random, count: 4U));
        var pile = new StateCell[pileCount];

        for (var index = 0; (index < pileCount); index++) {
            pile[index] = Keyed(index: index, value: Small(ref random));
        }

        var hand = (((pileCount > 0) && (Below(random: ref random, count: 2U) == 0U))
            ? (StateCell[])[new StateCell(RulesFixture.Name(value: "0"), CellValue.Bool(value: true))]
            : []);
        var section = new StateSection(Rows: [
            Slot(name: "a", value: CellValue.Int(value: a), kind: CellKind.Int),
            Slot(name: "b", value: CellValue.Int(value: b), kind: CellKind.Int),
            Slot(name: "c", value: CellValue.Int(value: c), kind: CellKind.Int),
            Slot(name: "f", value: CellValue.Fixed(rawBits: f), kind: CellKind.Fixed),
            Slot(name: "g", value: CellValue.Fixed(rawBits: g), kind: CellKind.Fixed),
            new StateRow(RulesFixture.Name(value: "scores"), CellKind.Int, Capacity: 4, Cells: scores),
            new StateRow(RulesFixture.Name(value: "pile"), CellKind.Int, Capacity: 3, Cells: pile),
            new StateRow(RulesFixture.Name(value: "hand"), CellKind.Bool, Capacity: 3, Domain: new StateDomain.KeysOf(RulesFixture.Name(value: "pile"), Ordered: true), Cells: hand),
        ]);
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog: catalog, options: null, section: section, time: ArenaTime.Origin);

        return (new ArenaEffectHost(arena: arena), new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core));
    }
}
