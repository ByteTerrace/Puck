using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// <para>A run may play its route more than once (<c>loops</c>), each loop armed from the one before it ends on the
/// tick after it, so every loop of a fresh run lands on ticks its caller chose.</para>
/// <para>Every member runs on the host pump.</para>
/// </remarks>
/// <param name="script">The pinned-line scheduler a run arms.</param>
/// <param name="router">Resolves the live seat router.</param>
/// <param name="source">The running document's source, which a relative route source resolves beside.</param>
/// <param name="timeline">The host's frame timeline, or <see langword="null"/> on a host that keeps none.</param>
/// <param name="pacing">The window pump's pacing control, or <see langword="null"/> without a window.</param>
/// <param name="stateRoot">The run's state root, which reports are written under, or <see langword="null"/>.</param>
/// <param name="gpu">The per-pass GPU timestamps, or <see langword="null"/> on a host that has none.</param>
internal sealed partial class WorldBenchmarkCommandModule(WorldTickScript script, Func<InputRouter> router, WorldDefinitionSource source, HostFrameTimeline? timeline = null, PresentPacingControl? pacing = null, WorldStateRoot? stateRoot = null, WorldGpuTiming? gpu = null) : ICommandModule {
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
    /// <summary>The most loops one run plays.</summary>
    public const int MaxLoops = 16;
    /// <summary>The ticks between the body's pose samples (and GPU pass samples) inside a chapter, which place a hitch
    /// within the chapter.</summary>
    public const int SampleTicks = 30;
    /// <summary>The most hitches a chapter lists, the longest first; its count is the whole count.</summary>
    public const int MaxHitchesListed = 32;

    // The readings taken at both ends of every chapter: console lines whose raw answers the report keeps, so the change
    // across the chapter is read from the answers themselves. A line a host cannot answer (a renderer's, headless) keeps
    // its refusal, which reads as unmeasured.
    private static readonly string[] ChapterReads = [
        "world.residency",
        "world.counters render.record --json",
        "world.counters gpu.queue --json",
        "world.counters sdf --json",
        "host.memory",
    ];

    private static readonly CommandForms WorldBenchmarkForms = CommandForms.Of(verb: "world.benchmark", "run <source.puck> [<chapter name prefix>|-] [at <tick>] [budget <ms>] [timeout <seconds>] [loops <n>] [output <directory>]", "stop");

    private sealed record Row(int At, int Slot, string Command, bool Refusal);

    private sealed record Program(string Name, IReadOnlyList<Row> Rows, int Duration, IReadOnlyList<string> Gates, int Body, string Identity);

    private sealed class Chapter(Program program, int loop, ulong start) {
        public Program Program { get; } = program;
        public string Name => Program.Name;
        public int Loop { get; } = loop;
        public ulong Start { get; } = start;
        public int Duration => Program.Duration;
        public int Body => Program.Body;
        public IReadOnlyList<string> Gates => Program.Gates;
        public List<(WorldTickScript.Line Line, bool Refusal)> Steps { get; } = [];
        public List<WorldTickScript.Line> Poses { get; } = [];
        public List<WorldTickScript.Line> OpenReads { get; } = [];
        public List<WorldTickScript.Line> CloseReads { get; } = [];
        public Dictionary<string, (double Sum, double Max, int Count)> Gpu { get; } = new(comparer: StringComparer.Ordinal);
        public bool LastOfLoop { get; set; }
        public WorldTickScript.Line? Where { get; set; }
        public WorldTickScript.Line? Hash { get; set; }
        public int BeginIndex { get; set; } = -1;
        public int EndIndex { get; set; } = -1;
        public long ShedAtBegin { get; set; }
        public long Shed { get; set; }
    }

    private readonly List<Chapter> m_chapters = [];
    private readonly List<Program> m_programs = [];
    // The controls the armed program holds at its end or wherever it is stopped, by seat slot and source, with the line
    // that lets each go.
    private readonly Dictionary<(int Slot, string Source), string> m_held = [];
    // The lines whose raw, multi-line answers the report keeps, each with its answer once it has run.
    private readonly Dictionary<WorldTickScript.Line, string> m_raw = [];
    private HostFrameRecording? m_recording;
    private string m_route = string.Empty;
    private string m_routeIdentity = string.Empty;
    private string m_directory = string.Empty;
    private string m_summary = "[world.benchmark: no run yet — world.benchmark run <source.puck> plays one]";
    private string m_entry = string.Empty;
    private double m_budget = DefaultBudgetMilliseconds;
    private long m_deadline;
    private int m_loops;
    private int m_bootEnd;
    private long m_bootDropped;
    private bool m_lockstepAtEntry;
    private bool m_gpuWasOn;
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

    // The lines one loop of the programs arms: each program's rows, its marks, the arrival reads, the two readings
    // taken at both ends and the samples between.
    private static int LinesOf(IReadOnlyList<Program> programs) => programs.Sum(selector: static program => (program.Rows.Count + 5 + (2 * ChapterReads.Length) + (2 * (program.Duration / SampleTicks))));

    private CommandResult Run(string reference, string prefix, ulong? at, double budget, int timeout, int loops, string? output) {
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
        var programs = new List<Program>();

        foreach (var test in tests) {
            var document = (JsonNode.Parse(utf8Json: test.Json) as JsonObject);
            var rows = (document?["schedule"]?["rows"] as JsonArray);
            var settle = (document?["schedule"]?["settleTicks"]?.GetValue<long>() ?? 1L);

            if ((test.Siblings.Count > 0) || (rows is null) || (rows.Count == 0)) {
                return CommandResult.Error(output: $"[world.benchmark: the chapter \"{test.Test}\" {((test.Siblings.Count > 0) ? "arms sibling worlds, and a run plays the running world" : "has no when lines")}]");
            }

            var first = rows[0]!["tick"]!.GetValue<long>();
            var last = first;
            var program = new List<Row>(capacity: rows.Count);

            foreach (var row in rows.OfType<JsonObject>()) {
                if ((row["world"] is not null) || !PrincipalTokens.TryParse(principal: out var principal, token: row["principal"]?.GetValue<string>()) || (principal.Kind != PrincipalKind.Seat)) {
                    return CommandResult.Error(output: $"[world.benchmark: the chapter \"{test.Test}\" has the line '{row["command"]}', which is not a seat's line of the running world]");
                }

                last = row["tick"]!.GetValue<long>();
                program.Add(item: new Row(At: checked((int)(last - first)), Slot: principal.Index, Command: row["command"]!.GetValue<string>(), Refusal: string.Equals(a: row["expect"]?.GetValue<string>(), b: nameof(WorldScheduleExpectation.Refused), comparisonType: StringComparison.OrdinalIgnoreCase)));
            }

            var gates = ((document?["state"]?["world"] as JsonArray)?.OfType<JsonObject>().Select(selector: static row => row["verdict"]?["gate"]?.GetValue<string>()).OfType<string>().ToArray() ?? []);
            var body = gates.Select(selector: static gate => (AxisGate().Match(input: gate) is { Success: true } axis) ? axis : GroundedGate().Match(input: gate)).Where(predicate: static match => match.Success).Select(selector: static match => int.Parse(s: match.Groups["body"].Value, provider: CultureInfo.InvariantCulture)).FirstOrDefault();
            var duration = checked((int)((last - first) + settle));

            programs.Add(item: new Program(Body: body, Duration: duration, Gates: gates, Identity: IdentityOf(rows: program, duration: duration, gates: gates), Name: test.Test, Rows: program));
        }
        if (LinesOf(programs: programs) > WorldTickScript.MaxPending) {
            return CommandResult.Error(output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.benchmark: one loop of the route is {LinesOf(programs: programs)} lines, which passes the {WorldTickScript.MaxPending} a World holds armed — play fewer chapters with a name prefix]"));
        }

        m_programs.Clear();
        m_programs.AddRange(collection: programs);
        m_chapters.Clear();
        m_held.Clear();
        m_raw.Clear();
        m_loops = loops;

        var firstTick = Math.Max(val1: (script.CompletedTick + LeadTicks), val2: (at ?? 0UL));

        if (ArmLoop(loop: 0, start: firstTick, next: out _) is { } refused) {
            _ = script.Clear();
            m_chapters.Clear();
            m_raw.Clear();

            return CommandResult.Error(output: $"[world.benchmark: {refused}]");
        }

        var lastTick = (firstTick + ((ulong)(m_programs.Sum(selector: static program => (program.Duration + GapTicks)) * loops)) - GapTicks);

        m_route = string.IsNullOrEmpty(value: prefix) ? reference : $"{reference}#{prefix}";
        m_routeIdentity = IdentityOf(identities: m_programs.Select(selector: static program => program.Identity));
        m_budget = budget;
        m_directory = (output ?? Path.Combine(path1: (stateRoot?.PathOf(name: "benchmarks") ?? Path.Combine(path1: Path.GetTempPath(), path2: "puck-benchmarks")), path2: DateTime.Now.ToString(format: "yyyyMMdd-HHmmss", provider: CultureInfo.InvariantCulture)));
        m_lockstepAtEntry = (pacing?.TickLocked == true);
        // A run plays as a player takes it, so it establishes display pacing before its span and says what it found.
        pacing?.SetTickLocked(locked: false);
        // A run armed before the first tick starts from the boot image on ticks its caller chose, so its hashes compare
        // with another such run's; one armed later starts wherever the world stood.
        m_entry = string.Create(provider: CultureInfo.InvariantCulture, handler: $"{((script.CompletedTick == 0UL) ? "fresh" : "attached")}: armed at tick {script.CompletedTick}, first line at tick {firstTick}; lockstep {(m_lockstepAtEntry ? "was on and is off for the run" : "off")}");
        m_recording = null;
        m_deadline = 0L;
        m_gpuWasOn = (gpu?.ReadoutEnabled == true);
        if (gpu is { Available: true }) {
            gpu.Set(enabled: true);
        }
        script.Answered += OnAnswered;
        if (timeline is { } frames) {
            m_recording = new HostFrameRecording(capacity: ((int)Math.Min(val1: ((long)HostFrameRecording.MaxCapacity), val2: ((((long)(lastTick - firstTick)) * 24L) + 8192L)))) {
                Flags = () => {
                    if ((m_deadline != 0L) && (frames.Time.GetTimestamp() > m_deadline)) {
                        _ = End(reason: $"the run passed its {timeout} s wall-time limit");
                    }

                    return ((pacing?.TickLocked == true) ? LockstepFlag : 0);
                },
                Ticks = () => script.CompletedTick,
            };
            m_deadline = (frames.Time.GetTimestamp() + (timeout * frames.Time.TimestampFrequency));
        }
        m_running = true;

        return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.benchmark: playing '{m_route}': {m_programs.Count} chapter(s) x {loops} loop(s), ticks {firstTick}..{lastTick}; {m_entry}; report in {PuckPaths.Normalize(path: m_directory)}]"));
    }

    // Arms one loop of the programs from a tick: each chapter's lines, its readings and its samples, all through the
    // doors a headless test uses. Returns why a line was refused, or null once the whole loop is armed; the tick the
    // next loop would start at is the chapter after the last.
    private string? ArmLoop(int loop, ulong start, out ulong next) {
        next = start;

        try {
            foreach (var program in m_programs) {
                var chapter = new Chapter(loop: loop, program: program, start: next);
                var index = m_chapters.Count;
                var where = $"body.where {program.Body.ToString(provider: CultureInfo.InvariantCulture)}";

                m_chapters.Add(item: chapter);
                _ = Pin(tick: (next - 1UL), line: $"world.benchmark mark {index} begin");
                foreach (var read in ChapterReads) {
                    chapter.OpenReads.Add(item: Pin(tick: (next - 1UL), line: read, raw: true));
                }
                foreach (var row in program.Rows) {
                    if (script.ArmSeat(line: out var step, router: router, slot: row.Slot, text: row.Command, tick: (next + (ulong)row.At)) is { } refused) {
                        throw new InvalidOperationException(message: $"the chapter \"{program.Name}\" could not arm '{row.Command}': {refused}");
                    }

                    chapter.Steps.Add(item: (step, row.Refusal));
                    Hold(command: row.Command, slot: row.Slot);
                }
                for (var offset = SampleTicks; (offset < program.Duration); offset += SampleTicks) {
                    chapter.Poses.Add(item: Pin(tick: (next + (ulong)offset), line: where));
                    _ = Pin(tick: (next + (ulong)offset), line: $"world.benchmark sample {index}");
                }

                var end = (next + (ulong)program.Duration);

                chapter.Where = Pin(tick: end, line: where);
                chapter.Hash = Pin(tick: end, line: "world.state.hash");
                foreach (var read in ChapterReads) {
                    chapter.CloseReads.Add(item: Pin(tick: end, line: read, raw: true));
                }
                _ = Pin(tick: end, line: $"world.benchmark mark {index} end");
                next = (end + GapTicks);
            }
        } catch (InvalidOperationException refusal) {
            return refusal.Message;
        }
        m_chapters[^1].LastOfLoop = true;

        return null;
    }

    // Arms a line of the run's own, or refuses with why the scheduler would not hold it.
    private WorldTickScript.Line Pin(ulong tick, string line, bool raw = false) {
        if (script.Arm(armed: out var armed, line: line, tick: tick) is { } refusal) {
            throw new InvalidOperationException(message: $"could not arm '{line}' at tick {tick}: {refusal}");
        }
        if (raw) {
            m_raw[armed!] = string.Empty;
        }

        return armed!;
    }

    // Keeps the raw answer of a line the report reads whole, before the one-line form flattens its line breaks.
    private void OnAnswered(WorldTickScript.Line line, CommandResult result) {
        if (m_raw.ContainsKey(key: line)) {
            m_raw[line] = result.Output;
        }
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

    // A chapter's identity: the hash of its canonical program, every row's offset, seat and command, its duration and its
    // arrival gates, so a chapter that is the same program is the same chapter whatever it is named.
    private static string IdentityOf(IReadOnlyList<Row> rows, int duration, IReadOnlyList<string> gates) {
        var text = new StringBuilder();

        foreach (var row in rows) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"{row.At}|{row.Slot}|{row.Refusal}|{row.Command}\n");
        }
        _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"duration {duration}\n");
        foreach (var gate in gates) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"gate {gate}\n");
        }

        return Convert.ToHexStringLower(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: text.ToString()))).Substring(startIndex: 0, length: 16);
    }

    private static string IdentityOf(IEnumerable<string> identities) => Convert.ToHexStringLower(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: string.Join(separator: '\n', values: identities)))).Substring(startIndex: 0, length: 16);

    private CommandResult Mark(int index, bool begin) {
        if (!m_running || (index < 0) || (index >= m_chapters.Count)) {
            return CommandResult.Error(output: "[world.benchmark: mark names no chapter of a running route]");
        }

        var chapter = m_chapters[index];

        if (begin) {
            if ((index == 0) && (m_recording is { } recording) && (timeline is not null)) {
                _ = timeline.StopRecording();
                timeline.StartRecording(recording: recording);
                m_bootEnd = timeline.Boot.Count;
                m_bootDropped = timeline.Boot.Dropped;
            }

            chapter.BeginIndex = (m_recording?.Count ?? 0);
            chapter.ShedAtBegin = (timeline?.ShedSteps ?? 0L);

            return new CommandResult(Output: $"[world.benchmark: chapter {index} \"{chapter.Name}\" begins]");
        }

        chapter.EndIndex = (m_recording?.Count ?? 0);
        chapter.Shed = ((timeline?.ShedSteps ?? 0L) - chapter.ShedAtBegin);

        if (!chapter.LastOfLoop) {
            return new CommandResult(Output: $"[world.benchmark: chapter {index} \"{chapter.Name}\" {Verdict(chapter: chapter, statistics: null)}]");
        }
        if ((chapter.Loop + 1) >= m_loops) {
            return End(reason: null);
        }

        // The next loop is armed as this one ends, from the tick after the gap, so its lines land on ticks that follow
        // from the run's own start and not from when this line happened to run.
        return ((ArmLoop(loop: (chapter.Loop + 1), start: (chapter.Start + (ulong)chapter.Duration + GapTicks), next: out _) is { } refused)
            ? End(reason: $"loop {(chapter.Loop + 1)} could not be armed: {refused}")
            : new CommandResult(Output: $"[world.benchmark: loop {chapter.Loop} ends; loop {(chapter.Loop + 1)} is armed]"));
    }

    // Folds the rolling mean of every GPU pass the nodes hold into the chapter the sample belongs to.
    private CommandResult Sample(int index) {
        if (!m_running || (index < 0) || (index >= m_chapters.Count) || (gpu is not { Available: true })) {
            return new CommandResult(Output: string.Empty);
        }

        var chapter = m_chapters[index];

        foreach (var timing in gpu.Readings()) {
            if (timing.Samples <= 0) {
                continue;
            }

            chapter.Gpu.TryGetValue(key: timing.Pass, value: out var held);
            chapter.Gpu[timing.Pass] = ((held.Sum + timing.Milliseconds), Math.Max(val1: held.Max, val2: timing.Milliseconds), (held.Count + 1));
        }

        return new CommandResult(Output: string.Empty);
    }

    // Ends the run where it stands: lets the held controls go, restores the pacing and timestamps it changed and writes
    // the report, whole or partial. A reason names why a run ended before its last chapter.
    private CommandResult End(string? reason) {
        if (!m_running) {
            return CommandResult.Error(output: "[world.benchmark: no run is playing]");
        }

        m_running = false;
        m_deadline = 0L;
        script.Answered -= OnAnswered;

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
        if (gpu is { Available: true }) {
            gpu.Set(enabled: m_gpuWasOn);
        }

        return Finish(reason: reason, dropped: dropped, recording: recording);
    }

    // Why a chapter's span cannot be vouched for, or an empty list.
    private static List<string> Disturbances(Chapter chapter, HostFrameStatistics? statistics) {
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

    private CommandResult Status() {
        if (!m_running) {
            return new CommandResult(Output: m_summary);
        }

        var current = m_chapters.FindLastIndex(match: static chapter => (chapter.BeginIndex >= 0));
        var completed = script.CompletedTick;

        return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.benchmark: playing '{m_route}' at tick {completed}: {((current < 0) ? $"waiting for tick {m_chapters[0].Start} (a paused world runs no route)" : $"loop {(m_chapters[current].Loop + 1)} of {m_loops}, chapter \"{m_chapters[current].Name}\"")}; {(m_recording?.Count ?? 0)} iteration(s) recorded; {m_entry}]"));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            name: "world.benchmark",
            description: $"Plays a route through the running world in real time and reports every host iteration of it: {WorldBenchmarkForms.Spelling}. A route is the test blocks of a .puck source whose names start with the prefix (every one, with none or '-'), each a chapter played in declaration order: its when lines are armed through the seat's own ingress at their ticks, as a headless puck test submits them, and its expect lines are its arrival. The first line lands {LeadTicks} ticks after the run is armed, or at the tick 'at' names when that is later, and each chapter follows the one before by {GapTicks} ticks. 'loops' plays the route that many times (at most {MaxLoops}), each loop armed from the tick the one before it ends on, so a run is reported as its cold first loop, its warm second and its steady rest. A run armed before the world's first tick (a paused boot) is fresh: every line lands on a tick its caller chose from the boot image, so its state hashes compare with another fresh run's armed at the same tick. Any other run is attached, starts wherever the world stands, and its hashes are informational. Nothing in a run pauses, settles or waits, and lockstep presentation is turned off for its span and restored after. Every host iteration from the first chapter's start is recorded past host.frames' ring, and the host's first iterations through that start are its boot span. Each chapter ends with the body's pose judged against its position and grounded gates (arrived or off course), the authority's state hash, its identity (the hash of its program), and its iterations' statistics: interval and work (the interval less the pacer's wait) as mean, median, 95th, 99th and 99.9th percentile and maximum, the counts whose work passed the budget (default one 60 Hz period), twice it, 50 ms and 100 ms, pacing deviation, collections and ticks shed; the iterations over budget with their tick, the body's last sampled position and their phases; each GPU pass's mean and maximum over samples taken every {SampleTicks} ticks; and the raw answers of world.residency, the render.record, gpu.queue and sdf counters and host.memory at both ends, whose change a reader takes. A chapter is written disturbed, with the reason and its statistics beside it, when presentation was locked to ticks in any iteration, the simulation stepped no tick for {StallMilliseconds:0} ms, or a step did not answer as authored. The run writes run.json (puck.benchmark.run.v1) and frames.csv (one row per iteration) to the output directory, by default benchmarks/<local date and time> under the run's state root. With no argument it reads the running route's chapter and tick, or the last run's summary. stop ends the run where it stands, drops its armed lines, lets go every control its program held and writes the partial report; a run that passes its wall-time limit (default {DefaultTimeoutSeconds} s) ends the same way. Refused while world.at lines are armed: a run owns the pinned lines while it plays. An iteration is the host loop's interval, never the time a frame stood on the display, and a headless host records iterations without a renderer.",
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
                if ((args.Count == 3) && args.Is(index: 0, value: "sample") && args.TryInt(index: 1, value: out var sampled)) {
                    return Sample(index: sampled);
                }
                if ((args.Count < 2) || !args.Is(index: 0, value: "run")) {
                    return WorldBenchmarkForms.Refuse(args: in args);
                }

                var next = 2;
                var prefix = string.Empty;
                ulong? at = null;
                var budget = ((float)DefaultBudgetMilliseconds);
                var timeout = DefaultTimeoutSeconds;
                var loops = 1;
                string? output = null;

                if ((args.Count > 2) && !args.Is(index: 2, value: "at") && !args.Is(index: 2, value: "budget") && !args.Is(index: 2, value: "timeout") && !args.Is(index: 2, value: "loops") && !args.Is(index: 2, value: "output")) {
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
                        : (args.Is(index: next, value: "loops") ? (args.TryInt(index: (next + 1), value: out loops) && (loops >= 1) && (loops <= MaxLoops))
                        : args.Is(index: next, value: "output")))));

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

                return Run(at: at, budget: budget, loops: loops, output: output, prefix: prefix, reference: args[1].ToString(), timeout: timeout);
            }
        );
    }
}
