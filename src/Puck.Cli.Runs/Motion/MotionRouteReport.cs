using System.Globalization;
using System.Text.Json;
using Puck.Cli.Sweep;

namespace Puck.Cli.Motion;

/// <summary>Compares semantic workloads; implementation work is reported separately and is allowed to improve.</summary>
public static class MotionRouteReport {
    private static readonly string[] SemanticFields = ["tick", "clockWhole", "clockFractionBits", "timeBits", "deltaBits",
        "alphaBits", "scope", "documentId", "program", "views", "transforms", "viewCount", "transformCount", "width", "height", "meshInstanceCount", "meshAssetSlotCount", "hostDrawCount", "unsupportedIdentityCount", "tickLocked", "convergesLighting"];

    public static IReadOnlyList<string> Compare(MotionRouteLeg first, MotionRouteLeg second, MotionRoutePlan plan) {
        var problems = new List<string>();
        var traces = new List<JsonDocument>();
        try {
            foreach (var leg in new[] { first, second }) {
                problems.AddRange(leg.Problems.Select(problem => $"{leg.Directory}: {problem}"));
                var setup = leg.DirectReceipts.Where(receipt => receipt.Stage == "setup").ToArray();
                if (!setup.Select(receipt => receipt.Script).SequenceEqual(plan.Setup)) { problems.Add($"{leg.Directory}: missing or reordered typed setup receipts."); }
                foreach (var receipt in leg.DirectReceipts) {
                    if (!receipt.Result.Ok || receipt.Result.Unmeasured) { problems.Add($"{leg.Directory}: {receipt.Stage} refused or unmeasured: {receipt.Script}: {string.Join("; ", receipt.Result.Refusals)}"); }
                }
                RequireSettledBoundary(leg, plan, problems);
                foreach (var check in plan.Checks) {
                    foreach (var readings in new[] { leg.BeforeChecks, leg.AfterChecks }) {
                        var answer = readings.TryGetValue(check.Name, out var output) ? new SweepAnswer(true, output, []) : null;
                        var evidence = SweepEvidence.Check(answer, check);
                        if (evidence.Status != SweepStatus.Clean) { problems.Add($"{leg.Directory}: setup check '{check.Name}' failed: {string.Join("; ", evidence.Reasons)}"); }
                    }
                }
                if (!leg.ReplayStatus.Contains("no divergence", StringComparison.Ordinal) ||
                    !leg.ReplayStatus.Contains($"tick {plan.LastTick + 1} of {plan.LastTick + 2}", StringComparison.Ordinal)) {
                    problems.Add($"{leg.Directory}: missing or divergent finite authority replay verdict: {leg.ReplayStatus}");
                }
                if (string.IsNullOrWhiteSpace(leg.Trace)) {
                    problems.Add($"{leg.Directory}: missing presentation trace.");
                    continue;
                }
                try {
                    var trace = JsonDocument.Parse(leg.Trace);
                    traces.Add(trace);
                    var root = trace.RootElement;
                    var samples = root.GetProperty("samples");
                    var errorsBefore = problems.Count;
                    if (!root.GetProperty("elapsedMilliseconds").TryGetDouble(out var elapsed) || !double.IsFinite(elapsed) || elapsed <= 0) {
                        problems.Add($"{leg.Directory}: the measured presentation interval is unavailable or invalid.");
                    }
                    var measuredCount = plan.LastTick - plan.WarmupTicks;
                    if (!root.GetProperty("complete").GetBoolean() || root.GetProperty("sampleCount").GetInt32() != measuredCount ||
                        root.GetProperty("firstTick").GetInt32() != plan.WarmupTicks + 1 || root.GetProperty("lastTick").GetInt32() != plan.LastTick ||
                        root.GetProperty("measureFirstTick").GetInt32() != plan.WarmupTicks + 1 || samples.GetArrayLength() != measuredCount ||
                        root.GetProperty("missing").GetArrayLength() != 0 || root.GetProperty("duplicateTicks").GetArrayLength() != 0 ||
                        root.GetProperty("outOfOrderTicks").GetArrayLength() != 0) {
                        problems.Add($"{leg.Directory}: incomplete, duplicated, out-of-order or incorrectly bounded tick coverage.");
                    }
                    for (var index = 0; index < samples.GetArrayLength(); index++) {
                        if (samples[index].GetProperty("unsupportedIdentityCount").GetInt32() != 0) {
                            problems.Add($"{leg.Directory}: sample {index + 1} has unsupported semantic identities: {root.GetProperty("unsupportedIdentityTypes").GetRawText()}."); break;
                        }
                        if (samples[index].GetProperty("tick").GetInt32() != plan.WarmupTicks + index + 1) { problems.Add($"{leg.Directory}: actual sample {index + 1} has the wrong tick."); break; }
                        if (!samples[index].GetProperty("tickLocked").GetBoolean() || samples[index].GetProperty("convergesLighting").GetBoolean()) {
                            problems.Add($"{leg.Directory}: sample {index + 1} did not use fixed tick clocks with normal budgeted lighting."); break;
                        }
                        foreach (var field in SemanticFields) {
                            if (!samples[index].TryGetProperty(field, out var value)) { problems.Add($"{leg.Directory}: sample {index + 1} lacks '{field}'."); break; }
                            var kind = field switch {
                                "scope" or "documentId" or "program" or "views" or "transforms" or "clockFractionBits" => JsonValueKind.String,
                                "tickLocked" => JsonValueKind.True,
                                "convergesLighting" => JsonValueKind.False,
                                _ => JsonValueKind.Number,
                            };
                            if (value.ValueKind != kind) { problems.Add($"{leg.Directory}: sample {index + 1} has malformed '{field}'."); break; }
                        }
                        if (problems.Count != errorsBefore) { break; }
                    }
                } catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException) {
                    problems.Add($"{leg.Directory}: malformed presentation trace: {exception.Message}");
                }
            }
            if (problems.Count != 0 || traces.Count != 2) { return problems; }
            var left = traces[0].RootElement.GetProperty("samples");
            var right = traces[1].RootElement.GetProperty("samples");
            for (var index = 0; index < left.GetArrayLength(); index++) {
                foreach (var field in SemanticFields) {
                    if (!JsonElement.DeepEquals(left[index].GetProperty(field), right[index].GetProperty(field))) {
                        problems.Add($"First presentation mismatch at tick {plan.WarmupTicks + index + 1}, field '{field}': {left[index].GetRawText()} versus {right[index].GetRawText()}.");
                        return problems;
                    }
                }
            }
            return problems;
        } finally { foreach (var trace in traces) { trace.Dispose(); } }
    }

    // Qualification is checked from typed control receipts too: an otherwise clean runner cannot silently compare
    // samples taken before the warmup's current geometry reached the device, or include the settlement hold.
    private static void RequireSettledBoundary(MotionRouteLeg leg, MotionRoutePlan plan, List<string> problems) {
        List<(string Stage, string Script)> required = [
            ("warmup-resume", "world.rate resume"),
            ("warmup-wait", $"world.wait until {plan.WarmupTicks}"),
            ("warmup-paused", "world.rate pause"),
            ("warmup-residency", "world.wait residency 600"),
        ];
        required.AddRange(plan.Checks.Select(check => ("before-check", check.Line)));
        required.Add(("trace-begin", $"world.view.trace begin {plan.WarmupTicks + 1} {plan.LastTick} {plan.WarmupTicks + 1}"));
        required.Add(("measured-resume", "world.rate resume"));
        var boundary = leg.DirectReceipts.Where(receipt => required.Any(item => item.Stage == receipt.Stage)).ToArray();
        if (!boundary.Select(receipt => (receipt.Stage, receipt.Script)).SequenceEqual(required)) {
            problems.Add($"{leg.Directory}: missing or reordered settled warmup/measurement control receipts.");
        }
        if (!boundary.Any(receipt => receipt.Stage == "warmup-residency" && receipt.Result.Output.StartsWith("[residency: settled", StringComparison.Ordinal))) {
            problems.Add($"{leg.Directory}: the current warmup geometry has no resident completion verdict.");
        }
        foreach (var metric in plan.Metrics) {
            if (!leg.Before.ContainsKey(metric.Name) || !leg.After.ContainsKey(metric.Name)) {
                problems.Add($"{leg.Directory}: missing tick-pinned endpoint reading '{metric.Name}'.");
            }
            if (metric.Parse == "gpu-transfers") {
                var before = SweepSignals.Read(metric.Parse, leg.Before.GetValueOrDefault(metric.Name, string.Empty), null);
                var after = SweepSignals.Read(metric.Parse, leg.After.GetValueOrDefault(metric.Name, string.Empty), null);
                if (SweepSignals.TransferWindowProblem(before, after) is { } reason) { problems.Add($"{leg.Directory}: {reason}"); }
            }
        }
        foreach (var check in plan.Checks) {
            if (!boundary.Any(receipt => receipt.Stage == "before-check" && receipt.Script == check.Line &&
                leg.BeforeChecks.GetValueOrDefault(check.Name) == receipt.Result.Output)) {
                problems.Add($"{leg.Directory}: missing settled before check '{check.Name}'.");
            }
        }
    }

    public static object Metrics(MotionRoutePlan plan, MotionRouteLeg leg) => plan.Metrics.Select(metric => {
        var before = Sweep.SweepSignals.Read(metric.Parse, leg.Before.GetValueOrDefault(metric.Name, string.Empty), null);
        var after = Sweep.SweepSignals.Read(metric.Parse, leg.After.GetValueOrDefault(metric.Name, string.Empty), null);
        var delta = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, value) in after) {
            if (metric.Parse == "gpu-transfers" && SweepSignals.TransferWindowProblem(before, after) is not null) { break; }
            if (key == "gpu.transfers.domains") { continue; }
            if (before.TryGetValue(key, out var previous) && double.TryParse(previous, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(value, CultureInfo.InvariantCulture, out var y)) { delta[key] = y - x; }
        }
        return new { metric.Name, metric.Run, metric.Parse, beforeTick = plan.WarmupTicks + 1, afterTick = plan.LastTick + 1,
            phase = "tick-command drain; opening readings precede authored commands at that tick", before, after, delta };
    }).ToArray();

    public static void Write(string directory, MotionRoutePlan plan, string routePath, string tapeSha256,
        MotionRouteLeg recorded, MotionRouteLeg a, MotionRouteLeg b, IReadOnlyList<string> problems) {
        foreach (var leg in new[] { recorded, a, b }) {
            File.WriteAllText(Path.Combine(leg.Directory, "leg.json"), JsonSerializer.Serialize(leg, new JsonSerializerOptions { WriteIndented = true }));
            if (leg.Trace.Length != 0) { File.WriteAllText(Path.Combine(leg.Directory, "trace.json"), leg.Trace + "\n"); }
        }
        var report = new {
            mode = "controlled-tick-throughput", normalRealtimeFrameBar = false,
            observerEnabledInBothLegs = true, timingIncludesObserverWork = true,
            timingIncludesAuthorityReplayVerification = true, lighting = "normal-budgeted",
            settlement = "Authority pauses after warmup while the current geometry becomes resident and its upload completes. Typed setup checks follow settlement; presentation observation covers only the measured ticks.",
            metricWindow = new { beforeTick = plan.WarmupTicks + 1, afterTick = plan.LastTick + 1,
                phase = "Both endpoints run in the tick-command drain. Opening metrics precede all authored input/presentation commands at the first measured tick; paused settlement work is excluded." },
            metricScope = "Numeric deltas are differences of tick-pinned endpoint readings: monotonic counters describe interval work; gauge deltas describe endpoint changes. The presentation timer spans the first through last measured sample, ending one tick before the closing metric drain. GPU summary answers are individual completed-submission snapshots. gpu-transfers metrics instead subtract same-domain cumulative prefixes: submitted work was accepted between endpoints; completed work became known complete between endpoints and may include earlier in-flight work. Host writes are CPU bytes, buffer copies include readback/carry traffic, image bytes and physical bus traffic are not inferred.",
            memoryScope = "Process and memory readings include the diagnostic observer and authority replay machinery. Normal-play physical budget qualification is a separate workload.",
            equivalent = problems.Count == 0, unmatched = problems,
            equivalenceEvidence = "Canonical per-tick authority replay verdicts and 64-bit semantic content signatures; diagnostic equivalence evidence, not a formal collision-free byte proof.",
            route = Path.GetFullPath(routePath).Replace('\\', '/'), plan,
            canonicalTapeSha256 = tapeSha256,
            first = new { a.Artifact, a.ArtifactSha256, a.BuildFiles, a.BuildSourceKey, a.ReplayStatus, a.PostWindowImage, a.DirectReceipts, a.BeforeChecks, a.AfterChecks, timing = Timing(a), metrics = Metrics(plan, a) },
            second = new { b.Artifact, b.ArtifactSha256, b.BuildFiles, b.BuildSourceKey, b.ReplayStatus, b.PostWindowImage, b.DirectReceipts, b.BeforeChecks, b.AfterChecks, timing = Timing(b), metrics = Metrics(plan, b) },
            comparison = "The embedded route plan plus canonical tape hash identify the workload. Resolved movement/events replay once; authored presentation controls are also reapplied. InputMasked does not cover every authority submission. Authority verdicts cover warmup and the measured interval through lastTick+1; the tape's final guard tick is not consumed. Setup postconditions and measured per-tick presentation signatures must match. Work, allocations, storage and transfers are results; they need not match.",
        };
        File.WriteAllText(Path.Combine(directory, "route-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
    private static object? Timing(MotionRouteLeg leg) {
        if (leg.Trace.Length == 0) { return null; }
        try {
            using var trace = JsonDocument.Parse(leg.Trace);
            var root = trace.RootElement;
            return new {
                elapsedMilliseconds = root.GetProperty("elapsedMilliseconds").Clone(),
                firstTick = root.GetProperty("measureFirstTick").GetInt32(),
                lastTick = root.GetProperty("lastTick").GetInt32(),
                interpretation = "Monotonic interval between the first and last measured presentation samples, with fixed tick clock, presentation observer and per-tick authority replay verification, no screenshot convergence; not a normal-paced frame bar."
            };
        } catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException) {
            return new { unavailable = exception.Message };
        }
    }
}
