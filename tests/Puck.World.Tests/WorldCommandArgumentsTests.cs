using Puck.Commands;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Drives <see cref="WorldCommandArguments"/> through a real registry, because its whole job is to agree with
/// the two tokenizers a submitted line can travel: the wire-native one that splits on
/// <see cref="char.IsWhiteSpace(char)"/>, and System.CommandLine's, which every quoted line falls through to.</summary>
public sealed class WorldCommandArgumentsTests {
    private static CommandRegistry Registry() => new(modules: [new TailProbeModule()]);

    [Fact]
    public void ATailSeparatedByExoticWhitespaceIsFoundJustLikeASpaceSeparatedOne() {
        var registry = Registry();

        // The registry's tokenizer splits on char.IsWhiteSpace and the repository deliberately exercises
        // vertical-tab-separated lines, so a reconstruction that only knew ' ' and '\t' answered string.Empty for a
        // line the verb had already accepted — and the verb then refused it with its usage text.
        Assert.Equal(expected: "{world.tick}", actual: registry.Submit(line: "tail.probe\v{world.tick}").Output);
        Assert.Equal(expected: "{world.tick}", actual: registry.Submit(line: "tail.probe\f{world.tick}").Output);
        Assert.Equal(expected: "{world.tick}", actual: registry.Submit(line: "tail.probe\t{world.tick}").Output);
        Assert.Equal(expected: "{world.tick}", actual: registry.Submit(line: "tail.probe {world.tick}").Output);
    }
    [Fact]
    public void AQuotedTailReachesTheHandlerWithItsSurroundingQuotesRemoved() {
        var registry = Registry();

        // A quoted line goes through System.CommandLine's splitter, which strips the pair before WireArgs sees it.
        // Reading the raw line back must not re-introduce it, or `world.hud.template "{world.tick} ticks"` renders its
        // own quotes into the HUD.
        Assert.Equal(expected: "{world.tick} ticks", actual: registry.Submit(line: "tail.probe \"{world.tick} ticks\"").Output);
        // The interior spacing the raw line carries is exactly why this reads Text rather than WireArgs.Tail.
        Assert.Equal(expected: "a   b", actual: registry.Submit(line: "tail.probe \"a   b\"").Output);
        Assert.Equal(expected: string.Empty, actual: registry.Submit(line: "tail.probe \"\"").Output);
    }
    [Fact]
    public void ATailThatIsNotOneQuotedTokenIsReturnedVerbatim() {
        var registry = Registry();

        // Only a single surrounding pair is removed: anything else is either more than one token or an escaped run,
        // and re-tokenizing it would collapse the interior spacing this reconstruction exists to preserve.
        Assert.Equal(expected: "\"a\" \"b\"", actual: registry.Submit(line: "tail.probe \"a\" \"b\"").Output);
        Assert.Equal(expected: "{\"kind\": \"row\"}", actual: registry.Submit(line: "tail.probe {\"kind\": \"row\"}").Output);
        Assert.Equal(expected: "plain tail", actual: registry.Submit(line: "tail.probe plain tail").Output);
    }
    [Fact]
    public void AnAddressedTailStripsExactlyTheLeadingTokensItWasToldTo() {
        var registry = Registry();

        Assert.Equal(expected: "the rest", actual: registry.Submit(line: "tail.after two the rest").Output);
        Assert.Equal(expected: "quoted rest", actual: registry.Submit(line: "tail.after two \"quoted rest\"").Output);
        Assert.Equal(expected: "the rest", actual: registry.Submit(line: "tail.after\vtwo\vthe rest").Output);
        // Fewer tokens than the address names carries no tail at all.
        Assert.Equal(expected: string.Empty, actual: registry.Submit(line: "tail.after two").Output);
    }
    [Fact]
    public void ATrailingPositionalTokenIsStrippedWhateverWhitespacePrecedesIt() {
        var registry = Registry();

        // identity.hud's grammar: <panel-json> [player]. The suffix has to be found by the SAME whitespace rule the
        // registry tokenized the line with — a narrower ' '/'\t' scan finds no separator at all in a vertical-tab
        // separated line, so the player index stayed glued to the payload and the verb tried to parse
        // `{"id":"hp"}\v3` as its JSON.
        Assert.Equal(expected: "{\"id\":\"hp\"}", actual: registry.Submit(line: "tail.between {\"id\":\"hp\"}\v3").Output);
        Assert.Equal(expected: "{\"id\":\"hp\"}", actual: registry.Submit(line: "tail.between {\"id\":\"hp\"}\f3").Output);
        Assert.Equal(expected: "{\"id\":\"hp\"}", actual: registry.Submit(line: "tail.between {\"id\":\"hp\"}\t3").Output);
        Assert.Equal(expected: "{\"id\":\"hp\"}", actual: registry.Submit(line: "tail.between {\"id\":\"hp\"} 3").Output);
        // No trailing token to strip: the whole tail is the payload.
        Assert.Equal(expected: "{\"id\":\"hp\"}", actual: registry.Submit(line: "tail.between {\"id\":\"hp\"}").Output);
    }
    [Fact]
    public void ATrailingTokenIsStrippedByTheSameSplitterThatCountedIt() {
        var registry = Registry();

        // The count comes from the PARSER's token list and the strip from a scan of the raw line, so the two have to
        // read a quoted run the same way. A quote-blind backwards scan found the space INSIDE `"x y"` and answered
        // `"{a}" "x` — the trailing token torn in half and the tail wearing the other half.
        Assert.Equal(expected: "{a}", actual: registry.Submit(line: "tail.between \"{a}\" \"x y\"").Output);
        Assert.Equal(expected: "{a} {b}", actual: registry.Submit(line: "tail.between {a} {b} \"x y\"").Output);
        // A quoted LEADING token is one token to the parser too, so the address strip must not cut it either.
        Assert.Equal(expected: "the rest", actual: registry.Submit(line: "tail.after \"an address\" the rest").Output);
    }
    [Fact]
    public void ADeeperAddressStillFindsItsOptionalKeyword() {
        var registry = Registry();

        // tail.deep spells verb + one address token before its tail. The floor for recognizing the trailing keyword is
        // therefore three tokens, not two: `tail.deep row force` names a tail called `force` after the address `row`,
        // exactly as `world.load force` names a file called `force`. The hard-coded floor of 2 read this as a flagged
        // line with an EMPTY tail.
        Assert.Equal(expected: "force|plain", actual: registry.Submit(line: "tail.deep row force").Output);
        Assert.Equal(expected: "w.json|force", actual: registry.Submit(line: "tail.deep row w.json force").Output);
        Assert.Equal(expected: "a tail|force", actual: registry.Submit(line: "tail.deep row a tail force").Output);
    }
    [Fact]
    public void AnOptionalTrailingKeywordIsRecognizedWhateverWhitespacePrecedesIt() {
        var registry = Registry();

        // world.load's grammar: <path> [force]. The keyword is decided off the registry's own tokens, so the exotic
        // separators the tokenizer splits on are found here too — the ' '/'\t' scan this replaced left `force` glued
        // to the path, and world.load then went looking for a file called `w.json\vforce`.
        Assert.Equal(expected: "w.json|force", actual: registry.Submit(line: "tail.keyword w.json\vforce").Output);
        Assert.Equal(expected: "w.json|force", actual: registry.Submit(line: "tail.keyword w.json\fforce").Output);
        Assert.Equal(expected: "w.json|force", actual: registry.Submit(line: "tail.keyword w.json\tforce").Output);
        Assert.Equal(expected: "w.json|force", actual: registry.Submit(line: "tail.keyword w.json force").Output);
        // Case-insensitive, matching the spelling world.load has always accepted.
        Assert.Equal(expected: "w.json|force", actual: registry.Submit(line: "tail.keyword w.json FORCE").Output);
    }
    [Fact]
    public void AnAbsentTrailingKeywordLeavesTheWholeTailAlone() {
        var registry = Registry();

        Assert.Equal(expected: "w.json|plain", actual: registry.Submit(line: "tail.keyword w.json").Output);
        // A path whose own last token merely CONTAINS the keyword is not the keyword.
        Assert.Equal(expected: "my forced.json|plain", actual: registry.Submit(line: "tail.keyword my forced.json").Output);
        // Nothing precedes the word, so it is the tail rather than the flag: the grammars using this all spell a
        // required tail first, and `world.load force` has always named a file called `force`.
        Assert.Equal(expected: "force|plain", actual: registry.Submit(line: "tail.keyword force").Output);
    }

    private sealed class TailProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "tail.probe",
                description: "Echoes the raw tail after its verb.",
                handler: static (context, args) => new CommandResult(Output: WorldCommandArguments.Raw(
                    args: in args,
                    context: context
                )),
                bindability: CommandBindability.Unbindable
            );
            yield return CommandDefinition.WithWireArgs(
                name: "tail.after",
                description: "Echoes the raw tail after its verb and one address token.",
                handler: static (context, args) => new CommandResult(Output: WorldCommandArguments.RawAfter(
                    args: in args,
                    context: context,
                    tokens: 2
                )),
                bindability: CommandBindability.Unbindable
            );
            yield return CommandDefinition.WithWireArgs(
                name: "tail.between",
                description: "Echoes the raw tail after its verb, less an optional trailing positional token.",
                handler: static (context, args) => new CommandResult(Output: WorldCommandArguments.RawBetween(
                    args: in args,
                    context: context,
                    leadingTokens: 1,
                    trailingTokens: ((args.Count < 2)
                        ? 0
                        : 1)
                )),
                bindability: CommandBindability.Unbindable
            );
            yield return CommandDefinition.WithWireArgs(
                name: "tail.deep",
                description: "Echoes the raw tail after its verb and one address token, less an optional trailing `force` word, then whether that word was there.",
                handler: static (context, args) => {
                    var tail = WorldCommandArguments.RawBeforeKeyword(
                        args: in args,
                        context: context,
                        keyword: "force",
                        leadingTokens: 2,
                        present: out var present
                    );

                    return new CommandResult(Output: $"{tail}|{(present
                        ? "force"
                        : "plain")}");
                },
                bindability: CommandBindability.Unbindable
            );
            yield return CommandDefinition.WithWireArgs(
                name: "tail.keyword",
                description: "Echoes the raw tail after its verb, less an optional trailing `force` word, then whether that word was there.",
                handler: static (context, args) => {
                    var tail = WorldCommandArguments.RawBeforeKeyword(
                        args: in args,
                        context: context,
                        keyword: "force",
                        leadingTokens: 1,
                        present: out var present
                    );

                    return new CommandResult(Output: $"{tail}|{(present
                        ? "force"
                        : "plain")}");
                },
                bindability: CommandBindability.Unbindable
            );
        }
    }
}
