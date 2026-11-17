using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions;
using Puck.Commands;
using Puck.Launcher;

namespace Puck.World;

internal sealed partial class WorldBenchmarkCommandModule {
    private static readonly string[] LoopKinds = ["cold", "warm"];

    // The kind a loop is reported as: the first is cold, the second warm and every later one steady.
    private static string KindOf(int loop) => ((loop < LoopKinds.Length) ? LoopKinds[loop] : "steady");

    // Writes the run's report and frames, whole or partial, and answers with the summary it also narrates on standard error.
    private CommandResult Finish(string? reason, int dropped, HostFrameRecording? recording) {
        var text = new StringBuilder();
        var report = new JsonObject {
            ["schema"] = "puck.benchmark.run.v1",
            ["route"] = m_route,
            ["routeIdentity"] = m_routeIdentity,
            ["complete"] = (reason is null),
            ["ended"] = (reason ?? "the last chapter ended"),
            ["entry"] = m_entry,
            ["start"] = (m_entry.StartsWith(value: "fresh", comparisonType: StringComparison.Ordinal) ? "fresh" : "attached"),
            ["lockstepAtEntry"] = m_lockstepAtEntry,
            ["rendered"] = (pacing is not null),
            ["budgetMilliseconds"] = m_budget,
            ["loopsRequested"] = m_loops,
            ["iterationsDropped"] = (recording?.Dropped ?? 0L),
            ["iterations"] = (recording?.Count ?? 0),
        };

        _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"[world.benchmark: '{m_route}' {((reason is null) ? "finished" : $"ended early ({reason}; {dropped} armed line(s) dropped)")}; {m_entry}");
        if (recording is null) {
            _ = text.Append(value: "; frames unmeasured: this host keeps no frame timeline");
        } else if (recording.Dropped > 0) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"; {recording.Dropped} iteration(s) past the recording's capacity were not kept");
        }

        report["boot"] = DescribeBoot();

        var loops = new JsonArray();

        foreach (var group in m_chapters.GroupBy(keySelector: static chapter => chapter.Loop)) {
            var chapters = new JsonArray();
            var members = group.ToArray();

            foreach (var chapter in members) {
                var statistics = (((recording is not null) && (chapter.BeginIndex >= 0) && (chapter.EndIndex >= 0)) ? recording.Statistics(budgetMilliseconds: m_budget, from: chapter.BeginIndex, to: chapter.EndIndex) : null);

                chapters.Add(item: Describe(chapter: chapter, recording: recording, statistics: statistics));
                _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"\n  [{KindOf(loop: chapter.Loop)}] \"{chapter.Name}\": {Verdict(chapter: chapter, statistics: statistics)}");
            }

            var loop = new JsonObject { ["index"] = group.Key, ["kind"] = KindOf(loop: group.Key), ["chapters"] = chapters };

            if ((recording is not null) && (members[0].BeginIndex >= 0)) {
                var end = members.Last(predicate: static chapter => (chapter.BeginIndex >= 0));

                loop["run"] = Describe(statistics: recording.Statistics(budgetMilliseconds: m_budget, from: members[0].BeginIndex, to: ((end.EndIndex >= 0) ? end.EndIndex : recording.Count)));
            }
            loops.Add(item: loop);
        }
        report["loops"] = loops;
        if ((recording is not null) && (m_chapters.Count > 0) && (m_chapters[0].BeginIndex >= 0)) {
            var whole = recording.Statistics(budgetMilliseconds: m_budget, from: m_chapters[0].BeginIndex, to: recording.Count);

            report["run"] = Describe(statistics: whole);
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"\n  run: {Frames(statistics: whole)}");
        }

        try {
            _ = Directory.CreateDirectory(path: m_directory);
            File.WriteAllText(contents: report.ToJsonString(options: new JsonSerializerOptions { WriteIndented = true }), path: Path.Combine(path1: m_directory, path2: "run.json"));
            if (recording is not null) {
                WriteFrames(path: Path.Combine(path1: m_directory, path2: "frames.csv"), recording: recording);
            }
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"\n  report: {PuckPaths.Normalize(path: m_directory)}/run.json{((recording is null) ? string.Empty : " and frames.csv")}]");
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"\n  report: not written to {PuckPaths.Normalize(path: m_directory)}: {exception.Message}]");
        }

        m_summary = text.ToString();
        Console.Error.WriteLine(value: m_summary);

        return new CommandResult(Output: m_summary) { IsError = (reason is not null) };
    }

    // The host's first iterations through the first chapter's start: its boot. A host that began no chapter has none.
    private JsonObject? DescribeBoot() {
        if ((timeline is null) || (m_chapters.Count == 0) || (m_chapters[0].BeginIndex < 0)) {
            return null;
        }

        return new JsonObject {
            ["iterations"] = m_bootEnd,
            ["truncated"] = (m_bootDropped > 0),
            ["frames"] = Describe(statistics: timeline.Boot.Statistics(budgetMilliseconds: m_budget, from: 0, to: m_bootEnd)),
        };
    }

    private JsonObject Describe(Chapter chapter, HostFrameRecording? recording, HostFrameStatistics? statistics) {
        var (failed, unjudged, read) = Arrival(chapter: chapter);
        var node = new JsonObject {
            ["name"] = chapter.Name,
            ["loop"] = chapter.Loop,
            ["identity"] = chapter.Program.Identity,
            ["startTick"] = chapter.Start,
            ["durationTicks"] = chapter.Duration,
            ["status"] = ((chapter.EndIndex >= 0) ? "ended" : ((chapter.BeginIndex >= 0) ? "unfinished" : "unreached")),
            ["arrival"] = ((chapter.EndIndex < 0) || !read ? "unmeasured" : ((failed.Count > 0) ? "off course" : "arrived")),
            ["arrivalFailed"] = Strings(values: failed),
            ["arrivalUnjudged"] = Strings(values: unjudged),
            ["pose"] = chapter.Where?.Answer,
            ["stateHash"] = chapter.Hash?.Answer,
            ["disturbed"] = Strings(values: Disturbances(chapter: chapter, statistics: statistics)),
            ["ticksShed"] = chapter.Shed,
            ["firstIteration"] = chapter.BeginIndex,
            ["endIteration"] = chapter.EndIndex,
        };

        if (statistics is not null) {
            node["frames"] = Describe(statistics: statistics);
            if ((recording is not null) && (statistics.WorstIndex >= 0)) {
                var worst = recording.Samples[statistics.WorstIndex];

                node["worst"] = new JsonObject {
                    ["iteration"] = statistics.WorstIndex,
                    ["atSeconds"] = (recording.Milliseconds(ticks: (worst.Start - recording.Samples[chapter.BeginIndex].Start)) / 1000.0),
                    ["intervalMs"] = recording.Milliseconds(ticks: worst.Interval),
                    ["workMs"] = recording.Milliseconds(ticks: worst.Work),
                    ["simulateMs"] = recording.Milliseconds(ticks: worst.Simulate),
                    ["produceMs"] = recording.Milliseconds(ticks: worst.Produce),
                    ["produceBlockedMs"] = recording.Milliseconds(ticks: worst.ProduceBlocked),
                    ["presentMs"] = recording.Milliseconds(ticks: worst.Present),
                    ["pauseMs"] = recording.Milliseconds(ticks: worst.Pause),
                    ["steps"] = worst.Steps,
                };
            }
            if (recording is not null) {
                node["hitches"] = Hitches(chapter: chapter, recording: recording);
            }
        }
        node["gpu"] = DescribeGpu(chapter: chapter);
        node["reads"] = DescribeReads(chapter: chapter);

        return node;
    }

    // The chapter's iterations whose work passed the budget, the longest first: each with its tick, the body's last
    // sampled position and the phases it spent its time in. The count is of every such iteration, the list of the
    // longest few; frames.csv holds them all.
    private JsonObject Hitches(Chapter chapter, HostFrameRecording recording) {
        var over = new List<(int Index, long Work)>();
        var origin = recording.Samples[chapter.BeginIndex].Start;

        for (var index = chapter.BeginIndex; (index < chapter.EndIndex); index++) {
            var work = recording.Samples[index].Work;

            if (recording.Milliseconds(ticks: work) > m_budget) {
                over.Add(item: (index, work));
            }
        }

        var poses = chapter.Poses.Where(predicate: static line => (line.Outcome == "ran")).ToArray();
        var listed = new JsonArray();

        foreach (var (index, _) in over.OrderByDescending(keySelector: static hitch => hitch.Work).ThenBy(keySelector: static hitch => hitch.Index).Take(count: MaxHitchesListed)) {
            var sample = recording.Samples[index];
            var place = poses.LastOrDefault(predicate: line => (line.Tick <= sample.Tick));
            var hitch = new JsonObject {
                ["iteration"] = index,
                ["tick"] = sample.Tick,
                ["atSeconds"] = (recording.Milliseconds(ticks: (sample.Start - origin)) / 1000.0),
                ["intervalMs"] = recording.Milliseconds(ticks: sample.Interval),
                ["workMs"] = recording.Milliseconds(ticks: sample.Work),
                ["inputMs"] = recording.Milliseconds(ticks: sample.Input),
                ["commandsMs"] = recording.Milliseconds(ticks: sample.Commands),
                ["simulateMs"] = recording.Milliseconds(ticks: sample.Simulate),
                ["beginMs"] = recording.Milliseconds(ticks: sample.Begin),
                ["produceMs"] = recording.Milliseconds(ticks: sample.Produce),
                ["produceBlockedMs"] = recording.Milliseconds(ticks: sample.ProduceBlocked),
                ["presentMs"] = recording.Milliseconds(ticks: sample.Present),
                ["waitMs"] = recording.Milliseconds(ticks: sample.Wait),
                ["pauseMs"] = recording.Milliseconds(ticks: sample.Pause),
                ["steps"] = sample.Steps,
                ["collections"] = (sample.Gen0 + sample.Gen1 + sample.Gen2),
            };

            if ((place is not null) && (Pose().Match(input: place.Answer) is { Success: true } pose)) {
                hitch["place"] = new JsonObject {
                    ["sampledTick"] = place.Tick,
                    ["x"] = double.Parse(s: pose.Groups["x"].Value, provider: CultureInfo.InvariantCulture),
                    ["y"] = double.Parse(s: pose.Groups["y"].Value, provider: CultureInfo.InvariantCulture),
                    ["z"] = double.Parse(s: pose.Groups["z"].Value, provider: CultureInfo.InvariantCulture),
                };
            }
            listed.Add(item: hitch);
        }

        return new JsonObject { ["count"] = over.Count, ["listed"] = listed.Count, ["sampleTicks"] = SampleTicks, ["items"] = listed };
    }

    // Each GPU pass's mean and longest over the samples taken in the chapter, each sample a pass's rolling window mean.
    // Null when the host composes no render graph or no sample was complete.
    private JsonNode? DescribeGpu(Chapter chapter) {
        if (gpu is not { Available: true }) {
            return null;
        }
        if (chapter.Gpu.Count == 0) {
            return new JsonArray();
        }

        var passes = new JsonArray();

        foreach (var (pass, (sum, max, count)) in chapter.Gpu.OrderBy(keySelector: static entry => entry.Key, comparer: StringComparer.Ordinal)) {
            passes.Add(item: new JsonObject { ["pass"] = pass, ["meanMs"] = (sum / count), ["maxMs"] = max, ["samples"] = count });
        }

        return passes;
    }

    // The raw answers of the readings taken at both ends of the chapter. An end that never ran has no answer.
    private JsonArray DescribeReads(Chapter chapter) {
        var reads = new JsonArray();

        for (var index = 0; (index < Math.Min(val1: chapter.OpenReads.Count, val2: chapter.CloseReads.Count)); index++) {
            reads.Add(item: new JsonObject {
                ["read"] = ChapterReads[index],
                ["begin"] = AnswerOf(line: chapter.OpenReads[index]),
                ["end"] = AnswerOf(line: chapter.CloseReads[index]),
            });
        }

        return reads;
    }

    private string? AnswerOf(WorldTickScript.Line line) => ((line.Outcome is "ran" or "refused" or "faulted") && m_raw.TryGetValue(key: line, value: out var raw)) ? raw : null;

    private static JsonArray Strings(IEnumerable<string> values) {
        var array = new JsonArray();

        foreach (var value in values) {
            array.Add(item: JsonValue.Create(value: value));
        }

        return array;
    }

    private static JsonObject Describe(HostFrameStatistics statistics) => new() {
        ["iterations"] = statistics.Frames,
        ["seconds"] = statistics.Seconds,
        ["averageRate"] = statistics.AverageRate,
        ["low1"] = statistics.Low1,
        ["low01"] = statistics.Low01,
        ["interval"] = Describe(distribution: statistics.Interval),
        ["work"] = Describe(distribution: statistics.Work),
        ["over"] = statistics.Over,
        ["overTwice"] = statistics.OverTwice,
        ["over50"] = statistics.Over50,
        ["over100"] = statistics.Over100,
        ["pacingDeviationMs"] = statistics.PacingDeviation,
        ["pacingStepMs"] = statistics.PacingStep,
        ["steps"] = statistics.Steps,
        ["gen0"] = statistics.Collections.Gen0,
        ["gen1"] = statistics.Collections.Gen1,
        ["gen2"] = statistics.Collections.Gen2,
        ["pauseTotalMs"] = statistics.PauseTotal,
        ["pauseLongestMs"] = statistics.PauseLongest,
        ["allocatedBytesPerIteration"] = statistics.AllocatedPerFrame,
        ["longestStallMs"] = statistics.LongestStall,
    };

    private static JsonObject Describe(HostFrameDistribution distribution) => new() {
        ["mean"] = distribution.Mean,
        ["p50"] = distribution.P50,
        ["p95"] = distribution.P95,
        ["p99"] = distribution.P99,
        ["p999"] = distribution.P999,
        ["max"] = distribution.Max,
    };

    private void WriteFrames(string path, HostFrameRecording recording) {
        using var writer = new StreamWriter(path: path, append: false, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var samples = recording.Samples;
        var chapter = 0;
        double Ms(long ticks) => recording.Milliseconds(ticks: ticks);

        writer.WriteLine(value: "iteration,loop,chapter,tick,startMs,intervalMs,workMs,inputMs,commandsMs,simulateMs,beginMs,produceMs,produceBlockedMs,presentMs,waitMs,lateMs,steps,gen0,gen1,gen2,pauseMs,allocatedBytes,frameThreadAllocatedBytes,flags");
        for (var index = 0; (index < samples.Length); index++) {
            ref readonly var sample = ref samples[index];

            while ((chapter < m_chapters.Count) && (m_chapters[chapter].EndIndex >= 0) && (index >= m_chapters[chapter].EndIndex)) { chapter++; }

            var inside = ((chapter < m_chapters.Count) && (m_chapters[chapter].BeginIndex >= 0) && (index >= m_chapters[chapter].BeginIndex));

            writer.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{index},{(inside ? m_chapters[chapter].Loop : -1)},{(inside ? chapter : -1)},{sample.Tick},{Ms(sample.Start):0.###},{Ms(sample.Interval):0.###},{Ms(sample.Work):0.###},{Ms(sample.Input):0.###},{Ms(sample.Commands):0.###},{Ms(sample.Simulate):0.###},{Ms(sample.Begin):0.###},{Ms(sample.Produce):0.###},{Ms(sample.ProduceBlocked):0.###},{Ms(sample.Present):0.###},{Ms(sample.Wait):0.###},{Ms(sample.Late):0.###},{sample.Steps},{sample.Gen0},{sample.Gen1},{sample.Gen2},{Ms(sample.Pause):0.###},{sample.Allocated},{sample.ThreadAllocated},{sample.Flags}"));
        }
    }
}
