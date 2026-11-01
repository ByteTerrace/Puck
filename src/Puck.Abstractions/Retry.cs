namespace Puck.Abstractions;

/// <summary>
/// The one attempt loop: a caller supplies the attempt cap, the delay between attempts, which failures are worth
/// another attempt, and (for the asynchronous loop) the line it wants printed for each one; the last attempt's
/// exception is the caller's, so a give-up never hides the cause. It serves the CLI verbs that talk to a remote they
/// cannot make succeed on the first try (az/docker steps, a freshly pushed package that is not installable yet,
/// nuget.org throttling) and the file-system operations that a scanner or a reader holding a file open refuses for a
/// moment.
/// </summary>
public static class Retry {
    /// <summary>Whether an exception is the file system refusing an operation: an <see cref="IOException"/> or an
    /// <see cref="UnauthorizedAccessException"/>, which Windows raises while another handle holds the file.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns><see langword="true"/> when the failure is an I/O refusal.</returns>
    public static bool IsFileSystemRefusal(Exception exception) =>
        (exception is IOException or UnauthorizedAccessException);

    // attempts counts total attempts, not retries: the delay is paid attempts-1 times at most, on clock (system time
    // when absent). retryable filters which exceptions earn another attempt (every one when absent). report renders
    // the stderr line for a failed attempt, given that attempt's exception and its 1-based number (nothing is printed
    // when absent).
    public static async Task<T> RetryAsync<T>(
        Func<Task<T>> action, int attempts, TimeSpan delay, Func<Exception, bool>? retryable = null, Func<Exception, int, string>? report = null,
        TimeProvider? clock = null
    ) {
        for (var attempt = 1; ; attempt++) {
            try {
                return await action();
            } catch (Exception error) when (((attempt < attempts) && ((retryable is null) || retryable(error)))) {
                if (report is not null) {
                    Console.Error.WriteLine(value: report(
                        error,
                        attempt
                    ));
                }
                await Task.Delay(
                    delay: delay,
                    timeProvider: (clock ?? TimeProvider.System)
                );
            }
        }
    }
    public static Task RetryAsync(
        Func<Task> action, int attempts, TimeSpan delay, Func<Exception, bool>? retryable = null, Func<Exception, int, string>? report = null,
        TimeProvider? clock = null
    ) =>
        RetryAsync<bool>(
            action: async () => { await action(); return true; },
            attempts: attempts,
            clock: clock,
            delay: delay,
            report: report,
            retryable: retryable
        );
    /// <summary>Runs <paramref name="action"/>, blocking the thread between attempts, until it returns or
    /// <paramref name="attempts"/> attempts have failed.</summary>
    /// <param name="action">The operation.</param>
    /// <param name="attempts">The total attempts, the first included.</param>
    /// <param name="backoff">The wait after a failed attempt, given that attempt's 1-based number.</param>
    /// <param name="retryable">Which failures earn another attempt; every one when <see langword="null"/>.</param>
    /// <exception cref="Exception">The last attempt's failure, or any failure <paramref name="retryable"/> refused.</exception>
    public static void Run(Action action, int attempts, Func<int, TimeSpan> backoff, Func<Exception, bool>? retryable = null) {
        ArgumentNullException.ThrowIfNull(argument: action);
        ArgumentNullException.ThrowIfNull(argument: backoff);

        for (var attempt = 1; ; attempt++) {
            try {
                action();

                return;
            } catch (Exception error) when (((attempt < attempts) && ((retryable is null) || retryable(error)))) {
                Thread.Sleep(timeout: backoff(attempt));
            }
        }
    }
}
