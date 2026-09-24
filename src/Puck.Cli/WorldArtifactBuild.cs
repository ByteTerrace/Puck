using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

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
/// The build restores only when the World has no <c>obj/project.assets.json</c> yet, as in a freshly created worktree.
/// </para>
/// </summary>
internal static class WorldArtifactBuild {
    /// <summary>The artifact a leg launches, inside every build.</summary>
    public const string ArtifactName = "Puck.World.dll";

    // The build's command line apart from the restore choice and the output directory, neither of which changes what
    // the sources build into. The key covers it, so a change here is never answered with a build made the old way.
    private static readonly string[] BuildArguments = ["build", WorldArtifactClosure.WorldProject, "-c", "Release", "--nologo", "-p:NuGetAudit=false"];

    private static bool TryBuild(string repositoryRoot, string outputDirectory, TimeSpan timeout, out CliProcessResult? build, out string error) {
        build = null;

        // A checkout that has never restored the World (a fresh worktree) has no assets file, and a no-restore build of
        // it fails with NETSDK1004; one that has restored skips the restore's cost and network.
        var restored = File.Exists(path: Path.Combine(
            path1: repositoryRoot,
            path2: "src",
            path3: "Puck.World",
            path4: "obj/project.assets.json"
        ));
        List<string> arguments = [.. BuildArguments, .. (restored ? (string[])["--no-restore"] : []), "--output", outputDirectory];

        try {
            build = CliProcess.RunCaptured(
                arguments: arguments,
                fileName: "dotnet",
                input: string.Empty,
                timeout: timeout,
                workingDirectory: repositoryRoot
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or Win32Exception)) {
            error = $"could not start the Puck.World build: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        if (build.TimedOut) {
            error = $"the Puck.World build exceeded its {timeout.TotalSeconds.ToString(format: "0", provider: CultureInfo.InvariantCulture)}-second budget.";

            return false;
        }
        if (build.ExitCode != 0) {
            error = $"the Puck.World build exited {build.ExitCode.ToString(provider: CultureInfo.InvariantCulture)}.";

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
    /// <param name="timeout">How long resolving may take, waiting for another run's build included.</param>
    /// <param name="artifact">The leased build, which the caller disposes once its last World process has exited;
    /// <see langword="null"/> on failure.</param>
    /// <param name="build">The captured build process when this run built, or <see langword="null"/> when it reused a
    /// build or could not start one. A caller that keeps build logs writes them from here whether or not the build
    /// succeeded.</param>
    /// <param name="error">One line saying why the artifact is unavailable, or empty on success.</param>
    /// <returns><see langword="true"/> when an artifact is available.</returns>
    public static bool TryResolve(string verb, string repositoryRoot, TimeSpan timeout, [NotNullWhen(returnValue: true)] out WorldArtifact? artifact, out CliProcessResult? build, out string error) =>
        TryResolve(
            artifact: out artifact,
            build: out build,
            builder: (string outputDirectory, TimeSpan remaining, out CliProcessResult? process, out string failure) => TryBuild(
                build: out process,
                error: out failure,
                outputDirectory: outputDirectory,
                repositoryRoot: repositoryRoot,
                timeout: remaining
            ),
            error: out error,
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
    /// <param name="timeout">How long resolving the checkout's build may take.</param>
    /// <param name="path">The entry assembly every leg launches, or <see langword="null"/> on failure.</param>
    /// <param name="lease">The leased checkout build, which the caller disposes once its last World process has
    /// exited; <see langword="null"/> for a named artifact or on failure.</param>
    /// <param name="build">The captured build process when this run built, or <see langword="null"/>.</param>
    /// <param name="error">One line saying why no artifact is available, or empty on success.</param>
    /// <returns><see langword="true"/> when an artifact is available.</returns>
    public static bool TryResolveNamed(string? named, string verb, string repositoryRoot, TimeSpan timeout, [NotNullWhen(returnValue: true)] out string? path, out WorldArtifact? lease, out CliProcessResult? build, out string error) {
        lease = null;
        build = null;

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
            build: out build,
            error: out error,
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
    /// <param name="timeout">How long resolving may take, waiting for another run's build included.</param>
    /// <param name="artifact">The leased build, or <see langword="null"/> on failure.</param>
    /// <param name="build">The captured build process when this run built, or <see langword="null"/>.</param>
    /// <param name="error">One line saying why the artifact is unavailable, or empty on success.</param>
    /// <returns><see langword="true"/> when an artifact is available.</returns>
    public static bool TryResolve(string verb, string repositoryRoot, WorldArtifactStore store, WorldArtifactBuilder builder, TimeSpan timeout, [NotNullWhen(returnValue: true)] out WorldArtifact? artifact, out CliProcessResult? build, out string error) {
        var clock = Stopwatch.StartNew();

        artifact = null;
        build = null;

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

            if (!builder(
                build: out build,
                error: out error,
                outputDirectory: staging,
                timeout: CliProcess.RemainingBudget(
                    budget: timeout,
                    clock: clock
                )
            )) {
                CliScratchDirectories.TryDelete(path: staging);

                return false;
            }
            if (!File.Exists(path: Path.Combine(
                path1: staging,
                path2: ArtifactName
            ))) {
                CliScratchDirectories.TryDelete(path: staging);
                error = $"the Puck.World build exited 0 but did not produce {ArtifactName}.";

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
