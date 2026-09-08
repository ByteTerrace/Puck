namespace Puck.World.Agents;

/// <summary>Moves a short, synchronous world-link operation from an agent worker onto the Puck host thread.</summary>
/// <remarks>The operation must never perform model inference, wait for user approval, or block on external I/O. It
/// exists only to cross the thread-affine in-process protocol boundary. A remote, thread-safe transport may provide
/// a dispatcher that invokes immediately; Puck's built-in loopback uses <see cref="WorldAgentMailbox"/>.</remarks>
public interface IWorldAgentDispatcher {
    /// <summary>Schedules one short operation and asynchronously returns its result.</summary>
    /// <typeparam name="TResult">The operation result.</typeparam>
    /// <param name="operation">The synchronous operation to execute on the transport's required thread.</param>
    /// <param name="cancellationToken">Cancels the operation while it is still queued. Once execution starts, the
    /// short operation runs to completion and its result wins the cancellation race.</param>
    /// <returns>An awaitable result that completes after the dispatcher executes the operation.</returns>
    ValueTask<TResult> InvokeAsync<TResult>(
        Func<TResult> operation,
        CancellationToken cancellationToken = default
    );
}
