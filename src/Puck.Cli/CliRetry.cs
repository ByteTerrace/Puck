namespace Puck.Cli;

// The one attempt loop behind every CLI verb that talks to a remote it cannot make succeed on the first try
// (az/docker steps, a freshly pushed package that is not installable yet, nuget.org throttling). A caller supplies
// the attempt cap, the fixed delay between attempts, which failures are worth another attempt, and the line it
// wants printed for each one; the last attempt's exception is the caller's, so a give-up never hides the cause.
internal static class CliRetry {
    // attempts counts total attempts, not retries: the delay is paid attempts-1 times at most. retryable filters
    // which exceptions earn another attempt (every one when absent). report renders the stderr line for a failed
    // attempt, given that attempt's exception and its 1-based number (nothing is printed when absent).
    internal static async Task<T> RetryAsync<T>(
        Func<Task<T>> action, int attempts, TimeSpan delay, Func<Exception, bool>? retryable = null, Func<Exception, int, string>? report = null
    ) {
        for (var attempt = 1; ; attempt++) {
            try {
                return await action();
            } catch (Exception error) when (((attempt < attempts) && ((retryable is null) || retryable(error)))) {
                if (report is not null) { Console.Error.WriteLine(value: report(error, attempt)); }
                await Task.Delay(delay: delay);
            }
        }
    }
    internal static Task RetryAsync(
        Func<Task> action, int attempts, TimeSpan delay, Func<Exception, bool>? retryable = null, Func<Exception, int, string>? report = null
    ) =>
        RetryAsync<bool>(
            action: async () => { await action(); return true; },
            attempts: attempts,
            delay: delay,
            report: report,
            retryable: retryable
        );
}
