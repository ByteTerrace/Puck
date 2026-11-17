using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.Abstractions;
using Puck.Commands;
using Puck.Launcher;
using Puck.World.Protocol;
using Puck.World.Server;
using Puck.World.Transpiler.Composition;

namespace Puck.World;

/// <summary>
/// <c>world.benchmark</c>: plays a route through the running world in real time and reports every host iteration of
/// it. A route is the <c>test</c> blocks of one <c>.puck</c> source, each a chapter: its <c>when</c> lines are the
/// chapter's controller program and its <c>expect</c> lines its arrival. The program is the schedule the compiler
/// lowers the block to, the rows a headless <c>puck test</c> submits, and each row is armed through the seat's own
/// ingress (<see cref="WorldTickScript.ArmSeat"/>) at its tick, so the live chapter and the headless test submit the
/// same lines through the same door in the same tick phase.
/// </summary>
/// <remarks>
/// <para>Nothing in a run pauses, settles or waits: the world keeps its own pacing, and the run records what the host
/// did (<see cref="HostFrameRecording"/>). A chapter whose span the run cannot vouch for is written disturbed with
/// the reason, and its statistics are still written beside it: lockstep presentation in any iteration, the simulation
/// stepping no tick for <see cref="StallMilliseconds"/>, or a step the seat's ingress refused.</para>
/// <para>Every member runs on the host pump.</para>
/// </remarks>
/// <param name="script">The pinned-line scheduler a run arms.</param>
/// <param name="router">Resolves the live seat router.</param>
/// <param name="source">The running document's source, which a relative route source resolves beside.</param>
/// <param name="timeline">The host's frame timeline, or <see langword="null"/> on a host that keeps none.</param>
/// <param name="pacing">The window pump's pacing control, or <see langword="null"/> without a window.</param>
/// <param name="stateRoot">The run's state root, which reports are written under, or <see langword="null"/>.</param>
public sealed partial class WorldBenchmarkCommandModule(WorldTickScript script, Func<InputRouter> router, WorldDefinitionSource source, HostFrameTimeline? timeline = null, PresentPacingControl? pacing = null, WorldStateRoot? stateRoot = null) : ICommandModule {
    /// <summary>The host work, in milliseconds, past which an iteration is over by default: one 60 Hz period.</summary>
    public const double DefaultBudgetMilliseconds = (1000.0 / 60.0);
    /// <summary>The wall time, in seconds, after which a run that has not finished is ended by default.</summary>
    public const int DefaultTimeoutSeconds = 900;
    /// <summary>The ticks from the tick a run is armed at to its first chapter's first line.</summary>
    public const int LeadTicks = 4;
    /// <summary>The ticks between one chapter's end and the next chapter's first line.</summary>
    public const int GapTicks = 2;
    /// <summary>The time, in milliseconds, the simulation may step no tick before a chapter is disturbed.</summary>
    public const double StallMilliseconds = 250.0;
    /// <summary>The flag an iteration carries when presentation was locked to ticks as it closed.</summary>
    public const int LockstepFlag = 1;

    private static readonly CommandForms WorldBenchmarkForms = CommandForms.Of(verb: "world.benchmark", "run <source.puck> [<chapter name prefix>|-] [at <tick>] [budget <ms>] [timeout <seconds>] [output <directory>]", "stop");

    private sealed class Chapter(string name, ulong start, int duration, int body, IReadOnlyList<string> gates) {
        public string Name { get; } = name;
        public ulong Start { get; } = start;
        public int Duration { get; } = duration;
        public int Body { get; } = body;
        public IReadOnlyList<string> Gates { get; } = gates;
        public List<(WorldTickScript.Line Line, bool Refusal)> Steps { get; } = [];
        public WorldTickScript.Line? Where { get; set; }
        public WorldTickScript.Line? Hash { get; set; }
        public int BeginIndex { get; set; } = -1;
        public int EndIndex { get; set; } = -1;
        public long ShedAtBegin { get; set; }
        public long Shed { get; set; }
    }

    private readonly List<Chapter> m_chapters = [];
    // The controls the armed program holds at its end or wherever it is stopped, by seat slot and source, with the line
    // that lets each go.
    private readonly Dictionary<(int Slot, string Source), string> m_held = [];
    private HostFrameRecording? m_recording;
    private string m_route = string.Empty;
    private string m_directory = string.Empty;
    private string m_summary = "[world.benchmark: no run yet — world.benchmark run <source.puck> plays one]";
    private string m_entry = string.Empty;
    private double m_budget = DefaultBudgetMilliseconds;
    private long m_deadline;
    private bool m_lockstepAtEntry;
    private bool m_running;

    [GeneratedRegex(pattern: @"^bodyPosition\(body, (?<body>\d+), (?<axis>[xyz])\) (?<op>>=|<=|==|!=|>|<) (?<value>-?\d+(?:\.\d+)?)$", options: RegexOptions.CultureInvariant)]
    private static partial Regex AxisGate();
    [GeneratedRegex(pattern: @"^fact\(body, (?<body>\d+), Grounded\) == 1$", options: RegexOptions.CultureInvariant)]
    private static partial Regex GroundedGate();
    [GeneratedRegex(pattern: @"pos=\((?<x>-?[\d.]+), (?<y>-?[\d.]+), (?<z>-?[\d.]+)\)", options: RegexOptions.CultureInvariant)]
    private static partial Regex Pose();
    [GeneratedRegex(pattern: @"facts=(?<facts>[^\s\]]+)", options: RegexOptions.CultureInvariant)]
    private static partial Regex Facts();
    [GeneratedRegex(pattern: @"^player\.signal (?<source>\S+) (?<value>.+)$", options: RegexOptions.CultureInvariant)]
    private static partial Regex Signal();

    private CommandResult Run(string reference, string prefix, ulong? at, double budget, int timeout, string? output) {
        if (m_running) {
            return CommandResult.Error(output: $"[world.benchmark: '{m_route}' is running — world.benchmark stop ends it]");
        }
        if (script.PendingCount > 0) {
            return CommandResult.Error(output: $"[world.benchmark: {script.PendingCount} world.at line(s) are armed, and a run owns the pinned lines while it plays — let them run or world.at clear first]");
        }

        var path = (File.Exists(path: reference) ? Path.GetFullPath(path: reference) : Path.GetFullPath(path: reference, basePath: WorldDocumentPaths.DirectoryOf(documentPath: source.SourcePath)));

        if (!File.Exists(path: path)) {
            return CommandResult.Error(output: $"[world.benchmark: the route source '{reference}' does not exist beside the working directory or the world's document]");
        }

        _ = WorldCompileCache.Shared.TryCompile(compiled: out var compiled, failure: out var failure, includeTests: true, path: path);
        if ((failure is not null) || (compiled is null)) {
            return CommandResult.Error(output: $"[world.benchmark: the route source '{reference}' does not compile: {string.Join(separator: "; ", values: (failure?.Diagnostics ?? []).Select(selector: static diagnostic => diagnostic.Message).Take(count: 3))}]");
        }

        var tests = compiled.Tests.Where(predicate: test => test.Test.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal)).ToArray();

        if (tests.Length == 0) {
            return CommandResult.Error(output: $"[world.benchmark: '{reference}' has no test whose name starts with '{prefix}' (it has {string.Join(separator: ", ", values: compiled.Tests.Select(selector: static test => $"\"{test.Test}\""))})]");
        }

        // Every chapter is read before any line is armed, so a route that cannot play arms nothing.
        var programs = new List<(string Name, List<(int At, int Slot, string Command, bool Refusal)> Rows, int Duration, string[] Gates)>();
        var lines = 0;

        foreach (var test in tests) {
            var document = (JsonNode.Parse(utf8Json: test.Json) as JsonObject);
            var rows = (document?["schedule"]?["rows"] as JsonArray);
            var settle = (document?["schedule"]?["settleTicks"]?.GetValue<long>() ?? 1L);

            if ((test.Siblings.Count > 0) || (rows is null) || (rows.Count == 0)) {
                return CommandResult.Error(output: $"[world.benchmark: the chapter \"{test.Test}\" {((test.Siblings.Count > 0) ? "arms sibling worlds, and a run plays the running world" : "has no when lines")}]");
            }

            var first = rows[0]!["tick"]!.GetValue<long>();
            var last = first;
            var program = new List<(int At, int Slot, string Command, bool Refusal)>(capacity: rows.Count);

            foreach (var row in rows.OfType<JsonObject>()) {
                if ((row["world"] is not null) || !PrincipalTokens.TryParse(principal: out var principal, token: row["principal"]?.GetValue<string>()) || (principal.Kind != PrincipalKind.Seat)) {
                    return CommandResult.Error(output: $"[world.benchmark: the chapter \"{test.Test}\" has the line '{row["command"]}', which is not a seat's line of the running world]");
                }

                last = row["tick"]!.GetValue<long>();
                program.Add(item: (checked((int)(last - first)), principal.Index, row["command"]!.GetValue<string>(), string.Equals(a: row["expect"]?.GetValue<string>(), b: nameof(WorldScheduleExpectation.Refused), comparisonType: StringComparison.OrdinalIgnoreCase)));
            }

            var gates = (document?["state"]?["world"] as JsonArray)?.OfType<JsonObject>().Select(selector: static row => row["verdict"]?["gate"]?.GetValue<string>()).OfType<string>().ToArray() ?? [];

            programs.Add(item: (test.Test, program, checked((int)((last - first) + settle)), gates));
            lines += (program.Count + 5);
        }
        if (lines > WorldTickScript.MaxPending) {
            return CommandResult.Error(output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.benchmark: the route's {lines} lines pass the {WorldTickScript.MaxPending} a World holds armed — play fewer chapters with a name prefix]"));
        }

        var start = Math.Max(val1: (script.CompletedTick + LeadTicks), val2: (at ?? 0UL));
        var total = 0;

        m_chapters.Clear();
        m_held.Clear();
        foreach (var (name, program, duration, gates) in programs) {
            var body = gates.Select(selector: static gate => (AxisGate().Match(input: gate) is { Success: true } axis) ? axis : GroundedGate().Match(input: gate)).Where(predicate: static match => match.Success).Select(selector: static match => int.Parse(s: match.Groups["body"].Value, provider: CultureInfo.InvariantCulture)).FirstOrDefault();
            var chapter = new Chapter(body: body, duration: duration, gates: gates, name: name, start: start);
            var index = m_chapters.Count;

            m_chapters.Add(item: chapter);
            Arm(tick: (start - 1UL), line: $"world.benchmark mark {index} begin");
            foreach (var (offset, slot, command, refusal) in program) {
                if (script.ArmSeat(line: out var step, router: router, slot: slot, text: command, tick: (start + (ulong)offset)) is { } refused) {
                    _ = script.Clear();
                    m_chapters.Clear();

                    return CommandResult.Error(output: $"[world.benchmark: the chapter \"{name}\" could not arm '{command}': {refused}]");
                }

                chapter.Steps.Add(item: (step, refusal));
                Hold(command: command, slot: slot);
            }

            var end = (start + (ulong)duration);

            chapter.Where = Arm(tick: end, line: $"body.where {body.ToString(provider: CultureInfo.InvariantCulture)}");
            chapter.Hash = Arm(tick: end, line: "world.state.hash");
            Arm(tick: end, line: $"world.benchmark mark {index} end");
            start = (end + GapTicks);
            total += (duration + GapTicks);
        }

        m_route = string.IsNullOrEmpty(value: prefix) ? reference : $"{reference}#{prefix}";
        m_budget = budget;
        m_directory = (output ?? Path.Combine(path1: (stateRoot?.PathOf(name: "benchmarks") ?? Path.Combine(path1: Path.GetTempPath(), path2: "puck-benchmarks")), path2: DateTime.Now.ToString(format: "yyyyMMdd-HHmmss", provider: CultureInfo.InvariantCulture)));
        m_lockstepAtEntry = (pacing?.TickLocked == true);
        // A run plays as a player takes it, so it establishes display pacing before its span and says what it found.
        pacing?.SetTickLocked(locked: false);
        // A run armed before the first tick starts from the boot image on ticks its caller chose, so its hashes compare
        // with another such run's; one armed later starts wherever the world stood.
        m_entry = string.Create(provider: CultureInfo.InvariantCulture, handler: $"{((script.CompletedTick == 0UL) ? "fresh" : "attached")}: armed at tick {script.CompletedTick}, first line at tick {m_chapters[0].Start}; lockstep {(m_lockstepAtEntry ? "was on and is off for the run" : "off")}");
        m_recording = null;
        m_deadline = 0L;
        if (timeline is { } frames) {
            m_recording = new HostFrameRecording(capacity: Math.Min(val1: HostFrameRecording.MaxCapacity, val2: ((total * 24) + 8192))) {
                Flags = () => {
                    if ((m_deadline != 0L) && (frames.Time.GetTimestamp() > m_deadline)) {
                        _ = End(reason: $"the run passed its {timeout} s wall-time limit");
                    }

                    return ((pacing?.TickLocked == true) ? LockstepFlag : 0);
                },
            };
            m_deadline = (frames.Time.GetTimestamp() + (timeout * frames.Time.TimestampFrequency));
        }
        m_running = true;

        return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.benchmark: playing '{m_route}': {m_chapters.Count} chapter(s), ticks {m_chapters[0].Start}..{(start - GapTicks)}; {m_entry}; report in {PuckPaths.Normalize(path: m_directory)}]"));
    }

    private WorldTickScript.Line? Arm(ulong tick, string line) {
        _ = script.Arm(armed: out var armed, line: line, tick: tick);

        return armed;
    }

    // Tracks what a program's signal lines leave held, so a run that ends or is stopped lets every control go.
    private void Hold(string command, int slot) {
        if (Signal().Match(input: command) is not { Success: true } signal) {
            return;
        }

        var key = (slot, signal.Groups["source"].Value);
        var values = signal.Groups["value"].Value.Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries);

        if (values[0] == "press") {
            m_held[key] = $"player.signal {key.Value} release";
        } else if ((values[0] == "release") || values.All(predicate: static value => (double.TryParse(s: value, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var number) && (number == 0.0)))) {
            _ = m_held.Remove(key: key);
        } else if (values.All(predicate: static value => double.TryParse(s: value, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out _))) {
            m_held[key] = $"player.signal {key.Value} {string.Join(separator: ' ', values: values.Select(selector: static _ => "0"))}";
        }
    }

    private CommandResult Mark(int index, bool begin) {
        if (!m_running || (index < 0) || (index >= m_chapters.Count)) {
            return CommandResult.Error(output: "[world.benchmark: mark names no chapter of a running route]");
        }

        var chapter = m_chapters[index];

        if (begin) {
            if ((index == 0) && (m_recording is { } recording) && (timeline is not null)) {
                _ = timeline.StopRecording();
                timeline.StartRecording(recording: recording);
            }

            chapter.BeginIndex = (m_recording?.Count ?? 0);
            chapter.ShedAtBegin = (timeline?.ShedSteps ?? 0L);

            return new CommandResult(Output: $"[world.benchmark: chapter {index} \"{chapter.Name}\" begins]");
        }

        chapter.EndIndex = (m_recording?.Count ?? 0);
        chapter.Shed = ((timeline?.ShedSteps ?? 0L) - chapter.ShedAtBegin);

        return ((index == (m_chapters.Count - 1))
            ? End(reason: null)
            : new CommandResult(Output: $"[world.benchmark: chapter {index} \"{chapter.Name}\" {Verdict(chapter: chapter, statistics: null)}]"));
    }

    // Ends the run where it stands: lets the held controls go, restores the pacing it changed and writes the report,
    // whole or partial. A reason names why a run ended before its last chapter.
    private CommandResult End(string? reason) {
        if (!m_running) {
            return CommandResult.Error(output: "[world.benchmark: no run is playing]");
        }

        m_running = false;
        m_deadline = 0L;

        var dropped = ((reason is null) ? 0 : script.Clear());
        var recording = m_recording;

        if (ReferenceEquals(objA: timeline?.Recording, objB: recording)) {
            _ = timeline?.StopRecording();
        }
        foreach (var ((slot, _), release) in m_held) {
            _ = script.ArmSeat(line: out _, router: router, slot: slot, text: release, tick: (script.CompletedTick + 1UL));
        }
        m_held.Clear();
        if (m_lockstepAtEntry) {
            pacing?.SetTickLocked(locked: true);
        }

        var text = new StringBuilder();
        var report = new JsonObject {
            ["schema"] = "puck.benchmark.run.v1",
            ["route"] = m_route,
            ["complete"] = (reason is null),
            ["ended"] = (reason ?? "the last chapter ended"),
            ["entry"] = m_entry,
            ["start"] = (m_entry.StartsWith(value: "fresh", comparisonType: StringComparison.Ordinal) ? "fresh" : "attached"),
            ["budgetMilliseconds"] = m_budget,
            ["iterationsDropped"] = (recording?.Dropped ?? 0L),
            ["iterations"] = (recording?.Count ?? 0),
        };
        var chapters = new JsonArray();

        _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"[world.benchmark: '{m_route}' {((reason is null) ? "finished" : $"ended early ({reason}; {dropped} armed line(s) dropped)")}; {m_entry}");
        if (recording is null) {
            _ = text.Append(value: "; frames unmeasured: this host keeps no frame timeline");
        } else if (recording.Dropped > 0) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"; {recording.Dropped} iteration(s) past the recording's capacity were not kept");
        }
        foreach (var chapter in m_chapters) {
            var statistics = (((recording is not null) && (chapter.BeginIndex >= 0) && (chapter.EndIndex >= 0)) ? recording.Statistics(budgetMilliseconds: m_budget, from: chapter.BeginIndex, to: chapter.EndIndex) : null);

            chapters.Add(item: Describe(chapter: chapter, recording: recording, statistics: statistics));
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"\n  \"{chapter.Name}\": {Verdict(chapter: chapter, statistics: statistics)}");
        }
        report["chapters"] = chapters;
        if ((recording is not null) && (m_chapters.Count > 0) && (m_chapters[0].BeginIndex >= 0)) {
            var whole = recording.Statistics(budgetMilliseconds: m_budget, from: m_chapters[0].BeginIndex, to: recording.Count);

            report["run"] = Describe(statistics: whole);
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"\n  run: {Frames(statistics: whole)}");
        }

        try {
            _ = Directory.CreateDirectory(path: m_directory);
            File.WriteAllText(contents: report.ToJsonString(options: new JsonSerializerOptions { WriteIndented = true }), path: Path.Combine(path1: m_directory, path2: "report.json"));
            if (recording is not null) {
                WriteFrames(path: Path.Combine(path1: m_directory, path2: "frames.csv"), recording: recording);
            }
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"\n  report: {PuckPaths.Normalize(path: m_directory)}/report.json{((recording is null) ? string.Empty : " and frames.csv")}]");
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"\n  report: not written to {PuckPaths.Normalize(path: m_directory)}: {exception.Message}]");
        }

        m_summary = text.ToString();
        Console.Error.WriteLine(value: m_summary);

        return new CommandResult(Output: m_summary) { IsError = (reason is not null) };
    }

    // Why a chapter's span cannot be vouched for, or an empty list.
    private List<string> Disturbances(Chapter chapter, HostFrameStatistics? statistics) {
        var reasons = new List<string>();
        var refused = chapter.Steps.Where(predicate: static step => (step.Refusal != (step.Line.Outcome == "refused")) || (step.Line.Outcome == "faulted")).Select(selector: static step => step.Line).ToArray();

        if (refused.Length > 0) {
            reasons.Add(item: $"{refused.Length} step(s) did not answer as authored, first '{refused[0].Text}' ({refused[0].Outcome}: {refused[0].Answer})");
        }
        if (statistics is { Flagged: > 0 }) {
            reasons.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"presentation was locked to ticks in {statistics.Flagged} iteration(s)"));
        }
        if (statistics is { LongestStall: > StallMilliseconds }) {
            reasons.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"the simulation stepped no tick for {statistics.LongestStall:0} ms (a pause, a hold or a stall)"));
        }

        return reasons;
    }

    // The gates the body's pose fails, the gates this read cannot judge, and whether the chapter ended at all.
    private static (List<string> Failed, List<string> Unjudged, bool Read) Arrival(Chapter chapter) {
        var failed = new List<string>();
        var unjudged = new List<string>();
        var where = (chapter.Where?.Answer ?? string.Empty);
        var pose = Pose().Match(input: where);
        var grounded = (Facts().Match(input: where) is { Success: true } facts) && facts.Groups["facts"].Value.Split(separator: '|').Contains(value: "grounded");

        foreach (var gate in chapter.Gates) {
            if (pose.Success && (AxisGate().Match(input: gate) is { Success: true } axis)) {
                var read = double.Parse(s: pose.Groups[axis.Groups["axis"].Value].Value, provider: CultureInfo.InvariantCulture);
                var value = double.Parse(s: axis.Groups["value"].Value, provider: CultureInfo.InvariantCulture);
                var holds = (axis.Groups["op"].Value switch { ">" => (read > value), ">=" => (read >= value), "<" => (read < value), "<=" => (read <= value), "==" => (read == value), _ => (read != value) });

                if (!holds) { failed.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{gate} (read {read})")); }
            } else if (pose.Success && GroundedGate().IsMatch(input: gate)) {
                if (!grounded) { failed.Add(item: $"{gate} (not grounded)"); }
            } else {
                unjudged.Add(item: gate);
            }
        }

        return (failed, unjudged, pose.Success);
    }

    private string Verdict(Chapter chapter, HostFrameStatistics? statistics) {
        if (chapter.EndIndex < 0) {
            return ((chapter.BeginIndex < 0) ? "unreached" : "unfinished");
        }

        var (failed, unjudged, read) = Arrival(chapter: chapter);
        var disturbed = Disturbances(chapter: chapter, statistics: statistics);
        var arrival = (!read ? "arrival unmeasured (no body pose was read)" : ((failed.Count > 0) ? $"OFF COURSE: {string.Join(separator: "; ", values: failed)}" : ((unjudged.Count > 0) ? $"arrived ({unjudged.Count} gate(s) only the headless test judges)" : "arrived")));

        return $"{arrival}{((disturbed.Count > 0) ? $"; DISTURBED: {string.Join(separator: "; ", values: disturbed)}" : string.Empty)}{((statistics is null) ? string.Empty : $"; {Frames(statistics: statistics)}")}{((chapter.Shed > 0) ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"; {chapter.Shed} tick(s) shed") : string.Empty)}";
    }

    private string Frames(HostFrameStatistics statistics) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{statistics.Frames} iterations in {statistics.Seconds:0.00} s ({statistics.AverageRate:0.0}/s), interval avg {statistics.Interval.Mean:0.00} p99 {statistics.Interval.P99:0.00} max {statistics.Interval.Max:0.00} ms, work avg {statistics.Work.Mean:0.00} p99 {statistics.Work.P99:0.00} max {statistics.Work.Max:0.00} ms, {statistics.Over} over {m_budget:0.0} ms ({statistics.OverTwice} over twice it)");

    private JsonObject Describe(Chapter chapter, HostFrameRecording? recording, HostFrameStatistics? statistics) {
        var (failed, unjudged, read) = Arrival(chapter: chapter);
        var node = new JsonObject {
            ["name"] = chapter.Name,
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
        }

        return node;
    }

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

        writer.WriteLine(value: "iteration,chapter,startMs,intervalMs,workMs,inputMs,commandsMs,simulateMs,beginMs,produceMs,produceBlockedMs,presentMs,waitMs,lateMs,steps,gen0,gen1,gen2,pauseMs,allocatedBytes,frameThreadAllocatedBytes,flags");
        for (var index = 0; (index < samples.Length); index++) {
            ref readonly var sample = ref samples[index];

            while ((chapter < m_chapters.Count) && (m_chapters[chapter].EndIndex >= 0) && (index >= m_chapters[chapter].EndIndex)) { chapter++; }

            var inside = ((chapter < m_chapters.Count) && (m_chapters[chapter].BeginIndex >= 0) && (index >= m_chapters[chapter].BeginIndex));

            writer.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{index},{(inside ? chapter : -1)},{Ms(sample.Start):0.###},{Ms(sample.Interval):0.###},{Ms(sample.Work):0.###},{Ms(sample.Input):0.###},{Ms(sample.Commands):0.###},{Ms(sample.Simulate):0.###},{Ms(sample.Begin):0.###},{Ms(sample.Produce):0.###},{Ms(sample.ProduceBlocked):0.###},{Ms(sample.Present):0.###},{Ms(sample.Wait):0.###},{Ms(sample.Late):0.###},{sample.Steps},{sample.Gen0},{sample.Gen1},{sample.Gen2},{Ms(sample.Pause):0.###},{sample.Allocated},{sample.ThreadAllocated},{sample.Flags}"));
        }
    }

    private CommandResult Status() {
        if (!m_running) {
            return new CommandResult(Output: m_summary);
        }

        var current = m_chapters.FindLastIndex(match: static chapter => (chapter.BeginIndex >= 0));
        var completed = script.CompletedTick;

        return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.benchmark: playing '{m_route}' at tick {completed}: {((current < 0) ? $"waiting for tick {m_chapters[0].Start} (a paused world runs no route)" : $"chapter {(current + 1)} of {m_chapters.Count} \"{m_chapters[current].Name}\"")}; {(m_recording?.Count ?? 0)} iteration(s) recorded; {m_entry}]"));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            name: "world.benchmark",
            description: $"Plays a route through the running world in real time and reports every host iteration of it: {WorldBenchmarkForms.Spelling}. A route is the test blocks of a .puck source whose names start with the prefix (every one, with none or '-'), each a chapter played in declaration order: its when lines are armed through the seat's own ingress at their ticks, as a headless puck test submits them, and its expect lines are its arrival. The first line lands {LeadTicks} ticks after the run is armed, or at the tick 'at' names when that is later, and each chapter follows the one before by {GapTicks} ticks. A run armed before the world's first tick (a paused boot) is fresh: every line lands on a tick its caller chose from the boot image, so its state hashes compare with another fresh run's armed at the same tick. Any other run is attached, starts wherever the world stands, and its hashes are informational. Nothing in a run pauses, settles or waits, and lockstep presentation is turned off for its span and restored after. Every host iteration from the first chapter's start is recorded past host.frames' ring. Each chapter ends with the body's pose judged against its position and grounded gates (arrived or off course), the authority's state hash, and its iterations' statistics: interval and work (the interval less the pacer's wait) as mean, median, 95th, 99th and 99.9th percentile and maximum, the counts whose work passed the budget (default one 60 Hz period), twice it, 50 ms and 100 ms, pacing deviation, collections and ticks shed. A chapter is written disturbed, with the reason and its statistics beside it, when presentation was locked to ticks in any iteration, the simulation stepped no tick for {StallMilliseconds:0} ms, or a step did not answer as authored. The run writes report.json (puck.benchmark.run.v1) and frames.csv (one row per iteration) to the output directory, by default benchmarks/<local date and time> under the run's state root. With no argument it reads the running route's chapter and tick, or the last run's summary. stop ends the run where it stands, drops its armed lines, lets go every control its program held and writes the partial report; a run that passes its wall-time limit (default {DefaultTimeoutSeconds} s) ends the same way. Refused while world.at lines are armed: a run owns the pinned lines while it plays. An iteration is the host loop's interval, never the time a frame stood on the display, and a headless host records iterations without a renderer.",
            forms: WorldBenchmarkForms,
            handler: (_, args) => {
                if (args.Count == 0) {
                    return Status();
                }
                if ((args.Count == 1) && args.Is(index: 0, value: "stop")) {
                    return End(reason: "stopped");
                }
                if ((args.Count == 3) && args.Is(index: 0, value: "mark") && args.TryInt(index: 1, value: out var chapter) && (args.Is(index: 2, value: "begin") || args.Is(index: 2, value: "end"))) {
                    return Mark(begin: args.Is(index: 2, value: "begin"), index: chapter);
                }
                if ((args.Count < 2) || !args.Is(index: 0, value: "run")) {
                    return WorldBenchmarkForms.Refuse(args: in args);
                }

                var next = 2;
                var prefix = string.Empty;
                ulong? at = null;
                var budget = ((float)DefaultBudgetMilliseconds);
                var timeout = DefaultTimeoutSeconds;
                string? output = null;

                if ((args.Count > 2) && !args.Is(index: 2, value: "at") && !args.Is(index: 2, value: "budget") && !args.Is(index: 2, value: "timeout") && !args.Is(index: 2, value: "output")) {
                    prefix = (args.Is(index: 2, value: "-") ? string.Empty : args[2].ToString());
                    next = 3;
                }
                for (; (next < args.Count); next += 2) {
                    if ((next + 1) >= args.Count) {
                        return WorldBenchmarkForms.Refuse(args: in args);
                    }
                    var tick = 0;
                    var read = (args.Is(index: next, value: "at") ? (args.TryInt(index: (next + 1), value: out tick) && (tick > 0))
                        : (args.Is(index: next, value: "budget") ? (args.TryFloat(index: (next + 1), value: out budget) && (budget > 0f))
                        : (args.Is(index: next, value: "timeout") ? (args.TryInt(index: (next + 1), value: out timeout) && (timeout > 0))
                        : args.Is(index: next, value: "output"))));

                    if (!read) {
                        return WorldBenchmarkForms.Refuse(args: in args);
                    }
                    if (tick > 0) {
                        at = ((ulong)tick);
                    }
                    if (args.Is(index: next, value: "output")) {
                        output = Path.GetFullPath(path: args[(next + 1)].ToString());
                    }
                }

                return Run(at: at, budget: budget, output: output, prefix: prefix, reference: args[1].ToString(), timeout: timeout);
            }
        );
    }
}
