using Puck.Storage;

namespace Puck.World.Tests;

/// <summary>Which <see cref="IObjectBlobStore"/> call a <see cref="StallingObjectBlobStore"/> is deciding whether to
/// stall.</summary>
internal enum StoreCall {
    /// <summary>A <see cref="IObjectBlobStore.ListAsync"/> call.</summary>
    List,

    /// <summary>A <see cref="IObjectBlobStore.ReadAsync"/> call.</summary>
    Read,

    /// <summary>A <see cref="IObjectBlobStore.WriteAsync"/> call.</summary>
    Write,
}
/// <summary>An <see cref="IObjectBlobStore"/> over <paramref name="inner"/> whose selected calls never answer: each one
/// waits until its token is cancelled and then throws, the shape of an unreachable storage endpoint. A deadline law
/// passes a store call's own bound on a virtual clock and expires it, so the call ends only when that bound fires.
/// Every other call is answered by <paramref name="inner"/>.</summary>
/// <param name="inner">The store that answers every call <paramref name="stalls"/> does not select.</param>
/// <param name="stalls">Selects a call by its kind and its key (a list call's key prefix).</param>
internal sealed class StallingObjectBlobStore(IObjectBlobStore inner, Func<StoreCall, string, bool> stalls) : IObjectBlobStore {
    private readonly TaskCompletionSource m_stalled = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets a task that completes once a selected call has begun to stall — the point at which the caller's
    /// bound on that call is armed and every earlier call has already answered.</summary>
    public Task Stalled => m_stalled.Task;

    /// <summary>Signals <paramref name="entered"/>, then waits until <paramref name="cancellationToken"/> is cancelled
    /// and throws — the shape of a remote call that never answers.</summary>
    /// <typeparam name="T">The call's result type, which is never produced.</typeparam>
    /// <param name="entered">Completed once the call has begun to stall.</param>
    /// <param name="cancellationToken">The call's own bound; its cancellation is the only way the call ends.</param>
    /// <returns>Nothing: the task always faults or is cancelled.</returns>
    public static async ValueTask<T> StallUntilCanceledAsync<T>(TaskCompletionSource entered, CancellationToken cancellationToken) {
        entered.TrySetResult();
        await Task.Delay(
            cancellationToken: cancellationToken,
            delay: Timeout.InfiniteTimeSpan
        ).ConfigureAwait(continueOnCapturedContext: false);

        throw new InvalidOperationException(message: "An infinite delay ended without cancellation.");
    }

    private ValueTask<T> StallAsync<T>(CancellationToken cancellationToken) => StallUntilCanceledAsync<T>(
        cancellationToken: cancellationToken,
        entered: m_stalled
    );

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) => (stalls(
        arg1: StoreCall.List,
        arg2: keyPrefix
    )
        ? StallAsync<IReadOnlyList<string>>(cancellationToken: cancellationToken)
        : inner.ListAsync(
            cancellationToken: cancellationToken,
            keyPrefix: keyPrefix,
            objectId: objectId,
            target: target
        )
    );
    /// <inheritdoc/>
    public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) => (stalls(
        arg1: StoreCall.Read,
        arg2: address.Key
    )
        ? StallAsync<ObjectBlobContent?>(cancellationToken: cancellationToken)
        : inner.ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: target
        )
    );
    /// <inheritdoc/>
    public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) => (stalls(
        arg1: StoreCall.Write,
        arg2: address.Key
    )
        ? StallAsync<ObjectBlobWriteResult>(cancellationToken: cancellationToken)
        : inner.WriteAsync(
            address: address,
            cancellationToken: cancellationToken,
            content: content,
            ifMatchVersion: ifMatchVersion,
            mode: mode,
            target: target
        )
    );
}
