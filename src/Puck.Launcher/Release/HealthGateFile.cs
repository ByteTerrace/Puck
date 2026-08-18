using System.Text.Json;
using Microsoft.Extensions.Hosting;

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
        m_applicationLifetime.ApplicationStarted.Register(callback: () => HealthGateFile.ClearHealthy(cacheRoot: m_options.CacheRoot, version: m_options.InstalledVersion));

        return Task.CompletedTask;
    }
    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
/// <summary>The health attempt counter's clearing half — see <see cref="SelfUpdateHealthGateHostedService"/>.</summary>
internal static class HealthGateFile {
    private sealed record HealthRecord(string Version, int Attempts);

    /// <summary>Resets the recorded attempt count for <paramref name="version"/> to zero.</summary>
    /// <param name="cacheRoot">The install root (the same directory the stub reads <c>state/health.json</c> under).</param>
    /// <param name="version">The version to clear.</param>
    public static void ClearHealthy(string cacheRoot, string version) {
        var directory = Path.Combine(path1: cacheRoot, path2: "state");
        var path = Path.Combine(path1: directory, path2: "health.json");
        var tmpPath = Path.Combine(path1: directory, path2: $"{Guid.NewGuid():n}.tmp");

        Directory.CreateDirectory(path: directory);
        File.WriteAllText(path: tmpPath, contents: JsonSerializer.Serialize(value: new HealthRecord(Attempts: 0, Version: version)));
        File.Move(destFileName: path, overwrite: true, sourceFileName: tmpPath);
    }
}
