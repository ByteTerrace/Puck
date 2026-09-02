using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins the shared echo grammar: a segment is either a run of <see cref="CommandEcho.Field(string, string)"/>
/// <c>key=value</c> tokens, or one <see cref="CommandEcho.Head(string)"/> word followed by qualifying
/// <see cref="CommandEcho.Field(string, string)"/> tokens — <c>[verb: key=value key=value | head field=value]</c> —
/// with a <see cref="CommandEcho.Segment"/> boundary's separator written only when further content follows, never
/// trailing.</summary>
public sealed class CommandEchoTests {
    [Fact]
    public void OpenFieldCloseProducesTheBracketedLine() {
        var line = CommandEcho.Open(verb: "world.status")
            .Field(key: "kits", value: 1)
            .Field(key: "dirty", value: false)
            .Close();

        Assert.Equal(actual: line, expected: "[world.status: kits=1 dirty=false]");
    }
    [Fact]
    public void SegmentBetweenHeadFieldGroupsInsertsThePipeOnlyWhenSomethingFollows() {
        // The world.groups shape: each segment is a declared Head word followed by qualifying Field tokens.
        var line = CommandEcho.Open(verb: "world.groups")
            .Head(head: "kind").Field(key: "name", value: "party")
            .Segment()
            .Head(head: "group").Field(key: "id", value: "alpha").Field(key: "kind", value: "party")
            .Segment()
            .Close();

        // The trailing Segment() before Close() vanishes rather than leaving a bare "| ]".
        Assert.Equal(actual: line, expected: "[world.groups: kind name=party | group id=alpha kind=party]");
    }
    [Fact]
    public void ASegmentFollowedByAFieldNeverTrails() {
        var line = CommandEcho.Open(verb: "world.contributions")
            .Text(text: "slot 'a' state=empty")
            .Segment()
            .Field(key: "tick", value: 3)
            .Close();

        Assert.Equal(actual: line, expected: "[world.contributions: slot 'a' state=empty | tick=3]");
    }
    [Fact]
    public void NoContentClosesToAnEmptyBody() {
        Assert.Equal(actual: CommandEcho.Open(verb: "world.market").Close(), expected: "[world.market:]");
    }
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData("a-b_c.d:e/f", "a-b_c.d:e/f")]
    [InlineData("C:\\my games", "\"C:\\\\my games\"")]
    [InlineData("[seat1]", "\"[seat1]\"")]
    [InlineData("a|b", "\"a|b\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    // A backslash is not itself reserved — it is only special INSIDE a quoted run — so a path carrying one but no
    // reserved character still rides through verbatim.
    [InlineData("C:\\games\\", "C:\\games\\")]
    // ...and when something else does force the quoting, the trailing backslash is escaped so it cannot swallow the
    // closing '"' and leave the run unterminated.
    [InlineData("C:\\my games\\", "\"C:\\\\my games\\\\\"")]
    // Whitespace is char.IsWhiteSpace, not a listed set: a vertical tab and a non-breaking space split a reader that
    // splits the way the wire tokenizer does, so both are reserved too.
    [InlineData("a\vb", "\"a\vb\"")]
    [InlineData("a\u00a0b", "\"a\u00a0b\"")]
    public void AValueIsQuotedExactlyWhenItCarriesAReservedCharacter(string value, string expected) {
        Assert.Equal(actual: CommandEcho.Quote(value: value), expected: expected);
    }
    [Theory]
    [InlineData("line\tbreak", "\"line\\tbreak\"")]
    [InlineData("two\nlines", "\"two\\nlines\"")]
    [InlineData("two\r\nlines", "\"two\\r\\nlines\"")]
    public void ALineBreakIsEscapedRatherThanCarriedSoAnEchoIsAlwaysOneLine(string value, string expected) {
        var quoted = CommandEcho.Quote(value: value);

        Assert.Equal(actual: quoted, expected: expected);

        // The point of escaping rather than merely quoting these: a driver splits the stream into LINES before it
        // looks for tokens, so a carried newline would tear the record in half no matter how well it was quoted.
        Assert.DoesNotContain(actualString: CommandEcho.Open(verb: "world.note").Field(
            key: "message",
            value: value
        ).Close(), comparisonType: StringComparison.Ordinal, expectedSubstring: "\n");
    }
    [Fact]
    public void AFieldValueCarryingADelimiterCannotEndTheTokenSegmentOrEnvelope() {
        // Unescaped, each of these would let a driver's split land inside the value: the space ends the token, the
        // ']' closes the envelope early, and the '|' opens a segment that was never authored.
        var line = CommandEcho.Open(verb: "world.update")
            .Field(key: "cacheRoot", value: "C:\\my games\\cache")
            .Field(key: "members", value: "[seat1|seat2]")
            .Close();

        Assert.Equal(actual: line, expected: "[world.update: cacheRoot=\"C:\\\\my games\\\\cache\" members=\"[seat1|seat2]\"]");

        // The envelope's own closing bracket is still the LAST character, which is what a driver keys on.
        Assert.EndsWith(actualString: line, comparisonType: StringComparison.Ordinal, expectedEndString: "]");
    }
    [Fact]
    public void ASplicedTagIsOneTokenEvenWhenTheNameItCarriesHasWhitespace() {
        const string echo = "[world.inhabitants: bodyIndex=0]";

        // The ordinary case is untouched: a safe name splices verbatim, so every existing instance/anchor tag reads
        // exactly as it did.
        Assert.Equal(
            actual: CommandEcho.SpliceTag(tag: "instance:alpha", text: echo),
            expected: "[world.inhabitants: bodyIndex=0 instance:alpha]"
        );

        // A name carrying a delimiter is spliced as ONE quoted token rather than closing the envelope it is being
        // spliced into.
        Assert.Equal(
            actual: CommandEcho.SpliceTag(tag: "instance:my world]", text: echo),
            expected: "[world.inhabitants: bodyIndex=0 \"instance:my world]\"]"
        );

        // A text that is not a closed echo is still returned untouched.
        Assert.Equal(
            actual: CommandEcho.SpliceTag(tag: "instance:my world", text: "not an echo"),
            expected: "not an echo"
        );
    }
    [Fact]
    public void HeadAndTextAreDeliberatelyUnquoted() {
        // A head word is a declared literal and Text is a prose segment nobody machine-parses, so neither is put
        // through the quoting rule — only Field values (and spliced tags) are.
        var line = CommandEcho.Open(verb: "world.contributions")
            .Text(text: "slot 'a' state=empty")
            .Segment()
            .Head(head: "listing")
            .Close();

        Assert.Equal(actual: line, expected: "[world.contributions: slot 'a' state=empty | listing]");
    }
    [Fact]
    public void HeadThenFieldsFormsOneSegmentDistinctFromAPureFieldSegment() {
        // world.market's own two segment shapes side by side: config is pure Field tokens (no Head), a listing opens
        // with the declared Head "listing".
        var line = CommandEcho.Open(verb: "world.market")
            .Field(key: "feeBasisPoints", value: 1000)
            .Segment()
            .Head(head: "listing").Field(key: "id", value: 1).Field(key: "status", value: "Active")
            .Close();

        Assert.Equal(actual: line, expected: "[world.market: feeBasisPoints=1000 | listing id=1 status=Active]");
    }
}
