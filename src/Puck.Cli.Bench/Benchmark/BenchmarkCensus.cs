using System.Globalization;
using System.Text.Json.Nodes;

namespace Puck.Cli.Bench;

/// <summary>
/// The memory census of a benchmark chapter: what the process held as the chapter ended, read from the raw answers the
/// World kept at the chapter's end (<c>host.memory</c>, <c>world.residency</c>) and from its device ledger. It names every
/// residency with the digest of the groups it holds, so a world resident twice under two names shows as a duplicate.
/// </summary>
internal static class BenchmarkCensus {
    /// <summary>Reads a chapter's census from its raw reads and its device ledger.</summary>
    /// <param name="chapter">The chapter as the World reported it, with its raw <c>reads</c> still present.</param>
    /// <param name="hostBarBytes">The host bar of the device profile the run played under, or <see langword="null"/>.</param>
    /// <param name="deviceBarBytes">The device bar of that profile, or <see langword="null"/>.</param>
    /// <returns>The census, or <see langword="null"/> when the chapter never ended.</returns>
    public static JsonObject? Of(JsonObject chapter, long? hostBarBytes, long? deviceBarBytes) {
        if (chapter["status"]?.GetValue<string>() != "ended") { return null; }

        string? EndOf(string read) => (chapter["reads"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(predicate: entry => (entry["read"]?.GetValue<string>() == read))?["end"]?.GetValue<string>();

        var census = new JsonObject();
        var memory = ConsoleReadings.Values(parse: ReadingParse.Memory, answer: EndOf(read: "host.memory"), note: out var memoryNote);

        if (memoryNote is null) {
            census["host"] = new JsonObject {
                ["privateMiB"] = Number(values: memory, suffix: "process.private"),
                ["workingSetMiB"] = Number(values: memory, suffix: "process.ws"),
                ["managedHeapMiB"] = Number(values: memory, suffix: "managed.heap"),
                ["barMiB"] = ((hostBarBytes is { } host) ? (host >> 20) : null),
            };
        } else {
            census["host"] = new JsonObject { ["unmeasured"] = memoryNote };
        }
        if (chapter["deviceMemory"] is JsonObject device) {
            var peak = device["peakBytes"]!.GetValue<long>();
            var beyond = device["beyondLedgerBytes"]!.GetValue<long>();
            var budget = device["budgetBytes"]!.GetValue<long>();

            census["device"] = new JsonObject {
                ["peakMiB"] = (peak >> 20),
                ["beyondLedgerMiB"] = (beyond >> 20),
                ["budgetMiB"] = (budget >> 20),
                ["barMiB"] = ((deviceBarBytes is { } bar) ? (bar >> 20) : null),
                ["overBudget"] = ((peak + beyond) >= budget),
                ["overBar"] = ((deviceBarBytes is { } cap) && ((peak + beyond) > cap)),
            };
        } else {
            census["device"] = new JsonObject { ["unmeasured"] = "the World read no device ledger (headless, or unified memory)" };
        }

        var residencyAnswer = EndOf(read: "world.residency");
        var residencies = ConsoleReadings.Values(parse: ReadingParse.Residency, answer: residencyAnswer, note: out var residencyNote);

        if (residencyNote is not null) {
            census["residencies"] = new JsonObject { ["unmeasured"] = residencyNote };

            return census;
        }

        var rows = new JsonArray();
        var byContent = new Dictionary<string, List<string>>(comparer: StringComparer.Ordinal);

        foreach (var name in residencies.Keys.Where(predicate: static key => key.EndsWith(value: ".residency-id", comparisonType: StringComparison.Ordinal)).Select(selector: static key => key[..^".residency-id".Length])) {
            var groups = Number(values: residencies, suffix: $"{name}.groups") ?? 0;
            var content = (residencies.TryGetValue(key: $"{name}.content", value: out var digest) ? digest : null);

            rows.Add(item: new JsonObject {
                ["name"] = name,
                ["groups"] = groups,
                ["content"] = content,
                ["vertexBytes"] = Number(values: residencies, suffix: $"{name}.vertex-bytes"),
                ["regionNamedBytes"] = Number(values: residencies, suffix: $"{name}.region-named"),
                ["regionBudgetBytes"] = Number(values: residencies, suffix: $"{name}.region-budget"),
            });
            if ((content is not null) && (groups > 0)) {
                (byContent.TryGetValue(key: content, value: out var named) ? named : (byContent[content] = [])).Add(item: name);
            }
        }

        census["residencies"] = rows;
        census["duplicates"] = new JsonArray([.. byContent.Values.Where(predicate: static names => (names.Count > 1)).Select(selector: static names => (JsonNode)new JsonArray([.. names.Select(selector: static name => (JsonNode)name)]))]);

        return census;
    }

    // The number a reading's value opens with, by the key it ends with; null where no key ends so.
    private static double? Number(SortedDictionary<string, string> values, string suffix) {
        foreach (var (key, value) in values) {
            if ((key == suffix) || key.EndsWith(value: ("." + suffix), comparisonType: StringComparison.Ordinal)) {
                var match = ConsoleReadings.Number().Match(input: value);

                return (match.Success ? double.Parse(s: match.Value, provider: CultureInfo.InvariantCulture) : null);
            }
        }

        return null;
    }
}
