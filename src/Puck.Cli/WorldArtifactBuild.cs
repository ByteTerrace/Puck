using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Puck.Cli;

/// <summary>Builds <c>src/Puck.World</c> into <paramref name="outputDirectory"/>.</summary>
/// <param name="outputDirectory">The empty directory the build writes into.</param>
/// <param name="timeout">How long the build may run.</param>
/// <param name="build">The captured build process, or <see langword="null"/> when it could not start.</param>
/// <param name="error">One line saying why the build failed, or empty on success.</param>
/// <returns><see langword="true"/> when the build exited 0 inside its budget.</returns>
internal delegate bool WorldArtifactBuilder(string outputDirectory, TimeSpan timeout, out CliProcessResult? build, out string error);

/// <summary>
/// Resolves the real <c>Puck.World</c> executable for a verb that boots it, building it in Release only when no run
/// has built the checkout's current sources before.
/// <para>
/// Each build is keyed by <see cref="WorldArtifactKey"/> and kept in a <see cref="WorldArtifactStore"/> under the
/// user's local application data. A run whose key the store already holds builds nothing. A run that finds another
/// run building the same key waits for it and takes its build. A build never goes into the checkout:
/// <c>Puck.World</c> carries a build-time reference to <c>Puck.Cli</c> (the CLI compiles the shipped <c>.puck</c>
/// worlds), so building the World in place writes into <c>src/Puck.Cli/bin/Release/net10.0</c>, which a CLI launched
/// from a branch's own build holds open, and copying a changed dependency over a loaded one fails with MSB3027. With
/// <c>--output</c>, every project in the World's closure, the CLI included, writes into the store instead, while
/// compilation stays incremental through the checkout's <c>obj</c> directories.
/// </para>
/// <para>
/// Every build restores. NuGet's no-op check compares each closure project's restore inputs with the hash its last
/// restore recorded, so restoring an unchanged closure starts no process beyond the build's own, writes no file and
/// makes no request, while a closure that gained a project or a package since its last restore is restored before it
/// builds.
/// </para>
/// <para>
/// A run that builds keeps the build's whole output in <see cref="BuildLogName"/> inside the caller's log directory,
/// and a failed build's refusal quotes the output's first error lines and names that file.
/// </para>
/// </summary>
internal static partial class WorldArtifactBuild {
    /// <summary>The artifact a leg launches, inside every build.</summary>
    public const string ArtifactName = "Puck.World.dll";
    /// <summary>The file a run that builds keeps the build's standard output and error in, inside its log
    /// directory.</summary>
    public const string BuildLogName = "world-build.log";

    // How many lines a failed build's refusal quotes; the log keeps the rest.
    private const int QuotedLines = 5;

    // The build's command line apart from the output directory, which does not change what the sources build into. The
    // key covers it, so a change here is never answered with a build made the old way.
    private static readonly string[] BuildArguments = BuildCommand(project: WorldArtifactClosure.WorldProject);

    // An error at the start or after an origin; the origin cannot contain an earlier diagnostic severity, so a
    // warning whose message mentions `error:` never becomes an error line.
    [GeneratedRegex(pattern: @"^(?:(?:(?!\b(?:warning|error)(?: [A-Za-z]+[0-9]+)?\s*:).)+:\s*)?error(?: [A-Za-z]+[0-9]+)?\s*:")]
    private static partial Regex ErrorLine();
    private static string[] BuildCommand(string project) => ["build", "--disable-build-servers", project, "-c", "Release", "--nologo", "-p:NuGetAudit=false"];
    // A failed build's refusal: why it failed, its first error lines (or its last lines when none is an error), and
    // where its whole output is kept.
    private static string Explain(string why, CliProcessResult build, string? log, string logFailure) {
        var lines = OutputLines(build: build).ToArray();
        var quoted = lines
            .Where(predicate: static line => ErrorLine().IsMatch(input: line))
            .Distinct(comparer: StringComparer.Ordinal)
            .Take(count: QuotedLines)
            .ToArray();
        var text = new StringBuilder(value: why);

        if (quoted.Length != 0) {
            _ = text.Append(value: " First errors:");
        } else {
            quoted = [.. lines.TakeLast(count: QuotedLines)];

            if (quoted.Length != 0) {
                _ = text.Append(value: " It reported no error line; its last lines:");
            }
        }

        foreach (var line in quoted) {
            _ = text.AppendLine().Append(value: "  ").Append(value: line);
        }

        _ = text.AppendLine().Append(value: ((log is null)
            ? $"Its output could not be kept: {logFailure}"
            : $"Its whole output is in {CliPaths.ToDisplay(fullPath: log)}."
        ));

        return text.ToString();
    }
    // Keeps the build's whole output in the log directory: the log's path, or null with the reason it could not be kept.
    private static string? KeepLog(CliProcessResult build, string logDirectory, out string reason) {
        var path = Path.GetFullPath(path: Path.Combine(
            path1: logDirectory,
            path2: BuildLogName
        ));
        var text = new StringBuilder(value: build.Stdout);

        if (build.Stderr.Length != 0) {
            _ = text.AppendLine().AppendLine(value: "--- stderr ---").Append(value: build.Stderr);
        }

        try {
            _ = Directory.CreateDirectory(path: logDirectory);
            File.WriteAllText(
                contents: text.ToString(),
                path: path
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return null;
        }

        reason = string.Empty;

        return path;
    }
    // The build's output lines, standard output first, each without its MSBuild node prefix (`3>`), blank lines dropped.
    private static IEnumerable<string> OutputLines(CliProcessResult build) =>
        $"{build.Stdout}\n{build.Stderr}"
            .ReplaceLineEndings(replacementText: "\n")
            .Split(separator: '\n')
            .Select(selector: static line => line.Trim())
            .Select(selector: static line => ((line.TrimStart(trimChars: ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9']) is ['>', .. var rest])
                ? rest.Trim()
                : line
            ))
            .Where(predicate: static line => (line.Length != 0));
    private static bool TryBuild(string repositoryRoot, string outputDirectory, TimeSpan timeout, out CliProcessResult? build, out string error) =>
        TryBuildProject(
            build: out build,
            error: out error,
            outputDirectory: outputDirectory,
            project: WorldArtifactClosure.WorldProject,
            repositoryRoot: repositoryRoot,
            timeout: timeout
        );

    /// <summary>Builds <paramref name="project"/> the way the World is built: in Release, into
    /// <paramref name="outputDirectory"/>, restoring its closure first.</summary>
    /// <param name="repositoryRoot">The directory the build runs from.</param>
    /// <param name="project">The project to build, relative to <paramref name="repositoryRoot"/> or absolute.</param>
    /// <param name="outputDirectory">The directory every project in the closure writes into.</param>
    /// <param name="timeout">How long the build may run.</param>
    /// <param name="build">The captured build process, or <see langword="null"/> when it could not start.</param>
    /// <param name="error">One line saying why the build failed, or empty on success.</param>
    /// <returns><see langword="true"/> when the build exited 0 inside its budget.</returns>
    public static bool TryBuildProject(string repositoryRoot, string project, string outputDirectory, TimeSpan timeout, out CliProcessResult? build, out string error) {
        build = null;

        var name = Path.GetFileNameWithoutExtension(path: project);
        List<string> arguments = [.. BuildCommand(project: project), "--output", outputDirectory];

        try {
            build = CliProcess.RunCaptured(
                arguments: arguments,
                fileName: "dotnet",
                input: string.Empty,
                timeout: timeout,
                workingDirectory: repositoryRoot
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or Win32Exception)) {
            error = $"could not start the {name} build: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        if (build.TimedOut) {
            error = $"the {name} build exceeded its {timeout.TotalSeconds.ToString(format: "0", provider: CultureInfo.InvariantCulture)}-second budget.";

            return false;
        }
        if (build.ExitCode != 0) {
            error = $"the {name} build exited {build.ExitCode.ToString(provider: CultureInfo.InvariantCulture)}.";

            return false;
        }

        error = string.Empty;

        return true;
    }
    /// <summary>Resolves the World build for the checkout's current sources, building it only when the store holds
    /// none.</summary>
    /// <param name="verb">The calling verb's name, which prefixes the progress lines written to standard error;
    /// standard output carries only the calling verb's results.</param>
    /// <param name="repositoryRoot">The checkout whose sources are built.</param>
    /// <param name="logDirectory">The calling run's directory, which keeps <see cref="BuildLogName"/> when this run
    /// builds; created when it does not exist.</param>
    /// <param name="timeout">How long resolving may take, waiting for another run's build included.</param>
    /// <param name="artifact">The leased build, which the caller disposes once its last World process has exited;
    /// <see langword="null"/> on failure.</param>
    /// <param name="error">Why the artifact is unavailable, or empty on success. A failed build's reason quotes its
    /// first error lines on further lines and names its log.</param>
    /// <returns><see langword="true"/> when an artifact is available.</returns>
    public static bool TryResolve(string verb, string repositoryRoot, string logDirectory, TimeSpan timeout, [NotNullWhen(returnValue: true)] out WorldArtifact? artifact, out string error) =>
        TryResolve(
            artifact: out artifact,
            builder: (string outputDirectory, TimeSpan remaining, out CliProcessResult? process, out string failure) => TryBuild(
                build: out process,
                error: out failure,
                outputDirectory: outputDirectory,
                repositoryRoot: repositoryRoot,
                timeout: remaining
            ),
            error: out error,
            logDirectory: logDirectory,
            repositoryRoot: repositoryRoot,
            store: new WorldArtifactStore(root: WorldArtifactStore.DefaultRoot),
            timeout: timeout,
            verb: verb
        );
    /// <summary>Resolves the World a verb boots: the build a caller names with <c>--world-artifact</c> (a producer-built
    /// package's entry assembly, say), used as given, or else the build of the checkout's current sources.</summary>
    /// <param name="named">The entry assembly the caller named, or <see langword="null"/> for the checkout's build.</param>
    /// <param name="verb">The calling verb's name, which prefixes the progress lines.</param>
    /// <param name="repositoryRoot">The checkout whose sources are built when nothing is named.</param>
    /// <param name="logDirectory">The calling run's directory, which keeps <see cref="BuildLogName"/> when this run
    /// builds.</param>
    /// <param name="timeout">How long resolving the checkout's build may take.</param>
    /// <param name="path">The entry assembly every leg launches, or <see langword="null"/> on failure.</param>
    /// <param name="lease">The leased checkout build, which the caller disposes once its last World process has
    /// exited; <see langword="null"/> for a named artifact or on failure.</param>
    /// <param name="error">Why no artifact is available, or empty on success.</param>
    /// <returns><see langword="true"/> when an artifact is available.</returns>
    public static bool TryResolveNamed(string? named, string verb, string repositoryRoot, string logDirectory, TimeSpan timeout, [NotNullWhen(returnValue: true)] out string? path, out WorldArtifact? lease, out string error) {
        lease = null;

        if (named is { }) {
            path = Path.GetFullPath(path: named);

            if (!File.Exists(path: path)) {
                error = $"--world-artifact {path} does not exist.";
                path = null;

                return false;
            }

            error = string.Empty;

            return true;
        }
        if (!TryResolve(
            artifact: out lease,
            error: out error,
            logDirectory: logDirectory,
            repositoryRoot: repositoryRoot,
            timeout: timeout,
            verb: verb
        )) {
            path = null;

            return false;
        }

        path = lease.Path;

        return true;
    }
    /// <summary>Resolves the World build through an explicit store and builder.</summary>
    /// <param name="verb">The calling verb's name, which prefixes the progress lines.</param>
    /// <param name="repositoryRoot">The checkout whose sources are keyed.</param>
    /// <param name="store">The store builds are kept in.</param>
    /// <param name="builder">What builds a missing key into an empty directory.</param>
    /// <param name="logDirectory">The calling run's directory, which keeps <see cref="BuildLogName"/> when this run
    /// builds.</param>
    /// <param name="timeout">How long resolving may take, waiting for another run's build included.</param>
    /// <param name="artifact">The leased build, or <see langword="null"/> on failure.</param>
    /// <param name="error">Why the artifact is unavailable, or empty on success.</param>
    /// <returns><see langword="true"/> when an artifact is available.</returns>
    public static bool TryResolve(string verb, string repositoryRoot, WorldArtifactStore store, WorldArtifactBuilder builder, string logDirectory, TimeSpan timeout, [NotNullWhen(returnValue: true)] out WorldArtifact? artifact, out string error) {
        var clock = Stopwatch.StartNew();

        artifact = null;

        var (roots, _) = WorldArtifactClosure.Walk(repositoryRoot: repositoryRoot);

        if (!WorldArtifactKey.TryCompute(
            buildArguments: BuildArguments,
            key: out var key,
            reason: out var unkeyed,
            repositoryRoot: repositoryRoot,
            roots: roots
        )) {
            // No key can name these sources, so this run builds under a name no other run can ask for.
            key = $"unkeyed-{Guid.NewGuid():N}";
            Console.Error.WriteLine(value: $"{verb}: {unkeyed}; the Puck.World build this run makes is not reused.");
        }

        try {
            if (store.TryTake(
                artifactName: ArtifactName,
                budget: timeout,
                clock: clock,
                key: key,
                reused: true
            ) is { } stored) {
                Console.Error.WriteLine(value: $"{verb}: reusing the Puck.World build of source state {key}.");
                artifact = stored;
                error = string.Empty;

                return true;
            }

            using var buildLock = store.AcquireBuild(
                budget: timeout,
                clock: clock,
                key: key,
                waited: out var waited
            );

            if (waited) {
                Console.Error.WriteLine(value: $"{verb}: another run is building source state {key}; waiting for it.");
            }
            if (buildLock is null) {
                error = $"another run's Puck.World build of source state {key} did not finish inside the {timeout.TotalSeconds.ToString(format: "0", provider: CultureInfo.InvariantCulture)}-second budget.";

                return false;
            }

            // The run that held the lock may have published this key while this one waited.
            if (store.TryTake(
                artifactName: ArtifactName,
                budget: timeout,
                clock: clock,
                key: key,
                reused: true
            ) is { } awaited) {
                Console.Error.WriteLine(value: $"{verb}: reusing the Puck.World build of source state {key}.");
                artifact = awaited;
                error = string.Empty;

                return true;
            }

            var staging = store.CreateStaging(key: key);

            Console.Error.WriteLine(value: $"{verb}: building Puck.World once (Release) for source state {key}.");

            var built = builder(
                build: out var build,
                error: out error,
                outputDirectory: staging,
                timeout: CliProcess.RemainingBudget(
                    budget: timeout,
                    clock: clock
                )
            );
            string? log = null;
            var logFailure = string.Empty;

            if (build is { }) {
                log = KeepLog(
                    build: build,
                    logDirectory: logDirectory,
                    reason: out logFailure
                );
            }
            if (built && !File.Exists(path: Path.Combine(
                path1: staging,
                path2: ArtifactName
            ))) {
                built = false;
                error = $"the Puck.World build exited 0 but did not produce {ArtifactName}.";
            }
            if (!built) {
                CliScratchDirectories.TryDelete(path: staging);

                if (build is { }) {
                    error = Explain(
                        build: build,
                        log: log,
                        logFailure: logFailure,
                        why: error
                    );
                }

                return false;
            }

            artifact = store.Publish(
                artifactName: ArtifactName,
                budget: timeout,
                clock: clock,
                key: key,
                staging: staging
            );

            if (artifact is null) {
                error = $"the Puck.World build store {store.Root} stayed locked past the {timeout.TotalSeconds.ToString(format: "0", provider: CultureInfo.InvariantCulture)}-second budget.";

                return false;
            }

            error = string.Empty;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            error = $"the Puck.World build store {store.Root} failed: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }
}
