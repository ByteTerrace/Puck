namespace Puck.Hosting;

/// <summary>
/// One candidate built on the shared thread pool and taken back on the thread that owns it — the way a render node
/// compiles a shader pipeline or creates GPU pipelines without stalling the pump that drains the console, steps the
/// simulation, and produces frames. The owner starts a build, polls <see cref="TryTake"/> once per produced frame, and
/// installs the result at that frame boundary; until then it keeps presenting what it already has.
/// <para>
/// A build runs on the thread pool, never on a thread of its own. The build delegate receives a token that
/// <see cref="Cancel"/> and <see cref="CancelAndWait"/> signal; it checks the token between units of work (one pipeline,
/// one compiler invocation), so a canceled build stops at the next boundary rather than mid-call.
/// </para>
/// <para>
/// Every member runs on the owner's thread. A result is handed to exactly one party: the owner through
/// <see cref="TryTake"/>, or the discard action given to a cancel, so a superseded result holding native objects is
/// released instead of leaked. Polling an idle or still-running build allocates nothing.
/// </para>
/// </summary>
/// <typeparam name="T">The built candidate.</typeparam>
public sealed class BackgroundBuild<T> where T : class {
    private CancellationTokenSource? m_cancellation;
    private Task<T>? m_task;

    /// <summary>Gets whether the pending build has finished, successfully or not, so <see cref="TryTake"/> would take
    /// it; <see langword="false"/> while it runs and when none is pending.</summary>
    public bool IsCompleted => (m_task is { IsCompleted: true });
    /// <summary>Gets whether a build has started and its result has not been taken or canceled.</summary>
    public bool IsPending => (m_task is not null);
    /// <summary>Gets the pending build's task, which completes when the build finishes, successfully or not, or
    /// <see langword="null"/> when none is pending. A waiter on another thread reads it under the owner's lock and waits
    /// outside it, then takes the result through the owner; it never takes the result from the task itself.</summary>
    public Task? Completion => m_task;

    // Faults of a detached build are observed here, so a superseded failure never reaches the unobserved-task handler.
    internal static void Finish(Task<T> task, CancellationTokenSource cancellation, Action<T>? discard) {
        if (task.IsCompletedSuccessfully) {
            discard?.Invoke(obj: task.Result);
        } else {
            _ = task.Exception;
        }

        cancellation.Dispose();
    }

    /// <summary>Cancels the pending build without waiting for it. The build stops at its next check of the token; if it
    /// completes anyway, <paramref name="discard"/> receives the result on the pool thread that finished it. Use this
    /// only when whatever the result depends on — the GPU device, above all — outlives the build; otherwise use
    /// <see cref="CancelAndWait"/>. Does nothing when no build is pending.</summary>
    /// <param name="discard">Releases a result that completed after the cancel, or <see langword="null"/> when the result
    /// owns nothing.</param>
    public void Cancel(Action<T>? discard = null) {
        if (m_task is not { } task) {
            return;
        }

        var cancellation = m_cancellation!;

        m_task = null;
        m_cancellation = null;
        cancellation.Cancel();
        _ = task.ContinueWith(
            cancellationToken: CancellationToken.None,
            continuationAction: static (completed, state) => {
                var (cancellation, discard) = ((ValueTuple<CancellationTokenSource, Action<T>?>)state!);

                Finish(
                    cancellation: cancellation,
                    discard: discard,
                    task: completed
                );
            },
            continuationOptions: TaskContinuationOptions.ExecuteSynchronously,
            scheduler: TaskScheduler.Default,
            state: (cancellation, discard)
        );
    }
    /// <summary>Cancels the pending build and blocks until it returns, then hands a completed result to
    /// <paramref name="discard"/> on this thread. The wait is bounded by the build's current unit of work. Use this
    /// before releasing what the build depends on: a device about to be destroyed must not have an object creation in
    /// flight. Does nothing when no build is pending.</summary>
    /// <param name="discard">Releases a result that completed, or <see langword="null"/> when the result owns nothing.</param>
    public void CancelAndWait(Action<T>? discard = null) =>
        Detach().Wait(discard: discard);
    /// <summary>Cancels the pending build and hands it to the caller to wait out with
    /// <see cref="CanceledBuild{T}.Wait"/>. The cancel takes effect before this returns, and the build is no longer
    /// pending; only the wait is deferred. Use it where the cancel must be visible inside a lock that guards the build
    /// and the wait must not hold that lock. Returns an empty handle when no build is pending.</summary>
    /// <returns>The canceled build, which the caller must wait out exactly once.</returns>
    public CanceledBuild<T> Detach() {
        if (m_task is not { } task) {
            return default;
        }

        var cancellation = m_cancellation!;

        m_task = null;
        m_cancellation = null;
        cancellation.Cancel();

        return new CanceledBuild<T>(
            cancellation: cancellation,
            task: task
        );
    }
    /// <summary>Starts a build on the thread pool.</summary>
    /// <param name="build">Builds the candidate. It runs on a pool thread, receives the token a cancel signals, and may
    /// throw; its exception is what <see cref="TryTake"/> reports.</param>
    /// <exception cref="ArgumentNullException"><paramref name="build"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A build is already pending; take or cancel it first.</exception>
    public void Start(Func<CancellationToken, T> build) {
        ArgumentNullException.ThrowIfNull(argument: build);

        if (m_task is not null) {
            throw new InvalidOperationException(message: "A build is already pending; take or cancel it before starting another.");
        }

        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;

        m_cancellation = cancellation;
        m_task = Task.Run(
            cancellationToken: CancellationToken.None,
            function: () => build(arg: token)
        );
    }
    /// <summary>Takes the finished build, if it has finished. Once this returns <see langword="true"/> the build is no
    /// longer pending and the caller owns the result.</summary>
    /// <param name="result">The built candidate when the build succeeded; otherwise <see langword="null"/>.</param>
    /// <param name="error">The exception the build threw when it failed; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a build finished, successfully or not; <see langword="false"/> when none is
    /// pending or the pending one is still running.</returns>
    public bool TryTake(out T? result, out Exception? error) {
        result = null;
        error = null;

        if (m_task is not { IsCompleted: true } task) {
            return false;
        }

        m_task = null;
        m_cancellation!.Dispose();
        m_cancellation = null;

        if (task.IsCompletedSuccessfully) {
            result = task.Result;
        } else {
            var exception = task.Exception!;

            error = ((exception.InnerExceptions.Count == 1)
                ? exception.InnerExceptions[0]
                : exception
            );
        }

        return true;
    }
}
/// <summary>
/// A build <see cref="BackgroundBuild{T}.Detach"/> canceled, still running or already finished, for the caller to wait
/// out once with <see cref="Wait"/> before releasing what the build depends on. The default value is an empty handle,
/// whose wait returns at once.
/// </summary>
/// <typeparam name="T">The built candidate.</typeparam>
public readonly struct CanceledBuild<T> where T : class {
    private readonly CancellationTokenSource? m_cancellation;
    private readonly Task<T>? m_task;

    internal CanceledBuild(Task<T> task, CancellationTokenSource cancellation) {
        m_cancellation = cancellation;
        m_task = task;
    }

    /// <summary>Blocks until the canceled build returns, then hands a completed result to
    /// <paramref name="discard"/> on this thread. The wait is bounded by the build's current unit of work.</summary>
    /// <param name="discard">Releases a result that completed, or <see langword="null"/> when the result owns
    /// nothing.</param>
    public void Wait(Action<T>? discard = null) {
        if (m_task is not { } task) {
            return;
        }

        ((IAsyncResult)task).AsyncWaitHandle.WaitOne();
        BackgroundBuild<T>.Finish(
            cancellation: m_cancellation!,
            discard: discard,
            task: task
        );
    }
}
