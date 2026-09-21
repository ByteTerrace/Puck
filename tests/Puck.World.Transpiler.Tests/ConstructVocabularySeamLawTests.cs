using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Both readers of a declaration's modifiers ask the description rather than keeping a list beside it: the
/// core parser reads exactly the bare flags the table carries, and the lowering admits exactly the modifiers it
/// describes and refuses a described modifier it does not write.</summary>
/// <remarks>Each law hands one compile a description it invented — the shipped table with one member added to
/// <c>slot</c> and nothing else — and asserts the behavior moves with the description. Acceptance under the
/// shipped table is the control: the added spelling is refused there.</remarks>
public class ConstructVocabularySeamLawTests {
    private const string Added = "freeze";
    private const string Slot = "slot";

    private static string Source(string modifier) => $"schema: \"puck.world.definition.v1\"\n\nstate {{\n    world {{\n        slot hp = 1 {modifier}\n    }}\n}}\n";
    // The shipped description with one extra member on `slot`, and nothing else changed.
    private static WorldDocumentVocabulary Describing(WorldConstructMember member) => new(constructs: new WorldConstructTable(
        constructs: [.. WorldConstructs.Table.Constructs.Select(selector: construct => (string.Equals(
            a: construct.Keyword,
            b: Slot,
            comparisonType: StringComparison.Ordinal
        )
            ? (construct with { Members = [.. construct.Members, member] })
            : construct))],
        excluded: WorldConstructs.Table.Excluded
    ));
    // The modifiers the parser read onto the first `slot` declaration of a parsed source.
    private static IReadOnlyList<string> ParsedModifiers(string source, IDocumentVocabulary vocabulary) {
        var parsed = PuckParser.ParseDocumentWithDiagnostics(
            source: source,
            vocabulary: vocabulary
        );

        Assert.NotNull(@object: parsed.Value);

        var pending = new Stack<StatementNode>(collection: parsed.Value!.Statements);

        while (pending.Count > 0) {
            switch (pending.Pop()) {
                case StateSlotDeclarationNode slot:
                    return [.. slot.Modifiers.Select(selector: static modifier => modifier.Name)];
                case BlockNode block:
                    foreach (var statement in block.Statements) {
                        pending.Push(item: statement);
                    }

                    break;
                default:
                    break;
            }
        }

        Assert.Fail(message: $"no 'slot' declaration was parsed from:\n{source}");

        return [];
    }

    [Fact]
    public void TheCoreParserReadsTheBareFlagsTheDescriptionCarries() {
        // A word the description does not carry is not a modifier: the cursor rewinds and it opens a statement of
        // its own, which is the whole reason the bare form needs an answer from the vocabulary.
        Assert.DoesNotContain(
            collection: ParsedModifiers(
                source: Source(modifier: Added),
                vocabulary: WorldDocumentVocabulary.Instance
            ),
            expected: Added
        );
        Assert.Contains(
            collection: ParsedModifiers(
                source: Source(modifier: Added),
                vocabulary: Describing(member: new(
                    DocumentKeys: [Added],
                    Kind: WorldMemberKind.Flag,
                    Name: Added,
                    Position: WorldMemberPosition.Modifier,
                    Summary: "A fixture flag."
                ))
            ),
            expected: Added
        );

        // `evicts` is a described `Flag` of `table`, and the parser reads it from the description alone.
        var evicts = PuckParser.ParseDocumentWithDiagnostics(
            source: "schema: \"puck.world.definition.v1\"\n\nstate {\n    world {\n        table bag capacity(2) evicts\n    }\n}\n",
            vocabulary: WorldDocumentVocabulary.Instance
        );

        Assert.NotNull(@object: evicts.Value);
        Assert.Contains(
            collection: evicts.Value!.Statements
                .OfType<BlockNode>()
                .SelectMany(selector: static state => state.Statements.OfType<BlockNode>())
                .SelectMany(selector: static world => world.Statements.OfType<StateTableDeclarationNode>())
                .SelectMany(selector: static table => table.Modifiers.Select(selector: static modifier => modifier.Name)),
            expected: "evicts"
        );
    }
    [Fact]
    public void ALoweringThatAdmitsFromTheDescriptionRefusesADescribedModifierItDoesNotWrite() {
        var source = Source(modifier: $"{Added}(1)");
        var shipped = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        // The control: undescribed, the modifier is refused as one the row does not admit.
        Assert.True(condition: shipped.Diagnostics.HasErrors);
        Assert.Contains(
            actualString: shipped.Diagnostics.FormatReport(""),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"'{Added}' is not a modifier 'hp' admits"
        );

        var described = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source,
            vocabulary: Describing(member: new(
                DocumentKeys: [Added],
                Kind: WorldMemberKind.Number,
                Name: Added,
                Position: WorldMemberPosition.Modifier,
                Summary: "A fixture modifier no lowering writes."
            ))
        );

        // Described and unwritten, it is refused by name rather than admitted and dropped.
        Assert.True(
            condition: described.Diagnostics.HasErrors,
            userMessage: $"a described modifier the lowering does not write compiled, so its value was discarded in silence: {described.Json?.ToJsonString()}"
        );
        Assert.Contains(
            actualString: described.Diagnostics.FormatReport(""),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"'{Added}' is described for 'hp' but this vocabulary does not lower it"
        );
    }
}
