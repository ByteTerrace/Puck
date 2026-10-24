using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Puck.Cli;

/// <summary>
/// Resolves the real <c>Puck.World</c> executable for a verb that boots it, building it in Release only when no run
/// has built the checkout's current sources before.
/// <para>
/// Each build is keyed by <see cref="WorldArtifactKey"/> and kept in a <see cref="WorldArtifactStore"/> under the
/// user's local application data. A run whose key the store already holds builds nothing. A run that finds another
/// run building the same key waits for it and takes its build. A build never goes into the checkout: with
/// <c>--output</c>, every project in the World's closure writes into the store instead, while compilation stays
/// incremental through the checkout's <c>obj</c> directories.
/// </para>
/// <para>
/// Every build restores. NuGet's no-op check compares each closure project's restore inputs with the hash its last
/// restore recorded, so restoring an unchanged closure starts no process beyond the build's own, writes no file and
/// makes no request, while a closure that gained a project or a package since its last restore is restored before it
/// builds.
/// </para>
/// <para>
/// A run that builds keeps the build's whole output in <c>Puck.World.build.log</c> inside the caller's log directory,
/// and a failed build's refusal quotes the output's first error lines and names that file.
/// </para>
/// </summary>
public static class WorldArtifactBuild {
    /// <summary>The artifact a leg launches, inside every build.</summary>
    public const string ArtifactName = "Puck.World.dll";

    /// <summary>Resolves the World build for the checkout's current sources, building it only when the store holds
    /// none.</summary>
    /// <param name="verb">The calling verb's name, which prefixes the progress lines written to standard error;
    /// standard output carries only the calling verb's results.</param>
    /// <param name="repositoryRoot">The checkout whose sources are built.</param>
    /// <param name="logDirectory">The calling run's directory, which keeps <c>Puck.World.build.log</c> when this run
    /// builds; created only when the build's output is kept.</param>
    /// <param name="timeout">How long resolving may take, waiting for another run's build included.</param>
    /// <param name="artifact">The leased build, which the caller disposes once its last World process has exited;
    /// <see langword="null"/> on failure.</param>
    /// <param name="error">Why the artifact is unavailable, or empty on success. A failed build's reason quotes its
    /// first error lines on further lines and names its log.</param>
    /// <param name="spirv">Whether the build carries Vulkan SPIR-V beside the Direct3D 12 bytecode. A Windows build of
    /// one project ships Direct3D 12 bytecode alone, so a World that boots on Vulkan asks for it; it is a different
    /// source state, so the store keeps it apart from the Direct3D 12 build.</param>
    /// <returns><see langword="true"/> when an artifact is available.</returns>
    public static bool TryResolve(string verb, string repositoryRoot, string logDirectory, TimeSpan timeout, [NotNullWhen(returnValue: true)] out WorldArtifact? artifact, out string error, bool spirv = false) =>
        TryResolve(
            artifact: out artifact,
            builder: null,
            error: out error,
            leaseRoot: () => CliLease.RootAsync(checkout: repositoryRoot).GetAwaiter().GetResult(),
            logDirectory: logDirectory,
            repositoryRoot: repositoryRoot,
            spirv: spirv,
            store: new WorldArtifactStore(root: WorldArtifactStore.DefaultRoot),
            timeout: timeout,
            verb: verb
        );
    /// <summary>Resolves the World a verb boots: the build a caller names with <c>--world-artifact</c> (a producer-built
    /// package's entry assembly, say), used as given, or else the build of the checkout's current sources.</summary>
    /// <param name="named">The entry assembly the caller named, or <see langword="null"/> for the checkout's build.</param>
    /// <param name="verb">The calling verb's name, which prefixes the progress lines.</param>
    /// <param name="repositoryRoot">The checkout whose sources are built when nothing is named.</param>
    /// <param name="logDirectory">The calling run's directory, which keeps <c>Puck.World.build.log</c> when this run
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
    /// <param name="builder">A captured build runner, or null to launch dotnet.</param>
    /// <param name="logDirectory">The calling run's directory, which keeps <c>Puck.World.build.log</c> when this run
    /// builds.</param>
    /// <param name="timeout">How long resolving may take, waiting for another run's build included.</param>
    /// <param name="artifact">The leased build, or <see langword="null"/> on failure.</param>
    /// <param name="error">Why the artifact is unavailable, or empty on success.</param>
    /// <returns><see langword="true"/> when an artifact is available.</returns>
    /// <param name="waiting">Called synchronously on this resolver's thread once its first build-lock attempt finds
    /// another holder, before it waits.</param>
    /// <param name="leaseRoot">Gives the shared lease directory whose <c>build</c> lease a build holds, or
    /// <see langword="null"/> for none. Builds of different source states queue on it machine-wide, one at a time, so
    /// N lanes with N source states do not run N World builds at once; a run that reuses a stored build never takes
    /// it, and the lease is held only for the build and its publication.</param>
    /// <param name="spirv">Whether the build carries Vulkan SPIR-V (<c>-p:PuckShaderSpirvEnabled=true</c>), which
    /// keys it apart from a Direct3D 12 build of the same sources.</param>
    public static bool TryResolve(string verb, string repositoryRoot, WorldArtifactStore store, Func<IReadOnlyList<string>, TimeSpan, CliProcessResult>? builder, string logDirectory, TimeSpan timeout, [NotNullWhen(returnValue: true)] out WorldArtifact? artifact, out string error, Action? waiting = null, Func<string>? leaseRoot = null, bool spirv = false) {
        var clock = Stopwatch.StartNew();
        IReadOnlyList<string> properties = (spirv ? ["-p:PuckShaderSpirvEnabled=true"] : []);

        artifact = null;

        var (roots, _) = WorldArtifactClosure.Walk(repositoryRoot: repositoryRoot);

        if (!WorldArtifactKey.TryCompute(
            buildArguments: [.. CliProjectBuild.Arguments(project: WorldArtifactClosure.WorldProject), .. properties],
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
                waited: out var waited,
                waiting: waiting
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

            // Builds of other source states queue here; the key's own lock above already serializes this state.
            using var slot = (leaseRoot is null)
                ? null
                : CliLease.AcquireAsync(
                    clock: TimeProvider.System,
                    holder: $"{verb} {key}",
                    name: "build",
                    root: leaseRoot(),
                    wait: CliProcess.RemainingBudget(budget: timeout, clock: clock)
                ).GetAwaiter().GetResult();
            var staging = store.CreateStaging(key: key);

            Console.Error.WriteLine(value: $"{verb}: building Puck.World once (Release) for source state {key}.");

            var built = CliProjectBuild.TryBuild(
                artifactName: ArtifactName,
                build: out _,
                project: WorldArtifactClosure.WorldProject,
                repositoryRoot: repositoryRoot,
                logDirectory: logDirectory,
                runner: builder,
                error: out error,
                outputDirectory: staging,
                properties: properties,
                timeout: CliProcess.RemainingBudget(
                    budget: timeout,
                    clock: clock
                )
            );

            if (!built) {
                RunDirectory.TryDelete(path: staging);

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
        } catch (TimeoutException exception) {
            error = $"the Puck.World build of source state {key} waited the whole {timeout.TotalSeconds.ToString(format: "0", provider: CultureInfo.InvariantCulture)}-second budget for other runs' builds: {exception.Message}";

            return false;
        } catch (InvalidOperationException exception) {
            error = $"the shared lease directory for the Puck.World build could not be found: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            error = $"the Puck.World build store {store.Root} failed: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }
}
