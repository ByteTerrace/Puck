using System.CommandLine;
using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Hosting;
using static Puck.Demo.CommandArgs;

namespace Puck.Demo;

/// <summary>
/// The live win/reveal-condition editor verbs — "the recursion": the win/reveal conditions that gated the editor are
/// themselves re-forgeable in-session, so a player (or an agent over stdin) can re-author the very meta gate that
/// unlocked the workshop. Every verb wraps the overworld's condition machinery through <see cref="IOverworldControlHost"/>
/// (the primitive-typed control seam the reveal/link/cart verbs already ride, reached lazily through the node's one
/// blessed <see cref="ICreatorModeHost.CreatorFrameSource"/> seam), so a piped stdin script drives the edit and reads
/// the echoed result:
/// <list type="bullet">
/// <item><c>condition.show &lt;cabinet&gt;</c> — echo the cabinet's current exit + victory condition (for assertions).</item>
/// <item><c>condition.set &lt;cabinet&gt; exit &lt;0xADDR&gt;&lt;op&gt;&lt;value&gt;</c> — set/replace the exit condition
/// (e.g. <c>condition.set 1 exit 0xC004&gt;=1</c>); a bad spec echoes a usage line, never throws.</item>
/// <item><c>condition.set &lt;cabinet&gt; victory solo target=&lt;guid&gt;</c> — set/replace a solo victory.</item>
/// <item><c>condition.set &lt;cabinet&gt; victory meta target=&lt;guid&gt; share=&lt;guid&gt; [group=&lt;g&gt;]</c> — set/replace a meta victory.</item>
/// <item><c>condition.clear &lt;cabinet&gt; exit|victory</c> — remove a condition.</item>
/// </list>
/// The edit is host-side authoring: the deterministic simulation hash never learns a condition changed (it is the
/// FOURTH-WALL / reveal instrumentation the host polls, not sim state). When the root is not the overworld every verb
/// returns "[condition.*: unavailable — the overworld is not the active root]".
///
/// PERSISTENCE SEAM (unwired this stage — USER DECISION: no persistence for now, cloud saves near-future): a re-forged
/// condition WOULD persist on the world document's <c>cabinet:&lt;n&gt;</c> placement (see
/// <c>src/Puck.Demo/World/WorldDocument.cs</c>) — the same serializable seam a cloud save syncs — but <c>world.save</c>
/// does NOT yet carry conditions, so a re-forge is session-only for now. The run-document schema is UNCHANGED: conditions
/// already exist there (<c>GamingBrickSource.Exit</c>/<c>.Victory</c>); live editing changes no schema.
/// </summary>
internal sealed class ConditionCommandModule : ICommandModule {
    // The overworld root, captured at construction (its frame source — the actual control host — is built lazily on the
    // node's first ProduceFrame, so resolve THROUGH it, exactly like OverworldControlCommandModule). Null for a
    // non-overworld root — every verb then reports unavailable.
    private readonly ICreatorModeHost? m_creatorHost;

    public ConditionCommandModule(IRenderNode rootNode) {
        m_creatorHost = (rootNode as ICreatorModeHost);
    }

    // The live control host (the frame source), resolved lazily through the node's one blessed seam — null until the
    // node's first ProduceFrame builds it, and for a non-overworld root.
    private IOverworldControlHost? Host => (m_creatorHost?.CreatorFrameSource as IOverworldControlHost);

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: "Echoes a cabinet's current exit + victory condition for assertions: condition.show <cabinet>.",
            handler: (_, args) => new CommandResult((Host is not { } host)
                ? Unavailable(verb: "condition.show")
                : (TryParseIndex(args: args, at: 0, value: out var index)
                    ? host.ShowCondition(index: index)
                    : "[condition.show: usage — condition.show <cabinet>]")),
            name: "condition.show"
        );
        yield return WithArgs(
            description: "Sets/replaces a cabinet's win/reveal condition: condition.set <cabinet> exit <0xADDR><op><value> | victory solo target=<guid> | victory meta target=<guid> share=<guid> [group=<g>].",
            handler: (_, args) => new CommandResult(SetCondition(args: args)),
            name: "condition.set"
        );
        yield return WithArgs(
            description: "Removes a cabinet's condition: condition.clear <cabinet> exit|victory.",
            handler: (_, args) => new CommandResult((Host is not { } host)
                ? Unavailable(verb: "condition.clear")
                : ((TryParseIndex(args: args, at: 0, value: out var index) && (args.Length > 1))
                    ? host.ClearCondition(index: index, which: args[1])
                    : "[condition.clear: usage — condition.clear <cabinet> exit|victory]")),
            name: "condition.clear"
        );
    }

    // Routes `condition.set <cabinet> exit <spec>` / `<cabinet> victory <mode> <tokens...>` to the control host. Forgiving
    // (usage strings beat parser throws on a game console): a missing/garbled channel or spec echoes a usage line.
    private string SetCondition(string[] args) {
        if (Host is not { } host) {
            return Unavailable(verb: "condition.set");
        }

        if (!TryParseIndex(args: args, at: 0, value: out var index) || (args.Length < 2)) {
            return "[condition.set: usage — condition.set <cabinet> exit <0xADDR><op><value> | victory solo|meta target=<guid> [share=<guid>] [group=<g>]]";
        }

        var channel = args[1];

        if (string.Equals(a: channel, b: "exit", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            // The exit spec is the single token after "exit"; a shell may also split "0xC004 >= 1" into pieces, so join
            // the remainder before parsing (the frame-source parser trims and locates the op itself).
            var spec = string.Join(separator: "", values: args[2..]);

            return host.SetExitConditionSpec(index: index, spec: spec);
        }

        if (string.Equals(a: channel, b: "victory", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            if (args.Length < 3) {
                return "[condition.set: usage — condition.set <cabinet> victory solo target=<guid> | victory meta target=<guid> share=<guid> [group=<g>]]";
            }

            var mode = args[2];
            var tokens = ((args.Length > 3) ? args[3..] : []);

            return host.SetVictoryConditionSpec(index: index, mode: mode, tokens: tokens);
        }

        return "[condition.set: usage — the channel must be 'exit' or 'victory']";
    }

    private static string Unavailable(string verb) =>
        $"[{verb}: unavailable — the overworld is not the active root]";

    // Parses a required non-negative index at position `at`.
    private static bool TryParseIndex(string[] args, int at, out int value) {
        value = 0;

        return ((args.Length > at) && TryParseInt(text: args[at], value: out value) && (value >= 0));
    }

    // An argument-taking console verb: one trailing token list, parsed by the handler (uniform + forgiving — usage
    // strings beat parser errors on a game console). Mirrors OverworldControlCommandModule.WithArgs.
    private static CommandDefinition WithArgs(string description, Func<CommandContext, string[], CommandResult> handler, string name) {
        var rest = new Argument<string[]>(name: "args") {
            Arity = ArgumentArity.ZeroOrMore,
            Description = description,
        };

        return new CommandDefinition(
            Description: description,
            Handler: context => handler(arg1: context, arg2: (context.Parse?.GetValue(argument: rest) ?? [])),
            Name: name,
            TextCommand: new Command(description: description, name: name) {
                rest,
            },
            ValueKind: CommandValueKind.Digital
        );
    }
}
