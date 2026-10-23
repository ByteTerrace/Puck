using System.Globalization;
using System.Text;

using Puck.Commands;
using Puck.Hosting;

namespace Puck.Launcher.Commands;

/// <summary>
/// The host's output surface: <c>host.log</c> reads the <see cref="HostLog"/> ring of the lines this process wrote to
/// standard output and standard error, so an Operator attachment reads the announcements only the launching process
/// otherwise sees. Registered in every terminal shape.
/// </summary>
internal sealed class HostLogCommandModule(HostLog log) : ICommandModule {
    private const string Usage = "host.log [last <n>] [since <seq>] [ticks <from>..<to>] [category <prefix>] [stream out|err] [text <word>|\"<words>\"]";
    private const int DefaultLast = 100;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            description: $"Reads the recent lines this World wrote to stdout and stderr (bake lines, refusals, narration, pacing), each as #<seq> t<tick> out|err <line>, where tick is the one the World had completed when the line was written (t- before the first). {Usage}: last answers the newest n matching lines (default {DefaultLast}); since only lines after a sequence (the header's next since reads on from this read); ticks only lines written at those ticks, either end optional; category only lines whose leading tag ([sdf.mesh-bake] …, [world.wait: …]) starts with the prefix; stream only one stream; text only lines containing the word, or the quoted words, ignoring case. Clauses combine in any order. The World holds its newest {HostLog.Capacity} lines and {HostLog.CharacterBudget} characters, evicting the oldest; command answers reach the session that asked and are not held, refusals are.",
            handler: (_, args) => {
                var query = new HostLogQuery { Last = DefaultLast };

                for (var index = 0; (index < args.Count); index += 2) {
                    if ((index + 1) >= args.Count) {
                        return Refuse();
                    }

                    var value = args[index + 1];

                    if (args.Is(index: index, value: "last") && CommandArgs.TryParseInt(text: value, value: out var last) && (last >= 1)) {
                        query = query with { Last = last };
                    } else if (args.Is(index: index, value: "since") && CommandArgs.TryParseLong(text: value, value: out var since) && (since >= 0)) {
                        query = query with { Since = since };
                    } else if (args.Is(index: index, value: "ticks") && TryTicks(text: value, from: out var from, to: out var to)) {
                        query = query with { FromTick = from, ToTick = to };
                    } else if (args.Is(index: index, value: "text")) {
                        query = query with { Text = value.ToString() };
                    } else if (args.Is(index: index, value: "category")) {
                        query = query with { Category = value.TrimStart(trimChar: '[').ToString() };
                    } else if (args.Is(index: index, value: "stream") && (value.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, other: "out") || value.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, other: "err"))) {
                        query = query with { Stream = (value.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, other: "out") ? HostLogStream.Output : HostLogStream.Error) };
                    } else {
                        return Refuse();
                    }
                }

                return new CommandResult(Output: Describe(read: log.Read(query: query)));
            },
            name: "host.log"
        );
    }

    private static CommandResult Refuse() => CommandResult.Error(output: $"[host.log: expected {Usage}]");
    // <from>..<to>, <from>.. or ..<to>.
    private static bool TryTicks(ReadOnlySpan<char> text, out ulong? from, out ulong? to) {
        from = null;
        to = null;

        var split = text.IndexOf(value: "..");

        if (split < 0) {
            return false;
        }

        var head = text[..split];
        var tail = text[(split + 2)..];

        if (!head.IsEmpty) {
            if (!CommandArgs.TryParseULong(text: head, value: out var first)) {
                return false;
            }

            from = first;
        }
        if (!tail.IsEmpty) {
            if (!CommandArgs.TryParseULong(text: tail, value: out var last)) {
                return false;
            }

            to = last;
        }

        return ((from is not null) || (to is not null)) && ((from ?? 0UL) <= (to ?? ulong.MaxValue));
    }
    private string Describe(HostLogRead read) {
        var builder = new StringBuilder();

        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"[host.log: {read.Lines.Count} of {read.Matched} matching line(s); holds {read.Held} of the newest {log.LineCapacity} lines, {read.Dropped} evicted; next since {read.Newest}]");

        foreach (var line in read.Lines) {
            _ = builder.Append(value: '\n');
            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"#{line.Sequence} t{((line.Tick is { } tick) ? tick.ToString(provider: CultureInfo.InvariantCulture) : "-")} {((line.Stream == HostLogStream.Output) ? "out" : "err")} {line.Text}");
        }

        return builder.ToString();
    }
}
