using System.Globalization;
using Puck.State;
using Puck.Testing;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A name printed for an operand reads back as that name, and a value an atom computes reads back as that
/// value, whatever the thread's culture: <see cref="ExpressionSpelling.PrintName(string, bool)"/> is the one printer of
/// a name, and every numeric test on the way to it reads the invariant spelling. Each case runs under a culture that
/// writes decimals with a comma and groups with a dot, as de-DE does.</summary>
public sealed class NameSpellingRoundTripLawTests {
    // Each text an atom may compute, and whether it is a number, which the operand writes as the number, rather than
    // a name, which it writes as the name.
    private static readonly Dictionary<string, bool> Texts = new(comparer: StringComparer.Ordinal) {
        ["row1"] = false,
        ["r2d2"] = false,
        ["seat-1"] = false,
        ["count"] = false,
        ["a.b"] = false,
        ["1,5"] = false,
        ["1.000,5"] = false,
        ["2m"] = false,
        ["90deg"] = false,
        ["250ms"] = false,
        ["0x1F"] = false,
        ["3"] = true,
        ["42"] = true,
        ["1.5"] = true,
        ["-2"] = true,
    };

    public static TheoryData<string> TextNames() => new(values: Texts.Keys);

    // The culture de-DE writes numbers in, built from the invariant one so it holds under invariant globalization.
    private static CultureInfo CommaDecimal() {
        var culture = ((CultureInfo)CultureInfo.InvariantCulture.Clone());

        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = ".";

        return culture;
    }
    private static IReadOnlyList<Instruction> Read(string text) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                program: out var program,
                text: text
            ),
            userMessage: $"'{text}' does not read back: {error}"
        );

        return program.Instructions;
    }
    private static void UnderCommaDecimal(Action test) {
        var previous = CultureInfo.CurrentCulture;

        CultureInfo.CurrentCulture = CommaDecimal();
        try {
            test();
        } finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [MemberData(nameof(TextNames))]
    [Theory]
    public void APrintedNameReadsBackAsThatName(string text) => UnderCommaDecimal(test: () => Assert.Equal(
        expected: [Instruction.Operand(name: text)],
        actual: Read(text: ExpressionSpelling.PrintName(name: text))
    ));
    [Fact]
    public void EveryPrintedNameOfTheCorpusReadsBackAsThatName() => UnderCommaDecimal(test: () => {
        var violations = new List<string>();

        foreach (var name in IdentifierCorpus.Names(count: 4000).Where(predicate: ExpressionSpelling.IsSpelledName)) {
            Instruction read;

            // A name the operand builder refuses is one no row carries, so no printer is handed it.
            try {
                read = Instruction.Operand(name: name);
            } catch (Exception refused) when ((refused is ArgumentException or FormatException)) {
                continue;
            }

            var printed = ExpressionSpelling.PrintName(name: name);

            if (!ExpressionSpelling.TryParse(error: out var error, program: out var program, text: printed) || !program.Instructions.SequenceEqual(second: [read])) {
                violations.Add(item: $"'{name}' prints '{printed}' {error}");
            }
        }

        Assert.True(
            condition: (violations.Count == 0),
            userMessage: $"{violations.Count} names do not read back:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: violations.Take(count: 40))}"
        );
    });
    // A name argument a document holds is written bare when it is a whole number and as the name it spells otherwise.
    [MemberData(nameof(TextNames))]
    [Theory]
    public void ANameArgumentReadsBackAsWhatTheDocumentHolds(string text) => UnderCommaDecimal(test: () => Assert.Equal(
        expected: [(long.TryParse(provider: CultureInfo.InvariantCulture, result: out var whole, s: text, style: NumberStyles.Integer)
            ? Instruction.Constant(value: whole)
            : Instruction.Operand(name: text))],
        actual: Read(text: DocumentLowering.BareSpelling(
            form: DocumentValueForm.Name,
            written: text
        ))
    ));
    [MemberData(nameof(TextNames))]
    [Theory]
    public void AnAtomReadsBackAsTheValueItComputes(string text) => UnderCommaDecimal(test: () => {
        var scope = new DocumentScope(
            constants: new Dictionary<string, ExpressionNode>(comparer: StringComparer.Ordinal) { ["k"] = new LiteralExpressionNode(Value: text) },
            vocabulary: WorldDocumentVocabulary.Instance
        );
        var spelled = DocumentLowering.SpliceAtoms(
            operand: PuckParser.CreateOperand(
                form: DocumentValueForm.Expression,
                text: "$\"{k}\""
            ),
            scope: scope
        );

        Assert.Empty(collection: scope.Diagnostics);
        Assert.Equal(
            expected: [(Texts[text]
                ? Instruction.Constant(value: decimal.Parse(provider: CultureInfo.InvariantCulture, s: text, style: NumberStyles.Float))
                : Instruction.Operand(name: text))],
            actual: Read(text: spelled)
        );
    });
}
