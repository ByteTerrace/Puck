using Puck.Commands;

namespace Puck.Cli.Canary;

internal static partial class CanaryCommand {
    // A buffered mutation verb that registers no verb of its own against WorldDeferredVerbEchoes leaves the universal
    // tick-boundary narration — "[world.mutation: <Describe> applied]" accepted, "[world.mutation rejected:
    // <Describe> — …]" refused, always on stderr — as its only observable answer. Describe's text is unique per
    // WorldMutation case (WorldServer.Describe.cs), so the prefix below names the verb's own records.
    private static readonly IReadOnlyDictionary<string, string> NarratedMutationVerbs = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
        ["world.generate"] = "Generate '",
        ["world.state.cell.remove"] = "RemoveStateCell '",
        ["world.state.cell.set"] = "UpsertStateCell '",
    };

    // Per-verb command-claim accounting shared by a single-process leg and a federated mesh leg's primary authority.
    // The World frames every result and narration as one ConsoleRecord, and a verb's records arrive in the order its
    // submissions were made, so the i-th record a verb's submissions answered pairs with that verb's i-th authored
    // occurrence. Each pair is checked against its declared stream: accepted implies stdout unless StreamOverride says
    // otherwise, except a narrated verb (NarratedMutationVerbs), whose only signal is stderr narration; refused always
    // implies stderr. wire.errors additionally carries the runner-owned terminal call, one beyond the authored ones.
    internal static IReadOnlyList<CanaryAssertionResult> EvaluateCommandAccounting(IReadOnlyList<CanaryCommandClaim> commands, IReadOnlyList<CliProcessOutputLine> outputLines) {
        var results = new List<CanaryAssertionResult>();
        var expectedRefusals = commands.Count(predicate: static claim => (claim.Outcome == CanaryCommandOutcome.Refused));
        var byVerb = commands.GroupBy(
            keySelector: static claim => claim.Verb,
            comparer: StringComparer.Ordinal
        ).ToList();
        var records = ResponseRecords(
            outputLines: outputLines,
            verbs: [.. byVerb.Select(selector: static group => group.Key)]
        );

        foreach (var group in byVerb) {
            var responseEvents = records[group.Key];
            var claims = group.OrderBy(keySelector: static claim => claim.Occurrence).ToList();
            var terminalAdjustment = ((group.Key == "wire.errors")
                ? 1
                : 0
            );
            var countPassed = (responseEvents.Count == (claims.Count + terminalAdjustment));

            results.Add(item: new CanaryAssertionResult(
                Detail: $"accounted {group.Key}: {responseEvents.Count} response(s) for {claims.Count} authored occurrence(s){((terminalAdjustment == 1)
                ? " plus terminal observation"
                : string.Empty)}",
                Passed: countPassed
            ));

            if (countPassed) {
                for (var index = 0; (index < claims.Count); index++) {
                    var expectedStream = (((claims[index].StreamOverride ?? DefaultStream(
                        outcome: claims[index].Outcome,
                        verb: group.Key
                    )) == CanaryStream.Stdout)
                        ? CliProcessOutputStream.Stdout
                        : CliProcessOutputStream.Stderr
                    );

                    results.Add(item: new CanaryAssertionResult(
                        Detail: $"{group.Key} occurrence {claims[index].Occurrence} was {claims[index].Outcome.ToString().ToLowerInvariant()}",
                        Passed: (responseEvents[index].Stream == expectedStream)
                    ));
                }
            }
        }

        var terminalResponses = outputLines
            .Where(predicate: static line => line.Line.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "[wire.errors:"
        ))
            .ToArray();
        var expectedTerminal = $"[wire.errors: {expectedRefusals} rejected]";
        var terminalPassed = ((terminalResponses.Length != 0) && (terminalResponses[^1].Stream == CliProcessOutputStream.Stdout) && string.Equals(
            a: terminalResponses[^1].Line,
            b: expectedTerminal,
            comparisonType: StringComparison.Ordinal
        ));

        results.Add(item: new CanaryAssertionResult(
            Detail: $"runner-owned terminal observation is exact '{expectedTerminal}' after all authored commands",
            Passed: terminalPassed
        ));

        return results;
    }

    // Refused always answers on stderr; accepted answers on stdout unless the verb is narration-only (above).
    private static CanaryStream DefaultStream(CanaryCommandOutcome outcome, string verb) =>
        (((outcome == CanaryCommandOutcome.Accepted) && !NarratedMutationVerbs.ContainsKey(key: verb))
            ? CanaryStream.Stdout
            : CanaryStream.Stderr
        );

    // The records one verb's submissions answered, in arrival order.
    internal static List<CliProcessOutputLine> ResponseEvents(IReadOnlyList<CliProcessOutputLine> outputLines, string verb) =>
        ResponseRecords(
            outputLines: outputLines,
            verbs: [verb]
        )[verb];
    // Every record's opening line, attributed to the verb whose submission it answers. A record opens on a line the
    // console did not indent (ConsoleRecord); its indented lines are the same answer, whichever stream's lines arrived
    // between them. An opening line "[<token>…" answers the verb its token names, or, for a sub-named facet such as
    // "[world.state.row 'a' …]", the longest listed verb the token extends by a dot; every record answers one
    // submission, so the listed verbs are the only ones its token can name. A narrated verb's records are the
    // universal mutation narration carrying its Describe prefix.
    internal static Dictionary<string, List<CliProcessOutputLine>> ResponseRecords(IReadOnlyList<CliProcessOutputLine> outputLines, IReadOnlyList<string> verbs) {
        var records = verbs.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static _ => new List<CliProcessOutputLine>(),
            keySelector: static verb => verb
        );

        foreach (var line in outputLines) {
            if (ConsoleRecord.IsContinuation(line: line.Line)) {
                continue;
            }

            if (AnsweringVerb(
                line: line.Line,
                verbs: verbs
            ) is { } verb) {
                records[verb].Add(item: line);
            }
        }

        return records;
    }

    private static string? AnsweringVerb(string line, IReadOnlyList<string> verbs) {
        if (!line.StartsWith(value: '[')) {
            return null;
        }

        var end = line.AsSpan().IndexOfAny(values: ": ]");
        var token = ((end < 0)
            ? line.AsSpan(start: 1)
            : line.AsSpan(
                length: (end - 1),
                start: 1
            ));
        string? answering = null;

        foreach (var verb in verbs) {
            if (NarratedMutationVerbs.TryGetValue(
                key: verb,
                value: out var describePrefix
            )) {
                if (
                    line.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: $"[world.mutation: {describePrefix}"
                ) ||
                    line.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: $"[world.mutation rejected: {describePrefix}"
                )
                ) {
                    return verb;
                }

                continue;
            }

            var names = (token.SequenceEqual(other: verb) || (
                token.StartsWith(comparisonType: StringComparison.Ordinal, value: verb) &&
                (token.Length > verb.Length) &&
                (token[verb.Length] == '.')
            ));

            if (
                names &&
                ((answering is null) || (verb.Length > answering.Length))
            ) {
                answering = verb;
            }
        }

        return answering;
    }
}
