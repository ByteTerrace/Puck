using Puck.Commands;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Drives <see cref="WorldCommandArguments"/> through a real registry, because its whole job is to agree with
/// the two tokenizers a submitted line can travel: the wire-native one that splits on
/// <see cref="char.IsWhiteSpace(char)"/>, and System.CommandLine's, which every quoted line falls through to.</summary>
public sealed class WorldCommandArgumentsTests {
    // Each row is a submitted line and the tail its probe verb reconstructs. The probes: tail.probe echoes the raw
    // tail; tail.after strips one address token; tail.between strips one leading and an optional trailing token;
    // tail.json keeps quotes; tail.keyword and tail.deep (one address token deeper) report a trailing `force` word.
    public static TheoryData<string, string> Lines() => new() {
        // A quoted line goes through System.CommandLine's splitter, which strips the pair; reading the raw line back
        // must not re-introduce it, and the interior spacing the raw line carries survives.
        { "tail.probe \"{world.tick} ticks\"", "{world.tick} ticks" },
        { "tail.probe \"a   b\"", "a   b" },
        { "tail.probe \"\"", "" },
        // Only a single surrounding pair is removed: more than one token or an escaped run is returned verbatim.
        { "tail.probe \"a\" \"b\"", "\"a\" \"b\"" },
        { "tail.probe {\"kind\": \"row\"}", "{\"kind\": \"row\"}" },
        { "tail.probe plain tail", "plain tail" },
        // The registry's tokenizer splits on char.IsWhiteSpace, so every separator it accepts is found here too.
        { "tail.probe\v{world.tick}", "{world.tick}" },
        { "tail.probe\f{world.tick}", "{world.tick}" },
        { "tail.probe\t{world.tick}", "{world.tick}" },
        { "tail.probe {world.tick}", "{world.tick}" },
        // An address strips exactly its leading tokens; fewer tokens than it names carry no tail.
        { "tail.after two the rest", "the rest" },
        { "tail.after two \"quoted rest\"", "quoted rest" },
        { "tail.after\vtwo\vthe rest", "the rest" },
        { "tail.after two", "" },
        { "tail.after \"an address\" the rest", "the rest" },
        // identity.hud's grammar, <panel-json> [player]: the trailing token is found by the tokenizer's own
        // whitespace rule and by the same quote-aware splitter that counted it.
        { "tail.between {\"id\":\"hp\"}\v3", "{\"id\":\"hp\"}" },
        { "tail.between {\"id\":\"hp\"}\f3", "{\"id\":\"hp\"}" },
        { "tail.between {\"id\":\"hp\"}\t3", "{\"id\":\"hp\"}" },
        { "tail.between {\"id\":\"hp\"} 3", "{\"id\":\"hp\"}" },
        { "tail.between {\"id\":\"hp\"}", "{\"id\":\"hp\"}" },
        { "tail.between \"{a}\" \"x y\"", "{a}" },
        { "tail.between {a} {b} \"x y\"", "{a} {b}" },
        // world.load's grammar, <path> [force]: the keyword is case-insensitive and decided off the registry's own
        // tokens; a path merely containing it is not it, and a lone word is the tail rather than the flag.
        { "tail.keyword w.json\vforce", "w.json|force" },
        { "tail.keyword w.json\fforce", "w.json|force" },
        { "tail.keyword w.json\tforce", "w.json|force" },
        { "tail.keyword w.json force", "w.json|force" },
        { "tail.keyword w.json FORCE", "w.json|force" },
        { "tail.keyword w.json", "w.json|plain" },
        { "tail.keyword my forced.json", "my forced.json|plain" },
        { "tail.keyword force", "force|plain" },
        // One address token deeper, the keyword floor is three tokens: `tail.deep row force` names a tail `force`.
        { "tail.deep row force", "force|plain" },
        { "tail.deep row w.json force", "w.json|force" },
        { "tail.deep row a tail force", "a tail|force" },
        // A JSON tail keeps its quotes and interior whitespace.
        { "tail.json /title \"my   title\"", "\"my   title\"" },
        { "tail.json /value {\"constant\":42}", "{\"constant\":42}" },
    };
    [MemberData(memberName: nameof(Lines))]
    [Theory]
    public void EveryLineReconstructsTheTailItsGrammarNames(string line, string expected) => Assert.Equal(
        expected: expected,
        actual: new CommandRegistry(modules: [new TailProbeModule()]).Submit(line: line).Output
    );

    private sealed class TailProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "tail.json",
                description: "Echoes a JSON tail without stripping quotes.",
                handler: static (context, args) => new CommandResult(Output: WorldCommandArguments.RawAfter(
                    args: in args,
                    context: context,
                    preserveQuotes: true,
                    tokens: 2
                )),
                bindability: CommandBindability.Unbindable
            );
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
