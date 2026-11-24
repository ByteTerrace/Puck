using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Puck.Cli.Bench;

/// <summary>
/// The short, human-readable page a benchmark report is written with (<c>summary.md</c>): for each run the device, window,
/// topology, backend and build it measured and its verdict on the machine, then per chapter its arrival, frame-time
/// statistics, frame-thread parts, heaviest GPU passes, counted work per iteration, memory census and captures. Every
/// figure is read from <c>report.json</c>; the page adds none of its own.
/// </summary>
internal static class BenchmarkSummary {
    // The counters a chapter's work per iteration is read from: each line's label and the counter names whose change is
    // summed for it.
    private static readonly (string Label, string[] Counters)[] Work = [
        ("captures", ["counters.sdf.captures.run"]),
        ("records restaged", ["counters.sdf.instances.records.posed"]),
        ("field queries marched", ["counters.world.field-queries.surface.marched", "counters.world.field-queries.clearance.marched"]),
        ("queue executes", ["counters.gpu.queue.executes"]),
    ];

    /// <summary>Writes the summary of a report.</summary>
    /// <param name="report">The report (<c>puck.bench.v1</c>) as written.</param>
    /// <returns>The page's text.</returns>
    public static string Of(JsonObject report) {
        var text = new StringBuilder();
        var benchmark = (report["benchmark"] as JsonObject ?? []);
        var build = (report["build"]?["version"]?.GetValue<string>() ?? "unknown build");

        _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"# Benchmark: {benchmark["route"]} on {benchmark["world"]}\n\n");
        _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"Build {build}. Conditions: {report["conditions"]}. {(report["complete"]?.GetValue<bool>() == true ? "Complete" : "INCOMPLETE")}.\n");
        foreach (var run in (benchmark["runs"] as JsonArray ?? []).OfType<JsonObject>()) {
            Run(run: run, text: text);
        }
        foreach (var refused in (benchmark["unrun"] as JsonArray ?? []).OfType<JsonObject>()) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"\n## {refused["leg"]}: not run\n\n{refused["reason"]}\n");
        }

        return text.ToString();
    }

    private static void Run(JsonObject run, StringBuilder text) {
        var conditions = (run["conditions"] as JsonObject ?? []);
        var window = (run["window"] as JsonObject);
        var topology = (run["topology"] as JsonObject);
        var profile = (run["profile"] as JsonObject);
        var adapter = (run["adapter"]?["adapter"]?.GetValue<string>() ?? profile?["adapter"]?.GetValue<string>() ?? "no adapter");
        var reasons = BenchmarkConditions.ReasonsOf(run: run);

        _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"\n## {run["leg"] ?? run["route"]} · {run["backend"]} · {adapter}\n\n");
        _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"- Device profile {profile?["profile"] ?? "none"} (table {profile?["table"] ?? "none"}); window {(window is null ? "none" : $"{window["width"]}x{window["height"]} {window["presentation"]}")}{((conditions["frameBar"] is { } bar) ? $", sized by --frame-bar {bar}" : string.Empty)}; budget {Number(node: run["budgetMilliseconds"]):0.00} ms ({run["budgetSource"]}).\n");
        _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"- Topology {topology?["kind"] ?? "none"}{((topology?["endpoint"] is { } endpoint) ? $" ({endpoint})" : string.Empty)}{((conditions["seats"] is { } seats) ? $", {seats} seat(s)" : string.Empty)}; start {run["start"]}; {run["iterations"]} iteration(s){((Number(node: run["iterationsDropped"]) > 0) ? $", {run["iterationsDropped"]} DROPPED past the recording" : string.Empty)}.\n");
        _ = text.Append(value: ((reasons.Count > 0)
            ? $"- Machine: DISTURBED, its timings are not a verdict: {string.Join(separator: "; ", values: reasons)}.\n"
            : ((run["machine"]?["verdict"]?.GetValue<string>() == BenchmarkConditions.Unwatched)
                ? "- Machine: UNWATCHED: no verb read the rest of the machine while it played; only the adapter's clock was judged.\n"
                : "- Machine: clean.\n")));
        if (run["complete"]?.GetValue<bool>() != true) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"- Ended early: {run["ended"]}.\n");
        }

        foreach (var loop in (run["loops"] as JsonArray ?? []).OfType<JsonObject>()) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"\n### Loop {loop["index"]} ({loop["kind"]})\n\n");
            _ = text.Append(value: "| Chapter | Arrival | Iterations | p50 | p95 | p99 | Worst | Hitches | Disturbed |\n|---|---|---|---|---|---|---|---|---|\n");
            foreach (var chapter in (loop["chapters"] as JsonArray ?? []).OfType<JsonObject>()) {
                var interval = (chapter["frames"]?["interval"] as JsonObject);
                var disturbed = (chapter["disturbed"] as JsonArray ?? []).Count;

                _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"| {chapter["name"]} | {chapter["arrival"]} | {chapter["frames"]?["iterations"]} | {Ms(node: interval?["p50"])} | {Ms(node: interval?["p95"])} | {Ms(node: interval?["p99"])} | {Ms(node: chapter["worst"]?["workMs"])} | {chapter["hitches"]?["count"]} | {((disturbed == 0) ? "no" : "YES")} |\n");
            }
            foreach (var chapter in (loop["chapters"] as JsonArray ?? []).OfType<JsonObject>()) {
                Chapter(chapter: chapter, text: text);
            }
        }
        Captures(run: run, text: text);
    }

    private static void Chapter(JsonObject chapter, StringBuilder text) {
        _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"\n#### {chapter["name"]}\n\n");
        foreach (var reason in (chapter["disturbed"] as JsonArray ?? []).OfType<JsonValue>()) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"- DISTURBED: {reason}\n");
        }
        if (chapter["failure"]?["reason"] is { } failure) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"- FAILED: {failure}\n");
        }

        var iterations = Number(node: chapter["frameThread"]?["iterations"]);

        if (iterations > 0) {
            foreach (var (label, member) in new[] { ("producing a frame", "producePartsMs"), ("stepping the worlds", "simulatePartsMs") }) {
                var parts = (chapter["frameThread"]?[member] as JsonObject ?? []).Select(selector: part => (Name: part.Key, Ms: (Number(node: part.Value) / iterations))).ToArray();

                _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"- Frame thread, {label}: {parts.Sum(selector: static part => part.Ms):0.00} ms an iteration").Append(value: string.Concat(values: parts
                    .Where(predicate: static part => (part.Ms >= 0.05))
                    .OrderByDescending(keySelector: static part => part.Ms)
                    .Select(selector: static part => string.Create(provider: CultureInfo.InvariantCulture, handler: $", {part.Name} {part.Ms:0.00}")))).Append(value: '\n');
            }
        }
        if (chapter["gpu"] is JsonArray { Count: > 0 } passes) {
            _ = text.Append(value: "- GPU passes, mean ms a frame (heaviest eight): ").Append(value: string.Join(separator: ", ", values: passes.OfType<JsonObject>()
                .OrderByDescending(keySelector: static pass => Number(node: pass["meanMs"]))
                .Take(count: 8)
                .Select(selector: static pass => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{pass["pass"]} {Number(node: pass["meanMs"]):0.00} (max {Number(node: pass["maxMs"]):0.00})")))).Append(value: '\n');
        }

        var readings = (chapter["readings"] as JsonObject ?? []);
        var frames = Change(readings: readings, name: "counters.render.record.frames");
        var work = new List<string> { string.Create(provider: CultureInfo.InvariantCulture, handler: $"allocated {Number(node: chapter["frames"]?["allocatedBytesPerIteration"]):0} B an iteration") };

        if (chapter["submitted"] is JsonObject submitted) {
            work.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"dispatches {Number(node: submitted["dispatchesMean"]):0} a frame (max {submitted["dispatchesMax"]}), draws {Number(node: submitted["drawsMean"]):0} (max {submitted["drawsMax"]}) over {submitted["samples"]} samples"));
        }

        foreach (var (label, counters) in Work) {
            var sum = counters.Select(selector: name => Change(readings: readings, name: name)).Where(predicate: static change => change is not null).Sum(selector: static change => change!.Value);

            if (counters.Any(predicate: name => readings.ContainsKey(propertyName: name))) {
                work.Add(item: ((frames is > 0) ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"{label} {(sum / frames.Value):0.##} a recorded frame") : string.Create(provider: CultureInfo.InvariantCulture, handler: $"{label} {sum:0} in the chapter")));
            }
        }
        _ = text.Append(value: "- Work: ").Append(value: string.Join(separator: ", ", values: work)).Append(value: '\n');

        if (chapter["census"] is JsonObject census) {
            var host = (census["host"] as JsonObject);
            var device = (census["device"] as JsonObject);

            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"- Memory at the end: host private {host?["privateMiB"] ?? "?"} MiB, working set {host?["workingSetMiB"] ?? "?"} MiB{((host?["barMiB"] is { } hostBar) ? $" (bar {hostBar} MiB)" : string.Empty)}; device {((device?["peakMiB"] is { } peak) ? $"peak {peak} MiB + {device["beyondLedgerMiB"]} MiB beyond the ledger, budget {device["budgetMiB"]} MiB{((device["barMiB"] is { } deviceBar) ? $", bar {deviceBar} MiB" : string.Empty)}{((device["overBar"]?.GetValue<bool>() == true) ? ", OVER THE BAR" : string.Empty)}" : (device?["unmeasured"]?.ToString() ?? "?"))}.\n");
            if (census["residencies"] is JsonArray residencies) {
                _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"- Residencies ({residencies.Count}): {string.Join(separator: ", ", values: residencies.OfType<JsonObject>().Select(selector: static row => $"{row["name"]} ({row["groups"]} groups)"))}\n");
            }
            if (census["duplicates"] is JsonArray { Count: > 0 } duplicates) {
                _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"- DUPLICATE residencies, one world's groups held twice: {string.Join(separator: "; ", values: duplicates.OfType<JsonObject>().Select(selector: static pair => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{string.Join(separator: " and ", values: (pair["residencies"] as JsonArray ?? []).Select(selector: static name => name!.ToString()))} share about {Number(node: pair["shared"]):P0} of their groups")))}\n");
            }
        }
    }

    private static void Captures(JsonObject run, StringBuilder text) {
        if (run["captures"] is not JsonArray { Count: > 0 } captures) { return; }

        _ = text.Append(value: "\n### Captures, after the timed run\n\n");
        foreach (var capture in captures.OfType<JsonObject>()) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"- {capture["name"]}: {capture["path"] ?? capture["refused"]}{((capture["partial"]?.GetValue<bool>() == true) ? $" (PARTIAL, waits released unsettled: {capture["settle"]})" : string.Empty)}\n");
        }
    }

    private static double? Change(JsonObject readings, string name) => ((readings[name]?["change"] is JsonValue value) && value.TryGetValue<double>(value: out var change)) ? change : null;

    private static double Number(JsonNode? node) => (((node is JsonValue value) && value.TryGetValue<double>(value: out var number)) ? number : 0.0);

    private static string Ms(JsonNode? node) => ((node is JsonValue value) && value.TryGetValue<double>(value: out var number)) ? number.ToString(format: "0.00", provider: CultureInfo.InvariantCulture) : "-";
}
