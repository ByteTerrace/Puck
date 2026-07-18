using System.CommandLine;
using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Hosting;
using static Puck.Commands.CommandArgs;

namespace Puck.Demo;

/// <summary>
/// The engine-narration console verbs — the demo's face on <see cref="TickTranscriptRecorder"/>, so an agent
/// debugging over the pipe can ask "what ran last tick, and what did it do to the state hash" instead of guessing
/// from side effects:
/// <list type="bullet">
/// <item><c>tick.explain [n] [full]</c> — narrates the last n recorded ticks (default 1, max <see cref="MaxExplainTicks"/>):
/// commands dispatched, hash before→after, or "quiet tick" when nothing ran. <c>full</c> shows the whole 64-bit
/// hash instead of the short 8-digit form.</item>
/// <item><c>tick.watch on|off</c> — toggles the same narration echoed live, once per tick (off by default).</item>
/// <item><c>hash.mark &lt;label&gt;</c> / <c>hash.marks</c> — records the current state hash under a label and lists
/// every mark taken — the cheap divergence-bisection primitive: mark, act, mark, compare across two scripted runs.</item>
/// </list>
/// Every verb wraps <see cref="IOverworldControlHost"/>, exactly like <c>OverworldControlCommandModule</c>; when the
/// root is not the overworld, every verb reports unavailable.
/// </summary>
internal sealed class IntrospectionCommandModule : ICommandModule {
    private const int MaxExplainTicks = 32;

    private readonly ICreatorModeHost? m_creatorHost;
    private readonly TickTranscriptRecorder m_recorder;

    /// <summary>Initializes the module over the overworld root (for lazy Host resolution) and the shared recorder.</summary>
    public IntrospectionCommandModule(IRenderNode rootNode, TickTranscriptRecorder recorder) {
        ArgumentNullException.ThrowIfNull(argument: rootNode);
        ArgumentNullException.ThrowIfNull(argument: recorder);

        m_creatorHost = (rootNode as ICreatorModeHost);
        m_recorder = recorder;
    }

    private IOverworldControlHost? Host => (m_creatorHost?.CreatorFrameSource as IOverworldControlHost);

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: "Narrates the last n recorded ticks (default 1, max 32): commands dispatched, hash before->after, or 'quiet tick'. Append 'full' for whole 64-bit hashes: tick.explain [n] [full].",
            handler: (_, args) => new CommandResult(Explain(args: args)),
            name: "tick.explain"
        );
        yield return WithArgs(
            description: "Toggles a live per-tick echo of the same tick.explain narration: tick.watch on|off (default off).",
            handler: (_, args) => new CommandResult(ToggleWatch(args: args)),
            name: "tick.watch"
        );
        yield return WithArgs(
            description: "Records the current state hash under a label: hash.mark <label> (re-issue the same label to overwrite). Cheap divergence bisection over the pipe: mark, act, mark, compare across two scripted runs.",
            handler: (_, args) => new CommandResult(((Host is not { } host)
                ? Unavailable(verb: "hash.mark")
                : ((args.Length > 0)
                    ? MarkHash(host: host, label: args[0])
                    : "[hash.mark: usage — hash.mark <label>]"))),
            name: "hash.mark"
        );
        yield return Plain(
            description: "Lists every hash.mark label with its tick and full hash.",
            handler: _ => new CommandResult(m_recorder.DescribeMarks()),
            name: "hash.marks"
        );
    }

    private string Explain(string[] args) {
        m_recorder.EnsureWired();

        var count = 1;
        var full = false;

        foreach (var arg in args) {
            if (string.Equals(a: arg, b: "full", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                full = true;
            } else if (TryParseInt(text: arg, value: out var parsed)) {
                count = parsed;
            }
        }

        count = Math.Clamp(value: count, max: MaxExplainTicks, min: 1);

        var entries = m_recorder.Transcript.LastEntries(count: count);

        if (entries.Count == 0) {
            return "[tick.explain: no ticks recorded yet]";
        }

        return string.Join(separator: '\n', values: entries.Select(selector: entry => TickNarration.Describe(entry: entry, tag: "tick.explain", full: full)));
    }
    private string ToggleWatch(string[] args) {
        m_recorder.EnsureWired();

        if (!TryParseOnOff(args: args, value: out var value)) {
            return "[tick.watch: usage — tick.watch on|off]";
        }

        m_recorder.Watch = value;

        return $"[tick.watch: {(value ? "on" : "off")}]";
    }
    private string MarkHash(IOverworldControlHost host, string label) {
        m_recorder.EnsureWired();

        var (tick, hash) = host.CurrentTickState();

        return m_recorder.Mark(label: label, tick: tick, hash: hash);
    }
    private static string Unavailable(string verb) =>
        $"[{verb}: unavailable — the overworld is not the active root]";

    // Parses a required on/off token (case-insensitive) — mirrors OverworldControlCommandModule's terminal toggle.
    private static bool TryParseOnOff(string[] args, out bool value) {
        value = false;

        if (args.Length == 0) {
            return false;
        }

        if (string.Equals(a: args[0], b: "on", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            value = true;

            return true;
        }

        return string.Equals(a: args[0], b: "off", comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    // A no-argument console verb.
    private static CommandDefinition Plain(string description, Func<CommandContext, CommandResult> handler, string name) =>
        CommandDefinition.Verb(description: description, handler: handler, name: name, valueKind: CommandValueKind.Digital);

    // An argument-taking console verb: one trailing token list, parsed by the handler. Mirrors
    // OverworldControlCommandModule.WithArgs.
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
