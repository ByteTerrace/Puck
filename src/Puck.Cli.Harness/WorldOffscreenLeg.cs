using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Puck.Cli;

/// <summary>
/// The leg machinery a verb that boots an authored world offscreen once per backend shares (<c>puck parity</c>,
/// <c>puck counters</c>, <c>puck qualify</c>): resolve the World build for the checkout's sources, keeping a build's
/// logs beside the run's transcripts, then run one World process per backend from fresh state with the verb's script on
/// standard input, closed by <c>wire.errors</c> and <c>quit</c>, and require every scripted command answered as
/// expected.
/// </summary>
public static class WorldOffscreenLeg {
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly string UnsupportedPrefix = Puck.Launcher.LauncherHostRun.UnsupportedLinePrefix(label: "world");

    /// <summary>The graphics backends a leg runs on, in the order a verb runs them.</summary>
    public static IReadOnlyList<string> Backends { get; } = [Puck.World.WorldBackendTokens.Vulkan, Puck.World.WorldBackendTokens.DirectX];

    /// <summary>Resolves the World build for the checkout's current sources, with the Vulkan SPIR-V its Vulkan legs
    /// need; a run that built keeps the build's standard output and error beside its transcripts.</summary>
    /// <param name="verb">The calling verb, which prefixes progress and refusal lines.</param>
    /// <param name="repositoryRoot">The checkout whose sources are built.</param>
    /// <param name="runDirectory">The run's scratch directory.</param>
    /// <param name="build">The resolved build, which the caller disposes after its last leg.</param>
    /// <returns><see langword="true"/> when a build is available; otherwise the refusal is already on standard
    /// error.</returns>
    public static bool TryResolveWorld(string verb, string repositoryRoot, string runDirectory, [NotNullWhen(returnValue: true)] out WorldBuild? build) {
        var built = WorldLaunch.TryResolve(
            build: out build,
            error: out var buildError,
            logDirectory: runDirectory,
            named: null,
            repositoryRoot: repositoryRoot,
            spirv: Backends.Any(predicate: static backend => WorldLaunch.NeedsSpirv(backend: WorldLaunch.ParseBackend(token: backend), shape: WorldShape.Document)),
            verb: verb
        );

        if (!built) {
            Console.Error.WriteLine(value: $"ERROR: {buildError}");
        }

        return built;
    }
    /// <summary>Returns the World state directory a leg on <paramref name="backend"/> boots with.</summary>
    /// <param name="runDirectory">The run's scratch directory.</param>
    /// <param name="backend">The leg's backend name.</param>
    /// <returns>The directory, which the World creates if nothing has.</returns>
    public static string StateDirectoryOf(string runDirectory, string backend) => Path.Combine(
        path1: runDirectory,
        path2: $"state-{backend}"
    );
    /// <summary>Runs one leg: the World at <paramref name="artifact"/> on <paramref name="backend"/>, booting
    /// <paramref name="world"/> (whose <c>host.presentation</c> decides the shape) with its own state directory, the
    /// script followed by <c>wire.errors</c> and <c>quit</c> on standard input, and the leg's streams written to
    /// <c>&lt;backend&gt;-stdout.log</c> and <c>&lt;backend&gt;-stderr.log</c> in the run directory.</summary>
    /// <param name="verb">The calling verb, which prefixes progress lines.</param>
    /// <param name="artifact">The World build's entry assembly.</param>
    /// <param name="world">The world document to boot.</param>
    /// <param name="backend">The backend to boot on.</param>
    /// <param name="script">The console lines to run, each ending with a line feed.</param>
    /// <param name="arguments">The leg's own further arguments to the World.</param>
    /// <param name="runDirectory">The run's scratch directory.</param>
    /// <param name="exitAfterSeconds">The World's own exit backstop; a healthy leg ends at its script's
    /// <c>quit</c>.</param>
    /// <param name="budget">The whole suite's budget.</param>
    /// <param name="suiteClock">The clock the budget runs on.</param>
    /// <param name="process">The finished leg, or <see langword="null"/> when it could not run.</param>
    /// <returns><see cref="CliExit.Success"/> when the leg exited 0 with every command accepted; otherwise
    /// <see cref="CliExit.Refused"/>, with the reason already on standard error.</returns>
    public static int Run(string verb, string artifact, string world, string backend, string script, IReadOnlyList<string> arguments, string runDirectory, int exitAfterSeconds, TimeSpan budget, Stopwatch suiteClock, out CliProcessResult? process) {
        var leg = Launch(
            arguments: arguments,
            artifact: artifact,
            backend: backend,
            budget: budget,
            exitAfterSeconds: exitAfterSeconds,
            runDirectory: runDirectory,
            script: script,
            suiteClock: suiteClock,
            verb: verb,
            world: world
        );

        process = leg.Process;

        if (leg.Status == WorldOffscreenLegStatus.Completed) {
            return CliExit.Success;
        }

        Console.Error.WriteLine(value: $"ERROR: {leg.Detail}");

        return CliExit.Refused;
    }
    /// <summary>Runs one leg as <see cref="Run"/> does and classifies how it ended instead of refusing, for a verb that
    /// judges a failed leg itself.</summary>
    /// <param name="verb">The calling verb, which prefixes progress lines.</param>
    /// <param name="artifact">The World build's entry assembly.</param>
    /// <param name="world">The world document to boot.</param>
    /// <param name="backend">The backend to boot on.</param>
    /// <param name="script">The console lines to run, each ending with a line feed.</param>
    /// <param name="arguments">The leg's own further arguments to the World.</param>
    /// <param name="runDirectory">The directory the leg's state directory and transcripts go in.</param>
    /// <param name="exitAfterSeconds">The World's own exit backstop, and the leg's kill ceiling when
    /// <paramref name="budget"/> leaves more.</param>
    /// <param name="budget">The whole suite's budget.</param>
    /// <param name="suiteClock">The clock the budget runs on.</param>
    /// <param name="expectedRejections">How many scripted commands the World is expected to refuse; the terminal
    /// <c>wire.errors</c> must report exactly this many.</param>
    /// <param name="environment">The environment entries the World process gets over this one's: a value sets a
    /// variable, <see langword="null"/> removes it.</param>
    /// <returns>How the leg ended, with one line of detail for every status but
    /// <see cref="WorldOffscreenLegStatus.Completed"/>, and the finished process when it ran.</returns>
    public static WorldOffscreenLegResult Launch(string verb, string artifact, string world, string backend, string script, IReadOnlyList<string> arguments, string runDirectory, int exitAfterSeconds, TimeSpan budget, Stopwatch suiteClock, int expectedRejections = 0, IReadOnlyDictionary<string, string?>? environment = null) {
        var remaining = CliProcess.RemainingBudget(
            budget: budget,
            clock: suiteClock
        );

        if (remaining <= TimeSpan.FromSeconds(value: 1)) {
            return new WorldOffscreenLegResult(
                Detail: $"the {budget.TotalSeconds:0}-second whole-suite budget was exhausted before the {backend} leg started.",
                Process: null,
                Status: WorldOffscreenLegStatus.NotRun
            );
        }

        Console.Error.WriteLine(value: $"{verb}: running {Path.GetFileName(path: world)} offscreen on {backend}.");

        CliProcessResult process;

        try {
            // The GPU lease and the host's headroom are other runs' time, so the suite's clock stands still for them.
            using var launch = WorldLaunch.Admit(
                queue: () => Pause(clock: suiteClock),
                request: new WorldLaunchRequest(Artifact: artifact, Step: verb) {
                    Arguments = arguments,
                    Backend = WorldLaunch.ParseBackend(token: backend),
                    Environment = environment,
                    ExitAfterSeconds = exitAfterSeconds,
                    Shape = WorldShape.Document,
                    StateDirectory = StateDirectoryOf(backend: backend, runDirectory: runDirectory),
                    World = world,
                }
            );

            process = launch.Run(
                // quit ends the leg the moment the script has run instead of idling out the exit backstop.
                input: $"{script}wire.errors\nquit\n",
                timeout: remaining
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or Win32Exception or TimeoutException or ArgumentException)) {
            return new WorldOffscreenLegResult(
                Detail: $"could not start the {backend} leg: {exception.Message.ReplaceLineEndings(replacementText: " ")}",
                Process: null,
                Status: WorldOffscreenLegStatus.NotRun
            );
        }

        WriteLog(
            name: $"{backend}-stdout.log",
            runDirectory: runDirectory,
            text: process.Stdout
        );
        WriteLog(
            name: $"{backend}-stderr.log",
            runDirectory: runDirectory,
            text: process.Stderr
        );

        if (process.TimedOut) {
            return new WorldOffscreenLegResult(
                Detail: $"the {backend} leg exceeded the remaining suite budget.",
                Process: process,
                Status: WorldOffscreenLegStatus.TimedOut
            );
        }
        if (process.ExitCode != 0) {
            var unsupported = process.OutputLines.FirstOrDefault(predicate: static line => ((line.Stream == CliProcessOutputStream.Stderr) && line.Line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: UnsupportedPrefix
            )));

            return (((process.ExitCode == Puck.Launcher.LauncherHostRun.UnsupportedExitCode) && (unsupported is not null))
                ? new WorldOffscreenLegResult(
                    Detail: $"the {backend} leg cannot run here: {unsupported.Line}",
                    Process: process,
                    Status: WorldOffscreenLegStatus.Unsupported
                )
                : new WorldOffscreenLegResult(
                    Detail: $"the {backend} leg exited {process.ExitCode}; its transcripts are in the run directory.",
                    Process: process,
                    Status: WorldOffscreenLegStatus.Exited
                ));
        }

        var expected = $"[wire.errors: {expectedRejections.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} rejected]";
        var terminal = process.OutputLines.LastOrDefault(predicate: static line => line.Line.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "[wire.errors:"
        ));

        if (
            (terminal is null) ||
            (terminal.Stream != CliProcessOutputStream.Stdout) ||
            !string.Equals(
                a: terminal.Line,
                b: expected,
                comparisonType: StringComparison.Ordinal
            )
        ) {
            return new WorldOffscreenLegResult(
                Detail: $"the {backend} leg did not answer every scripted command as expected (expected exactly '{expected}' on stdout); its transcripts are in the run directory.",
                Process: process,
                Status: WorldOffscreenLegStatus.CommandsRejected
            );
        }

        return new WorldOffscreenLegResult(
            Detail: string.Empty,
            Process: process,
            Status: WorldOffscreenLegStatus.Completed
        );
    }

    // Stops a running clock until the scope ends; a clock already stopped stays stopped.
    private static IDisposable Pause(Stopwatch clock) {
        var running = clock.IsRunning;

        clock.Stop();

        return new Resume(clock: clock, running: running);
    }
    private sealed class Resume(Stopwatch clock, bool running) : IDisposable {
        public void Dispose() {
            if (running) { clock.Start(); }
        }
    }
    private static void WriteLog(string runDirectory, string name, string text) =>
        File.WriteAllText(
            contents: text,
            encoding: Utf8,
            path: Path.Combine(
                path1: runDirectory,
                path2: name
            )
        );
}
/// <summary>How one offscreen leg ended.</summary>
public enum WorldOffscreenLegStatus {
    /// <summary>The World exited 0 and answered every scripted command as expected.</summary>
    Completed,
    /// <summary>No World process ran: the suite's budget was spent, or the process could not start.</summary>
    NotRun,
    /// <summary>The World was killed at the leg's ceiling.</summary>
    TimedOut,
    /// <summary>The World announced that this machine cannot provide what the leg needs, such as a usable device for
    /// the backend, and exited with the unsupported code.</summary>
    Unsupported,
    /// <summary>The World exited with another nonzero code.</summary>
    Exited,
    /// <summary>The World exited 0, but its terminal <c>wire.errors</c> reported another count of refused commands than
    /// the leg expected.</summary>
    CommandsRejected,
}
/// <summary>One offscreen leg's end.</summary>
/// <param name="Status">How the leg ended.</param>
/// <param name="Detail">One line saying why, or empty for <see cref="WorldOffscreenLegStatus.Completed"/>.</param>
/// <param name="Process">The finished World process, or <see langword="null"/> when none ran.</param>
public sealed record WorldOffscreenLegResult(WorldOffscreenLegStatus Status, string Detail, CliProcessResult? Process);
