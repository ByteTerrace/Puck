using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Formatting;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary><see cref="PuckFormatter"/> against the `.puck` DSL sugar wave's new constructs: reserved-channel
/// tokens stay intact, and formatting the decompiled form of every shipped world is idempotent.</summary>
public class FormatterSugarTests {
    private static IEnumerable<string> ExtractDollarTokens(string line) {
        var start = -1;

        for (var i = 0; (i < line.Length); i++) {
            if (
                (line[i] == '$') &&
                (start < 0)
            ) {
                start = i;
            } else if (
                (start >= 0) &&
                !(char.IsLetterOrDigit(c: line[i]) || (line[i] is '_' or '$' or '.' or ':' or '[' or ']' or '-'))
            ) {
                yield return line[start..i];
                start = -1;
            }
        }
        if (start >= 0) {
            yield return line[start..];
        }
    }

    [Fact]
    public void FormattingIsIdempotentOnAGateWithMixedChannelsAndKindSuffix() {
        var source = "rule \"r\" {\n    when solitaireFreecell[from] != solitaireFreecell[to] : Int\n}\n";
        var pass1 = PuckFormatter.Format(source);
        var pass2 = PuckFormatter.Format(pass1);

        Assert.Equal(
            actual: pass2,
            expected: pass1
        );
        Assert.Contains(
            actualString: pass1,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "solitaireFreecell[from]"
        );
    }
    [MemberData(nameof(ShippedWorldFiles))]
    [Theory]
    public void FormattingTheDecompiledFormOfEveryShippedWorldIsIdempotent(string relativePath) {
        var fullPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        );

        Assert.True(
            condition: File.Exists(path: fullPath),
            userMessage: $"Shipped world file not found: {fullPath}"
        );
        var decompiled = WorldDecompiler.Decompile(jsonText: File.ReadAllText(path: fullPath));

        var pass1 = PuckFormatter.Format(decompiled);
        var pass2 = PuckFormatter.Format(pass1);

        Assert.Equal(
            actual: pass2,
            expected: pass1
        );
    }
    [InlineData("when $physics:quiescent == 1 and settleHold < 60")]
    [InlineData("when $upright:placement:$each >= 0.5")]
    [InlineData("when $zones[solitaireFreecell[from]][solitaireFreecell[card]] == 1")]
    [InlineData("transform t = transfer(from: $zones[solitaireFreecell[from]], to: $zones[solitaireFreecell[to]], selector: Slice, key: $cell:solitaireFreecell:card)")]
    [Theory]
    public void ReservedChannelTokensSurviveFormattingUnsplit(string line) {
        var source = $"rule \"r\" {{\n    {line}\n}}\n";
        var formatted = PuckFormatter.Format(source);

        // Every `$name:segment` token in the input must reappear in the output with no space inserted around its
        // internal colons — the regression this guards is `$physics:quiescent` splicing into `$physics: quiescent`.
        foreach (var token in ExtractDollarTokens(line: line)) {
            Assert.Contains(
                actualString: formatted,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: token
            );
        }
    }

    public static TheoryData<string> ShippedWorldFiles => new() {
        "games/backgammon.world.json",
        "games/billiards.world.json",
        "games/bowling.world.json",
        "games/chess.world.json",
        "games/chinese-checkers.world.json",
        "games/dominoes.world.json",
        "games/freecell.world.json",
        "games/hexlines.world.json",
        "games/klondike.world.json",
        "games/mancala.world.json",
        "games/poker.world.json",
        "games/solitaire.world.json",
        "games/spider.world.json",
        "games/tictactoe.world.json",
        "pipeline.world.json",
        "puck.world.json",
    };

}
