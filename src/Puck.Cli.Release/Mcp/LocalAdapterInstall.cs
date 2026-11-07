using Microsoft.Extensions.DependencyInjection;
using Puck.Confinement;
using Puck.Launcher;
using Puck.Launcher.Release;

namespace Puck.Cli.Mcp;

internal static class LocalAdapterInstall {
    public static async Task<string> ActivateAsync(string build, string install, CancellationToken token) {
        // Publication writes release content into the install root; the pruner holds the same lease while it deletes.
        using (await InstallLeases.AcquireAsync(cancellationToken: token, fileName: InstallLeases.PublicationLockFileName, installRoot: install).ConfigureAwait(continueOnCapturedContext: false)) {
            var pointer = Path.Combine(path1: install, path2: "current");
            var current = (File.Exists(path: pointer) ? File.ReadAllText(path: pointer).Trim() : "0.0.0");
            var ready = Path.Combine(path1: build, path2: "puck-build-ready");

            if (!File.Exists(path: ready) && (current != "0.0.0")) { return current; }
            var stamp = File.ReadAllText(path: ready).Trim();

            if (!ulong.TryParse(result: out var sequence, s: stamp)) { throw new IOException(message: "No completed CLI build stamp."); }
            var version = $"0.0.{sequence}";

            if (!ReleaseVersion.IsStrictlyGreaterThan(left: version, right: current)) { return current; }
            var source = new LocalBuildReleaseSource(buildDirectory: build, sourceDirectory: Path.Combine(path1: install, path2: InstallPruner.LocalSourceDirectoryName));
            var anchor = source.Publish("puck.operator", "development", version, Path.Combine(path1: install, path2: "development-key.pk8"), sequence);

            if (!File.Exists(path: ready) || (File.ReadAllText(path: ready).Trim() != stamp)) {
                if (current != "0.0.0") { return current; }
                throw new IOException(message: "The CLI build changed during publication; wait for it to complete.");
            }
            var services = new ServiceCollection();

            services.AddSelfUpdate(options: new UpdateOptions("puck.operator", "development", install, anchor, current), releaseSource: source);
            using var provider = services.BuildServiceProvider();
            var result = await provider.GetRequiredService<UpdateService>().ApplyAsync(DateTimeOffset.UtcNow, token).ConfigureAwait(continueOnCapturedContext: false);

            // A refused build (one the stub already rolled back keeps its consumed sequence) must not take the adapter
            // down: keep serving the installed version until a newer build publishes.
            if (!result.Applied) {
                if (current == "0.0.0") { throw new IOException(message: $"Operator development update refused: {result.Detail}"); }
                Console.Error.WriteLine(value: $"puck-mcp: kept {current}; build {version} was refused ({result.Detail}). Rebuild src/Puck.Cli to publish a newer one.");
                return current;
            }
            Console.Error.WriteLine(value: $"puck-mcp: activated {version} through UpdateService ({result.Detail}).");
            return version;
        }
    }
    // Proves health with the registration the CLI's entry point took before it loaded anything else.
    public static async Task HealthyAsync(string install, CancellationToken cancellationToken) {
        var version = new DirectoryInfo(path: AppContext.BaseDirectory).Name;
        var cleared = await HealthGateFile.ClearHealthyAsync(cacheRoot: install,
            registration: InstallRegistration.RegisterProcess(cancellationToken: cancellationToken), version: version,
            cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (cleared.Pruned is { } pruned) {
            Console.Error.WriteLine(value: ((pruned.Skipped is { } skipped)
                ? $"puck-mcp: pruning skipped: {skipped}"
                : (($"puck-mcp: pruned {pruned.DeletedVersions.Count} version(s) and {pruned.DeletedObjects} object(s); kept {string.Join(separator: ", ", values: pruned.KeptVersions)}" +
                    ((pruned.RunningVersions.Count == 0) ? "" : $" ({string.Join(separator: ", ", values: pruned.RunningVersions)} running)")) +
                    ((pruned.Refused.Count == 0) ? "" : $"; refused {string.Join(separator: "; ", values: pruned.Refused)}"))));
        }
    }
}
