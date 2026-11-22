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
/// run building the same key waits for it and takes its build. A build publishes the World into the store
/// (<see cref="Command"/>): the closure builds into the checkout's own outputs, exactly as an ordinary
/// <c>dotnet build</c> of <c>Puck.World</c> does, so a build of sources an earlier build or a developer already compiled
/// compiles nothing, and only the published files go into the store, where the Worlds run from. The publish is
/// ReadyToRun-precompiled for this machine's runtime, one image per assembly, and each image is made again only when its
/// assembly changed.
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

    /// <summary>How long the build of one source state may run, when its caller names no other. It measures the build
    /// alone: time spent waiting for other runs (see <see cref="CliLease.DefaultWait"/>) is never part of it.</summary>
    public static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(minutes: 30);

    /// <summary>The MSBuild switches every World build passes beside <see cref="CliProjectBuild.Arguments"/>, which
    /// the source-state key covers with them. <c>AppendRuntimeIdentifierToOutputPath=false</c> keeps the runtime-specific
    /// publish on the portable build's outputs, so it compiles nothing an ordinary build did not (the recipe CI publishes
    /// with).</summary>
    /// <param name="spirv">Whether the build carries Vulkan SPIR-V (<c>-p:PuckShaderSpirvEnabled=true</c>).</param>
    /// <returns>The switches, in the order the build passes them.</returns>
    public static string[] BuildProperties(bool spirv) =>
        [AppendRuntimeIdentifier, .. (spirv ? (string[])["-p:PuckShaderSpirvEnabled=true"] : [])];

    /// <summary>The whole World build command before its publish directory, which the source-state key covers.</summary>
    /// <param name="spirv">Whether the build carries Vulkan SPIR-V.</param>
    /// <returns>The arguments.</returns>
    public static string[] Command(bool spirv) =>
        [.. CliProjectBuild.Arguments(project: WorldArtifactClosure.WorldProject, publish: true), .. BuildProperties(spirv: spirv)];

    private const string AppendRuntimeIdentifier = "-p:AppendRuntimeIdentifierToOutputPath=false";

    /// <summary>Resolves the World build for the checkout's current sources, building it only when the store holds
    /// none.</summary>
    /// <param name="verb">The calling verb's name, which prefixes the progress lines written to standard error;
    /// standard output carries only the calling verb's results.</param>
    /// <param name="repositoryRoot">The checkout whose sources are built.</param>
    /// <param name="logDirectory">The calling run's directory, which keeps <c>Puck.World.build.log</c> when this run
    /// builds; created only when the build's output is kept.</param>
    /// <param name="timeout">How long the build itself may run. Waiting for other runs is not counted; see
    /// <paramref name="wait"/>.</param>
    /// <param name="artifact">The leased build, which the caller disposes once its last World process has exited;
    /// <see langword="null"/> on failure.</param>
    /// <param name="error">Why the artifact is unavailable, or empty on success. A failed build's reason quotes its
    /// first error lines on further lines and names its log.</param>
    /// <param name="spirv">Whether the build carries Vulkan SPIR-V beside the Direct3D 12 bytecode. A Windows build of
    /// one project ships Direct3D 12 bytecode alone, so a World that boots on Vulkan asks for it; it is a different
    /// source state, so the store keeps it apart from the Direct3D 12 build.</param>
    /// <param name="wait">How long the run may wait, in total, for other runs' builds of this source state and for the
    /// machine's build lease, or <see langword="null"/> for <see cref="CliLease.DefaultWait"/>.</param>
    /// <param name="cancellationToken">Cancels the build and its waits.</param>
    /// <returns><see langword="true"/> when an artifact is available.</returns>
    public static bool TryResolve(string verb, string repositoryRoot, string logDirectory, TimeSpan timeout, [NotNullWhen(returnValue: true)] out WorldArtifact? artifact, out string error, bool spirv = false, TimeSpan? wait = null, CancellationToken cancellationToken = default) =>
        TryResolve(
            cancellationToken: cancellationToken,
            artifact: out artifact,
            builder: null,
            error: out error,
            leaseRoot: () => CliLease.RootAsync(cancellationToken: cancellationToken, checkout: repositoryRoot).GetAwaiter().GetResult(),
            logDirectory: logDirectory,
            repositoryRoot: repositoryRoot,
            spirv: spirv,
            store: new WorldArtifactStore(root: WorldArtifactStore.DefaultRoot),
            timeout: timeout,
            verb: verb,
            wait: wait
        );
    /// <summary>Resolves the World a verb boots: the build a caller names with <c>--world-artifact</c> (a producer-built
    /// package's entry assembly, say), used as given, or else the build of the checkout's current sources.</summary>
    /// <param name="named">The entry assembly the caller named, or <see langword="null"/> for the checkout's build.</param>
    /// <param name="verb">The calling verb's name, which prefixes the progress lines.</param>
    /// <param name="repositoryRoot">The checkout whose sources are built when nothing is named.</param>
    /// <param name="logDirectory">The calling run's directory, which keeps <c>Puck.World.build.log</c> when this run
    /// builds.</param>
    /// <param name="timeout">How long the checkout's build itself may run; waiting for other runs is not counted.</param>
    /// <param name="path">The entry assembly every leg launches, or <see langword="null"/> on failure.</param>
    /// <param name="lease">The leased checkout build, which the caller disposes once its last World process has
    /// exited; also retained for a named managed-store artifact, and <see langword="null"/> for an external package
    /// or on failure.</param>
    /// <param name="error">Why no artifact is available, or empty on success.</param>
    /// <param name="spirv">Whether the checkout's build carries Vulkan SPIR-V; a named build is used as given.</param>
    /// <param name="wait">How long the run may wait for other runs' builds and the machine's build lease, or
    /// <see langword="null"/> for <see cref="CliLease.DefaultWait"/>.</param>
    /// <param name="cancellationToken">Cancels the build and its waits.</param>
    /// <returns><see langword="true"/> when an artifact is available.</returns>
    public static bool TryResolveNamed(string? named, string verb, string repositoryRoot, string logDirectory, TimeSpan timeout, [NotNullWhen(returnValue: true)] out string? path, out WorldArtifact? lease, out string error, bool spirv = false, TimeSpan? wait = null, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        lease = null;

        if (named is { }) {
            path = Path.GetFullPath(path: named);

            if (!File.Exists(path: path)) {
                error = $"--world-artifact {path} does not exist.";
                path = null;

                return false;
            }

            // Named artifacts in the managed store need the same pruning lease as a source-resolved build. An
            // external package remains caller-owned; no store manages or prunes its directory.
            var entry = new DirectoryInfo(path: Path.GetDirectoryName(path: path)!);
            var parent = entry.Parent;

            static string ResolvedDirectory(DirectoryInfo directory) => Path.TrimEndingDirectorySeparator(
                path: (directory.ResolveLinkTarget(returnFinalTarget: true) ?? directory).FullName);
            var managedRoot = new DirectoryInfo(path: WorldArtifactStore.DefaultRoot);

            if (managedRoot.Exists && (parent is not null) && Puck.Abstractions.PuckPaths.Comparer.Equals(x: ResolvedDirectory(directory: parent), y: ResolvedDirectory(directory: managedRoot))) {
                if (((entry.Attributes & FileAttributes.ReparsePoint) != 0) || ((File.GetAttributes(path: path) & FileAttributes.ReparsePoint) != 0)) {
                    error = "A named managed-store artifact must be a published entry, not a symbolic-link alias.";
                    path = null;
                    return false;
                }
                lease = new WorldArtifactStore(root: parent.FullName).TryTake(key: WorldArtifactStore.KeyOf(name: entry.Name), artifactName: Path.GetFileName(path: path),
                    reused: true, clock: Stopwatch.StartNew(), budget: timeout, cancellationToken: cancellationToken);
                if ((lease is null) || !Puck.Abstractions.PuckPaths.Comparer.Equals(x: Path.GetDirectoryName(path: lease.Path), y: entry.FullName)) {
                    lease?.Dispose();
                    lease = null;
                    error = "The named managed-store artifact is not its key's published build, so it could not be retained against pruning.";
                    path = null;
                    return false;
                }
                path = lease.Path;
            }
            error = string.Empty;

            return true;
        }
        if (!TryResolve(
            artifact: out lease,
            cancellationToken: cancellationToken,
            error: out error,
            logDirectory: logDirectory,
            repositoryRoot: repositoryRoot,
            spirv: spirv,
            timeout: timeout,
            verb: verb,
            wait: wait
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
    /// <param name="timeout">How long the build itself may run; waiting for other runs is not counted.</param>
    /// <param name="artifact">The leased build, or <see langword="null"/> on failure.</param>
    /// <param name="error">Why the artifact is unavailable, or empty on success.</param>
    /// <param name="cancellationToken">Cancels the build and its waits.</param>
    /// <returns><see langword="true"/> when an artifact is available.</returns>
    /// <param name="wait">How long the run may wait, in total, for another run's build of this source state and for the
    /// build lease, or <see langword="null"/> for <see cref="CliLease.DefaultWait"/>. The wait and the build have
    /// separate limits, so a deep queue never shortens the build. The lease prints the queue position while it
    /// waits.</param>
    /// <param name="waiting">Called synchronously on this resolver's thread once its first build-lock attempt finds
    /// another holder, before it waits.</param>
    /// <param name="leaseRoot">Gives the shared lease directory whose <c>build</c> lease a build holds, or
    /// <see langword="null"/> for none. Builds of different source states queue on it machine-wide, one at a time, so
    /// N lanes with N source states do not run N World builds at once; a run that reuses a stored build never takes
    /// it, and the lease is held only for the build and its publication.</param>
    /// <param name="spirv">Whether the build carries Vulkan SPIR-V (<c>-p:PuckShaderSpirvEnabled=true</c>), which
    /// keys it apart from a Direct3D 12 build of the same sources.</param>
    public static bool TryResolve(string verb, string repositoryRoot, WorldArtifactStore store, Func<IReadOnlyList<string>, TimeSpan, CliProcessResult>? builder, string logDirectory, TimeSpan timeout, [NotNullWhen(returnValue: true)] out WorldArtifact? artifact, out string error, Action? waiting = null, Func<string>? leaseRoot = null, bool spirv = false, TimeSpan? wait = null, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        var queue = Stopwatch.StartNew();
        var limit = (wait ?? CliLease.DefaultWait);
        IReadOnlyList<string> properties = BuildProperties(spirv: spirv);

        artifact = null;

        var (roots, _) = WorldArtifactClosure.Walk(repositoryRoot: repositoryRoot);

        var keyed = WorldArtifactKey.TryCompute(
            buildArguments: Command(spirv: spirv),
            key: out var key,
            reason: out var unkeyed,
            repositoryRoot: repositoryRoot,
            roots: roots
        );

        if (!keyed) {
            // No key can name these sources, so this run builds under a name no other run can ask for.
            key = $"unkeyed-{Guid.NewGuid():N}";
            Console.Error.WriteLine(value: $"{verb}: {unkeyed}; the Puck.World build this run makes is not reused.");
        }

        try {
            if (store.TryTake(
                artifactName: ArtifactName,
                budget: limit,
                cancellationToken: cancellationToken,
                clock: queue,
                key: key,
                reused: true
            ) is { } stored) {
                Console.Error.WriteLine(value: $"{verb}: reusing the Puck.World build of source state {key}.");
                artifact = stored;
                error = string.Empty;

                return true;
            }

            using var claim = store.AcquireBuild(
                budget: limit,
                cancellationToken: cancellationToken,
                clock: queue,
                key: key,
                waited: out var waited,
                waiting: waiting
            );

            if (waited) {
                Console.Error.WriteLine(value: $"{verb}: another run is building source state {key}; waiting for it.");
            }
            if (claim is null) {
                error = $"another run's Puck.World build of source state {key} did not finish inside the {Minutes(span: limit)}-minute wait.";

                return false;
            }

            // The run that held the lock may have published this key while this one waited.
            if (store.TryTake(
                artifactName: ArtifactName,
                budget: limit,
                cancellationToken: cancellationToken,
                clock: queue,
                key: key,
                reused: true
            ) is { } awaited) {
                Console.Error.WriteLine(value: $"{verb}: reusing the Puck.World build of source state {key}.");
                artifact = awaited;
                error = string.Empty;

                return true;
            }

            // Builds of other source states queue here; the key's own lock above already serializes this state.
            using var slot = ((leaseRoot is null)
                ? null
                : CliLease.AcquireAsync(
                    cancellationToken: cancellationToken,
                    ancestor: ProcessInspection.IsAncestor,
                    checkout: repositoryRoot,
                    clock: TimeProvider.System,
                    holder: $"{verb} {key}",
                    lane: CliLease.LaneOf(checkout: repositoryRoot),
                    name: "build",
                    root: leaseRoot(),
                    wait: TimeSpan.FromTicks(value: Math.Max(val1: 0L, val2: (limit - queue.Elapsed).Ticks))
                ).GetAwaiter().GetResult());

            if (SourcesChanged()) {
                error = "the checkout's sources changed while waiting to build Puck.World; request the build again.";
                return false;
            }
            var staging = store.CreateStaging(claim: claim);

            try {
                Console.Error.WriteLine(value: $"{verb}: building and publishing Puck.World once (Release, ReadyToRun) for source state {key}.");

                var built = CliProjectBuild.TryBuild(
                    artifactName: ArtifactName,
                    build: out _,
                    cancellationToken: cancellationToken,
                    error: out error,
                    logDirectory: logDirectory,
                    outputDirectory: staging,
                    project: WorldArtifactClosure.WorldProject,
                    properties: properties,
                    publish: true,
                    repositoryRoot: repositoryRoot,
                    runner: builder,
                    timeout: timeout
                );

                if (!built) {
                    return false;
                }
                if (SourcesChanged()) {
                    error = "the checkout's sources changed while building Puck.World; the build was discarded.";
                    return false;
                }

                artifact = store.Publish(
                    cancellationToken: cancellationToken,
                    artifactName: ArtifactName,
                    budget: limit,
                    claim: claim,
                    clock: Stopwatch.StartNew(),
                    staging: staging
                );

                if (artifact is null) {
                    error = $"the Puck.World build store {store.Root} stayed locked past the {Minutes(span: limit)}-minute wait.";

                    return false;
                }

                error = string.Empty;

                return true;
            } finally {
                store.Discard(claim: claim, staging: staging);
            }
        } catch (TimeoutException exception) {
            error = $"the Puck.World build of source state {key} waited the whole {Minutes(span: limit)}-minute limit for other runs' builds: {exception.Message}";

            return false;
        } catch (LeaseProtocolException exception) {
            error = $"the Puck.World build lease was refused: {exception.Message}";

            return false;
        } catch (InvalidOperationException exception) {
            error = $"the shared lease directory for the Puck.World build could not be found: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            error = $"the Puck.World build store {store.Root} failed: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        static string Minutes(TimeSpan span) => span.TotalMinutes.ToString(format: "0", provider: CultureInfo.InvariantCulture);

        bool SourcesChanged() {
            if (!keyed) { return false; }
            var (currentRoots, _) = WorldArtifactClosure.Walk(repositoryRoot: repositoryRoot);
            return (!WorldArtifactKey.TryCompute(
                buildArguments: Command(spirv: spirv),
                key: out var currentKey, reason: out _, repositoryRoot: repositoryRoot, roots: currentRoots
            ) || (currentKey != key));
        }
    }
}
