namespace Puck.Abstractions;

/// <summary>Observes the outcome of a task nothing awaits, so an abandoned operation's late failure never reaches the
/// unobserved-task handler.</summary>
public static class TaskObservation {
    /// <summary>Observes <paramref name="task"/>'s failure or cancellation, without waiting for it and without
    /// replaying its work. A caller that stops waiting for an operation that ignores cancellation hands it here.</summary>
    /// <param name="task">The task to observe.</param>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is <see langword="null"/>.</exception>
    public static void Observe(this Task task) {
        ArgumentNullException.ThrowIfNull(argument: task);

        if (task.IsCompleted) {
            _ = task.Exception;
            return;
        }

        _ = ObserveAsync(task: task);
    }

    private static async Task ObserveAsync(Task task) {
        await task.ConfigureAwait(options: ConfigureAwaitOptions.SuppressThrowing);
        _ = task.Exception;
    }
}
