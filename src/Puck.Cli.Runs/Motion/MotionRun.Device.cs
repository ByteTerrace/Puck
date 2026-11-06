using System.Diagnostics;
using System.Globalization;
using Puck.Confinement;

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
                ConfinedDirectory.RemoveTree(path: request.StateDirectory);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)) {
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

        WorldProcess process;

        try {
            using var launch = WorldLaunch.Admit(request: new WorldLaunchRequest(Artifact: request.Artifact, Step: "motion") {
                Arguments = ((request.CacheDirectory is { } cacheDirectory) ? ["--cache-dir", cacheDirectory] : []),
                Backend = request.Backend,
                ExitAfterSeconds = WorldLaunch.Backstop(deadline: request.Deadline),
                Shape = WorldShape.Document,
                StateDirectory = request.StateDirectory,
                WorkingDirectory = request.WorkingDirectory,
                World = request.World,
            });

            process = launch.Start(logDirectory: request.Directory);
        } catch (Exception exception) when ((exception is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)) {
            return Fail(clock: clock, error: $"the World did not start: {exception.Message.ReplaceLineEndings(replacementText: " ")}", notes: notes, request: request);
        }

        using var started = process;

        progress.WriteLine(value: $"motion: World pid {process.ProcessId.ToString(provider: CultureInfo.InvariantCulture)} booting {request.World}");

        // Lock before the first bake frame: otherwise its wall-clock animation and exposure survive into the pinned
        // run. Each line runs after the one before it, so the tick read follows the locked bake wait.
        var mark = process.LineCount;

        ulong bootTick;
        try {
            bootTick = process.RunScript(["world.lockstep on", $"world.wait bakes {((int)BakeDeadline.TotalSeconds).ToString(provider: CultureInfo.InvariantCulture)}"],
                BakeDeadline + TimeSpan.FromSeconds(60)).Tick;
        } catch (Exception exception) when (exception is IOException or TimeoutException) {
            return Abort(clock: clock, error: $"the World never answered after its bake wait: {exception.Message}{Tail(process: process)}", notes: notes, process: process, request: request);
        }

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

        var remaining = (request.Deadline - clock.Elapsed);
        try {
            _ = process.RunScript([$"world.wait until {(plan.LastTick + 1UL).ToString(provider: CultureInfo.InvariantCulture)}"],
                remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1));
        } catch (Exception exception) when (exception is IOException or TimeoutException) {
            return Abort(clock: clock, error: $"the run did not reach tick {plan.LastTick.ToString(provider: CultureInfo.InvariantCulture)}: {exception.Message}{Tail(process: process)}", notes: notes, process: process, request: request);
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
    private static MotionRunResult Abort(MotionRunRequest request, Stopwatch clock, string error, List<string> notes, WorldProcess process) {
        process.Kill();

        return Fail(clock: clock, error: error, notes: notes, request: request);
    }
    private static string Tail(WorldProcess process) {
        var lines = process.LinesFrom(from: 0);

        return ((lines.Count == 0)
            ? string.Empty
            : $"{Environment.NewLine}last World output:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: lines.TakeLast(count: 6).Select(selector: static line => ("  " + line.Text)))}"
        );
    }
}
