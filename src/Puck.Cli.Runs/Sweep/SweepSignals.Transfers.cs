using System.Globalization;
using System.Text.Json;

namespace Puck.Cli.Sweep;

public static partial class SweepSignals {
    private static readonly string[] TransferStages = ["recorded", "submitted", "completed", "discarded"];
    private static readonly string[] TransferFields = ["host-write-operations", "host-write-bytes", "copy-operations", "buffer-copy-bytes"];

    // Different node names can expose one ledger. Count each domain once, and refuse unsupported or torn aliases.
    private static void ReadGpuTransfers(string output, SortedDictionary<string, string> metrics) {
        const string Prefix = "gpu.transfers.";
        try {
            var text = output.Trim();
            const string Echo = "[world.counters:";
            if (text.StartsWith(Echo, StringComparison.Ordinal) && text.EndsWith(']')) { text = text[Echo.Length..^1].Trim(); }
            using var document = JsonDocument.Parse(text);
            var gpu = document.RootElement.GetProperty("gpu");
            var scope = gpu.GetProperty("transfer-scope").GetString();
            if (scope != "registered-render-device-ledgers") { throw new InvalidDataException("Unknown cumulative transfer scope."); }
            var domains = new SortedDictionary<long, string>();
            var sums = new long[(TransferStages.Length * TransferFields.Length) + 2];
            foreach (var node in gpu.GetProperty("nodes").EnumerateArray()) {
                var totals = node.GetProperty("transfers");
                if (totals.ValueKind != JsonValueKind.Object) { throw new InvalidDataException($"Node '{node.GetProperty("name").GetString()}' has no cumulative transfer accounting."); }
                var domain = totals.GetProperty("domain").GetInt64();
                if (domain <= 0) { throw new InvalidDataException("A transfer domain must be positive."); }
                if (domains.TryGetValue(domain, out var previous)) {
                    if (previous != totals.GetRawText()) { throw new InvalidDataException($"Transfer domain {domain} changed between its aliases."); }
                    continue;
                }
                domains.Add(domain, totals.GetRawText());
                var submitted = totals.GetProperty("submitted-through").GetInt64();
                var completed = totals.GetProperty("completed-through").GetInt64();
                if (completed < 0 || submitted < completed) { throw new InvalidDataException("Invalid transfer completion watermarks."); }
                sums[^2] = checked(sums[^2] + submitted);
                sums[^1] = checked(sums[^1] + completed);
                for (var stage = 0; stage < TransferStages.Length; stage++) {
                    for (var field = 0; field < TransferFields.Length; field++) {
                        var value = totals.GetProperty(TransferStages[stage]).GetProperty(TransferFields[field]).GetInt64();
                        if (value < 0) { throw new InvalidDataException("A cumulative transfer count is negative."); }
                        var index = (stage * TransferFields.Length) + field;
                        sums[index] = checked(sums[index] + value);
                    }
                }
            }
            if (domains.Count == 0) { throw new InvalidDataException("No transfer domains were reported."); }
            metrics[Prefix + "scope"] = scope;
            metrics[Prefix + "domains"] = string.Join(",", domains.Keys);
            metrics[Prefix + "domain-count"] = domains.Count.ToString(CultureInfo.InvariantCulture);
            metrics[Prefix + "submitted-count"] = sums[^2].ToString(CultureInfo.InvariantCulture);
            metrics[Prefix + "completed-count"] = sums[^1].ToString(CultureInfo.InvariantCulture);
            for (var stage = 0; stage < TransferStages.Length; stage++) {
                for (var field = 0; field < TransferFields.Length; field++) {
                    metrics[Prefix + TransferStages[stage] + "." + TransferFields[field]] = sums[(stage * TransferFields.Length) + field].ToString(CultureInfo.InvariantCulture);
                }
            }
            metrics[Prefix + "coverage"] = "complete";
        } catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or InvalidDataException) {
            metrics.Clear();
            metrics[Prefix + "coverage"] = "unavailable";
            metrics[Prefix + "error"] = error.Message;
        }
    }

    /// <summary>Checks that cumulative endpoint readings cover the same complete ledger domains. A replaced or
    /// missing domain is not zero work; its window is unmeasured rather than a misleading aggregate difference.</summary>
    /// <param name="before">The opening parsed metrics.</param><param name="after">The closing parsed metrics.</param>
    /// <returns>Null for complete coverage, or the reason the window cannot be subtracted.</returns>
    public static string? TransferWindowProblem(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) {
        const string Prefix = "gpu.transfers.";
        if (before.GetValueOrDefault(Prefix + "coverage") != "complete" || after.GetValueOrDefault(Prefix + "coverage") != "complete") {
            return "Cumulative transfer endpoints are unavailable: " + before.GetValueOrDefault(Prefix + "error") + "; " + after.GetValueOrDefault(Prefix + "error");
        }
        if (before.GetValueOrDefault(Prefix + "domains") != after.GetValueOrDefault(Prefix + "domains")) {
            return "Cumulative transfer ledger domains changed during the window; retired/new owners cannot be subtracted as zero.";
        }
        foreach (var (key, previous) in before) {
            if (!long.TryParse(previous, CultureInfo.InvariantCulture, out var start) || key == Prefix + "domains") { continue; }
            if (!after.TryGetValue(key, out var current) || !long.TryParse(current, CultureInfo.InvariantCulture, out var end) || end < start) {
                return $"Cumulative transfer count '{key}' decreased or disappeared.";
            }
        }
        return null;
    }
}
