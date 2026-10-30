using Puck.Confinement;

namespace Puck.Launcher.Release;

/// <summary>
/// The install root's two persistent leases, each an exclusive lock on a file directly under the root that is never
/// removed, so every process locks the same file and a holder's exit releases it. <see cref="UpdateLockFileName"/>
/// serializes everything that reads or moves the version pointers or the versions themselves: <see cref="UpdateService"/>,
/// the stub, health clearing, process registration (<see cref="InstallRegistration"/>) and pruning
/// (<see cref="InstallPruner"/>). <see cref="PublicationLockFileName"/> serializes writing release content into the root
/// (a local publisher such as the operator adapter's development channel) with pruning, which deletes what nothing
/// references. A holder of both takes publication first, then update.
/// </summary>
public static class InstallLeases {
    /// <summary>The publication lease's file name, directly under the install root.</summary>
    public const string PublicationLockFileName = "publication.lock";
    /// <summary>The update lease's file name, directly under the install root. The stub spells the same name.</summary>
    public const string UpdateLockFileName = "update.lock";

    /// <summary>Takes a lease on an install root without waiting, creating the root when it is missing.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="fileName">The lease's file name.</param>
    /// <returns>The held lease, which the caller disposes to release, or <see langword="null"/> when another holder has
    /// it.</returns>
    /// <exception cref="IOException">The root or the lease file cannot be opened, or the lease file is a link.</exception>
    public static FileStream? TryAcquire(string installRoot, string fileName) {
        using var root = ConfinedDirectory.OpenAnchor(create: true, path: installRoot)!;

        return root.TryAcquireLock(name: fileName);
    }
    /// <summary>Waits for a lease on an install root without a deadline, creating the root when it is missing.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="fileName">The lease's file name.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>The held lease, which the caller disposes to release.</returns>
    /// <exception cref="IOException">The root or the lease file cannot be opened, or the lease file is a link.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    public static Task<FileStream> AcquireAsync(string installRoot, string fileName, CancellationToken cancellationToken) => Task.Run(function: () => {
        using var root = ConfinedDirectory.OpenAnchor(create: true, path: installRoot)!;

        return root.AcquireLock(cancellationToken: cancellationToken, name: fileName, timeout: Timeout.InfiniteTimeSpan);
    }, cancellationToken: cancellationToken);
}
