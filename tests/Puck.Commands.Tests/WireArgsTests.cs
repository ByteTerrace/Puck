using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Exercises the zero-copy trailing-argument view a wire-native handler receives — token joining, optional
/// tokens, numeric parsing, and the agreement between its span mode (the fast path) and its array mode (the
/// System.CommandLine fallback a quoted line takes).</summary>
public sealed class WireArgsTests {
    [Fact]
    public void TailJoinsTokensWithSingleSpacesCollapsingWhitespaceRuns() {
        var registry = new CommandRegistry(modules: [new ProbeModule()]);

        Assert.Equal(expected: "a b c", actual: registry.Submit(line: "wire.tail a\t\t b   c").Output);
    }
    [Fact]
    public void TailStartingPastTheLastTokenIsEmpty() {
        var registry = new CommandRegistry(modules: [new ProbeModule()]);

        Assert.Equal(expected: string.Empty, actual: registry.Submit(line: "wire.tail.from5 a b").Output);
        Assert.Equal(expected: string.Empty, actual: registry.Submit(line: "wire.tail").Output);
    }
    [Fact]
    public void TailRentsTheHeapForATailWiderThanItsStackScratch() {
        var registry = new CommandRegistry(modules: [new ProbeModule()]);
        var tokens = Enumerable.Range(count: 40, start: 0)
            .Select(selector: static index => new string(
                c: ((char)('a' + (index % 26))),
                count: 20
            ))
            .ToArray();
        var joined = string.Join(
            separator: ' ',
            values: tokens
        );

        // 40 twenty-character tokens plus separators is 839 characters — past the 512-character stack scratch, so the
        // join takes the heap branch and must still produce exactly the same text.
        Assert.True(condition: (joined.Length > 512));
        Assert.Equal(expected: joined, actual: registry.Submit(line: $"wire.tail {joined}").Output);
    }
    [Fact]
    public void AnOutOfRangeIndexIsAMissRatherThanAThrow() {
        var registry = new CommandRegistry(modules: [new ProbeModule()]);

        // Is/TryInt/TryFloat all answer "no" — and leave their out values zeroed — for a token that is not there, so a
        // handler can probe an OPTIONAL argument without first checking Count.
        Assert.Equal(expected: "True|False|False|False|0|0", actual: registry.Submit(line: "wire.optional YES").Output);
        Assert.Equal(expected: "False|False|False|False|0|0", actual: registry.Submit(line: "wire.optional").Output);
    }
    [Fact]
    public void NumericTokensParseInvariantlyAndFailAsIndividualTokens() {
        var registry = new CommandRegistry(modules: [new ProbeModule()]);

        Assert.Equal(expected: "7|1.5", actual: registry.Submit(line: "wire.numbers 7 1.5").Output);
        Assert.Equal(expected: "-|1.5", actual: registry.Submit(line: "wire.numbers seven 1.5").Output);
        Assert.Equal(expected: "7|-", actual: registry.Submit(line: "wire.numbers 7 NaN").Output);
    }
    [Fact]
    public void SpanModeAndArrayModeSeeTheSameTokens() {
        var registry = new CommandRegistry(modules: [new ProbeModule()]);

        // The quoted form takes the System.CommandLine fallback and reaches the SAME handler through array mode; a
        // handler must not be able to tell which path carried it.
        Assert.Equal(expected: "3|a b c|c", actual: registry.Submit(line: "wire.shape a b c").Output);
        Assert.Equal(expected: "3|a b c|c", actual: registry.Submit(line: "wire.shape a \"b\" c").Output);
    }

    private sealed class ProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "wire.tail",
                description: "Joins every trailing token.",
                handler: static (_, args) => new CommandResult(Output: args.Tail(start: 0)),
                bindability: CommandBindability.Unbindable
            );
            yield return CommandDefinition.WithWireArgs(
                name: "wire.tail.from5",
                description: "Joins the trailing tokens from index five onward.",
                handler: static (_, args) => new CommandResult(Output: args.Tail(start: 5)),
                bindability: CommandBindability.Unbindable
            );
            yield return CommandDefinition.WithWireArgs(
                name: "wire.optional",
                description: "Reports how the view answers for present and absent tokens.",
                handler: static (_, args) => new CommandResult(Output: string.Join(
                    separator: '|',
                    args.Is(index: 0, value: "yes"),
                    args.Is(index: 9, value: "yes"),
                    args.TryInt(index: 9, value: out var absentInteger),
                    args.TryFloat(index: 9, value: out var absentReal),
                    absentInteger,
                    absentReal
                )),
                bindability: CommandBindability.Unbindable
            );
            yield return CommandDefinition.WithWireArgs(
                name: "wire.numbers",
                description: "Parses an integer then a float, reporting each token's verdict on its own.",
                handler: static (_, args) => new CommandResult(Output: string.Join(
                    separator: '|',
                    (args.TryInt(index: 0, value: out var integer)
                        ? integer.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
                        : "-"),
                    (args.TryFloat(index: 1, value: out var real)
                        ? real.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
                        : "-")
                )),
                bindability: CommandBindability.Unbindable
            );
            yield return CommandDefinition.WithWireArgs(
                name: "wire.shape",
                description: "Reports the token count and the joined tail, whichever mode carried it.",
                handler: static (_, args) => new CommandResult(Output: string.Join(
                    separator: '|',
                    args.Count,
                    args.Tail(start: 0),
                    args[2].ToString()
                )),
                bindability: CommandBindability.Unbindable
            );
        }
    }
}
