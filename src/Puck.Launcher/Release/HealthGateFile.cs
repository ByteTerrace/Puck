using Microsoft.Extensions.Hosting;
using Puck.Assets;
using System.Text.Json;

namespace Puck.Launcher.Release;

/// <summary>
/// Clears the stub's health attempt counter (<c>Puck.Launcher.Stub.StubHealth</c>'s file, an independent reader —
/// the two sides share the file's shape by convention, not by reference, since the stub takes no project reference
/// at all) once this process reports healthy. Registered by <see cref="LauncherServiceRegistration.AddSelfUpdate"/>
/// as a hosted service that clears on <see cref="IHostApplicationLifetime.ApplicationStarted"/> — the one
/// "hosting completed startup" signal both <c>HeadlessTickHostedService</c> and <c>LauncherWindowHostedService</c>
/// already depend on, reused here rather than inventing a second liveness signal.
/// </summary>
/// <param name="applicationLifetime">The host lifetime whose <see cref="IHostApplicationLifetime.ApplicationStarted"/> marks healthy.</param>
/// <param name="options">Names the install root and the version this process reports itself as.</param>
internal sealed class SelfUpdateHealthGateHostedService(IHostApplicationLifetime applicationLifetime, UpdateOptions options) : IHostedService {
    private readonly IHostApplicationLifetime m_applicationLifetime = applicationLifetime;
    private readonly UpdateOptions m_options = options;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) {
        m_applicationLifetime.ApplicationStarted.Register(callback: () => HealthGateFile.ClearHealthy(
            cacheRoot: m_options.CacheRoot,
            version: m_options.InstalledVersion,
            runningDirectory: AppContext.BaseDirectory
        ));

        return Task.CompletedTask;
    }
    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The health attempt counter's clearing half — see <see cref="SelfUpdateHealthGateHostedService"/>.</summary>
public static class HealthGateFile {
    private sealed record HealthRecord(string Version, int Attempts);

    /// <summary>Marks a selected install unproven before the current pointer changes. The caller holds update.lock.
    /// Zero attempts is pending startup; only deletion proves startup.</summary>
    /// <param name="cacheRoot">The install root.</param>
    /// <param name="version">The version about to become current.</param>
    public static void MarkPending(string cacheRoot, string version) => AtomicFile.WriteAllText(
        path: Path.Combine(path1: cacheRoot, path2: "state", path3: "health.json"),
        contents: JsonSerializer.Serialize(value: new HealthRecord(Attempts: 0, Version: version)));
    /// <summary>Deletes the health marker only for the process still selected by current, under the install lease.
    /// A busy install leaves the marker intact; an older process cannot bless a newer selected install.</summary>
    /// <param name="cacheRoot">The install root (the same directory the stub reads <c>state/health.json</c> under).</param>
    /// <param name="version">The version to clear.</param>
    /// <param name="runningDirectory">The actual executable directory, independent of the mutable current pointer.</param>
    public static void ClearHealthy(string cacheRoot, string version, string runningDirectory) {
        try {
            Directory.CreateDirectory(path: cacheRoot);
            using var lease = new FileStream(path: Path.Combine(path1: cacheRoot, path2: "update.lock"), mode: FileMode.OpenOrCreate,
                access: FileAccess.ReadWrite, share: FileShare.None);
            var currentPath = Path.Combine(path1: cacheRoot, path2: "current");

            if (!File.Exists(path: currentPath) || (File.ReadAllText(path: currentPath).Trim() != version)) {
                return;
            }
            var expected = Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: Path.Combine(path1: cacheRoot, path2: "versions", path3: version)));
            var actual = Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: runningDirectory));

            if (!string.Equals(a: expected, b: actual,
                comparisonType: (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) {
                return;
            }
            File.Delete(path: Path.Combine(path1: cacheRoot, path2: "state", path3: "health.json"));
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
            // Startup remains unproven if a concurrent update or unavailable install prevents clearing.
        }
    }
}
