using System.Reflection;
using Xunit;

namespace Puck.State.Tests;

/// <summary>The expression IR's own laws: one operator table row per operation, a closed payload union, the exact
/// comparison a fractional literal against an Int operand lowers to, folds and subprogram calls, and absence as a
/// value an author answers rather than a refusal the compiler picks.</summary>
public sealed class ExpressionIrLawTests {
    private static CellName Name(string text) => CellName.Parse(candidate: text);
    private static StateRow Slot(string name, long value) => new(
        Name(text: name),
        CellKind.Int,
        Cells: [new StateCell(
            Key: StateRow.SlotKey,
            Value: CellValue.Int(value: value)
        )]
    );
    private static StateRow FixedRow(string name, params long[] rawBits) => new(
        Name(text: name),
        CellKind.Fixed,
        Capacity: Math.Max(
            val1: 1,
            val2: rawBits.Length
        ),
        Cells: rawBits.Select(selector: (value, index) => new StateCell(
            Name(text: index.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)),
            CellValue.Fixed(rawBits: value)
        )).ToArray()
    );
    private static StateRow Row(string name, params long[] values) => new(
        Name(text: name),
        CellKind.Int,
        Capacity: Math.Max(
            val1: 1,
            val2: values.Length
        ),
        Cells: values.Select(selector: (value, index) => new StateCell(
            Name(text: index.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)),
            CellValue.Int(value: value)
        )).ToArray()
    );

    // Every operation the evaluator can meet has exactly one row, so an operation's arity, kinds, cost and spelling
    // are answered in one place and no operation can be described twice.
    [Fact]
    public void EveryOperationHasExactlyOneOperatorTableRow() {
        var rows = ExpressionOperators.All;

        Assert.Equal(
            actual: rows.Select(selector: static row => row.Operation).Distinct().Count(),
            expected: rows.Count
        );
        foreach (var operation in Enum.GetValues<ExpressionOp>()) {
            Assert.NotNull(@object: ExpressionOperators.Find(operation: operation));
        }
        Assert.Equal(
            actual: rows.Count,
            expected: Enum.GetValues<ExpressionOp>().Length
        );
    }
    // The payload hierarchy is closed the way every union in this tree is: the marker, nested sealed cases only,
    // and a private constructor no case outside the file can reach.
    [Theory]
    [InlineData(typeof(InstructionPayload))]
    [InlineData(typeof(VectorOperand))]
    public void APayloadUnionIsClosed(Type union) {
        Assert.NotNull(@object: union.GetCustomAttribute<UnionAttribute>());
        Assert.True(condition: union.IsAbstract);

        var cases = union.Assembly.GetTypes().Where(predicate: type => type.IsSubclassOf(c: union)).ToArray();

        Assert.NotEmpty(collection: cases);
        foreach (var arm in cases) {
            Assert.True(
                condition: (arm.IsSealed && (arm.DeclaringType == union)),
                userMessage: $"{arm.Name} is a case of {union.Name} declared outside it"
            );
        }
        foreach (var constructor in union.GetConstructors(bindingAttr: BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            Assert.True(
                condition: (constructor.IsPrivate || constructor.IsFamilyAndAssembly || constructor.GetParameters().Any(predicate: parameter => (parameter.ParameterType == union))),
                userMessage: $"{union.Name} has a constructor a case outside it could reach"
            );
        }
    }
    // The three vector functions are ordinary rows of the one operator table, distinguished only by the payload
    // their instruction carries: they read their two operands from that payload instead of the stack, and they
    // stay out of the call dictionary because their operands are not scalar expressions.
    [InlineData(ExpressionOp.Dot, "dot", ExpressionSignature.Int)]
    [InlineData(ExpressionOp.Similarity, "similarity", ExpressionSignature.Fixed)]
    [InlineData(ExpressionOp.Identical, "identical", ExpressionSignature.Int)]
    [Theory]
    public void AVectorFunctionIsAnOperatorTableRowOverAnInstructionPayload(ExpressionOp operation, string spelling, ExpressionSignature signature) {
        var row = ExpressionOperators.Find(operation: operation);

        Assert.NotNull(@object: row);
        Assert.Equal(
            actual: row!.Name,
            expected: spelling
        );
        Assert.Equal(
            actual: row.Signature,
            expected: signature
        );
        Assert.Equal(
            actual: row.Arity,
            expected: 0
        );
        Assert.Equal(
            actual: row.Payload,
            expected: PayloadShape.Vector
        );
        Assert.DoesNotContain(
            collection: ExpressionOperators.Calls.Keys,
            filter: candidate => string.Equals(
                a: candidate,
                b: spelling,
                comparisonType: StringComparison.Ordinal
            )
        );

        var instruction = Instruction.Vector(
            left: new VectorOperand.Cell(Key: "a", Name: "vectors"),
            operation: operation,
            right: new VectorOperand.Literal(Value: "AAAA")
        );

        Assert.Equal(
            actual: ExpressionOperators.PayloadOf(operation: instruction.Operation),
            expected: PayloadShape.Vector
        );
        Assert.IsType<InstructionPayload.Vector>(@object: instruction.Payload);
    }
    // Print-then-parse is the identity: a program prints to a spelling that parses back to exactly it.
    [Theory]
    [InlineData("hp")]
    [InlineData("hp[$each]")]
    [InlineData("hp - minimum(damage, hp)")]
    [InlineData("a > 1 ? b : c")]
    [InlineData("a ?? 0")]
    [InlineData("isAbsent(a) * 2")]
    [InlineData("(a & b) == 0 ? 1 : 0")]
    [InlineData("all(scores, s -> s == 0)")]
    [InlineData("sum(scores, s -> s * 2)")]
    [InlineData("count(scores, s -> s > 0) + any(scores, s -> s < 0)")]
    [InlineData("dot(memories[north], stance)")]
    [InlineData("similarity(stance, vector(\"AAAA\"))")]
    [InlineData("identical(memories[north], memories[east])")]
    public void PrintingIsTheInverseOfParsing(string text) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                program: out var program,
                text: text
            ),
            userMessage: error
        );
        Assert.Equal(
            actual: ExpressionSpelling.Print(program: program),
            expected: text
        );

        Assert.True(condition: ExpressionSpelling.TryParse(
            error: out _,
            program: out var reparsed,
            text: ExpressionSpelling.Print(program: program)
        ));
        Assert.Equal(
            actual: reparsed.Instructions,
            expected: program.Instructions
        );
        Assert.Equal(
            actual: reparsed.Subprograms.Select(selector: static subprogram => subprogram.Instructions),
            expected: program.Subprograms.Select(selector: static subprogram => subprogram.Instructions)
        );
    }
    // A fractional literal compared against an Int operand is lowered to the exact integer comparison: the same
    // rule `compareState` lowers a constant comparand by, so the two spellings of one comparison agree.
    [Theory]
    [InlineData("score > 2.5", 2L, false)]
    [InlineData("score > 2.5", 3L, true)]
    [InlineData("score >= 2.5", 2L, false)]
    [InlineData("score >= 2.5", 3L, true)]
    [InlineData("score < 2.5", 2L, true)]
    [InlineData("score < 2.5", 3L, false)]
    [InlineData("score <= 2.5", 2L, true)]
    [InlineData("score <= 2.5", 3L, false)]
    [InlineData("score == 2.5", 2L, false)]
    [InlineData("score == 2.5", 3L, false)]
    [InlineData("score != 2.5", 2L, true)]
    [InlineData("score != 2.5", 3L, true)]
    [InlineData("2.5 < score", 2L, false)]
    [InlineData("2.5 < score", 3L, true)]
    [InlineData("2.5 > score", 2L, true)]
    [InlineData("2.5 > score", 3L, false)]
    public void AFractionalLiteralAgainstAnIntOperandComparesExactly(string text, long score, bool expected) {
        var reader = new Section(Slot(
            name: "score",
            value: score
        ));

        Assert.Equal(
            actual: (reader.Evaluate(text: text) != 0L),
            expected: expected
        );
    }
    // A fold reduces its family in one pass: the body is a subprogram evaluated once per member.
    [Theory]
    [InlineData("all(scores, s -> s == 0)", 0L)]
    [InlineData("any(scores, s -> s == 0)", 1L)]
    [InlineData("count(scores, s -> s > 0)", 2L)]
    [InlineData("sum(scores, s -> s * 2)", 8L)]
    public void AFoldReducesItsFamilyThroughItsSubprogram(string text, long expected) {
        var reader = new Section(Row(
            "scores",
            0L,
            1L,
            3L
        ));

        Assert.Equal(
            actual: reader.Evaluate(text: text),
            expected: expected
        );
    }
    // A fold is priced as its family's size times its body, so a reduction never hides its cost behind one token.
    [Fact]
    public void AFoldCostsItsFamilySizeTimesItsBody() {
        var reader = new Section(Row(
            "scores",
            0L,
            1L,
            3L
        ));
        var one = Rules.RuleWorkBudget.ExpressionCost(
            context: reader.Context,
            kind: CellKind.Int,
            tokens: reader.Compile(text: "count(scores, s -> s > 0)")
        ).Units;
        var deeper = Rules.RuleWorkBudget.ExpressionCost(
            context: reader.Context,
            kind: CellKind.Int,
            tokens: reader.Compile(text: "count(scores, s -> s * s * s > 0)")
        ).Units;

        Assert.True(
            condition: (deeper > one),
            userMessage: $"a heavier body priced {deeper}, no more than the lighter body's {one}"
        );

        var two = Cost(members: 2);
        var four = Cost(members: 4);
        var six = Cost(members: 6);

        Assert.True(
            condition: (((six - four) == (four - two)) && ((four - two) > 0L)),
            userMessage: $"one body over 2, 4, and 6 members priced {two}, {four}, {six}; the price is not linear in the family size"
        );

        static long Cost(int members) {
            var reader = new Section(Row(
                "scores",
                [.. Enumerable.Range(
                    count: members,
                    start: 0
                ).Select(selector: static index => ((long)index))]
            ));

            return Rules.RuleWorkBudget.ExpressionCost(
                context: reader.Context,
                kind: CellKind.Int,
                tokens: reader.Compile(text: "count(scores, s -> s * s > 0)")
            ).Units;
        }
    }
    // A predicate fold's body computes in its member's kind, so a Fixed family folds like an Int one.
    [Fact]
    public void APredicateFoldOverAFixedFamilyComputesInFixed() {
        var reader = new Section(FixedRow(
            "scores",
            0L,
            (1L << 16),
            -(3L << 16)
        ));

        Assert.Equal(
            actual: reader.Evaluate(text: "count(scores, s -> s > 0)"),
            expected: 1L
        );
        Assert.Equal(
            actual: reader.Evaluate(text: "any(scores, s -> s < 0)"),
            expected: 1L
        );
    }
    // A predicate fold yields Int wherever it stands, so it can be the condition of a Fixed conditional.
    [Fact]
    public void APredicateFoldIsAConditionInAFixedExpression() {
        var reader = new Section(FixedRow(
            "scores",
            (1L << 16),
            (2L << 16)
        ));

        Assert.Equal(
            actual: reader.Evaluate(
                kind: CellKind.Fixed,
                text: "all(scores, s -> s > 0.5) ? 1.5 : 0.0"
            ),
            expected: ((3L << 16) / 2L)
        );
        Assert.Equal(
            actual: reader.Evaluate(
                kind: CellKind.Fixed,
                text: "all(scores, s -> s > 1.5) ? 1.5 : 0.0"
            ),
            expected: 0L
        );
    }
    // A called helper may be a predicate: it leaves the Int its comparison yields even inside a Fixed body.
    [Fact]
    public void ACalledPredicateHelperServesAFixedFoldBody() {
        var reader = new Section(FixedRow(
            "scores",
            0L,
            (1L << 16),
            -(3L << 16)
        ));
        var program = new ExpressionProgram(Instructions: [Instruction.Fold(
            binder: "s",
            family: "scores",
            operation: ExpressionOp.Count,
            subprogram: 0
        )]) {
            Subprograms = [
                new Subprogram(
                    Arity: 0,
                    Instructions: [
                        Instruction.Of(operation: ExpressionOp.Member),
                        Instruction.Call(subprogram: 1),
                    ],
                    Name: "body"
                ),
                new Subprogram(
                    Arity: 1,
                    Instructions: [
                        Instruction.Argument(index: 0),
                        Instruction.Constant(value: 0m),
                        Instruction.Of(operation: ExpressionOp.Greater),
                    ],
                    Name: "positive"
                ),
            ],
        };

        Assert.Equal(
            actual: reader.Evaluate(program: program),
            expected: 1L
        );
    }
    // A fold body reads the arguments of the call it sits inside, so a subprogram can fold with an operand.
    [Fact]
    public void AFoldBodyReadsItsEnclosingCallsArguments() {
        var reader = new Section(Row(
            "scores",
            1L,
            2L,
            3L
        ));
        var program = new ExpressionProgram(Instructions: [
            Instruction.Constant(value: 10m),
            Instruction.Call(subprogram: 0),
        ]) {
            Subprograms = [
                new Subprogram(
                    Arity: 1,
                    Instructions: [Instruction.Fold(
                        binder: "s",
                        family: "scores",
                        operation: ExpressionOp.Sum,
                        subprogram: 1
                    )],
                    Name: "shifted"
                ),
                new Subprogram(
                    Arity: 0,
                    Instructions: [
                        Instruction.Of(operation: ExpressionOp.Member),
                        Instruction.Argument(index: 0),
                        Instruction.Of(operation: ExpressionOp.Add),
                    ],
                    Name: "body"
                ),
            ],
        };

        Assert.Equal(
            actual: reader.Evaluate(program: program),
            expected: 36L
        );
    }
    // A fold nests at most once: the inner one is refused by the family it names.
    [Fact]
    public void AFoldNestsAtMostOnce() {
        var reader = new Section(Row(
            "scores",
            0L,
            1L
        ));
        var refusal = Assert.Throws<FormatException>(testCode: () => reader.Compile(text: "all(scores, s -> any(scores, t -> t == s))"));

        Assert.Contains(
            actualString: refusal.Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "a fold nests at most once"
        );
    }
    // A call consumes its operands, evaluates the shared subprogram over them, and pushes one value; the body
    // reaches nothing of the caller but the arguments it was handed.
    [Fact]
    public void ACallEvaluatesItsSubprogramOverTheOperandsItConsumed() {
        var reader = new Section(Slot(
            name: "hp",
            value: 7L
        ));
        var program = new ExpressionProgram(Instructions: [
            Instruction.Operand(name: "hp"),
            Instruction.Constant(value: 3m),
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
        };

        Assert.Equal(
            actual: reader.Evaluate(program: program),
            expected: 4L
        );
    }
    // A fractional literal is lowered exactly only when it is the comparand itself; a literal consumed by an
    // arithmetic or unary operation before the comparison is an ordinary operand, and the gate keeps its meaning.
    [Theory]
    [InlineData("hp > 2.5 * 4", 11L, 1L)]
    [InlineData("hp > 2.5 * 4", 8L, 0L)]
    [InlineData("hp > sign(2.5)", 1L, 0L)]
    [InlineData("hp > sign(2.5)", 2L, 1L)]
    public void AFractionalLiteralInsideAComparedSubexpressionIsNotFolded(string text, long hp, long expected) {
        var reader = new Section(Slot(
            name: "hp",
            value: hp
        ));

        Assert.Equal(
            actual: reader.Evaluate(text: text),
            expected: expected
        );
    }
    // A subprogram compiles once per program and every call site shares that one body, so a diamond of calls
    // costs its authored size to compile rather than doubling at each level.
    [Fact]
    public void EveryCallSiteSharesOneCompiledSubprogramBody() {
        var reader = new Section(Slot(
            name: "hp",
            value: 1L
        ));
        var subprograms = new List<Subprogram> {
            new(
                Arity: 1,
                Instructions: [Instruction.Argument(index: 0)],
                Name: "leaf"
            ),
        };

        // Sixteen levels prove the sharing: the value doubles at each, and evaluating it is still quick.
        for (var level = 1; (level < 16); level++) {
            subprograms.Add(item: new Subprogram(
                Arity: 1,
                Instructions: [
                    Instruction.Argument(index: 0),
                    Instruction.Call(subprogram: (level - 1)),
                    Instruction.Argument(index: 0),
                    Instruction.Call(subprogram: (level - 1)),
                    Instruction.Of(operation: ExpressionOp.Add),
                ],
                Name: $"level{level}"
            ));
        }

        var top = (subprograms.Count - 1);
        var program = new ExpressionProgram(Instructions: [
            Instruction.Operand(name: "hp"),
            Instruction.Call(subprogram: top),
            Instruction.Operand(name: "hp"),
            Instruction.Call(subprogram: top),
            Instruction.Of(operation: ExpressionOp.Add),
        ]) { Subprograms = subprograms };
        var compiled = reader.Compile(program: program);
        var calls = compiled.Where(predicate: static token => (token.Call is not null)).ToArray();

        Assert.Equal(
            actual: calls.Length,
            expected: 2
        );
        Assert.Same(
            actual: calls[1].Call!.Body,
            expected: calls[0].Call!.Body
        );
        Assert.Equal(
            actual: reader.Evaluate(program: program),
            expected: (2L << top)
        );
    }
    // The value stack is leased at the program's own length, so a program at the token ceiling evaluates with its
    // stack as deep as a program can make it, and one token more is refused by name.
    [Fact]
    public void AProgramAtTheTokenCeilingEvaluatesAtItsDeepestStackAndOneMoreIsRefused() {
        var reader = new Section(Slot(
            name: "hp",
            value: 1L
        ));
        var pushes = (RuleCapacity.MaxExpressionTokens / 2);

        ExpressionProgram Sum(int operands) => new(Instructions: [
            .. Enumerable.Repeat(
                count: operands,
                element: Instruction.Operand(name: "hp")
            ),
            .. Enumerable.Repeat(
                count: (operands - 1),
                element: Instruction.Of(operation: ExpressionOp.Add)
            ),
        ]);

        Assert.Equal(
            actual: reader.Evaluate(program: Sum(operands: pushes)),
            expected: ((long)pushes)
        );
        Assert.Contains(
            actualString: Assert.ThrowsAny<Exception>(testCode: () => reader.Compile(program: Sum(operands: (pushes + 1)))).Message,
            expectedSubstring: $"1..{RuleCapacity.MaxExpressionTokens} postfix instructions"
        );
    }
    // A chain that calls the level below twice at each level is a few tokens a level to write and doubles with its
    // depth to run. At the table's full depth nothing may walk it: compiling it, folding its constants, counting
    // its steps and pricing it all take time in its authored size, and its price is an overflow, which no ceiling
    // admits.
    [Fact]
    public void ADoublingCallChainAtTheCeilingIsCompiledAndPricedInItsAuthoredSize() {
        var reader = new Section(Slot(
            name: "hp",
            value: 1L
        ));
        var subprograms = new List<Subprogram> {
            new(
                Arity: 1,
                Instructions: [Instruction.Argument(index: 0)],
                Name: "leaf"
            ),
        };

        for (var level = 1; (level < RuleCapacity.MaxSubprograms); level++) {
            subprograms.Add(item: new Subprogram(
                Arity: 1,
                Instructions: [
                    Instruction.Argument(index: 0),
                    Instruction.Call(subprogram: (level - 1)),
                    Instruction.Argument(index: 0),
                    Instruction.Call(subprogram: (level - 1)),
                    Instruction.Of(operation: ExpressionOp.Add),
                ],
                Name: $"level{level}"
            ));
        }

        var top = (subprograms.Count - 1);

        foreach (var argument in new[] { Instruction.Operand(name: "hp"), Instruction.Constant(value: 1L) }) {
            var compiled = reader.Compile(program: new ExpressionProgram(Instructions: [
                argument,
                Instruction.Call(subprogram: top),
            ]) { Subprograms = subprograms });
            var call = Assert.Single(
                collection: compiled,
                predicate: static token => (token.Call is not null)
            ).Call!;

            Assert.Equal(
                actual: call.Steps,
                expected: long.MaxValue
            );
            Assert.Equal(
                actual: Rules.RuleWorkBudget.Steps(tokens: compiled),
                expected: long.MaxValue
            );
            Assert.True(condition: Rules.RuleWorkBudget.ExpressionCost(
                context: reader.Context,
                kind: CellKind.Int,
                tokens: compiled
            ).IsOverflow);
        }
    }
    // The subprogram table is bounded so a call chain, which the acyclic call graph makes at most as deep as the
    // table is long, nests a bounded number of frames at evaluation.
    [Fact]
    public void MoreSubprogramsThanTheCeilingAreRefusedByName() {
        var reader = new Section(Slot(
            name: "hp",
            value: 1L
        ));
        var program = new ExpressionProgram(Instructions: [
            Instruction.Operand(name: "hp"),
            Instruction.Call(subprogram: 0),
        ]) {
            Subprograms = [.. Enumerable.Range(
                count: (RuleCapacity.MaxSubprograms + 1),
                start: 0
            ).Select(selector: static index => new Subprogram(
                Arity: 1,
                Instructions: [Instruction.Argument(index: 0)],
                Name: $"s{index}"
            ))],
        };
        var refusal = Assert.Throws<RuleException>(testCode: () => reader.Compile(program: program));

        Assert.Contains(
            actualString: refusal.Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"at most {RuleCapacity.MaxSubprograms} are admitted"
        );
    }
    // A subprogram index the program does not carry is refused by name rather than read as nothing.
    [Fact]
    public void ACallNamingNoSubprogramIsRefused() {
        var reader = new Section(Slot(
            name: "hp",
            value: 1L
        ));
        var refusal = Assert.Throws<RuleException>(testCode: () => reader.Compile(program: new ExpressionProgram(Instructions: [Instruction.Call(subprogram: 4)])));

        Assert.Contains(
            actualString: refusal.Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "which the program does not carry"
        );
    }
    // A subprogram that calls itself, directly or around a chain, would recurse at evaluation; the compiler
    // refuses it by the subprogram's own name instead.
    [Fact]
    public void ACallCycleIsRefusedByName() {
        var reader = new Section(Slot(
            name: "hp",
            value: 1L
        ));
        var program = new ExpressionProgram(Instructions: [
            Instruction.Operand(name: "hp"),
            Instruction.Call(subprogram: 0),
        ]) {
            Subprograms = [new Subprogram(
                Arity: 1,
                Instructions: [
                    Instruction.Argument(index: 0),
                    Instruction.Call(subprogram: 0),
                ],
                Name: "loop"
            )],
        };
        var refusal = Assert.Throws<RuleException>(testCode: () => reader.Compile(program: program));

        Assert.Contains(
            actualString: refusal.Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "the call graph carries no cycle"
        );
        Assert.Contains(
            actualString: refusal.Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "loop"
        );
    }
    // Absence is a value the author answers: `isAbsent` reads it, `??` replaces it, and every other operation
    // consuming it still faults exactly as it always has.
    [Fact]
    public void AbsenceIsAValueIsAbsentAndCoalesceAnswer() {
        var reader = new Section(Row(
            "pile",
            5L
        ));

        Assert.Equal(
            actual: reader.Evaluate(text: "isAbsent(pile[0])"),
            expected: 0L
        );
        Assert.Equal(
            actual: reader.Evaluate(text: "pile[0] ?? 9"),
            expected: 5L
        );

        var zoned = new Section(
            Row(
                "pile",
                5L
            ),
            new StateRow(
                Name(text: "hand"),
                CellKind.Bool,
                Capacity: 3,
                Domain: new StateDomain.KeysOf(
                    Name(text: "pile"),
                    Ordered: true
                ),
                Cells: []
            )
        );
        var absent = Instruction.Operand(
            key: "$zone:hand:first",
            name: "pile"
        );

        Assert.Equal(
            actual: zoned.Evaluate(program: new ExpressionProgram(Instructions: [
                absent,
                Instruction.Of(operation: ExpressionOp.IsAbsent),
            ])),
            expected: 1L
        );
        Assert.Equal(
            actual: zoned.Evaluate(program: new ExpressionProgram(Instructions: [
                absent,
                Instruction.Constant(value: 9m),
                Instruction.Of(operation: ExpressionOp.Coalesce),
            ])),
            expected: 9L
        );
    }
    // The evaluator allocates nothing per evaluation: the stack, the absence marks and a call frame are all
    // stack-allocated, so a rule sampled thousands of times a tick adds no garbage.
    [Fact]
    public void EvaluationAllocatesNothing() {
        var reader = new Section(Row(
            "scores",
            1L,
            2L,
            3L
        ));
        var program = reader.Compile(text: "count(scores, s -> s > 1) + minimum(scores[0], 4)");

        for (var warm = 0; (warm < 64); warm++) {
            _ = Rules.RuleExpressions.TryEvaluate(
                fault: out _,
                kind: CellKind.Int,
                program: program,
                reader: reader,
                value: out _
            );
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var run = 0; (run < 256); run++) {
            _ = Rules.RuleExpressions.TryEvaluate(
                fault: out _,
                kind: CellKind.Int,
                program: program,
                reader: reader,
                value: out _
            );
        }

        Assert.Equal(
            actual: GC.GetAllocatedBytesForCurrentThread(),
            expected: before
        );
    }

    private sealed class Section : IStateReader, IStateSection {
        private readonly long[] m_locals = new long[RuleCapacity.MaxLocalsPerRule];

        public Section(params StateRow[] rows) {
            Rows = rows;
            Catalog = StateCatalog.Compile(section: this);
            Arena = new StateArena(
                catalog: Catalog,
                options: null,
                section: this,
                time: ArenaTime.Origin
            );
        }

        public StateArena Arena { get; }
        public CellKey BoundEachKey { get; set; }
        public CellKey BoundPreviousKey { get; set; }
        public CellKey BoundTokenKey { get; set; }
        public StateCatalog Catalog { get; }
        public Rules.RuleCompileContext Context => new(
            this,
            Catalog,
            null,
            null,
            null,
            240,
            Rules.RuleVocabulary.Core
        );
        public ulong EngineTick => 0UL;
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public Span<long> Locals => m_locals;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
        public Span<long> PatternWord => [];
        public IReadOnlyList<StateRow> Rows { get; }
        public ulong Tick => 0UL;

        public int BoundIndex(BoundKey key) => -1;
        public Rules.CompiledExpressionToken[] Compile(string text, CellKind kind = CellKind.Int) =>
            Compile(
                kind: kind,
                program: ExpressionProgram.Parse(text: text)
            );
        public Rules.CompiledExpressionToken[] Compile(ExpressionProgram program, CellKind kind = CellKind.Int) =>
            Rules.RuleCompiler.CompileExpression(
                program,
                kind,
                "ir-law",
                "ir-law",
                Context
            );
        public long Evaluate(string text, CellKind kind = CellKind.Int) =>
            Evaluate(
                kind: kind,
                program: ExpressionProgram.Parse(text: text)
            );
        public long Evaluate(ExpressionProgram program, CellKind kind = CellKind.Int) {
            Assert.True(condition: Rules.RuleExpressions.TryEvaluate(
                fault: out var fault,
                kind: kind,
                program: Compile(
                    kind: kind,
                    program: program
                ),
                reader: this,
                value: out var value
            ), userMessage: fault.ToString());
            return value;
        }
    }
}
