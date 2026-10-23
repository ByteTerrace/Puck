using System.Diagnostics;
using System.Globalization;

namespace Puck.Cli.Motion;

/// <summary>
/// Boots the real World, runs a plan on pinned ticks through it, and turns the stills it wrote into contact sheets.
/// One World runs every selected scenario, each from a reset body, so the boot's bake wait is paid once.
/// </summary>
public static class MotionRun {
    /// <summary>The ticks between the World's boot tick and the first pinned line: the arming lines' round trips take the World
    /// forward in lockstep by at most one tick a frame.</summary>
    public const int ArmingMargin = 120;
    /// <summary>The spacing, in ticks, of the start ticks a run chooses.</summary>
    public const ulong StartGrid = 300UL;

    private static readonly TimeSpan BakeDeadline = TimeSpan.FromSeconds(value: 600);

    /// <summary>Runs the scenarios in a fresh World and removes the World's state directory afterwards.</summary>
    /// <param name="request">What to run and where to write it.</param>
    /// <param name="progress">Where one-line progress reports go.</param>
    /// <returns>The outcome.</returns>
    public static MotionRunResult Execute(MotionRunRequest request, TextWriter progress) {
        try {
            return Run(progress: progress, request: request);
        } finally {
            try {
                if (Directory.Exists(path: request.StateDirectory)) {
                    Directory.Delete(path: request.StateDirectory, recursive: true);
                }
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                progress.WriteLine(value: $"motion: the World state directory {request.StateDirectory} was not removed: {exception.Message}");
            }
        }
    }

    private static MotionRunResult Run(MotionRunRequest request, TextWriter progress) {
        var clock = Stopwatch.StartNew();
        var notes = new List<string>();
        var world = Fail(clock: clock, error: string.Empty, notes: notes, request: request);

        _ = Directory.CreateDirectory(path: request.Directory);
        _ = Directory.CreateDirectory(path: request.StateDirectory);

        // Built before the World boots, so a world the check cannot read refuses the run at once.
        if (!MotionClearance.TryCreate(clearance: out var clearance, error: out var clearanceError, worldPath: request.World)) {
            return Fail(clock: clock, error: $"the camera clearance check could not be built: {clearanceError}", notes: notes, request: request);
        }

        List<string> arguments = [
            "--world", request.World,
            .. WorldDevices.NoneArguments,
            "--exit-after-seconds", (((int)request.Deadline.TotalSeconds) + 120).ToString(provider: CultureInfo.InvariantCulture),
            "--state-dir", request.StateDirectory,
        ];

        if (request.Backend is { } backend) {
            arguments.AddRange(collection: ["--backend", backend]);
        }
        if (request.CacheDirectory is { } cacheDirectory) {
            arguments.AddRange(collection: ["--cache-dir", cacheDirectory]);
        }

        using var process = MotionWorldProcess.Start(
            arguments: arguments,
            artifact: request.Artifact,
            logDirectory: request.Directory,
            workingDirectory: request.WorkingDirectory
        );

        progress.WriteLine(value: $"motion: World pid {process.ProcessId.ToString(provider: CultureInfo.InvariantCulture)} booting {request.World}");

        // Lock before the first bake frame: otherwise its wall-clock animation and exposure survive into the pinned
        // run. Each line runs after the one before it, so the tick read follows the locked bake wait.
        var mark = process.LineCount;

        _ = process.Send(line: "world.lockstep on");
        _ = process.Send(line: $"world.wait bakes {((int)BakeDeadline.TotalSeconds).ToString(provider: CultureInfo.InvariantCulture)}");
        _ = process.Send(line: "world.at");

        var readBack = process.WaitFor(accept: static line => ((line.Stream == CliProcessOutputStream.Stdout) && MotionTranscript.TryParseReadBack(line: line.Text, tick: out _)), from: mark, timeout: (BakeDeadline + TimeSpan.FromSeconds(value: 60)));

        if (readBack is null) {
            return Abort(clock: clock, error: $"the World never answered world.at after its bake wait; its output is in {request.Directory}/stdout.log and stderr.log{Tail(process: process)}", notes: notes, process: process, request: request);
        }

        _ = MotionTranscript.TryParseReadBack(line: readBack.Text, tick: out var bootTick);

        if (process.LinesFrom(from: mark).FirstOrDefault(predicate: static line => line.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[bakes: not settled")) is { } unsettled) {
            notes.Add(item: unsettled.Text);
        }

        var start = (request.StartTick ?? StartFor(bootTick: bootTick));

        if (start < (bootTick + ((ulong)ArmingMargin))) {
            _ = process.Send(line: "quit");
            _ = process.WaitForExit(timeout: TimeSpan.FromSeconds(value: 60));

            return world with { BootTick = bootTick, Error = $"the World had reached tick {bootTick.ToString(provider: CultureInfo.InvariantCulture)} when it was ready, past the start tick {start.ToString(provider: CultureInfo.InvariantCulture)} the run was asked to pin to", TooLateStart = StartFor(bootTick: bootTick), Seconds = clock.Elapsed.TotalSeconds };
        }

        var stills = Path.GetFullPath(path: request.Directory).Replace(newChar: '/', oldChar: '\\');
        var plan = MotionPlan.Build(selected: request.Selected, set: request.Set, startTick: start, stillsRoot: stills);

        progress.WriteLine(value: $"motion: ready at tick {bootTick.ToString(provider: CultureInfo.InvariantCulture)}; pinning {request.Selected.Count.ToString(provider: CultureInfo.InvariantCulture)} scenario(s) to ticks {start.ToString(provider: CultureInfo.InvariantCulture)}..{plan.LastTick.ToString(provider: CultureInfo.InvariantCulture)}");

        foreach (var scenario in plan.Scenarios) {
            _ = Directory.CreateDirectory(path: Path.Combine(path1: request.Directory, path2: scenario.Scenario.Name, path3: "raw"));
        }

        foreach (var arming in plan.ArmingLines()) {
            mark = process.LineCount;

            if (!process.Send(line: arming)) {
                return Abort(clock: clock, error: "the World closed its input while the run was being armed", notes: notes, process: process, request: request);
            }

            var answer = process.WaitFor(accept: static line => ((line.Stream is CliProcessOutputStream.Stdout or CliProcessOutputStream.Stderr) && line.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[world.at: ")), from: mark, timeout: TimeSpan.FromSeconds(value: 60));

            if ((answer is null) || !answer.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[world.at: armed")) {
                var late = ((answer?.Text.Contains(comparisonType: StringComparison.Ordinal, value: "already completed")) == true);

                _ = process.Send(line: "quit");
                _ = process.WaitForExit(timeout: TimeSpan.FromSeconds(value: 60));

                return world with { BootTick = bootTick, Error = $"the World refused to arm the run: {(answer?.Text ?? "no answer")}", Seconds = clock.Elapsed.TotalSeconds, StartTick = start, TooLateStart = (late ? (start + 600UL) : null) };
            }
        }

        mark = process.LineCount;
        _ = process.Send(line: $"world.wait until {(plan.LastTick + 1UL).ToString(provider: CultureInfo.InvariantCulture)}");
        _ = process.Send(line: "world.at");

        var remaining = (request.Deadline - clock.Elapsed);
        var done = process.WaitFor(accept: static line => ((line.Stream == CliProcessOutputStream.Stdout) && MotionTranscript.TryParseReadBack(line: line.Text, tick: out _)), from: mark, timeout: ((remaining > TimeSpan.Zero) ? remaining : TimeSpan.FromSeconds(value: 1)));

        if (done is null) {
            return Abort(clock: clock, error: $"the run did not reach tick {plan.LastTick.ToString(provider: CultureInfo.InvariantCulture)} within {request.Deadline.TotalMinutes:0} minutes (a hung capture, or a World that stopped stepping){Tail(process: process)}", notes: notes, process: process, request: request);
        }

        _ = process.Send(line: "quit");

        var exit = process.WaitForExit(timeout: TimeSpan.FromSeconds(value: 60));

        if (exit is not 0) {
            notes.Add(item: $"the World exited with {((exit is null) ? "no code (it had to be killed)" : exit.Value.ToString(provider: CultureInfo.InvariantCulture))}");
        }

        return MotionCollector.Collect(
            bootTick: bootTick,
            cellWidth: request.CellWidth,
            clearance: clearance,
            columns: request.Columns,
            directory: request.Directory,
            lines: [.. process.LinesFrom(from: 0).Select(selector: static line => line.Text)],
            notes: notes,
            plan: plan,
            seconds: clock.Elapsed.TotalSeconds
        );
    }

    /// <summary>Returns the start tick a World that was ready at a tick pins to: the first multiple of <see cref="StartGrid"/>
    /// past the arming margin, so Worlds that boot within a grid step of each other pin to the same ticks.</summary>
    /// <param name="bootTick">The tick the World had reached when its bakes settled and lockstep was on.</param>
    /// <returns>The start tick.</returns>
    public static ulong StartFor(ulong bootTick) => ((((bootTick + ((ulong)ArmingMargin)) + (StartGrid - 1UL)) / StartGrid) * StartGrid);

    private static MotionRunResult Fail(MotionRunRequest request, Stopwatch clock, string error, List<string> notes) => new(
        BootTick: 0UL,
        Cameras: [],
        Directory: request.Directory,
        Error: error,
        Notes: notes,
        Scenarios: [],
        Seconds: clock.Elapsed.TotalSeconds,
        StartTick: 0UL,
        TooLateStart: null
    );
    private static MotionRunResult Abort(MotionRunRequest request, Stopwatch clock, string error, List<string> notes, MotionWorldProcess process) {
        process.Kill();

        return Fail(clock: clock, error: error, notes: notes, request: request);
    }
    private static string Tail(MotionWorldProcess process) {
        var lines = process.LinesFrom(from: 0);

        return ((lines.Count == 0)
            ? string.Empty
            : $"{Environment.NewLine}last World output:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: lines.TakeLast(count: 6).Select(selector: static line => ("  " + line.Text)))}"
        );
    }
}
