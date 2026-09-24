using Puck.Testing;
using Xunit;

namespace Puck.State.Tests;

/// <summary>One lexical identifier rule, <see cref="IdentifierSpelling"/>, and every printer in this assembly held to
/// it by <see cref="SpellingLaws"/> over the edges of the rule and a seeded fuzz: a printed name reads back as
/// itself, and a printer writes a name bare exactly when its own reader takes that bare spelling as the name and the
/// one rule, less the position's own reservations, admits it. Each law carries its mutation proof: a printer carrying
/// a drifted copy of the rule is caught. The document printers are held to the same laws in
/// <c>tests/Puck.World.Transpiler.Tests/IdentifierAgreementLawTests.cs</c>.</summary>
public sealed class IdentifierSpellingLawTests {
    private static readonly IReadOnlyList<string> Corpus = IdentifierCorpus.Names(count: 4000);

    // A position carries a name exactly when its builder admits it: the expression spelling's builder refuses a name
    // no spelling can write, so the laws below hold the printer to every name it is handed.
    private static bool Carried(string name, Func<string> print) {
        try {
            _ = print();

            return true;
        } catch (ArgumentException) {
            return false;
        } catch (FormatException) {
            return false;
        }
    }
    private static InstructionPayload.State? SingleRead(string text) => ((
        ExpressionSpelling.TryParse(
            error: out _,
            program: out var program,
            text: text
        ) &&
        (program.Instructions.Count == 1) &&
        (program.Instructions[0].Payload is InstructionPayload.State state)
    )
        ? state
        : null
    );
    // A reserved channel's `:segment`, a dotted path and a live-zone bracket are the expression spelling's own walk
    // over the rule, so a name carrying one is decided by that walk; the laws still hold the printer to its reader.
    private static bool LayeredByExpression(string name) => (name.IndexOfAny(anyOf: ['.', ':', '[']) >= 0);
    private static string RowText(string name) => ExpressionSpelling.Print(instructions: [Instruction.Operand(name: name)]);
    private static string KeyText(string name) => ExpressionSpelling.Print(instructions: [Instruction.Operand(
        key: name,
        name: "row"
    )]);
    private static string Quote(string name) => $"\"{name.Replace(newValue: "\\\\", oldValue: "\\").Replace(newValue: "\\\"", oldValue: "\"")}\"";
    private static SpellingPosition Exact(string label, Func<string, bool> carries, Func<string, string> print, Func<string, string> bare, Func<string, string> quoted, Func<string, string?> read, Func<string, bool?> rule, Func<string, bool>? reserved = null) => new(
        Bare: bare,
        Carries: carries,
        Label: label,
        Print: print,
        PrintedBare: (name, printed) => string.Equals(a: printed, b: bare(arg: name), comparisonType: StringComparison.Ordinal),
        Quoted: quoted,
        Read: read,
        Reserved: (reserved ?? (static _ => false)),
        Rule: rule
    );

    /// <summary>Gets every position this assembly prints a name in.</summary>
    private static IReadOnlyList<SpellingPosition> Positions { get; } = [
        Exact(
            bare: static name => name,
            carries: static name => Carried(name: name, print: () => RowText(name: name)),
            label: "expression row",
            print: RowText,
            quoted: static name => $"`{name}`",
            read: static text => ((SingleRead(text: text) is { Key: null, Name.PoolField: null } state)
                ? state.Name.Spelling
                : null
            ),
            reserved: ExpressionSpelling.IsReservedName,
            rule: static name => (LayeredByExpression(name: name)
                ? null
                : IdentifierSpelling.IsName(text: name)
            )
        ),
        Exact(
            bare: static name => $"row[{name}]",
            carries: static name => (
                !name.StartsWith(value: IdentifierSpelling.Sigil) &&
                Carried(name: name, print: () => KeyText(name: name))
            ),
            label: "expression key",
            print: KeyText,
            quoted: static name => $"row[`{name}`]",
            read: static text => (((SingleRead(text: text) is { Key: { PoolField: null } key } state) && (state.Name.Spelling == "row"))
                ? key.Spelling
                : null
            ),
            // Inside brackets nothing is reserved, and a number is a numeric key.
            rule: static name => ((LayeredByExpression(name: name) || ((name.Length > 0) && char.IsAsciiDigit(c: name[0])))
                ? null
                : IdentifierSpelling.IsName(text: name)
            )
        ),
        Exact(
            bare: static name => name,
            carries: static name => (name.Length > 0),
            label: "pattern symbol",
            print: static name => PatternSpelling.Print(node: new PatternNode.Symbol(Name: name)),
            quoted: Quote,
            read: static text => ((PatternSpelling.TryParse(
                error: out _,
                node: out var node,
                text: text
            ) && (node is PatternNode.Symbol symbol))
                ? symbol.Name
                : null
            ),
            reserved: PatternSpelling.ReservedWords.Contains,
            rule: static name => IdentifierSpelling.IsIdentifier(text: name)
        ),
        Exact(
            bare: static name => $"board({name}, 0..1)",
            carries: static name => CellName.TryParse(
                candidate: name,
                name: out _,
                reason: out _
            ),
            label: "cell-set row",
            print: static name => CellSetSpelling.Print(expression: new CellSetExpression.Board(
                High: 1,
                Low: 0,
                Row: CellName.Parse(candidate: name)
            )),
            quoted: static name => $"board({Quote(name: name)}, 0..1)",
            read: static text => ((CellSetSpelling.TryParse(
                error: out _,
                expression: out var expression,
                text: text
            ) && (expression is CellSetExpression.Board board))
                ? board.Row.Value
                : null
            ),
            rule: static name => IdentifierSpelling.IsIdentifier(text: name)
        ),
    ];

    private static void AssertNone(List<string> violations) => Assert.True(
        condition: (violations.Count == 0),
        userMessage: SpellingLaws.Describe(violations: violations)
    );
    private static SpellingPosition PositionOf(string label) => Positions.Single(predicate: position => (position.Label == label));

    public static TheoryData<string> PositionLabels() => [.. Positions.Select(selector: static position => position.Label)];
    [MemberData(memberName: nameof(PositionLabels))]
    [Theory]
    public void APrintedNameReadsBackAsItself(string label) => AssertNone(violations: SpellingLaws.RoundTripViolations(
        names: Corpus,
        position: PositionOf(label: label)
    ));
    [MemberData(memberName: nameof(PositionLabels))]
    [Theory]
    public void ANamePrintsBareExactlyWhenItsReaderAndTheRuleTakeItBare(string label) => AssertNone(violations: SpellingLaws.AgreementViolations(
        names: Corpus,
        position: PositionOf(label: label)
    ));
    // The rule stated independently of its implementation: an ASCII letter or underscore, then ASCII letters, digits
    // and underscores, with the sigil admitted only in front of a name.
    [Fact]
    public void TheRuleIsAsciiWithTheSigilOnlyInFront() {
        static bool Start(char character) => (character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_');
        static bool Part(char character) => (Start(character: character) || (character is >= '0' and <= '9'));
        static bool Identifier(string text) => ((text.Length > 0) && Start(character: text[0]) && text.Skip(count: 1).All(predicate: Part));

        foreach (var name in Corpus) {
            var identifier = Identifier(text: name);
            var named = (identifier || (name.StartsWith(value: '$') && Identifier(text: name[1..])));

            Assert.True(
                condition: ((IdentifierSpelling.IsIdentifier(text: name) == identifier) && (IdentifierSpelling.IsName(text: name) == named)),
                userMessage: $"'{name}': IsIdentifier {IdentifierSpelling.IsIdentifier(text: name)}, IsName {IdentifierSpelling.IsName(text: name)}"
            );
        }
    }
    // The backquoted form has no escape, so the language's answer to a name holding a backquote, or to no name at all,
    // is refusal where the name is minted: an operand refuses exactly the references whose spelling IsSpelledName
    // refuses, and every name a state row or cell key can hold is one it spells.
    [Fact]
    public void AnOperandRefusesExactlyTheNamesNoSpellingWrites() {
        foreach (var name in Corpus) {
            var spelled = ExpressionSpelling.IsSpelledName(name: StateChannelRef.Parse(spelling: name).Spelling);

            foreach (var build in new Func<Instruction>[] { () => Instruction.Operand(name: name), () => Instruction.Operand(key: name, name: "row") }) {
                var refused = false;

                try {
                    _ = build();
                } catch (ArgumentException) {
                    refused = true;
                }
                Assert.True(
                    condition: (refused == !spelled),
                    userMessage: $"'{name}': refused {refused}, spelled {spelled}"
                );
            }
        }
    }
    [Fact]
    public void EveryCellNameIsASpelledName() {
        foreach (var name in Corpus) {
            Assert.True(
                condition: (!CellName.TryParse(candidate: name, name: out _, reason: out _) || ExpressionSpelling.IsSpelledName(name: name)),
                userMessage: $"'{name}' is a cell name no expression can write"
            );
        }
    }
    // The mutation proof: a cell-name rule without its backquote clause admits a name from the corpus that no
    // expression can write, so the law above would catch the clause going missing.
    [Fact]
    public void TheCellNameLawCatchesARuleThatAdmitsABackquote() => Assert.Contains(
        collection: Corpus,
        filter: static name => (IdentifierRules.TryValidateKernel(candidate: name, reason: out _) && !name.Contains(value: '.') && !ExpressionSpelling.IsSpelledName(name: name))
    );
    [Fact]
    public void AnIrOperandNoSpellingWritesIsRefusedOnRead() {
        foreach (var node in new[] { """{"instructions":[{"op":"Operand","name":""}]}""", """{"instructions":[{"op":"Operand","name":"a`b"}]}""", """{"instructions":[{"op":"Operand","name":"row","key":""}]}""" }) {
            _ = Assert.Throws<System.Text.Json.JsonException>(testCode: () => ExpressionProgramJsonConverter.FromNode(node: System.Text.Json.Nodes.JsonNode.Parse(json: node)));
        }
    }
    [MemberData(memberName: nameof(PositionLabels))]
    [Theory]
    public void TheRoundTripLawCatchesAPrinterCarryingTheUnicodeCopy(string label) => Assert.NotEmpty(collection: SpellingLaws.RoundTripViolations(
        names: Corpus,
        position: SpellingLaws.WithUnicodeCopy(position: PositionOf(label: label))
    ));
    [MemberData(memberName: nameof(PositionLabels))]
    [Theory]
    public void TheAgreementLawCatchesAPrinterThatQuotesTooMuch(string label) => Assert.NotEmpty(collection: SpellingLaws.AgreementViolations(
        names: Corpus,
        position: SpellingLaws.WithUnderscoresQuoted(position: PositionOf(label: label))
    ));
}
