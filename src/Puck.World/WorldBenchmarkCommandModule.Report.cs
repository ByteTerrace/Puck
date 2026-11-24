using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions;
using Puck.Abstractions.Memory;
using Puck.Commands;
using Puck.Hosting;
using Puck.Hosting.Jobs;
using Puck.Hosting.Telemetry;
using Puck.Launcher;

namespace Puck.World;

public sealed partial class WorldBenchmarkCommandModule {
    private static readonly string[] LoopKinds = ["cold", "warm"];

    // The kind a loop is reported as: the first is cold, the second warm and every later one steady.
    private static string KindOf(int loop) => ((loop < LoopKinds.Length) ? LoopKinds[loop] : "steady");

    // The device profile the run played under: what was selected and how, the table and caps it applied, and the limits
    // the process account admitted against while it ran.
    private JsonNode ProfileReport() {
        var selection = (deviceProfile?.Current ?? WorldDeviceProfileState.Selection.None);
        var host = MemoryAccount.Shared.ReadAdmission(pool: PhysicalMemory.Host);
        var device = MemoryAccount.Shared.ReadAdmission(pool: PhysicalMemory.Device);

        return new JsonObject {
            ["profile"] = selection.Profile,
            ["source"] = selection.Source,
            ["adapter"] = selection.Adapter,
            ["table"] = selection.Table,
            ["deviceMemoryCapBytes"] = selection.DeviceMemoryBytes,
            ["hostMemoryCapBytes"] = selection.HostMemoryBytes,
            ["deviceMemoryLimitBytes"] = device.Limit,
            ["hostMemoryLimitBytes"] = host.Limit,
        };
    }
    // The adapter's state over the whole run, from its first chapter's start to its last chapter's end (or now), with the
    // provider and the ceilings every chapter's reading is judged against. A host with no provider reports why.
    private JsonNode? AdapterReport() {
        if (m_adapter is not { } adapter) { return null; }

        var begun = m_chapters.Where(predicate: static chapter => (chapter.BeginStamp != 0L)).ToArray();
        var from = ((begun.Length == 0) ? 0L : begun[0].BeginStamp);
        var to = (((begun.Length == 0) || begun.Any(predicate: static chapter => (chapter.EndStamp == 0L))) ? 0L : begun[^1].EndStamp);

        return AdapterNode(summary: adapter.Summarize(from: from, to: to), whole: true);
    }

    private static JsonNode? AdapterNode(AdapterTelemetrySummary? summary, bool whole) {
        if (summary is null) { return null; }

        static JsonNode? Spread(AdapterSpread? spread) => ((spread is { } value) ? new JsonObject { ["min"] = value.Min, ["median"] = value.Median, ["max"] = value.Max } : null);

        var node = new JsonObject();

        if (whole) {
            node["provider"] = summary.Info.Provider;
            node["adapter"] = summary.Info.Adapter;
            node["maxGraphicsClockMhz"] = summary.Info.MaxGraphicsClockMhz;
            node["maxMemoryClockMhz"] = summary.Info.MaxMemoryClockMhz;
            node["powerLimitWatts"] = summary.Info.PowerLimitWatts;
            node["unavailable"] = summary.Info.Unavailable;
        }

        node["samples"] = summary.Samples;
        node["loadedSamples"] = summary.LoadedSamples;
        node["loadedAtUtilizationPercent"] = AdapterTelemetrySummary.LoadedUtilizationPercent;
        node["graphicsClockMhz"] = Spread(spread: summary.GraphicsClockMhz);
        node["loadedGraphicsClockMhz"] = Spread(spread: summary.LoadedGraphicsClockMhz);
        node["memoryClockMhz"] = Spread(spread: summary.MemoryClockMhz);
        node["powerWatts"] = Spread(spread: summary.PowerWatts);
        node["utilizationPercent"] = Spread(spread: summary.UtilizationPercent);
        node["temperatureCelsius"] = Spread(spread: summary.TemperatureCelsius);
        node["performanceStates"] = new JsonObject([.. summary.PerformanceStates.Select(selector: static entry => KeyValuePair.Create<string, JsonNode?>(key: $"P{entry.Key.ToString(provider: CultureInfo.InvariantCulture)}", value: entry.Value))]);
        node["clockEvents"] = new JsonObject([.. summary.ClockEventSamples.Select(selector: static entry => KeyValuePair.Create<string, JsonNode?>(key: entry.Key.ToString(), value: entry.Value))]);
        node["judged"] = (summary.NotJudged is null);
        node["notJudged"] = summary.NotJudged;
        node["heldReason"] = summary.HeldReason;

        return node;
    }

    // The topology the route played in, from the seat's own route as the run ends: the authority the seat's client is
    // routed to, whether this process advances that authority's clock (in-process) or another does (remote), and the
    // witness that the seat's body is there, which is the body that authority last delivered at the route's address. A
    // seat whose route still names a world this process steps is an in-process reading, whatever the run was started
    // with.
    private JsonNode TopologyReport() {
        var slot = (m_driver?.Seat ?? 0);

        if (seatRouter?.TryRoute(slot: slot) is not { } route) {
            return new JsonObject { ["endpoint"] = null, ["kind"] = "none" };
        }

        var endpoint = route.Endpoint;
        var delivered = endpoint.Mirror.TryReadBody(body: out var body, index: route.EntityIndex);
        var ready = (delivered && (body.Generation == route.Entity.Generation) && (endpoint.ClockOwnedHere || (endpoint.Mirror.HasStreamDelivery && !ReferenceEquals(objA: endpoint.Definition, objB: WorldProjection.Undisclosed))));

        return new JsonObject {
            ["endpoint"] = endpoint.Identity,
            ["kind"] = (endpoint.ClockOwnedHere ? "in-process" : "remote"),
            ["authority"] = endpoint.Authority,
            ["seat"] = (slot + 1),
            ["body"] = route.EntityIndex,
            ["generation"] = route.Entity.Generation,
            ["bodyDelivered"] = ready,
            ["deliveredAtTick"] = (ready ? body.Tick : null),
        };
    }

    // Writes the run's report and frames, whole or partial, and answers with the summary it also narrates on standard error.
    private CommandResult Finish(string? reason, int dropped, HostFrameRecording? recording) {
        var text = new StringBuilder();
        var report = new JsonObject {
            ["schema"] = "puck.benchmark.run.v2",
            ["route"] = m_route,
            ["routeIdentity"] = m_routeIdentity,
            ["complete"] = (reason is null),
            ["ended"] = (reason ?? "the last chapter ended"),
            ["entry"] = m_entry,
            ["start"] = (m_entry.StartsWith(value: "fresh", comparisonType: StringComparison.Ordinal) ? "fresh" : "attached"),
            ["lockstepAtEntry"] = m_lockstepAtEntry,
            ["rendered"] = ((host is null) ? (pacing is not null) : !host.Headless),
            ["budgetMilliseconds"] = m_budget,
            ["budgetSource"] = m_budgetSource,
            // What a report folded from this run alone (a player build's) states for itself: its build, backend and machine.
            ["build"] = WorldSchema.SourceRevision,
            ["backend"] = ((host is null) ? null : (host.Headless ? "headless" : (host.HostsOnDirectX ? "directx" : "vulkan"))),
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["cpus"] = Environment.ProcessorCount,
            ["window"] = ((host is null || host.Headless) ? null : new JsonObject { ["presentation"] = host.Presentation.ToString(), ["width"] = host.Width, ["height"] = host.Height, ["presentMode"] = host.PresentMode.ToString() }),
            ["loopsRequested"] = m_loops,
            ["iterationsDropped"] = (recording?.Dropped ?? 0L),
            ["iterations"] = (recording?.Count ?? 0),
            ["overlay"] = new JsonObject { ["enabledAtEntry"] = m_overlayAtEntry, ["changedDuringRun"] = m_overlayChanged },
            ["gpu"] = (gpuTiming?.Capture() ?? new JsonObject { ["state"] = "absent" }),
            ["profile"] = ProfileReport(),
            ["adapter"] = AdapterReport(),
            ["topology"] = TopologyReport(),
            ["preparation"] = m_prepared,
        };

        m_prepared = null;
        m_adapter?.Dispose();
        m_adapter = null;

        gpuTiming?.RequireRecording(false);

        _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"[world.benchmark: '{m_route}' {((reason is null) ? "finished" : $"ended early ({reason}; {dropped} armed line(s) dropped)")}; {m_entry}");
        if (report["preparation"] is JsonObject prepared) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"; {prepared["what"]} prepared in {prepared["seconds"]!.GetValue<double>():0.0} s before the run, outside its frames");
        }
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
            ["arrival"] = ((chapter.EndIndex < 0) ? "unmeasured" : (chapter.Driven ? (chapter.Arrived ? "arrived" : "failed") : (!read ? "unmeasured" : ((failed.Count > 0) ? "off course" : "arrived")))),
            ["seats"] = ((chapter.Seats.Count == 0) ? null : new JsonArray([.. chapter.Seats.Select(selector: static seat => (JsonNode)new JsonObject {
                ["seat"] = (seat.Seat + 1),
                ["arrival"] = (seat.Arrived ? "arrived" : "failed"),
                ["failure"] = seat.Failure,
            })])),
            ["arrivalFailed"] = Strings(values: failed),
            ["arrivalUnjudged"] = Strings(values: unjudged),
            ["pose"] = chapter.EndPose?.Answer,
            ["startPlaces"] = PlacesOf(read: chapter.Opening),
            ["endPlaces"] = PlacesOf(read: chapter.EndPlaces),
            ["placesVisited"] = (chapter.Driven ? Strings(values: chapter.Visited.Select(selector: static visit => visit.Place)) : Visited(chapter: chapter)),
            ["stateHash"] = chapter.Hash?.Answer,
            ["disturbed"] = Strings(values: Disturbances(chapter: chapter, statistics: statistics)),
            ["ticksShed"] = chapter.Shed,
            ["firstIteration"] = chapter.BeginIndex,
            ["endIteration"] = chapter.EndIndex,
        };

        if (chapter.Driven) {
            node["stepsReached"] = chapter.StepReached;
            node["plans"] = chapter.Plans;
            node["controlLines"] = chapter.ControlLines;
            node["visits"] = new JsonArray([.. chapter.Visited.Select(selector: static visit => (JsonNode)new JsonObject { ["tick"] = visit.Tick, ["place"] = visit.Place })]);
            node["delivery"] = chapter.Delivery;
            node["failure"] = ((chapter.Failure is null) ? null : new JsonObject {
                ["kind"] = chapter.FailureKind,
                ["tick"] = chapter.FailureTick,
                ["place"] = chapter.FailurePlace,
                ["reason"] = chapter.Failure,
            });
        }
        if (statistics is not null) {
            node["frames"] = Describe(statistics: statistics);
            if (recording is not null) {
                node["phases"] = Phases(from: chapter.BeginIndex, recording: recording, to: chapter.EndIndex);
            }
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
                node["frameThread"] = DescribeFrameThread(recording, chapter.BeginIndex,
                    chapter.EndIndex < 0 ? recording.Count : chapter.EndIndex);
            }
        }
        node["gpu"] = DescribeGpu(chapter: chapter);
        if (chapter.Submitted is { Count: > 0 } submitted) {
            node["submitted"] = new JsonObject {
                ["samples"] = submitted.Count,
                ["drawsMean"] = (submitted.Draws / (double)submitted.Count),
                ["drawsMax"] = submitted.DrawsMax,
                ["dispatchesMean"] = (submitted.Dispatches / (double)submitted.Count),
                ["dispatchesMax"] = submitted.DispatchesMax,
            };
        }
        if (chapter.EndIndex >= 0) {
            var (peak, budget, beyond) = chapter.Device;

            node["deviceMemory"] = ((budget <= 0L) ? null : new JsonObject {
                ["peakAtBeginBytes"] = chapter.DevicePeakAtBegin,
                ["peakBytes"] = peak,
                ["beyondLedgerBytes"] = beyond,
                ["budgetBytes"] = budget,
            });
        }
        node["adapter"] = AdapterNode(summary: chapter.Adapter, whole: false);
        node["reads"] = DescribeReads(chapter: chapter);

        return node;
    }

    private static JsonObject DescribeFrameThread(HostFrameRecording recording, int first, int end) {
        first = Math.Clamp(first, 0, recording.Count);
        end = Math.Clamp(end, first, recording.Count);
        var worlds = new Dictionary<string, HostWorldWork>(StringComparer.Ordinal);
        HostFrameWorkTimes parts = default;
        long produce = 0, blocked = 0, simulate = 0, renders = 0;
        var samples = recording.Samples;
        for (var index = first; index < end; index++) {
            ref readonly var sample = ref samples[index];
            produce += sample.Produce;
            blocked += sample.ProduceBlocked;
            simulate += sample.Simulate;
            renders += sample.ScheduledRenders;
            for (var part = 0; part < (int)HostFrameWorkPart.Count; part++) { parts[part] += sample.Parts[part]; }
            foreach (ref readonly var world in recording.Worlds(index)) {
                ref var total = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(worlds, world.World, out _);
                total.Steps += world.Steps;
                total.Ticks += world.Ticks;
                for (var part = (int)HostFrameWorkPart.TickIngress; part < (int)HostFrameWorkPart.Count; part++) {
                    total.Parts[part] += world.Parts[part];
                }
            }
        }
        var worldTimes = new JsonObject();
        foreach (var (name, world) in worlds) {
            worldTimes[name] = new JsonObject {
                ["steps"] = world.Steps,
                ["stepMs"] = HostFrameRecording.WorkMilliseconds(world.Ticks),
                ["partsMs"] = DescribeParts(world.Parts, HostFrameWorkPart.TickIngress, HostFrameWorkPart.Count, HostFrameRecording.WorkMilliseconds(world.Ticks)),
            };
        }
        return new JsonObject {
            ["iterations"] = end - first,
            ["scheduledRenders"] = renders,
            ["produceFenceMs"] = recording.Milliseconds(blocked),
            ["producePartsMs"] = DescribeParts(parts, HostFrameWorkPart.Pump, HostFrameWorkPart.TickIngress, recording.Milliseconds(produce - blocked)),
            ["simulatePartsMs"] = DescribeParts(parts, HostFrameWorkPart.TickIngress, HostFrameWorkPart.Count, recording.Milliseconds(simulate)),
            ["worlds"] = worldTimes,
        };
    }

    private static JsonObject DescribeParts(in HostFrameWorkTimes parts, HostFrameWorkPart first, HostFrameWorkPart end, double total) {
        var result = new JsonObject();
        for (var part = (int)first; part < (int)end; part++) {
            var milliseconds = HostFrameRecording.WorkMilliseconds(parts[part]);
            result[((HostFrameWorkPart)part).ToString()] = milliseconds;
            total -= milliseconds;
        }
        result["other"] = total;
        return result;
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

        var poses = chapter.PoseSamples.ToArray();
        var places = chapter.PlaceSamples.ToArray();
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
                ["frameThread"] = DescribeFrameThread(recording, index, index + 1),
            };

            if ((place is not null) && (Pose().Match(input: place.Answer) is { Success: true } pose)) {
                hitch["place"] = new JsonObject {
                    ["sampledTick"] = place.Tick,
                    ["x"] = double.Parse(s: pose.Groups["x"].Value, provider: CultureInfo.InvariantCulture),
                    ["y"] = double.Parse(s: pose.Groups["y"].Value, provider: CultureInfo.InvariantCulture),
                    ["z"] = double.Parse(s: pose.Groups["z"].Value, provider: CultureInfo.InvariantCulture),
                };
            }
            if (places.LastOrDefault(predicate: line => (line.Tick <= sample.Tick)) is { } named) {
                hitch["places"] = PlacesOf(read: named);
                hitch["placesSampledTick"] = named.Tick;
            }
            listed.Add(item: hitch);
        }

        return new JsonObject { ["count"] = over.Count, ["listed"] = listed.Count, ["sampleTicks"] = SampleTicks, ["items"] = listed };
    }

    // Each GPU pass's mean and maximum across the chapter's readings of its rolling per-frame cost.
    // Null when the host composes no render graph or no sample was complete.
    private JsonNode? DescribeGpu(Chapter chapter) {
        if (gpuTiming is not { Available: true }) {
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

    // The innermost place of the chapter's start, of each sample and of its end, in order, a repeat collapsed: the rooms
    // the walk passed through, at the samples' grain.
    private static JsonArray Visited(Chapter chapter) {
        var visited = new List<string>();

        foreach (var read in chapter.PlaceSamples.Prepend(element: chapter.Opening!).Append(element: chapter.EndPlaces!)) {
            if ((PlacesOf(read: read) is { } places) && (((places.Count > 0) ? places[0]!.GetValue<string>() : "none") is var at) && ((visited.Count == 0) || (visited[^1] != at))) {
                visited.Add(item: at);
            }
        }

        return Strings(values: visited);
    }
    // The places a reading of the body named, innermost first; null when there was no reading or it named no place list.
    private static JsonArray? PlacesOf(Read? read) {
        if ((read is null) || (PlaceList().Match(input: read.Answer) is not { Success: true } match)) {
            return null;
        }

        var value = match.Groups["places"].Value;

        return Strings(values: ((value == "none") ? [] : value.Split(separator: ',')));
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

    // Where a span's iterations spent the frame thread's time, each phase apart: taking input, running console lines,
    // stepping the worlds this process simulates, and beginning, producing and presenting a frame. A phase a host has
    // none of (a headless host's three frame phases) reads zero.
    private static JsonObject Phases(HostFrameRecording recording, int from, int to) {
        var samples = recording.Samples;
        var count = Math.Max(val1: 0, val2: (Math.Min(val1: to, val2: samples.Length) - from));
        (string Name, Func<HostFrameSample, long> Of)[] phases = [
            ("inputMs", static sample => sample.Input),
            ("commandsMs", static sample => sample.Commands),
            ("simulateMs", static sample => sample.Simulate),
            ("beginMs", static sample => sample.Begin),
            ("produceMs", static sample => sample.Produce),
            ("produceBlockedMs", static sample => sample.ProduceBlocked),
            ("presentMs", static sample => sample.Present),
        ];
        var node = new JsonObject();

        foreach (var (name, of) in phases) {
            var sum = 0L;
            var max = 0L;

            for (var index = from; (index < (from + count)); index++) {
                var ticks = of(arg: samples[index]);

                sum += ticks;
                max = Math.Max(val1: max, val2: ticks);
            }
            node[name] = new JsonObject {
                ["mean"] = ((count == 0) ? 0.0 : (recording.Milliseconds(ticks: sum) / count)),
                ["max"] = recording.Milliseconds(ticks: max),
            };
        }

        return node;
    }

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
