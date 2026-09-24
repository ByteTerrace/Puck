using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>LSP completion for a dot-access read: completing a declared table's own cell keys after <c>row.</c>,
/// including recovery from an in-progress, not-yet-closed statement.</summary>
public class StateDeclarationLspTests {
    private const string Declared = "schema: \"puck.world.definition.v1\"\nstate {\n    world {\n        table vitals {\n            health = 100\n            mana = 50\n        }\n    }\n}\nrule \"heal\" {\n";

    // The `rule "heal" {` block of the unclosed case is never closed anywhere in the buffer — this is what a
    // document looks like mid-keystroke, before the author has typed a closing brace.
    [InlineData("    when vitals.|\n    hp += 1\n}\n", "health, mana")]
    [InlineData("    when vitals.he|\n    hp += 1\n}\n", "health")]
    [InlineData("    when vitals.|", "health, mana")]
    [Theory]
    public async Task CompletionAfterADotOffersTheDeclaredTablesOwnCellKeys(string rest, string keys) {
        var labels = await LanguageServerClient.CompletionLabelsAsync(markedSource: (Declared + rest));

        foreach (var key in keys.Split(separator: ", ")) {
            Assert.Contains(
                expected: key,
                set: labels
            );
        }
        // After a dot, the statement keywords are not what the author is typing.
        Assert.DoesNotContain(
            expected: "when",
            set: labels
        );
    }
}
