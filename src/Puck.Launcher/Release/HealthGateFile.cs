using Microsoft.Extensions.Hosting;
using Puck.Abstractions;
using Puck.Confinement;
using System.Text.Json;

namespace Puck.Launcher.Release;

/// <summary>
/// Clears the stub's health attempt counter (<c>Puck.Launcher.Stub.StubHealth</c>'s file, an independent reader: the
/// two sides share the file's shape by convention) once this process reports healthy, and prunes past versions.
/// Registered by <see cref="LauncherServiceRegistration.AddSelfUpdate"/> as a hosted service that clears on
/// <see cref="IHostApplicationLifetime.ApplicationStarted"/>, the one "hosting completed startup" signal both
/// <c>HeadlessTickHostedService</c> and <c>LauncherWindowHostedService</c> already depend on. It proves health with this
/// process's registration (<see cref="InstallRegistration.Process"/>), which the entry point took before it loaded
/// anything else and which lives as long as the process.
/// </summary>
/// <param name="applicationLifetime">The host lifetime whose <see cref="IHostApplicationLifetime.ApplicationStarted"/> marks healthy.</param>
/// <param name="options">Names the install root, the version this process reports itself as, and how many past versions to keep.</param>
internal sealed class SelfUpdateHealthGateHostedService(IHostApplicationLifetime applicationLifetime, UpdateOptions options) : BackgroundService {
    private readonly IHostApplicationLifetime m_applicationLifetime = applicationLifetime;
    private readonly UpdateOptions m_options = options;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        // A host whose entry point registered already gets that registration back.
        var registration = InstallRegistration.RegisterProcess(cancellationToken: stoppingToken);
        var started = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        using var signal = m_applicationLifetime.ApplicationStarted.Register(callback: () => started.TrySetResult());

        await started.Task.WaitAsync(cancellationToken: stoppingToken).ConfigureAwait(continueOnCapturedContext: false);
        await HealthGateFile.ClearHealthyAsync(
            cacheRoot: m_options.CacheRoot,
            keepVersions: m_options.KeepVersions,
            registration: registration,
            version: m_options.InstalledVersion,
            cancellationToken: stoppingToken
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
}

/// <summary>What <see cref="HealthGateFile.ClearHealthy"/> proved and pruned.</summary>
/// <param name="Cleared">Whether this process was the selected install and its pending health marker was cleared.</param>
/// <param name="Pruned">The prune pass that followed clearing, or <see langword="null"/> when nothing was cleared.</param>
/// <param name="Retryable">A transient lock or I/O failure prevented the decision.</param>
public sealed record HealthClearResult(bool Cleared, InstallPruneResult? Pruned, bool Retryable = false);
/// <summary>The health attempt counter's clearing half; see <see cref="SelfUpdateHealthGateHostedService"/>.</summary>
public static class HealthGateFile {
    private sealed record HealthRecord(string Version, int Attempts);

    /// <summary>Retries transient health-clear failures until selection is decided or the host stops.</summary>
    public static async Task<HealthClearResult> ClearHealthyAsync(string cacheRoot, string version, InstallRegistration registration,
        CancellationToken cancellationToken, int keepVersions = AddSelfUpdateDefaults.KeepVersions) {
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ClearHealthy(cacheRoot: cacheRoot, keepVersions: keepVersions, registration: registration, version: version);

            if (!result.Retryable) { return result; }
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds: 100), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
    /// <summary>Marks a selected install unproven before the current pointer changes. The caller holds update.lock.
    /// Zero attempts is pending startup; only deletion proves startup.</summary>
    /// <param name="cacheRoot">The install root.</param>
    /// <param name="version">The version about to become current.</param>
    public static void MarkPending(string cacheRoot, string version) {
        var path = Path.Combine(path1: cacheRoot, path2: "state", path3: "health.json");
        // Until current is published the previous version still owns its attempts. A marker for either version
        // keeps last-good protected; only the stub, after reading committed current under the lease, changes owner.
        if (!File.Exists(path: path)) {
            AtomicFile.WriteAllText(path: path, contents: JsonSerializer.Serialize(value: new HealthRecord(Attempts: 0, Version: version)));
        }
    }
    /// <summary>Deletes the health marker only for the process still selected by current, under the install lease, then
    /// runs one <see cref="InstallPruner.Prune"/> pass. The process is the selected one when its registration holds
    /// <c>versions/&lt;version&gt;</c> of this very install root (identity, not path spelling, decided both) and current
    /// names that version. A
    /// busy install leaves the marker intact; an older process cannot bless a newer selected install and never prunes.</summary>
    /// <param name="cacheRoot">The install root (the same directory the stub reads <c>state/health.json</c> under).</param>
    /// <param name="version">The version to clear.</param>
    /// <param name="registration">This process's registration.</param>
    /// <param name="keepVersions">The number of most recently installed versions kept beyond the protected ones.</param>
    /// <returns>Whether the marker was cleared, and what the prune pass did.</returns>
    public static HealthClearResult ClearHealthy(string cacheRoot, string version, InstallRegistration registration, int keepVersions = AddSelfUpdateDefaults.KeepVersions) {
        ArgumentNullException.ThrowIfNull(argument: registration);
        var comparison = (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        if (!string.Equals(a: registration.Version, b: version, comparisonType: comparison)) {
            return new HealthClearResult(Cleared: false, Pruned: null);
        }
        try {
            using var root = ConfinedDirectory.OpenAnchor(create: false, path: cacheRoot);
            using var lease = root?.TryAcquireLock(name: InstallLeases.UpdateLockFileName);
            var currentPath = Path.Combine(path1: cacheRoot, path2: InstallLayout.CurrentFileName);

            if ((root is not null) && (lease is null)) { return new HealthClearResult(Cleared: false, Pruned: null, Retryable: true); }
            if ((root is null) || (registration.Root != root.Identity) || !File.Exists(path: currentPath) || !string.Equals(a: File.ReadAllText(path: currentPath).Trim(), b: version, comparisonType: StringComparison.Ordinal)) {
                return new HealthClearResult(Cleared: false, Pruned: null);
            }
            File.Delete(path: Path.Combine(path1: cacheRoot, path2: "state", path3: "health.json"));
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)) {
            // Startup remains unproven if a concurrent update or unavailable install prevents clearing.
            return new HealthClearResult(Cleared: false, Pruned: null, Retryable: true);
        }
        // Pruning takes the publication lease before the update lease, so it runs after this one is released.
        return new HealthClearResult(Cleared: true, Pruned: InstallPruner.Prune(installRoot: cacheRoot, keepVersions: keepVersions));
    }
}
