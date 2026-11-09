using Microsoft.Extensions.Hosting;
using Puck.Abstractions;
using Puck.Confinement;

namespace Puck.Launcher.Release;

/// <summary>
/// Records this process's startup health in the install's release record (<see cref="InstallTransaction.RecordHealthy"/>)
/// once it reports healthy, and prunes past versions. Registered by <see cref="LauncherServiceRegistration.AddSelfUpdate"/>
/// as a hosted service that records on <see cref="IHostApplicationLifetime.ApplicationStarted"/>, the one "hosting completed
/// startup" signal both <c>HeadlessTickHostedService</c> and <c>LauncherWindowHostedService</c> already depend on. It proves
/// health with this process's registration (<see cref="InstallRegistration.Process"/>), which the entry point took before
/// it loaded anything else and which lives as long as the process.
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
/// <param name="Cleared">Whether this process was the selected install and its health was recorded.</param>
/// <param name="Pruned">The prune pass that followed, or <see langword="null"/> when nothing was recorded.</param>
/// <param name="Retryable">Lock contention prevented the decision; permanent I/O, permissions and platform errors do not retry.</param>
public sealed record HealthClearResult(bool Cleared, InstallPruneResult? Pruned, bool Retryable = false);
/// <summary>Startup health's recording half; see <see cref="SelfUpdateHealthGateHostedService"/>.</summary>
public static class HealthGateFile {
    /// <summary>Retries transient health-recording failures until selection is decided or the host stops.</summary>
    /// <param name="cacheRoot">The install root.</param>
    /// <param name="version">The version to record healthy.</param>
    /// <param name="registration">This process's registration.</param>
    /// <param name="cancellationToken">Ends the retries.</param>
    /// <param name="keepVersions">The number of most recently installed versions kept beyond the protected ones.</param>
    /// <returns>Whether health was recorded, and what the prune pass did.</returns>
    public static async Task<HealthClearResult> ClearHealthyAsync(string cacheRoot, string version, InstallRegistration registration,
        CancellationToken cancellationToken, int keepVersions = AddSelfUpdateDefaults.KeepVersions) {
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ClearHealthy(cacheRoot: cacheRoot, keepVersions: keepVersions, registration: registration, version: version);

            if (!result.Retryable) { return result; }
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds: 100), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
    /// <summary>Records startup health for the process still selected by the release record, under the install's
    /// transaction, then runs one <see cref="InstallPruner.Prune"/> pass. The process is the selected one when its
    /// registration holds <c>versions/&lt;version&gt;</c> of this very install root (identity, not path spelling, decided
    /// both) and the record's current version names it; the proof is that install's exact identity
    /// (<see cref="InstallTransaction.RecordHealthy"/>). A busy install retries; an older process cannot bless a newer
    /// selected install and never prunes.</summary>
    /// <param name="cacheRoot">The install root.</param>
    /// <param name="version">The version to record healthy.</param>
    /// <param name="registration">This process's registration.</param>
    /// <param name="keepVersions">The number of most recently installed versions kept beyond the protected ones.</param>
    /// <returns>Whether health was recorded, and what the prune pass did.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registration"/> is <see langword="null"/>.</exception>
    public static HealthClearResult ClearHealthy(string cacheRoot, string version, InstallRegistration registration, int keepVersions = AddSelfUpdateDefaults.KeepVersions) {
        ArgumentNullException.ThrowIfNull(argument: registration);
        var comparison = (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        if (!string.Equals(a: registration.Version, b: version, comparisonType: comparison)) {
            return new HealthClearResult(Cleared: false, Pruned: null);
        }
        try {
            using var root = ConfinedDirectory.OpenAnchor(create: false, path: cacheRoot);

            if (root is null) { return new HealthClearResult(Cleared: false, Pruned: null); }
            using var transaction = InstallTransaction.TryBegin(root: root);

            if (transaction is null) { return new HealthClearResult(Cleared: false, Pruned: null, Retryable: true); }
            if ((registration.Root != root.Identity) || !transaction.RecordHealthy(version: registration.Version!)) {
                return new HealthClearResult(Cleared: false, Pruned: null);
            }
        } catch (IOException error) when (FileLock.IsContention(exception: error)) {
            return new HealthClearResult(Cleared: false, Pruned: null, Retryable: true);
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or InvalidDataException or PlatformNotSupportedException)) {
            // Permissions, unsupported hosts, an invalid record and permanent I/O failures cannot be repaired by polling.
            return new HealthClearResult(Cleared: false, Pruned: null);
        }
        // Pruning takes the publication lease before the update lease, so it runs after this one is released.
        return new HealthClearResult(Cleared: true, Pruned: InstallPruner.Prune(installRoot: cacheRoot, keepVersions: keepVersions));
    }
}
