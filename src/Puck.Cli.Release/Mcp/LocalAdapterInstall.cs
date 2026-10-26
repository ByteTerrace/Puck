using Microsoft.Extensions.DependencyInjection;
using Puck.Launcher;
using Puck.Launcher.Release;

namespace Puck.Cli.Mcp;

internal static class LocalAdapterInstall {
    public static async Task<string> ActivateAsync(string build, string install, CancellationToken token) {
        Directory.CreateDirectory(path: install);
        FileStream lease;

        while (true) {
            token.ThrowIfCancellationRequested();
            try { lease = new FileStream(Path.Combine(path1: install, path2: "development.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; } catch (IOException) { await Task.Delay(cancellationToken: token, millisecondsDelay: 200).ConfigureAwait(continueOnCapturedContext: false); }
        }
        using (lease) {
            var pointer = Path.Combine(path1: install, path2: "current");
            var current = (File.Exists(path: pointer) ? File.ReadAllText(path: pointer).Trim() : "0.0.0");
            var ready = Path.Combine(path1: build, path2: "puck-build-ready");

            if (!File.Exists(path: ready) && (current != "0.0.0")) { return current; }
            var stamp = File.ReadAllText(path: ready).Trim();

            if (!ulong.TryParse(result: out var sequence, s: stamp)) { throw new IOException(message: "No completed CLI build stamp."); }
            var version = $"0.0.{sequence}";

            if (!ReleaseVersion.IsStrictlyGreaterThan(left: version, right: current)) { return current; }
            var source = new LocalBuildReleaseSource(buildDirectory: build, sourceDirectory: Path.Combine(path1: install, path2: "development-source"));
            var anchor = source.Publish("puck.operator", "development", version, Path.Combine(path1: install, path2: "development-key.pk8"), sequence);

            if (!File.Exists(path: ready) || (File.ReadAllText(path: ready).Trim() != stamp)) {
                if (current != "0.0.0") { return current; }
                throw new IOException(message: "The CLI build changed during publication; wait for it to complete.");
            }
            var services = new ServiceCollection();

            services.AddSelfUpdate(options: new UpdateOptions("puck.operator", "development", install, anchor, current), releaseSource: source);
            using var provider = services.BuildServiceProvider();
            var result = await provider.GetRequiredService<UpdateService>().ApplyAsync(DateTimeOffset.UtcNow, token).ConfigureAwait(continueOnCapturedContext: false);

            if (!result.Applied) { throw new IOException(message: $"Operator development update refused: {result.Detail}"); }
            Console.Error.WriteLine(value: $"puck-mcp: activated {version} through UpdateService ({result.Detail}).");
            return version;
        }
    }
    public static void Healthy(string install) {
        var version = new DirectoryInfo(path: AppContext.BaseDirectory).Name;

        HealthGateFile.ClearHealthy(install, version, AppContext.BaseDirectory);
    }
}
