using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.Abstractions;
using Puck.Abstractions.Memory;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Commands;
using Puck.Hosting.Telemetry;
using Puck.Launcher;
using Puck.World.Protocol;
using Puck.World.Server;
using Puck.World.Transpiler;

namespace Puck.World;

/// <summary>
/// <c>world.benchmark</c>: plays a route through the running world in real time and reports every host iteration of
/// it. A route is a <c>routes</c> row of the running document, whose chapters its seat program plays through the seat's
/// own controls (<see cref="Puck.World.Client.WorldSeatDriver"/>), or the <c>test</c> blocks of one <c>.puck</c> source, each a chapter: its <c>when</c> lines are the
/// chapter's controller program and its <c>expect</c> lines its arrival. The program is the schedule the compiler
/// lowers the block to, the rows a headless <c>puck test</c> submits, and each row is armed through the seat's own
/// ingress (<see cref="WorldTickScript.ArmSeat"/>) at its tick, so the live chapter and the headless test submit the
/// same lines through the same door in the same tick phase.
/// </summary>
/// <remarks>
/// <para>Nothing in a run pauses, settles or waits: the world keeps its own pacing, and the run records what the host
/// did (<see cref="HostFrameRecording"/>). A chapter whose span the run cannot vouch for is written disturbed with
/// the reason, and its statistics are still written beside it: lockstep presentation in any iteration, the simulation
/// stepping no tick for <see cref="StallMilliseconds"/>, a step the seat's ingress refused, or the device's memory
/// rising in the chapter to what the system budgets the process, where a pass that touches moved memory waits for it.</para>
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
/// <param name="readings">The optional presentation registry the run publishes its live figures into.</param>
/// <param name="gpuTiming">The optional GPU timestamp owner, whose per-pass readings a run samples through each chapter.</param>
/// <param name="server">The boot authority, whose document is the one this process was started with: a run names its
/// <c>routes</c> and its seat programs, which are the client's own. <see langword="null"/> on a host that plays only
/// test blocks.</param>
/// <param name="seatRouter">The seats' authority routes.</param>
/// <param name="senses">What each seat's client was delivered, which a route's seat driver reads; <see langword="null"/>
/// on a host that composes no seat view.</param>
/// <param name="deviceProfile">The device profile the process runs under, which the report names; absent reports none.</param>
/// <param name="host">The host settings the process resolved, whose window the report names; absent reports none.</param>
public sealed partial class WorldBenchmarkCommandModule(WorldTickScript script, Func<InputRouter> router, WorldDefinitionSource source, HostFrameTimeline? timeline = null, PresentPacingControl? pacing = null, WorldStateRoot? stateRoot = null, PresentationReadings? readings = null, WorldGpuTiming? gpuTiming = null, WorldServer? server = null, Puck.World.Client.WorldSeatAuthorityRouter? seatRouter = null, Puck.World.Client.WorldSeatSenses? senses = null, WorldDeviceProfileState? deviceProfile = null, WorldHostSettings? host = null) : ICommandModule {
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
    /// <summary>The most seats one run names.</summary>
    public const int MaxSeats = 4;
    /// <summary>The ticks between the body's pose samples (and GPU pass samples) inside a chapter, which place a hitch
    /// within the chapter.</summary>
    public const int SampleTicks = 30;
    /// <summary>The most hitches a chapter lists, the longest first; its count is the whole count.</summary>
    public const int MaxHitchesListed = 32;
    /// <summary>The time between readings of the adapter's clocks, power and utilization while a run plays.</summary>
    public static readonly TimeSpan AdapterSampleInterval = TimeSpan.FromMilliseconds(milliseconds: 500);

    // The readings taken at both ends of every chapter: console lines whose raw answers the report keeps, so the change
    // across the chapter is read from the answers themselves. A line a host cannot answer (a renderer's, headless) keeps
    // its refusal, which reads as unmeasured.
    private static readonly string[] ChapterReads = [
        "world.residency",
        "world.counters render.record --json",
        "world.counters gpu.queue --json",
        "world.counters gpu --json",
        "world.counters sdf --json",
        "world.counters world.field-queries --json",
        "world.counters world.captures --json",
        "world.counters world.disclosure --json",
        "world.counters physics.bodies --json",
        "host.memory",
        "host.memory families 0",
    ];

    private static readonly CommandForms WorldBenchmarkForms = CommandForms.Of(verb: "world.benchmark", "run <route>|<source.puck> [<chapter name prefix>|-] [at <tick>] [budget <ms>] [timeout <seconds>] [loops <n>] [seats <n>] [output <directory>]", "stop", "topology", "marks <route>", "overlay on|off|auto");

    private sealed record Row(int At, int Slot, string Command, bool Refusal);

    // A run that has asked the seat's navigation to bake and starts once it has, or fails once the run's own wall-time
    // limit has passed without it; Stopped is set by world.benchmark stop.
    private sealed class Preparation(string reference, Puck.World.Client.WorldAuthorityEndpoint endpoint, long started, long limit) {
        public Puck.World.Client.WorldAuthorityEndpoint Endpoint { get; } = endpoint;
        public string Reference { get; } = reference;
        public long Limit { get; } = limit;
        public long Started { get; } = started;
        public bool Stopped { get; set; }
    }

    // One reading of the seat's body: the tick it was taken on and its answer, a body.where or body.place line's or, for
    // a route chapter, what the seat driver read from the seat's own deliveries, spelled as those lines spell it.
    private sealed record Read(ulong Tick, string Answer);

    // A chapter's program: a test block's scheduled rows and arrival gates, or a route chapter's steps (Goals), which a
    // seat program plays and whose length is known only once it ends.
    private sealed record Program(string Name, IReadOnlyList<Row> Rows, int Duration, IReadOnlyList<string> Gates, int Body, string Identity, WorldRouteChapter? Goals = null);

    private sealed class Chapter(Program program, int loop, ulong start) {
        public Program Program { get; } = program;
        public string Name => Program.Name;
        public int Loop { get; } = loop;
        public ulong Start { get; } = start;
        public int Duration { get; set; } = program.Duration;
        public int Body => Program.Body;
        public IReadOnlyList<string> Gates => Program.Gates;
        public List<(WorldTickScript.Line Line, bool Refusal, int Slot)> Steps { get; } = [];
        public List<WorldTickScript.Line> Poses { get; } = [];
        public List<WorldTickScript.Line> Places { get; } = [];
        public WorldTickScript.Line? StartPlace { get; set; }
        public WorldTickScript.Line? EndPlace { get; set; }
        // A route chapter's readings of the seat's body, which its driver takes from the seat's deliveries: no console
        // line is armed for them, since a line routed to a remote authority holds the pump for its answer.
        public List<Read> Samples { get; } = [];
        public Read? StartRead { get; set; }
        public Read? EndRead { get; set; }
        // The seat's authority and its mirror's delivery work as the chapter began, and the work it did across it.
        public Puck.World.Client.WorldAuthorityEndpoint? Endpoint { get; set; }
        public Puck.World.Client.WorldDeliveryWork DeliveryAtBegin { get; set; }
        public JsonObject? Delivery { get; set; }
        public List<WorldTickScript.Line> OpenReads { get; } = [];
        public List<WorldTickScript.Line> CloseReads { get; } = [];
        public Dictionary<string, (double Sum, double Max, int Count)> Gpu { get; } = new(comparer: StringComparer.Ordinal);
        public bool LastOfLoop { get; set; }
        public WorldTickScript.Line? Where { get; set; }
        public WorldTickScript.Line? Hash { get; set; }
        public int BeginIndex { get; set; } = -1;
        public int EndIndex { get; set; } = -1;
        // The adapter's state across the chapter: the timestamps its span is read between and the summary of the readings in it.
        public long BeginStamp { get; set; }
        public long EndStamp { get; set; }
        public AdapterTelemetrySummary? Adapter { get; set; }
        public long ShedAtBegin { get; set; }
        public long Shed { get; set; }
        // The device's own memory across the chapter (DeviceLedger): the most its ledger had held at once as the chapter
        // began and as it ended, with what the system budgeted the process and used beyond the ledger at its end.
        public long DevicePeakAtBegin { get; set; }
        public (long Peak, long Budget, long Beyond) Device { get; set; }
        // A route chapter's outcome, copied from the seat driver as it ends.
        public bool Arrived { get; set; }
        public string? Failure { get; set; }
        public string? FailureKind { get; set; }
        public ulong FailureTick { get; set; }
        public string? FailurePlace { get; set; }
        public int StepReached { get; set; }
        public int Plans { get; set; }
        public int ControlLines { get; set; }
        public int ControlRefusals { get; set; }
        public IReadOnlyList<(ulong Tick, string Place)> Visited { get; set; } = [];
        public bool Driven => (Program.Goals is not null);
        // The body's pose and places where the chapter starts and ends, and each sample between, from either source.
        public Read? Opening => (Driven ? StartRead : ReadOf(line: StartPlace));
        public Read? EndPose => (Driven ? EndRead : ReadOf(line: Where));
        public Read? EndPlaces => (Driven ? EndRead : ReadOf(line: EndPlace));
        public IEnumerable<Read> PoseSamples => (Driven ? Samples : Poses.Select(selector: ReadOf).OfType<Read>());
        public IEnumerable<Read> PlaceSamples => (Driven ? Samples : Places.Select(selector: ReadOf).OfType<Read>());

        private static Read? ReadOf(WorldTickScript.Line? line) => ((line is { Outcome: "ran" }) ? new Read(Answer: line.Answer, Tick: line.Tick) : null);
    }

    private readonly List<Chapter> m_chapters = [];
    private readonly List<Program> m_programs = [];
    // Every seat line the run has armed, in the order they run: the chapters' program lines and the releases each chapter
    // starts with. A run that ends lets go what the lines that reached the seat left held.
    private readonly List<(WorldTickScript.Line Line, int Slot)> m_signals = [];
    // The lines whose raw, multi-line answers the report keeps, each with its answer once it has run.
    private readonly Dictionary<WorldTickScript.Line, string> m_raw = [];
    private HostFrameRecording? m_recording;
    // Reads the adapter on a timer for the run's life, off the frame thread, so each chapter's state is a span of its readings.
    private AdapterTelemetryRecorder? m_adapter;
    private string m_route = string.Empty;
    private string m_routeIdentity = string.Empty;
    private string m_directory = string.Empty;
    private string m_summary = "[world.benchmark: no run yet — world.benchmark run <source.puck> plays one]";
    private string m_entry = string.Empty;
    private double m_budget = DefaultBudgetMilliseconds;
    private string m_budgetSource = string.Empty;
    private long m_deadline;
    private int m_loops;
    private int m_bootEnd;
    private long m_bootDropped;
    private bool m_lockstepAtEntry;
    private bool m_running;
    private bool? m_overlay;
    private bool m_overlayAtEntry;
    private bool m_overlayChanged;
    private bool m_registeredReadings;
    // Counts the runs entered, so a wall-time watch left by a run that ended ends no later one.
    private int m_runSerial;
    // A route run: the seat driver, the simulation rate it reads seconds in, and the chapter it is playing.
    private Puck.World.Client.WorldSeatDriver? m_driver;
    private int m_rateHz;
    private Chapter? m_goalChapter;
    // A route run waiting for its seat's navigation to bake, off the tick, before it arms; and what that wait took, which
    // the run's report names beside its frames and which none of them include.
    private Preparation? m_preparing;
    private JsonObject? m_prepared;

    private int CurrentChapter => m_chapters.FindLastIndex(static chapter => chapter.BeginIndex >= 0);
    private bool OverlayVisible => m_overlay ?? m_running;

    private void RegisterReadings() {
        if (m_registeredReadings || readings is null) { return; }
        m_registeredReadings = true;
        readings.Register("benchmark.visible", () => new(PresentationReadingState.Present, OverlayVisible ? 1 : 0));
        readings.Register("benchmark.tick", () => new(PresentationReadingState.Present, script.PublishedTick));
        readings.Register("benchmark.budget", () => new(PresentationReadingState.Present, m_budget));
        readings.Register("benchmark.chapter", () => CurrentChapter is var index && index >= 0
            ? new(PresentationReadingState.Present, Text: m_chapters[index].Name)
            : new(PresentationReadingState.Unmeasured));
        readings.Register("benchmark.state", () => new(PresentationReadingState.Present, Text: m_running ? "playing" : m_chapters.Count == 0 ? "idle" : "ended"));
    }

    [GeneratedRegex(pattern: @"pos=\((?<x>-?[\d.]+), (?<y>-?[\d.]+), (?<z>-?[\d.]+)\)", options: RegexOptions.CultureInvariant)]
    private static partial Regex Pose();
    [GeneratedRegex(pattern: @"facts=(?<facts>[^\s\]]+)", options: RegexOptions.CultureInvariant)]
    private static partial Regex Facts();
    [GeneratedRegex(pattern: @"places=(?<places>[^\s\]]+)", options: RegexOptions.CultureInvariant)]
    private static partial Regex PlaceList();
    [GeneratedRegex(pattern: @"^player\.signal (?<source>\S+) (?<value>.+)$", options: RegexOptions.CultureInvariant)]
    private static partial Regex Signal();

    // The lines one loop of the programs arms: each program's rows, its marks, the arrival reads, the places at its start
    // and end, the two readings taken at both ends and the pose, place and mark samples between.
    private static int LinesOf(IReadOnlyList<Program> programs) => programs.Sum(selector: static program => (program.Rows.Count + Held(lines: program.Rows.Select(selector: static row => (row.Command, row.Slot))).Count + 7 + (2 * ChapterReads.Length) + (3 * (program.Duration / SampleTicks))));

    private CommandResult Run(CommandContext context, string reference, string prefix, ulong? at, double budget, int timeout, int loops, string? output) {
        if (m_running) {
            return CommandResult.Error(output: $"[world.benchmark: '{m_route}' is running — world.benchmark stop ends it]");
        }
        if (m_preparing is { } preparing) {
            return CommandResult.Error(output: $"[world.benchmark: '{preparing.Reference}' is preparing navigation — world.benchmark stop ends it]");
        }
        if (m_finishOwed is not null) {
            return CommandResult.Error(output: $"[world.benchmark: '{m_route}' finished and its report waits for its last chapter reads; ask again once it is written]");
        }
        if (script.PendingCount > 0) {
            return CommandResult.Error(output: $"[world.benchmark: {script.PendingCount} world.at line(s) are armed, and a run owns the pinned lines while it plays — let them run or world.at clear first]");
        }

        if ((server?.Definition.Routes.FirstOrDefault(predicate: candidate => string.Equals(a: candidate.Name, b: reference, comparisonType: StringComparison.Ordinal)) is { } route)) {
            if ((senses is null) || (seatRouter is null)) {
                return CommandResult.Error(output: "[world.benchmark: this host composes no seat routes or seat views, so it plays no route]");
            }
            if (seatRouter.TryRoute(slot: (route.Seat - 1)) is not { } seat) {
                return CommandResult.Error(output: $"[world.benchmark: seat {route.Seat} is routed to no authority, so the route '{reference}' has no world to play in]");
            }

            // The paths are planned over the document the seat's authority delivered, which bakes off the pump first.
            var endpoint = seat.Endpoint;

            if ((context.TextSession is { } session) && !senses.Navigation(endpoint: endpoint).Ready && route.Chapters.Any(predicate: chapter =>
                chapter.Name.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal) && chapter.Steps.Any(predicate: static step => step is WorldRouteStep.Go { Direct: false }))) {
                return Prepare(at: at, budget: budget, context: context, endpoint: endpoint, loops: loops, output: output, prefix: prefix, reference: reference, session: session, timeout: timeout);
            }
            return RunRoute(at: at, budget: budget, loops: loops, output: output, prefix: prefix, route: route, session: context.TextSession, timeout: timeout);
        }

        var path = (File.Exists(path: reference) ? Path.GetFullPath(path: reference) : Path.GetFullPath(path: reference, basePath: WorldDocumentPaths.DirectoryOf(documentPath: source.SourcePath)));

        if (!File.Exists(path: path)) {
            return CommandResult.Error(output: $"[world.benchmark: '{reference}' names no route of the running world ({string.Join(separator: ", ", values: (server?.Definition.Routes.Select(selector: static candidate => candidate.Name) ?? []))}) and no .puck source beside the working directory or the world's document]");
        }

        _ = WorldCompileSession.Shared.TryCompile(compiled: out var compiled, failure: out var failure, includeTests: true, path: path);
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
            var body = WorldRouteArrival.BodyOf(gates: gates);
            var duration = checked((int)((last - first) + settle));

            programs.Add(item: new Program(Body: body, Duration: duration, Gates: gates, Identity: IdentityOf(rows: program, duration: duration, gates: gates), Name: test.Test, Rows: program));
        }
        if (LinesOf(programs: programs) > WorldTickScript.MaxPending) {
            return CommandResult.Error(output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.benchmark: one loop of the route is {LinesOf(programs: programs)} lines, which passes the {WorldTickScript.MaxPending} a World holds armed — play fewer chapters with a name prefix]"));
        }

        m_programs.Clear();
        m_programs.AddRange(collection: programs);
        m_driver = null;
        m_goalChapter = null;
        m_chapters.Clear();
        m_signals.Clear();
        m_raw.Clear();
        m_loops = loops;

        var firstTick = Math.Max(val1: (script.CompletedTick + LeadTicks), val2: (at ?? 0UL));

        if (ArmLoop(loop: 0, start: firstTick, next: out _) is { } refused) {
            _ = script.Clear();
            m_chapters.Clear();
            m_signals.Clear();
            m_raw.Clear();

            return CommandResult.Error(output: $"[world.benchmark: {refused}]");
        }

        var lastTick = (firstTick + ((ulong)(m_programs.Sum(selector: static program => (program.Duration + GapTicks)) * loops)) - GapTicks);

        return Enter(budget: budget, firstTick: firstTick, label: (string.IsNullOrEmpty(value: prefix) ? reference : $"{reference}#{prefix}"), lastTick: lastTick, loops: loops, output: output, session: context.TextSession, timeout: timeout);
    }

    // Asks the seat's navigation to bake and answers at once. The pump starts the run once the bake is done, or reports that
    // it never started once the run's own wall-time limit passes, so the wait is bounded by the caller's timeout and not by
    // the time its call may take to answer; world.benchmark reads which. The wait precedes the run's recording, so no frame
    // of it is a measured one.
    private CommandResult Prepare(CommandContext context, TextCommandSession session, Puck.World.Client.WorldAuthorityEndpoint endpoint, string reference, string prefix, ulong? at, double budget, int timeout, int loops, string? output) {
        var started = Stopwatch.GetTimestamp();
        var preparing = new Preparation(endpoint: endpoint, limit: (started + (Stopwatch.Frequency * timeout)), reference: reference, started: started);

        senses!.Navigation(endpoint: endpoint).Prepare();
        m_preparing = preparing;
        session.CompleteOnPump(
            done: () => {
                var navigation = senses.Navigation(endpoint: endpoint);

                if (preparing.Stopped || navigation.Ready || (Stopwatch.GetTimestamp() >= preparing.Limit)) { return true; }
                navigation.Prepare();
                return false;
            },
            complete: () => {
                m_preparing = null;
                if (preparing.Stopped) { return; }

                var navigation = senses.Navigation(endpoint: endpoint);
                var (baked, declared) = navigation.Domains;
                var seconds = Stopwatch.GetElapsedTime(startingTimestamp: started).TotalSeconds;

                if (!navigation.Ready) {
                    Unstarted(reference: reference, reason: string.Create(provider: CultureInfo.InvariantCulture, handler: $"navigation preparation did not finish within the run's {timeout} s limit; {baked}/{declared} domains of the document '{endpoint.Identity}' delivered are baked, the rest bake on"));
                    return;
                }

                m_prepared = new JsonObject { ["what"] = "navigation", ["seconds"] = seconds, ["domainsBaked"] = baked, ["domainsDeclared"] = declared };

                var begun = Run(at: at, budget: budget, context: context, loops: loops, output: output, prefix: prefix, reference: reference, timeout: timeout);

                if (begun.IsError) {
                    m_prepared = null;
                    Unstarted(reference: reference, reason: begun.Output);
                } else {
                    Console.Error.WriteLine(value: begun.Output);
                }
            }
        );

        return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.benchmark: preparing navigation for '{reference}' over the document '{endpoint.Identity}' delivered, off the tick; the run starts once it is baked, or never starts after {timeout} s — world.benchmark reads which]"));
    }

    // Records, and narrates on standard error, that a route run never started.
    private void Unstarted(string reference, string reason) {
        m_summary = $"[world.benchmark: the route '{reference}' never started: {reason}]";
        Console.Error.WriteLine(value: m_summary);
    }

    // Plays a route of the running document: each chapter's steps are played by the route's seat program through the
    // seat's own ingress, one chapter after another from the tick hook, since a chapter ends when its steps do.
    private CommandResult RunRoute(WorldRoute route, string prefix, ulong? at, double budget, int timeout, int loops, string? output, TextCommandSession? session) {
        if ((seatRouter is null) || (senses is null) || (server is null)) {
            return CommandResult.Error(output: "[world.benchmark: this host composes no seat routes or seat views, so it plays no route]");
        }

        var program = server.Definition.SeatPrograms.FirstOrDefault(predicate: candidate => string.Equals(a: candidate.Name, b: route.Program, comparisonType: StringComparison.Ordinal));
        var chapters = route.Chapters.Where(predicate: chapter => chapter.Name.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal)).ToArray();

        if (program is null) {
            return CommandResult.Error(output: $"[world.benchmark: the route '{route.Name}' names the seat program '{route.Program}', which the running world does not author]");
        }
        if (chapters.Length == 0) {
            return CommandResult.Error(output: $"[world.benchmark: the route '{route.Name}' has no chapter whose name starts with '{prefix}' (it has {string.Join(separator: ", ", values: route.Chapters.Select(selector: static chapter => $"\"{chapter.Name}\""))})]");
        }

        var description = Puck.World.Client.WorldSeatDriver.Describe(program: program);
        var slot = (route.Seat - 1);

        m_programs.Clear();
        m_programs.AddRange(collection: chapters.Select(selector: chapter => new Program(Body: (route.Seat - 1), Duration: 0, Gates: [], Goals: chapter, Identity: IdentityOf(chapter: chapter, program: description), Name: chapter.Name, Rows: [])));
        m_chapters.Clear();
        m_signals.Clear();
        m_raw.Clear();
        m_loops = loops;
        m_rateHz = server.Definition.SimulationRateHz;
        m_driver = new Puck.World.Client.WorldSeatDriver(program: program, route: route, senses: senses, submit: text => {
            var line = script.SubmitSeat(router: router, slot: slot, text: text);

            return () => line.Outcome;
        });

        var firstTick = Math.Max(val1: (script.CompletedTick + LeadTicks), val2: (at ?? 0UL));

        try {
            ArmGoalChapter(loop: 0, position: 0, start: firstTick);
        } catch (InvalidOperationException refusal) {
            _ = script.Clear();
            m_chapters.Clear();
            m_raw.Clear();
            m_driver = null;
            return CommandResult.Error(output: $"[world.benchmark: {refusal.Message}]");
        }
        script.Ticked += OnTicked;

        // A route's length is known only as it plays; its chapters' time limits bound it.
        var bound = (ulong)(chapters.Sum(selector: chapter => Math.Ceiling(a: (chapter.TimeoutSeconds * m_rateHz)) + GapTicks) * loops);

        return Enter(budget: budget, firstTick: firstTick, label: (string.IsNullOrEmpty(value: prefix) ? route.Name : $"{route.Name}#{prefix}"), lastTick: (firstTick + bound), loops: loops, output: output, session: session, timeout: timeout);
    }

    // Arms a route chapter's start at a tick: its begin mark and opening reads on the tick before, which is also the tick
    // whose hook starts the seat driver on its first step.
    private void ArmGoalChapter(int position, int loop, ulong start) {
        var program = m_programs[position];
        var chapter = new Chapter(loop: loop, program: program, start: start) { LastOfLoop = (position == (m_programs.Count - 1)) };
        var index = m_chapters.Count;

        m_chapters.Add(item: chapter);
        _ = Pin(tick: (start - 1UL), line: $"world.benchmark mark {index} begin");
        foreach (var read in ChapterReads) {
            chapter.OpenReads.Add(item: Pin(tick: (start - 1UL), line: read, raw: true));
        }
        m_goalChapter = chapter;
    }

    // The tick hook of a route run: starts the chapter whose start is the next tick, advances the one playing, samples it,
    // and when it ends arms its closing reads on the next tick and the next chapter after the gap.
    private void OnTicked(ulong tick) {
        if (!m_running || (m_driver is not { } driver) || (m_goalChapter is not { } chapter)) {
            return;
        }
        if ((tick + 1UL) == chapter.Start) {
            chapter.Endpoint = seatRouter?.TryRoute(slot: driver.Seat)?.Endpoint;
            chapter.DeliveryAtBegin = (chapter.Endpoint?.Mirror.DeliveryWork ?? default);
            driver.Begin(chapter: chapter.Program.Goals!, rateHz: m_rateHz, tick: tick);
            return;
        }
        if ((tick < chapter.Start) || driver.Ended) {
            return;
        }

        driver.Advance(rateHz: m_rateHz, tick: tick);

        var next = (tick + 1UL);
        var index = m_chapters.IndexOf(item: chapter);
        var read = ReadOf(read: driver.Read, tick: tick);

        // The chapter's first tick poses the body, so its start is read on the tick after.
        if ((tick == (chapter.Start + 1UL)) || ((chapter.StartRead is null) && (tick > (chapter.Start + 1UL)))) {
            chapter.StartRead = read;
        }

        try {
            if (!driver.Ended) {
                if (((next - chapter.Start) % SampleTicks) == 0UL) {
                    if (read is not null) {
                        chapter.Samples.Add(item: read);
                    }
                    _ = Pin(tick: next, line: $"world.benchmark sample {index}");
                }
                return;
            }

            var endpoint = seatRouter?.TryRoute(slot: driver.Seat)?.Endpoint;

            chapter.Duration = (int)(next - chapter.Start);
            chapter.Arrived = driver.Arrived;
            chapter.Failure = driver.Failure;
            chapter.FailureKind = driver.FailureKind;
            chapter.FailureTick = driver.FailureTick;
            chapter.FailurePlace = driver.FailurePlace;
            chapter.StepReached = driver.StepReached;
            chapter.Plans = driver.Plans;
            chapter.ControlLines = driver.Submitted;
            chapter.ControlRefusals = driver.Refused;
            chapter.Visited = [.. driver.Visited];
            chapter.EndRead = (read ?? chapter.Samples.LastOrDefault());
            chapter.Delivery = DescribeDelivery(begin: chapter.Endpoint, end: endpoint, workAtBegin: chapter.DeliveryAtBegin);
            // The state a client can hash is its own process's. A seat whose authority is another process has none to
            // read here, and the boot world's hash would describe a world the seat is not in.
            chapter.Hash = ((endpoint is { ClockOwnedHere: true }) ? Pin(tick: next, line: "world.state.hash") : null);
            foreach (var reading in ChapterReads) {
                chapter.CloseReads.Add(item: Pin(tick: next, line: reading, raw: true));
            }
            _ = Pin(tick: next, line: $"world.benchmark mark {index} end");

            var position = (m_programs.IndexOf(item: chapter.Program) + 1);
            var loop = chapter.Loop;

            if (position >= m_programs.Count) {
                position = 0;
                loop++;
            }

            m_goalChapter = null;
            if (loop < m_loops) {
                ArmGoalChapter(loop: loop, position: position, start: (next + GapTicks));
            }
        } catch (InvalidOperationException refusal) {
            _ = End(reason: refusal.Message);
        }
    }

    // A seat driver's reading spelled as body.where and body.place spell theirs, so one reader serves both sources. The
    // tick is the host's; the reading says which authority tick it was delivered for.
    private static Read? ReadOf(Puck.World.Client.WorldSeatSenses.Reading? read, ulong tick) => ((read is { } seat)
        ? new Read(
            Answer: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[delivered: body:{seat.Index} of {seat.World} at its tick {seat.Body.Tick} pos=({seat.Body.Position.X:0.00}, {seat.Body.Position.Y:0.00}, {seat.Body.Position.Z:0.00}) facts={((seat.Body.Facts == Puck.Physics.Motion.BodyFacts.None) ? "none" : Puck.Physics.Motion.BodyFactVocabulary.Describe(facts: seat.Body.Facts))} vel=({seat.Body.Velocity.X:0.00}, {seat.Body.Velocity.Y:0.00}, {seat.Body.Velocity.Z:0.00}) places={((seat.Places.Count == 0) ? "none" : string.Join(separator: ',', values: seat.Places))}]"),
            Tick: tick)
        : null);

    // What the seat's authority delivered across a chapter and what applying it cost: the change of its mirror's
    // delivery work, and the evenness of its latest snapshot arrivals. A seat that changed authority inside the chapter
    // has no one mirror to difference, so the chapter names both and reports no counts.
    private static JsonObject? DescribeDelivery(Puck.World.Client.WorldAuthorityEndpoint? begin, Puck.World.Client.WorldAuthorityEndpoint? end, Puck.World.Client.WorldDeliveryWork workAtBegin) {
        if (end is null) {
            return null;
        }

        var node = new JsonObject {
            ["endpoint"] = end.Identity,
            ["authority"] = end.Authority,
            ["clockOwnedHere"] = end.ClockOwnedHere,
        };

        if (!ReferenceEquals(objA: begin, objB: end)) {
            node["beganAt"] = begin?.Identity;
            return node;
        }

        var work = (end.Mirror.DeliveryWork - workAtBegin);
        Span<double> intervals = stackalloc double[63];
        var count = end.Mirror.SnapshotArrivalIntervals(milliseconds: intervals);

        node["snapshots"] = work.Snapshots;
        node["definitions"] = work.Definitions;
        node["states"] = work.States;
        node["applyMs"] = work.Milliseconds;
        node["snapshotApplyMs"] = ((work.SnapshotTicks * 1000.0) / System.Diagnostics.Stopwatch.Frequency);
        if (count > 0) {
            var sum = 0.0;
            var max = 0.0;

            foreach (var interval in intervals[..count]) {
                sum += interval;
                max = Math.Max(val1: max, val2: interval);
            }
            node["arrivalIntervalMs"] = new JsonObject { ["mean"] = (sum / count), ["max"] = max, ["samples"] = count };
        }

        return node;
    }

    // A route chapter's identity: the hash of its canonical steps, its time limit and the seat program that plays it, so
    // a chapter that is the same goals played the same way is the same chapter whatever it is named.
    private static string IdentityOf(WorldRouteChapter chapter, string program) {
        var text = new StringBuilder(value: program);

        foreach (var step in chapter.Steps) {
            _ = text.Append(value: Puck.World.Client.WorldSeatDriver.Describe(step: step)).Append(value: '\n');
        }
        _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"timeout {chapter.TimeoutSeconds}\n");

        return AssetContentHash.OfText(text: text).Hex;
    }

    // Establishes a run's span and starts recording it: what a test-block run and a route run share once their first
    // chapter is armed.
    private CommandResult Enter(string label, ulong firstTick, ulong lastTick, double budget, int timeout, int loops, string? output, TextCommandSession? session) {
        m_route = label;
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
        script.Answered += OnAnswered;
        if (timeline is { } frames) {
            m_recording = new HostFrameRecording(capacity: ((int)Math.Min(val1: ((long)HostFrameRecording.MaxCapacity), val2: ((((long)(lastTick - firstTick)) * 24L) + 8192L)))) {
                Flags = () => ((pacing?.TickLocked == true) ? LockstepFlag : 0),
                Ticks = () => script.PublishedTick,
            };
            m_deadline = (frames.Time.GetTimestamp() + (timeout * frames.Time.TimestampFrequency));
            // The wall-time limit ends the run in a console drain, where its end may read the authority and arm the
            // seat's releases; a frame sample's callback runs while a tick may be in flight on the step thread. A drain
            // runs on a paused or held host too, so a run that never ticks still ends at its limit.
            if (session is not null) {
                var run = ++m_runSerial;

                session.CompleteOnPump(
                    done: () => ((run != m_runSerial) || !m_running || (frames.Time.GetTimestamp() > m_deadline)),
                    complete: () => {
                        if ((run == m_runSerial) && m_running) {
                            _ = End(reason: $"the run passed its {timeout} s wall-time limit");
                        }
                    }
                );
            }
        }
        m_adapter?.Dispose();
        m_adapter = new AdapterTelemetryRecorder(telemetry: AdapterTelemetry.Open(adapter: deviceProfile?.Current.Adapter), interval: AdapterSampleInterval);
        m_running = true;
        m_overlayAtEntry = OverlayVisible;
        m_overlayChanged = false;
        gpuTiming?.RequireRecording(true);

        return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.benchmark: playing '{m_route}': {m_programs.Count} chapter(s) x {loops} loop(s), ticks {firstTick}..{(m_driver is null ? string.Empty : "at most ")}{lastTick}; {m_entry}; report in {PuckPaths.Normalize(path: m_directory)}]"));
    }

    // Arms one loop of the programs from a tick: each chapter's lines, its readings and its samples, all through the
    // doors a headless test uses. Returns why a line was refused, or null once the whole loop is armed; the tick the
    // next loop would start at is the chapter after the last.
    private string? ArmLoop(int loop, ulong start, out ulong next) {
        next = start;

        try {
            for (var position = 0; (position < m_programs.Count); position++) {
                var program = m_programs[position];
                var chapter = new Chapter(loop: loop, program: program, start: next);
                var index = m_chapters.Count;
                var where = $"body.where {program.Body.ToString(provider: CultureInfo.InvariantCulture)}";
                var place = $"body.place {program.Body.ToString(provider: CultureInfo.InvariantCulture)}";
                // The chapter before this one in the run, whose program may end with controls still held: a chapter
                // starts from released input whatever came before it, so those are let go on its first tick, ahead of
                // its own lines.
                var before = ((position > 0) ? m_programs[(position - 1)] : ((loop > 0) ? m_programs[^1] : null));

                m_chapters.Add(item: chapter);
                _ = Pin(tick: (next - 1UL), line: $"world.benchmark mark {index} begin");
                foreach (var read in ChapterReads) {
                    chapter.OpenReads.Add(item: Pin(tick: (next - 1UL), line: read, raw: true));
                }
                foreach (var ((slot, _), release) in Held(lines: (before?.Rows ?? []).Select(selector: static row => (row.Command, row.Slot)))) {
                    m_signals.Add(item: (ArmStep(line: release, program: program, slot: slot, tick: next), slot));
                }
                foreach (var row in program.Rows) {
                    var step = ArmStep(line: row.Command, program: program, slot: row.Slot, tick: (next + (ulong)row.At));

                    chapter.Steps.Add(item: (step, row.Refusal, row.Slot));
                    m_signals.Add(item: (step, row.Slot));
                }
                // The chapter's first tick poses the body, so its start place is read on the tick after.
                chapter.StartPlace = Pin(tick: (next + 1UL), line: place);
                for (var offset = SampleTicks; (offset < program.Duration); offset += SampleTicks) {
                    chapter.Poses.Add(item: Pin(tick: (next + (ulong)offset), line: where));
                    chapter.Places.Add(item: Pin(tick: (next + (ulong)offset), line: place));
                    _ = Pin(tick: (next + (ulong)offset), line: $"world.benchmark sample {index}");
                }

                var end = (next + (ulong)program.Duration);

                chapter.Where = Pin(tick: end, line: where);
                chapter.EndPlace = Pin(tick: end, line: place);
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
        if ((m_finishOwed is { } recording) && !ReadsPending()) {
            m_finishOwed = null;
            script.Answered -= OnAnswered;
            _ = Finish(reason: null, dropped: 0, recording: recording);
        }
    }

    // Arms one seat line of a chapter through the seat's own ingress, or refuses with why.
    private WorldTickScript.Line ArmStep(string line, int slot, ulong tick, Program program) => ((script.ArmSeat(line: out var step, router: router, slot: slot, text: line, tick: tick) is { } refused)
        ? throw new InvalidOperationException(message: $"the chapter \"{program.Name}\" could not arm '{line}': {refused}")
        : step);

    // Folds seat lines, in the order they run, into the controls their signals leave pressed or deflected, each with the
    // line that lets it go, ordered by seat and source so the releases arm in the same order on every run.
    private static SortedDictionary<(int Slot, string Source), string> Held(IEnumerable<(string Command, int Slot)> lines) {
        var held = new SortedDictionary<(int Slot, string Source), string>(comparer: Comparer<(int Slot, string Source)>.Create(comparison: static (left, right) => ((left.Slot != right.Slot) ? left.Slot.CompareTo(value: right.Slot) : string.CompareOrdinal(strA: left.Source, strB: right.Source))));

        foreach (var (command, slot) in lines) {
            if (Signal().Match(input: command) is not { Success: true } signal) {
                continue;
            }

            var key = (slot, signal.Groups["source"].Value);
            var values = signal.Groups["value"].Value.Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries);

            if (values[0] == "press") {
                held[key] = $"player.signal {key.Value} release";
            } else if ((values[0] == "release") || values.All(predicate: static value => (double.TryParse(s: value, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var number) && (number == 0.0)))) {
                _ = held.Remove(key: key);
            } else if (values.All(predicate: static value => double.TryParse(s: value, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out _))) {
                held[key] = $"player.signal {key.Value} {string.Join(separator: ' ', values: values.Select(selector: static _ => "0"))}";
            }
        }

        return held;
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

        return AssetContentHash.OfText(text: text).Hex;
    }

    private static string IdentityOf(IEnumerable<string> identities) => AssetContentHash.OfText(text: string.Join(separator: '\n', values: identities)).Hex;

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
            chapter.BeginStamp = Stopwatch.GetTimestamp();
            chapter.ShedAtBegin = (timeline?.ShedSteps ?? 0L);
            chapter.DevicePeakAtBegin = DeviceLedger().Peak;

            return new CommandResult(Output: $"[world.benchmark: chapter {index} \"{chapter.Name}\" begins]");
        }

        chapter.EndIndex = (m_recording?.Count ?? 0);
        chapter.EndStamp = Stopwatch.GetTimestamp();
        chapter.Shed = ((timeline?.ShedSteps ?? 0L) - chapter.ShedAtBegin);
        chapter.Device = DeviceLedger();
        chapter.Adapter = m_adapter?.Summarize(from: chapter.BeginStamp, to: chapter.EndStamp);

        if (!chapter.LastOfLoop) {
            return new CommandResult(Output: $"[world.benchmark: chapter {index} \"{chapter.Name}\" {Verdict(chapter: chapter, statistics: null)}]");
        }
        if ((chapter.Loop + 1) >= m_loops) {
            return End(reason: null);
        }
        if (m_driver is not null) {
            return new CommandResult(Output: $"[world.benchmark: chapter {index} \"{chapter.Name}\" {Verdict(chapter: chapter, statistics: null)}; loop {chapter.Loop} ends]");
        }

        // The next loop is armed as this one ends, from the tick after the gap, so its lines land on ticks that follow
        // from the run's own start and not from when this line happened to run.
        return ((ArmLoop(loop: (chapter.Loop + 1), start: (chapter.Start + (ulong)chapter.Duration + GapTicks), next: out _) is { } refused)
            ? End(reason: $"loop {(chapter.Loop + 1)} could not be armed: {refused}")
            : new CommandResult(Output: $"[world.benchmark: loop {chapter.Loop} ends; loop {(chapter.Loop + 1)} is armed]"));
    }

    // Folds the rolling mean of every GPU pass the nodes hold into the chapter the sample belongs to.
    private CommandResult Sample(int index) {
        if (!m_running || (index < 0) || (index >= m_chapters.Count) || (gpuTiming is not { Available: true })) {
            return new CommandResult(Output: string.Empty);
        }

        var chapter = m_chapters[index];

        foreach (var timing in gpuTiming.Readings()) {
            if (timing.LastSubmission == 0) {
                continue;
            }

            chapter.Gpu.TryGetValue(key: timing.Pass, value: out var held);
            chapter.Gpu[timing.Pass] = ((held.Sum + timing.PerFrameMs), Math.Max(val1: held.Max, val2: timing.PerFrameMs), (held.Count + 1));
        }

        return new CommandResult(Output: string.Empty);
    }

    /// <summary>Ends a playing run as <c>stop</c> does when the World shuts down: its partial report is written, naming
    /// the shutdown as its reason. A no-op when no run is playing. Runs on the host's thread after the host loop has
    /// exited.</summary>
    public void Shutdown() {
        if (m_running) {
            _ = End(reason: "the World shut down", releases: false);
        } else if (m_finishOwed is { } recording) {
            m_finishOwed = null;
            script.Answered -= OnAnswered;
            _ = Finish(reason: null, dropped: 0, recording: recording);
        }
    }

    // Ends the run where it stands: lets the held controls go, restores the pacing and timestamps it changed and writes
    // the report, whole or partial. A reason names why a run ended before its last chapter. A World that is shutting down
    // steps no further tick, so it arms no release.
    private CommandResult End(string? reason, bool releases = true) {
        if (!m_running) {
            return CommandResult.Error(output: "[world.benchmark: no run is playing]");
        }

        m_running = false;
        m_deadline = 0L;
        script.Ticked -= OnTicked;

        var dropped = ((reason is null) ? 0 : script.Clear());
        var recording = m_recording;

        if (ReferenceEquals(objA: timeline?.Recording, objB: recording)) {
            _ = timeline?.StopRecording();
        }
        // What the lines that reached the seat left held: a line the clear dropped never pressed anything, and a refused
        // one changed nothing, while one still settling is let go behind it.
        if (releases) {
            foreach (var ((slot, _), release) in Held(lines: m_signals.Where(predicate: static signal => signal.Line.Outcome is "pending" or "ran" or "faulted").Select(selector: static signal => (signal.Line.Text, signal.Slot)))) {
                _ = script.ArmSeat(line: out _, router: router, slot: slot, text: release, tick: (script.CompletedTick + 1UL));
            }
            foreach (var release in (m_driver?.TakeReleases() ?? [])) {
                _ = script.ArmSeat(line: out _, router: router, slot: (m_programs[0].Body), text: release, tick: (script.CompletedTick + 1UL));
            }
        }
        if (m_lockstepAtEntry) {
            pacing?.SetTickLocked(locked: true);
        }
        if (reason is not null) {
            AddUnarmedLoops();
        }
        m_goalChapter = null;

        // A read whose answer settles after its line ran (host.memory walks the process off the pump) is waited for
        // by a run that finished, so its report holds every reading; a run ended early writes what it has.
        if ((reason is null) && ReadsPending()) {
            m_finishOwed = recording;
            return new CommandResult(Output: $"[world.benchmark: '{m_route}' finished; its report is written once its last chapter reads answer]");
        }
        script.Answered -= OnAnswered;
        return Finish(reason: reason, dropped: dropped, recording: recording);
    }

    // The recording of a finished run whose report waits for its pending reads, or null.
    private HostFrameRecording? m_finishOwed;

    private bool ReadsPending() => m_raw.Keys.Any(predicate: static line => (line.Outcome is "pending" or "unreached"));

    // A run that ends early lists every loop it was asked for: each loop never armed holds its chapters, unreached, at
    // the ticks it would have started on.
    private void AddUnarmedLoops() {
        if (m_chapters.Count == 0) {
            return;
        }

        var span = (ulong)m_programs.Sum(selector: static program => (program.Duration + GapTicks));
        var origin = m_chapters[0].Start;

        if (m_driver is not null) {
            var last = m_chapters[^1];

            for (var position = (m_programs.IndexOf(item: last.Program) + 1); (position < m_programs.Count); position++) {
                m_chapters.Add(item: new Chapter(loop: last.Loop, program: m_programs[position], start: 0UL) { LastOfLoop = (position == (m_programs.Count - 1)) });
            }
        }
        for (var loop = (m_chapters[^1].Loop + 1); (loop < m_loops); loop++) {
            var next = (origin + (span * (ulong)loop));

            foreach (var program in m_programs) {
                m_chapters.Add(item: new Chapter(loop: loop, program: program, start: next));
                next += (ulong)(program.Duration + GapTicks);
            }
            m_chapters[^1].LastOfLoop = true;
        }
    }

    // Why a chapter's span cannot be vouched for, or an empty list.
    private static List<string> Disturbances(Chapter chapter, HostFrameStatistics? statistics) {
        var reasons = new List<string>();
        var refused = chapter.Steps.Where(predicate: static step => (step.Refusal != (step.Line.Outcome == "refused")) || (step.Line.Outcome == "faulted")).Select(selector: static step => step.Line).ToArray();

        if (refused.Length > 0) {
            reasons.Add(item: $"{refused.Length} step(s) did not answer as authored, first '{refused[0].Text}' ({refused[0].Outcome}: {refused[0].Answer})");
        }
        if (chapter.ControlRefusals > 0) {
            reasons.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"the seat's ingress refused {chapter.ControlRefusals} of the seat program's {chapter.ControlLines} control line(s)"));
        }
        if (statistics is { Flagged: > 0 }) {
            reasons.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"presentation was locked to ticks in {statistics.Flagged} iteration(s)"));
        }
        if (statistics is { LongestStall: > StallMilliseconds }) {
            reasons.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"the simulation stepped no tick for {statistics.LongestStall:0} ms (a pause, a hold or a stall)"));
        }
        if (chapter.Adapter?.HeldReason is { } held) {
            reasons.Add(item: held);
        }
        // A chapter that raised the most the device ever held to what the system budgets the process is one in which
        // the system moved memory: a pass that touched it waited for it, so a pass's reading there is not its cost.
        var (peak, budget, beyond) = chapter.Device;

        if ((budget > 0L) && (peak > chapter.DevicePeakAtBegin) && ((peak + beyond) >= budget)) {
            reasons.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"the device's memory held {(peak >> 20)} MiB at once with {(beyond >> 20)} MiB beyond the ledger, past the {(budget >> 20)} MiB the system budgets the process (a pass that touched moved memory waited for it)"));
        }

        return reasons;
    }
    // The device's own memory now: the most its ledger has held at once, what the system budgets the process there, and
    // what the system counts in use beyond what the ledger holds (the driver's and the swapchain's). Zeroes on unified
    // memory and without a device.
    private static (long Peak, long Budget, long Beyond) DeviceLedger() {
        var pools = Puck.Hosting.Jobs.MemoryAccount.Shared.ReadPools();
        var (peak, held, budget, usage) = (0L, 0L, 0L, 0L);

        foreach (var segment in pools.SegmentAdmissions) {
            if (segment.Segment.Pool == PhysicalMemory.Device) {
                peak += segment.Peak;
                held += segment.Held;
            }
        }
        foreach (var segment in pools.DeviceReading.Segments) {
            if (segment.Segment.Pool == PhysicalMemory.Device) {
                budget += (long)segment.Budget;
                usage += (long)(segment.Usage ?? 0UL);
            }
        }

        return (peak, budget, Math.Max(val1: 0L, val2: (usage - held)));
    }

    // The gates the body's pose fails, the gates this read cannot judge, and whether the chapter ended at all.
    private static (IReadOnlyList<string> Failed, IReadOnlyList<string> Unjudged, bool Read) Arrival(Chapter chapter) {
        var where = (chapter.EndPose?.Answer ?? string.Empty);
        var pose = Pose().Match(input: where);
        WorldRoutePose? posed = (pose.Success
            ? new WorldRoutePose(
                Grounded: ((Facts().Match(input: where) is { Success: true } facts) && facts.Groups["facts"].Value.Split(separator: '|').Contains(value: "grounded")),
                X: double.Parse(s: pose.Groups["x"].Value, provider: CultureInfo.InvariantCulture),
                Y: double.Parse(s: pose.Groups["y"].Value, provider: CultureInfo.InvariantCulture),
                Z: double.Parse(s: pose.Groups["z"].Value, provider: CultureInfo.InvariantCulture))
            : null);
        var failed = WorldRouteArrival.Judge(gates: chapter.Gates, pose: body => ((body == chapter.Body) ? posed : null), unjudged: out var unjudged);

        return (failed, unjudged, pose.Success);
    }

    private string Verdict(Chapter chapter, HostFrameStatistics? statistics) {
        if (chapter.EndIndex < 0) {
            return ((chapter.BeginIndex < 0) ? "unreached" : "unfinished");
        }

        var (failed, unjudged, read) = Arrival(chapter: chapter);
        var disturbed = Disturbances(chapter: chapter, statistics: statistics);
        var arrival = (chapter.Driven
            ? (chapter.Arrived ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"arrived in {chapter.Duration} ticks") : $"FAILED: {chapter.Failure}")
            : (!read ? "arrival unmeasured (no body pose was read)" : ((failed.Count > 0) ? $"OFF COURSE: {string.Join(separator: "; ", values: failed)}" : ((unjudged.Count > 0) ? $"arrived ({unjudged.Count} gate(s) only the headless test judges)" : "arrived"))));

        var from = PlacesOf(read: chapter.Opening) is { Count: > 0 } start ? start[0]!.GetValue<string>() : "none";
        var to = PlacesOf(read: chapter.EndPlaces) is { Count: > 0 } end ? end[0]!.GetValue<string>() : "none";

        return $"{arrival} ({from} to {to}){((disturbed.Count > 0) ? $"; DISTURBED: {string.Join(separator: "; ", values: disturbed)}" : string.Empty)}{((statistics is null) ? string.Empty : $"; {Frames(statistics: statistics)}")}{((chapter.Shed > 0) ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"; {chapter.Shed} tick(s) shed") : string.Empty)}";
    }

    private string Frames(HostFrameStatistics statistics) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{statistics.Frames} iterations in {statistics.Seconds:0.00} s ({statistics.AverageRate:0.0}/s), interval avg {statistics.Interval.Mean:0.00} p99 {statistics.Interval.P99:0.00} max {statistics.Interval.Max:0.00} ms, work avg {statistics.Work.Mean:0.00} p99 {statistics.Work.P99:0.00} max {statistics.Work.Max:0.00} ms, {statistics.Over} over {m_budget:0.0} ms ({statistics.OverTwice} over twice it)");

    private CommandResult Status() {
        if (m_preparing is { } preparing) {
            var (baked, declared) = senses!.Navigation(endpoint: preparing.Endpoint).Domains;

            return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.benchmark: preparing navigation for '{preparing.Reference}': {baked}/{declared} domains baked, {Stopwatch.GetElapsedTime(startingTimestamp: preparing.Started).TotalSeconds:0} s of {Stopwatch.GetElapsedTime(startingTimestamp: preparing.Started, endingTimestamp: preparing.Limit).TotalSeconds:0} s]"));
        }
        // A finished run whose report waits for its last readings is still playing to whoever polls for the report: the
        // summary of the run before it, or of none, is not this run's.
        if (m_finishOwed is not null) {
            return new CommandResult(Output: $"[world.benchmark: playing '{m_route}': finished; its report is written once its last chapter reads answer]");
        }
        if (!m_running) {
            return new CommandResult(Output: m_summary);
        }

        var current = CurrentChapter;
        var completed = script.CompletedTick;

        return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.benchmark: playing '{m_route}' at tick {completed}: {((current < 0) ? $"waiting for tick {m_chapters[0].Start} (a paused world runs no route)" : $"loop {(m_chapters[current].Loop + 1)} of {m_loops}, chapter \"{m_chapters[current].Name}\"")}; {(m_recording?.Count ?? 0)} iteration(s) recorded; {m_entry}]"));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        RegisterReadings();
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            name: "world.benchmark",
            description: $"Plays a route through the running world in real time and reports every host iteration of it: {WorldBenchmarkForms.Spelling}. A route named by a routes row of the running document is played chapter by chapter by its seat program, which works the seat's own controls each tick toward the chapter's places; a chapter arrives when its steps finish and fails, naming the kind, the tick and the place, on a stick held while the body goes nowhere, on leaving the places its path crosses, on a lost path or past its time limit; its driver is the seat's client and reads only what the seat's authority delivered, in this process or another, and each chapter is armed as the one before it ends. Otherwise a route is the test blocks of a .puck source whose names start with the prefix (every one, with none or '-'), each a chapter played in declaration order: its when lines are armed through the seat's own ingress at their ticks, as a headless puck test submits them, and its expect lines are its arrival. The first line lands {LeadTicks} ticks after the run is armed, or at the tick 'at' names when that is later, and each chapter follows the one before by {GapTicks} ticks. 'loops' plays the route that many times (at most {MaxLoops}), each loop armed from the tick the one before it ends on, so a run is reported as its cold first loop, its warm second and its steady rest. A run armed before the world's first tick (a paused boot) is fresh: every line lands on a tick its caller chose from the boot image, so its state hashes compare with another fresh run's armed at the same tick. Any other run is attached, starts wherever the world stands, and its hashes are informational. Nothing in a run pauses, settles or waits, and lockstep presentation is turned off for its span and restored after. A route whose paths need the seat's navigation baked first waits for the bake before it arms, off the tick and without holding the call: the line answers at once, world.benchmark reads the bake's progress, and the run starts when it is baked or is reported as never started once 'timeout' seconds have passed. That wait is the run's preparation, written beside its frames and in none of them. Every host iteration from the first chapter's start is recorded past host.frames' ring, and the host's first iterations through that start are its boot span. Each chapter ends with the body's pose judged against its position and grounded gates (arrived or off course), the authority's state hash, its identity (the hash of its program), and its iterations' statistics: interval and work (the interval less the pacer's wait) as mean, median, 95th, 99th and 99.9th percentile and maximum, the counts whose work passed the budget, twice it, 50 ms and 100 ms, pacing deviation, collections and ticks shed; the iterations over budget with their tick, the body's last sampled position and their phases; each GPU pass's mean and maximum over samples taken every {SampleTicks} ticks; the raw answers of world.residency, the render.record, gpu.queue, gpu, sdf, world.field-queries, world.captures, world.disclosure and physics.bodies counters and host.memory at both ends, whose change a reader takes; and the device ledger's peak, its memory beyond the ledger and what the system budgets the process as the chapter ends. A chapter is written disturbed, with the reason and its statistics beside it, when presentation was locked to ticks in any iteration, the simulation stepped no tick for {StallMilliseconds:0} ms, a step did not answer as authored, or the device's memory rose in the chapter to what the system budgets the process (a pass that touched moved memory waited for it, so its reading is not its cost). The run writes run.json (puck.benchmark.run.v2) and frames.csv (one row per iteration) to the output directory, by default benchmarks/<local date and time> under the run's state root. The imported authored HUD panel follows a playing run by default; overlay on|off|auto overrides that visibility on demand. The report records that choice and the closing rolling GPU timestamp window, including the overlay pass when measured. With no argument it reads the running route's chapter and tick, or the last run's summary. Each chapter's first tick releases, ahead of its own lines, every control the chapter before it left held, so a chapter starts from released input whatever came before it. topology answers, as one JSON line, the authority seat 1 (or the playing route's seat) is routed to: its endpoint, whether this process steps its clock (in-process) or another does (remote), and whether its latest delivery holds the seat's body, so a caller waits on it before arming a route against a remote authority. marks <route> answers, as one JSON line, the places the route's captures name with each region's centre and half extents, which a capture pass frames from four sides. The budget an iteration is judged against defaults to the period of the selected device profile's frame bar (render.devices' frameBar), else one 60 Hz period, and the report names which. stop ends the run where it stands, drops its armed lines, lets go every control the lines that reached the seat left held and writes the partial report, listing every requested loop; a run that passes its wall-time limit (default {DefaultTimeoutSeconds} s) ends the same way, and so does a run whose World shuts down. Refused while world.at lines are armed: a run owns the pinned lines while it plays. An iteration is the host loop's interval, never the time a frame stood on the display, and a headless host records iterations without a renderer.",
            forms: WorldBenchmarkForms,
            handler: (context, args) => {
                if (args.Count == 2 && args.Is(0, "overlay")) {
                    if (!args.Is(1, "on") && !args.Is(1, "off") && !args.Is(1, "auto")) { return WorldBenchmarkForms.Refuse(args: in args); }
                    var before = OverlayVisible;
                    m_overlay = args.Is(1, "auto") ? null : args.Is(1, "on");
                    m_overlayChanged |= m_running && before != OverlayVisible;
                    return new CommandResult(Output: $"[world.benchmark overlay: {(m_overlay is null ? "auto" : m_overlay.Value ? "on" : "off")} visible={OverlayVisible.ToString().ToLowerInvariant()}]");
                }
                if (args.Count == 0) {
                    return Status();
                }
                if ((args.Count == 1) && args.Is(index: 0, value: "topology")) {
                    return new CommandResult(Output: $"[world.benchmark topology: {TopologyReport().ToJsonString()}]");
                }
                if ((args.Count == 2) && args.Is(index: 0, value: "marks")) {
                    return Marks(reference: args[1].ToString());
                }
                if ((args.Count == 1) && args.Is(index: 0, value: "stop")) {
                    if (m_preparing is { } preparing) {
                        preparing.Stopped = true;
                        m_preparing = null;
                        Unstarted(reference: preparing.Reference, reason: "world.benchmark stop ended its navigation preparation");

                        return new CommandResult(Output: m_summary);
                    }

                    return End(reason: "stopped");
                }
                if ((args.Count == 3) && args.Is(index: 0, value: "mark") && args.TryInt(index: 1, value: out var chapter) && (args.Is(index: 2, value: "begin") || args.Is(index: 2, value: "end"))) {
                    return Mark(begin: args.Is(index: 2, value: "begin"), index: chapter);
                }
                if ((args.Count == 2) && args.Is(index: 0, value: "sample") && args.TryInt(index: 1, value: out var sampled)) {
                    return Sample(index: sampled);
                }
                if ((args.Count < 2) || !args.Is(index: 0, value: "run")) {
                    return WorldBenchmarkForms.Refuse(args: in args);
                }

                var next = 2;
                var prefix = string.Empty;
                ulong? at = null;
                var (fallback, fallbackSource) = DeviceBudget();
                var budget = ((float)fallback);
                var budgetSource = fallbackSource;
                var timeout = DefaultTimeoutSeconds;
                var loops = 1;
                string? output = null;

                var seats = 1;

                if ((args.Count > 2) && !args.Is(index: 2, value: "at") && !args.Is(index: 2, value: "budget") && !args.Is(index: 2, value: "timeout") && !args.Is(index: 2, value: "loops") && !args.Is(index: 2, value: "seats") && !args.Is(index: 2, value: "output")) {
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
                        : (args.Is(index: next, value: "seats") ? (args.TryInt(index: (next + 1), value: out seats) && (seats >= 1) && (seats <= MaxSeats))
                        : args.Is(index: next, value: "output"))))));

                    if (!read) {
                        return WorldBenchmarkForms.Refuse(args: in args);
                    }
                    if (tick > 0) {
                        at = ((ulong)tick);
                    }
                    if (args.Is(index: next, value: "budget")) {
                        budgetSource = "named by the run";
                    }
                    if (args.Is(index: next, value: "output")) {
                        output = Path.GetFullPath(path: args[(next + 1)].ToString());
                    }
                }

                if (seats > 1) {
                    return CommandResult.Error(output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.benchmark: a route played on {seats} seats of one client needs one seat driver per seat against one authority, which this World does not run yet; it waits on the multi-seat client (docs/plans/open-items.md, a multi-seat client)]"));
                }
                if (!m_running && (m_preparing is null) && (m_finishOwed is null)) {
                    m_budgetSource = budgetSource;
                }

                return Run(at: at, budget: budget, context: context, loops: loops, output: output, prefix: prefix, reference: args[1].ToString(), timeout: timeout);
            }
        );
    }

    // The places a route's capture pass frames (its `captures`), each with its region's centre and half extents read from
    // the running document's places, as one JSON line; refuses, naming them, places the document does not author.
    private CommandResult Marks(string reference) {
        if (server?.Definition.Routes.FirstOrDefault(predicate: candidate => string.Equals(a: candidate.Name, b: reference, comparisonType: StringComparison.Ordinal)) is not { } route) {
            return CommandResult.Error(output: $"[world.benchmark: '{reference}' names no route of the running world]");
        }

        var places = WorldPlaces.Of(definition: server.Definition);
        var marks = new JsonArray();
        var missing = new List<string>();

        foreach (var name in (route.Captures ?? [])) {
            var found = false;

            foreach (var row in places.Rows) {
                if (!string.Equals(a: row.Placement.Id, b: name, comparisonType: StringComparison.Ordinal)) { continue; }

                var (center, half) = (row.StaticShape.Center, row.StaticShape.HalfExtents);

                marks.Add(item: new JsonObject {
                    ["place"] = name,
                    ["center"] = new JsonArray((double)center.X, (double)center.Y, (double)center.Z),
                    ["half"] = new JsonArray((double)half.X, (double)half.Y, (double)half.Z),
                });
                found = true;
                break;
            }
            if (!found) { missing.Add(item: name); }
        }

        return ((missing.Count > 0)
            ? CommandResult.Error(output: $"[world.benchmark: the route '{reference}' captures {string.Join(separator: ", ", values: missing)}, which the running world authors no place for]")
            : new CommandResult(Output: $"[world.benchmark marks: {marks.ToJsonString()}]"));
    }

    // The budget an iteration is judged against when a run names none: the period of the selected device profile's frame
    // bar, so each device is judged at its own rate, or one 60 Hz period where no profile with a bar is selected.
    private (double Milliseconds, string Source) DeviceBudget() {
        if ((deviceProfile?.Current.Profile is { } name) && (server is not null) && (WorldDeviceProfiles.Find(name: name, render: server.Definition.Render) is { FrameBar: { } bar })) {
            return (bar.PeriodMilliseconds, string.Create(provider: CultureInfo.InvariantCulture, handler: $"the frame bar of the device profile '{name}' ({bar.Hertz} Hz)"));
        }

        return (DefaultBudgetMilliseconds, "one 60 Hz period: no selected device profile authors a frame bar");
    }
}
