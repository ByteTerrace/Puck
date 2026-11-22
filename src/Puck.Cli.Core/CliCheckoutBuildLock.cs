using System.Diagnostics;
using Puck.Abstractions;

namespace Puck.Cli;

/// <summary>
/// Serializes every MSBuild run the CLI starts in one checkout. Builds of a checkout share its <c>bin</c> and
/// <c>obj</c> directories (the World build store publishes from them, as a <c>dotnet build</c> leaves them), and two
/// MSBuild processes writing one project's outputs at once leave a half-written reference assembly that the other
/// fails to read (<c>CS0009</c>). <see cref="CliProcess"/> takes this lock around each <c>dotnet build</c>,
/// <c>publish</c>, <c>pack</c>, <c>restore</c> or <c>msbuild</c> child it starts, so no caller can start one without it:
/// a World store build, a canary or law build, and a hand build run through <c>puck lease build --</c> all queue on the
/// same lock file, <c>obj/puck-build.lock</c> at the checkout's root. The lock is the innermost one a run takes: it is
/// held only while the child runs and is never held while waiting for a lease. A <c>dotnet build</c> started outside
/// the CLI takes no lock and can still collide.
/// </summary>
public static class CliCheckoutBuildLock {
    /// <summary>The lock file, relative to the checkout's root.</summary>
    public const string RelativePath = "obj/puck-build.lock";

    /// <summary>Returns whether a child process runs MSBuild over the tree: <c>dotnet</c> with a first argument of
    /// <c>build</c>, <c>publish</c>, <c>pack</c>, <c>restore</c> or <c>msbuild</c>.</summary>
    /// <param name="fileName">The executable, a bare name or a path.</param>
    /// <param name="arguments">The arguments, the subcommand first.</param>
    /// <returns><see langword="true"/> for an MSBuild run.</returns>
    public static bool Builds(string fileName, IReadOnlyList<string> arguments) =>
        string.Equals(a: Path.GetFileNameWithoutExtension(path: fileName), b: "dotnet", comparisonType: StringComparison.OrdinalIgnoreCase) &&
        (arguments.Count != 0) && (arguments[0] is "build" or "publish" or "pack" or "restore" or "msbuild");

    /// <summary>Waits for the checkout lock of an MSBuild child (<see cref="Builds"/>) that starts in
    /// <paramref name="workingDirectory"/>; any other child, or one outside a checkout, takes none.</summary>
    /// <param name="fileName">The child's executable.</param>
    /// <param name="arguments">The child's arguments.</param>
    /// <param name="workingDirectory">The directory the child starts in, or <see langword="null"/> for this process's.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The held lock, released when disposed; or <see langword="null"/> when the child takes none.</returns>
    /// <exception cref="TimeoutException">Another build held the lock for all of <see cref="CliLease.DefaultWait"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while waiting.</exception>
    public static async Task<IDisposable?> AcquireAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken = default) {
        if (!Builds(arguments: arguments, fileName: fileName) ||
            (RepositoryPaths.FindRootAbove(start: Path.GetFullPath(path: (workingDirectory ?? Environment.CurrentDirectory))) is not { } checkout)) {
            return null;
        }

        return await AcquireAsync(checkout: checkout, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Waits for the build lock of <paramref name="checkout"/>, writing one line to standard error when
    /// another build holds it.</summary>
    /// <param name="checkout">The checkout's root.</param>
    /// <param name="waiting">Called once, synchronously, when the first attempt finds another holder, after the line;
    /// <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The held lock, released when disposed.</returns>
    /// <exception cref="TimeoutException">Another build held the lock for all of <see cref="CliLease.DefaultWait"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while waiting.</exception>
    public static async Task<IDisposable> AcquireAsync(string checkout, Action? waiting = null, CancellationToken cancellationToken = default) {
        var path = Path.Combine(path1: checkout, path2: RelativePath);
        var asked = Stopwatch.GetTimestamp();

        return (await FileLock.AcquireAsync(
            cancellationToken: cancellationToken,
            expired: () => (Stopwatch.GetElapsedTime(startingTimestamp: asked) >= CliLease.DefaultWait),
            path: path,
            waiting: () => {
                Console.Error.WriteLine(value: $"waiting for another build of {PuckPaths.Normalize(path: checkout)} to finish: one MSBuild run at a time writes a checkout's outputs ({RelativePath}).");
                waiting?.Invoke();
            }
        ).ConfigureAwait(continueOnCapturedContext: false)) ?? throw new TimeoutException(message: $"another build held {PuckPaths.Normalize(path: path)} for {CliLease.DefaultWait.TotalHours:0} hours.");
    }
}
