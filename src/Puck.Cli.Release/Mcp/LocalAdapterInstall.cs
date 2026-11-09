using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Puck.Confinement;
using Puck.Launcher;
using Puck.Launcher.Release;
using Puck.Launcher.Stub;

namespace Puck.Cli.Mcp;

// The operator's local development channel. The install root is read and written only by the code of the build being
// activated: a session's front and its stub outlive every build they serve, so neither may carry the release record's
// rules. `--prepare-install` (ActivateAsync) is the one writer; the front follows a build by running that build's own
// `--prepare-install` (Follow), never its own copy of it.
internal static class LocalAdapterInstall {
    // Written by Puck.Cli.csproj after a completed build and deleted before the next.
    private const string BuildReadyFileName = "puck-build-ready";
    private const string StubName = "Puck.Launcher.Stub";

    private static string Executable(string name) => (OperatingSystem.IsWindows() ? $"{name}.exe" : name);

    public static async Task<string> ActivateAsync(string build, string install, CancellationToken token) {
        string current, version;
        LocalBuildReleaseSource source;
        ReleaseTrustAnchor anchor;

        // Publication writes release content into the install root; the pruner holds the same lease while it deletes. Once
        // the channel manifest is written its objects are referenced, so the apply that follows takes the lease again itself.
        using (await InstallLeases.AcquireAsync(cancellationToken: token, fileName: InstallLeases.PublicationLockFileName, installRoot: install).ConfigureAwait(continueOnCapturedContext: false)) {
            InstallStub(build: build, install: install);
            // A display read: UpdateService re-checks the version under the install's transaction before it acts.
            current = (ReleasePolicy.ReadUnlocked(installRoot: install).Current ?? "0.0.0");
            var ready = Path.Combine(path1: build, path2: BuildReadyFileName);

            if (!File.Exists(path: ready) && (current != "0.0.0")) { return current; }
            var stamp = File.ReadAllText(path: ready).Trim();

            if (!ulong.TryParse(result: out var sequence, s: stamp)) { throw new IOException(message: "No completed CLI build stamp."); }
            version = $"0.0.{sequence}";

            if (!ReleaseVersion.IsStrictlyGreaterThan(left: version, right: current)) { return current; }
            source = new LocalBuildReleaseSource(buildDirectory: build, sourceDirectory: Path.Combine(path1: install, path2: InstallPruner.LocalSourceDirectoryName));
            anchor = source.Publish("puck.operator", "development", version, Path.Combine(path1: install, path2: "development-key.pk8"), sequence);

            if (!File.Exists(path: ready) || (File.ReadAllText(path: ready).Trim() != stamp)) {
                if (current != "0.0.0") { return current; }
                throw new IOException(message: "The CLI build changed during publication; wait for it to complete.");
            }
        }
        var services = new ServiceCollection();

        services.AddSelfUpdate(options: new UpdateOptions("puck.operator", "development", install, anchor, current), releaseSource: source);
        using var provider = services.BuildServiceProvider();
        // Several clients can start the adapter at once: an apply waits for another one rather than report the install busy.
        var result = await provider.GetRequiredService<UpdateService>().ApplyAsync(cancellationToken: token, now: DateTimeOffset.UtcNow, waitForOtherAppliers: true)
            .ConfigureAwait(continueOnCapturedContext: false);

        // A refused build (one the stub already rolled back keeps its consumed sequence) must not take the adapter
        // down: keep serving the installed version until a newer build publishes.
        if (!result.Applied) {
            // Another client's activation of the same build got there first.
            if ((ReleasePolicy.ReadUnlocked(installRoot: install).Current is { } installed) && !ReleaseVersion.IsStrictlyGreaterThan(left: version, right: installed)) {
                return installed;
            }
            if (current == "0.0.0") { throw new IOException(message: $"Operator development update refused: {result.Detail}"); }
            Console.Error.WriteLine(value: $"puck-mcp: kept {current}; build {version} was refused ({result.Detail}). Rebuild src/Puck.Cli to publish a newer one.");
            return current;
        }
        Console.Error.WriteLine(value: $"puck-mcp: activated {version} through UpdateService ({result.Detail}).");
        return version;
    }
    // Installs the stub this build carries, and its configuration, beside the release record it will read. A running stub
    // keeps its image: AtomicFile moves a mapped file aside rather than failing, so a session that never ends still gets the
    // stub matching every build it activates. A file whose bytes already match is left alone.
    private static void InstallStub(string build, string install) {
        var manifest = Path.Combine(path1: build, path2: $"{StubName}.deps.json");

        if (!File.Exists(path: manifest)) { throw new IOException(message: $"The CLI build at {build} carries no {StubName}; rebuild src/Puck.Cli."); }
        var files = new SortedSet<string>(comparer: StringComparer.Ordinal) { Executable(name: StubName), $"{StubName}.deps.json", $"{StubName}.runtimeconfig.json" };

        // The stub's runtime closure is what its own dependency manifest names, so a dependency added to the stub ships
        // without this list changing.
        using (var document = JsonDocument.Parse(utf8Json: File.ReadAllBytes(path: manifest))) {
            foreach (var target in document.RootElement.GetProperty(propertyName: "targets").EnumerateObject()) {
                foreach (var library in target.Value.EnumerateObject()) {
                    if (!library.Value.TryGetProperty(propertyName: "runtime", value: out var runtime)) { continue; }
                    foreach (var file in runtime.EnumerateObject()) { _ = files.Add(item: file.Name); }
                }
            }
        }
        foreach (var file in files) {
            var target = Path.Combine(path1: install, path2: file);

            AtomicFile.DeleteReplacedBackups(path: target);
            if (AtomicFile.WriteAllBytesIfChanged(bytes: File.ReadAllBytes(path: Path.Combine(path1: build, path2: file)), path: target)) {
                Console.Error.WriteLine(value: $"puck-mcp: installed {file} from {build}");
            }
        }
        _ = AtomicFile.WriteAllBytesIfChanged(path: Path.Combine(path1: install, path2: StubInstall.StubConfigFileName),
            bytes: JsonSerializer.SerializeToUtf8Bytes(value: new StubConfiguration(AppExecutableFileName: Executable(name: "Puck.Cli"))));
    }
    // The front's activation: notices a completed build by its stamp and has that build activate itself, so the release
    // record and the stub are always written by code at least as new as the worker the stub launches next. One activation
    // runs per stamp: a refused build is not offered again on every call, and an unchanged build costs one file read.
    public static Func<CancellationToken, Task<string>> Follow(string build, string install) {
        string? activated = null;
        // The front was launched from the install's current version until it first activates.
        var version = new DirectoryInfo(path: AppContext.BaseDirectory).Name;

        return async token => {
            string stamp;

            try {
                stamp = (await File.ReadAllTextAsync(cancellationToken: token, path: Path.Combine(path1: build, path2: BuildReadyFileName)).ConfigureAwait(continueOnCapturedContext: false)).Trim();
            } catch (Exception error) when ((error is FileNotFoundException or DirectoryNotFoundException)) {
                // A build in progress: keep serving the activated version until it completes.
                return version;
            }
            if (stamp == activated) { return version; }
            var answer = await CliProcess.RunAsync(
                arguments: ["mcp", "--profile", "operator", "--prepare-install", "--install-root", install, "--build-source", build],
                cancellationToken: token,
                fileName: Path.Combine(path1: build, path2: Executable(name: "Puck.Cli")),
                workingDirectory: build
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!string.IsNullOrWhiteSpace(value: answer.Stderr)) { Console.Error.Write(value: answer.Stderr); }
            var printed = CliText.Lines(text: answer.Stdout).LastOrDefault()?.Trim();

            if ((answer.ExitCode != 0) || string.IsNullOrEmpty(value: printed)) {
                throw new IOException(message: $"build {stamp} at {build} could not activate itself (its `mcp --prepare-install` exited {answer.ExitCode}): " +
                    string.Join(separator: " | ", values: CliText.Lines(text: answer.Stderr).TakeLast(count: 4)));
            }
            activated = stamp;
            version = printed;
            return version;
        };
    }
    // Proves health with the registration the CLI's entry point took before it loaded anything else. Pruning past
    // versions follows in the background: it holds the install's leases while it deletes, and a front or worker that
    // waited for it would answer its client's initialize only after every past version was gone.
    public static async Task HealthyAsync(string install, CancellationToken cancellationToken) {
        var version = new DirectoryInfo(path: AppContext.BaseDirectory).Name;
        var cleared = await HealthGateFile.ClearHealthyAsync(cacheRoot: install,
            registration: InstallRegistration.RegisterProcess(cancellationToken: cancellationToken), version: version,
            cancellationToken: cancellationToken, prune: false).ConfigureAwait(continueOnCapturedContext: false);

        if (!cleared.Cleared) { return; }
        _ = Task.Run(action: () => {
            var pruned = InstallPruner.Prune(installRoot: install, keepVersions: AddSelfUpdateDefaults.KeepVersions);

            Console.Error.WriteLine(value: ((pruned.Skipped is { } skipped)
                ? $"puck-mcp: pruning skipped: {skipped}"
                : (($"puck-mcp: pruned {pruned.DeletedVersions.Count} version(s) and {pruned.DeletedObjects} object(s); kept {string.Join(separator: ", ", values: pruned.KeptVersions)}" +
                    ((pruned.RunningVersions.Count == 0) ? "" : $" ({string.Join(separator: ", ", values: pruned.RunningVersions)} running)")) +
                    ((pruned.Refused.Count == 0) ? "" : $"; refused {string.Join(separator: "; ", values: pruned.Refused)}"))));
        }, cancellationToken: CancellationToken.None);
    }
}
