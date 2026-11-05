using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins <see cref="CommandResult.Usage"/> as the one spelling of a grammar refusal: its echo of the offending
/// token, and <see cref="CommandResult.Error"/> refusing a hand-spelled <c>[verb: expected ...]</c>.</summary>
public sealed class CommandUsageRefusalTests {
    [Fact]
    public void UsageNamesTheVerbAndTheDeclaredForm() {
        var result = CommandResult.Usage(form: "<a> [b]", verb: "x.y");

        Assert.True(condition: result.IsError);
        Assert.Equal(actual: result.Output, expected: "[x.y: expected <a> [b]]");
        Assert.Equal(actual: CommandResult.Usage(form: "", verb: "x.y").Output, expected: "[x.y: expected no arguments]");
    }

    [Fact]
    public void UsageEchoesTheTokenItCouldNotRead() {
        Assert.Equal(actual: CommandResult.Usage(form: "[on|quiet]", got: "loud", verb: "wire.ack").Output, expected: "[wire.ack: unrecognized 'loud' — expected [on|quiet]]");
        Assert.Equal(actual: CommandResult.Usage(form: "", got: "x", verb: "x.y").Output, expected: "[x.y: unrecognized 'x' — expected no arguments]");
    }

    [Fact]
    public void TheWireVerbsEchoTheBadWord() {
        var registry = new CommandRegistry(modules: []);

        Assert.Equal(actual: registry.Submit(line: "wire.ack loud").Output, expected: "[wire.ack: unrecognized 'loud' — expected [on|quiet]]");
        Assert.Equal(actual: registry.Submit(line: "wire.errors now").Output, expected: "[wire.errors: unrecognized 'now' — expected [reset]]");
    }

    [Theory]
    [InlineData("[body.fly: expected 7 values]")]
    [InlineData("[world.wait: expected <ticks>]")]
    public void AHandSpelledGrammarRefusalIsRefused(string text) {
        _ = Assert.Throws<ArgumentException>(testCode: () => CommandResult.Error(output: text));
    }

    [Theory]
    [InlineData("[body.fly: body 3 is pending]")]
    [InlineData("[world.target: 'x' is not a Hz value — expected is not special here]")]
    [InlineData("not bracketed: expected")]
    public void OtherRefusalsAreUnchanged(string text) {
        Assert.Equal(actual: CommandResult.Error(output: text).Output, expected: text);
    }
}
