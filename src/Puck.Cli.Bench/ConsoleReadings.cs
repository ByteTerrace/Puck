using System.Text.Json;
using System.Text.RegularExpressions;
using Puck.Cli.Sweep;

namespace Puck.Cli.Bench;

/// <summary>How a console answer's readings are named: bare pairs, under the residency each report block names, under
/// the words that lead each <c>|</c>-separated part of a <c>host.memory</c> answer, from a counters JSON answer, or not
/// read as pairs at all.</summary>
internal enum ReadingParse { Pairs, Residency, Memory, Counters, Text }

/// <summary>A reading some plan names that a console answer carries, looked for by key in the answers. The key is a
/// suffix, so a residency's block prefix and a counter's source prefix both match.</summary>
/// <param name="Read">The reading the key belongs to.</param>
/// <param name="Key">The key's name, or the suffix after the block prefix.</param>
/// <param name="Meaning">What the reading counts.</param>
internal sealed record ReadingKey(string Read, string Key, string Meaning);

/// <summary>The readings a World's console answers hold, read as named numbers: the one parser a frame window and a
/// benchmark chapter share to take the change of a counter across a span.</summary>
internal static partial class ConsoleReadings {
    /// <summary>The readings that name streaming, memory and GPU queue work, by the console line that answers them.</summary>
    public static readonly ReadingKey[] Keys = [
        new("frames", "steps.shed", "ticks shed: whole owed steps dropped when catch-up spent its wall-time budget"),
        new("residency", "owed", "bytes of mesh words the region still owes"),
        new("residency", "owed-frames", "frames the region spent owing words"),
        new("residency", "tiers-owed", "groups drawing a coarser tier than their demand asks"),
        new("residency", "owed-error", "the worst such group's cell, in display pixels"),
        new("residency", "worst-owed-error", "the worst owed-error since the bake started"),
        new("residency", "worst-owed-frames", "the longest a group went owing a tier since the bake started"),
        new("residency", "refusals", "meshes the region refused"),
        new("residency", "relocations", "arena relocations"),
        new("residency", "late-stagings", "stagings a residency's preparation did not make"),
        new("residency", "unbound", "background-work pumps that found no binding to upload against"),
        new("residency", "region-refused", "meshes the region's budget refused"),
        new("render.record", "render.record.frames", "frames the render graph recorded"),
        new("render.record", "render.record.failed", "recordings that failed"),
        new("render.record", "render.record.longest-microseconds", "the longest recording"),
        new("gpu.queue", "gpu.queue.executes", "queue executes"),
        new("memory", "ws", "the process's working set, MiB"),
        new("memory", "private", "the process's private bytes, MiB"),
        new("memory", "budget", "each pool's and segment's budget"),
        new("memory", "spend-limit", "each pool's and segment's spend limit"),
        new("memory", "usage", "each segment's usage as the API reports it"),
        new("memory", "reservation", "each segment's current reservation"),
        new("memory", "ledger-held", "the bytes the backend's ledger placed in each segment"),
        new("memory", "usage-beyond-ledger", "usage past the ledger's held bytes: driver, swapchain and uncounted memory"),
        new("memory", "peak", "each ledger row's peak"),
        new("memory", "released", "each ledger row's released bytes"),
    ];

    [GeneratedRegex(@"(?<![\w.\-])(?<key>[a-z][a-z0-9.\-]*)[=:](?<value>[^\s,\]\[=]+)", RegexOptions.CultureInvariant)]
    private static partial Regex PairPattern();

    [GeneratedRegex(@"residency=(?<name>\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex ResidencyName();

    /// <summary>Matches the number a reading's value opens with.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"^-?\d+(\.\d+)?", RegexOptions.CultureInvariant)]
    public static partial Regex Number();

    /// <summary>Reads the key-value readings of one answer, each key by the name a plan uses; a residency's keys carry its
    /// name.</summary>
    /// <param name="parse">How the answer names its readings.</param>
    /// <param name="answer">The answer, or <see langword="null"/> when the World gave none.</param>
    /// <param name="note">Why an answer holds none: the verb refused, or the World gave no answer; <see langword="null"/>
    /// when it held readings.</param>
    /// <returns>The readings by name.</returns>
    public static SortedDictionary<string, string> Values(ReadingParse parse, string? answer, out string? note) {
        var values = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);

        note = null;
        if (string.IsNullOrWhiteSpace(value: answer)) {
            note = "the pinned line has no answer in the World's log";

            return values;
        }
        switch (parse) {
            case ReadingParse.Counters: {
                var text = answer.Trim();

                if (!text.StartsWith(value: "[world.counters: {", comparisonType: StringComparison.Ordinal) && !text.StartsWith(value: '{')) {
                    note = $"refused: {Truncate(text: text)}";

                    return values;
                }
                try {
                    foreach (var (name, value) in SweepSignals.Read(parse: "counters", output: text, extra: null)) {
                        if (name != "text") { values[name] = value; }
                    }
                } catch (JsonException exception) {
                    note = $"unreadable: {exception.Message}";
                }
                break;
            }
            case ReadingParse.Residency: {
                if (answer.Contains(value: "renderer not built yet", comparisonType: StringComparison.Ordinal) || answer.Contains(value: "unknown command", comparisonType: StringComparison.Ordinal)) {
                    note = $"absent in this World: {Truncate(text: answer.Trim())}";

                    return values;
                }

                var prefix = string.Empty;

                foreach (var line in answer.Split(separator: '\n')) {
                    if (line.Contains(value: "residency-id=", comparisonType: StringComparison.Ordinal)) {
                        prefix = ((ResidencyName().Match(input: line) is { Success: true } name) ? (name.Groups["name"].Value + ".") : string.Empty);
                    }
                    foreach (Match pair in PairPattern().Matches(input: line)) { _ = values.TryAdd(key: (prefix + pair.Groups["key"].Value), value: pair.Groups["value"].Value); }
                }
                break;
            }
            case ReadingParse.Memory:
                foreach (var part in answer.Split(separator: ['|', '\n'])) {
                    // The words before a part's first pair name it: `pool device`, `segment local`, `ledger vulkan.local`.
                    var first = PairPattern().Match(input: part);
                    var head = (first.Success ? part[..first.Index] : part).Trim().TrimStart(trimChar: '[').Replace(oldValue: ": ", newValue: ".").Replace(oldChar: ' ', newChar: '.').Trim(trimChar: '.');
                    var prefix = ((head.Length == 0) ? string.Empty : (head + "."));

                    foreach (Match pair in PairPattern().Matches(input: part)) { _ = values.TryAdd(key: (prefix + pair.Groups["key"].Value), value: pair.Groups["value"].Value); }
                }
                break;
            case ReadingParse.Pairs:
                foreach (Match pair in PairPattern().Matches(input: answer)) { _ = values.TryAdd(key: pair.Groups["key"].Value, value: pair.Groups["value"].Value); }
                break;
        }

        return values;
    }

    /// <summary>Shortens a refusal for a note.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text, or its first 160 characters and an ellipsis.</returns>
    public static string Truncate(string text) => ((text.Length <= 160) ? text : (text[..160] + "..."));
}
