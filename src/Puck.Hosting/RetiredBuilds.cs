namespace Puck.Hosting;

/// <summary>
/// Canceled builds whose owners let go of them before they finished, each discarded on a pool thread as it finishes
/// (<see cref="CanceledBuild{T}.DiscardAsync"/>), so an owner that supersedes or releases a build never waits for it on
/// the thread that renders. The table outlives the owners that retire into it and stands between their builds and what
/// those builds depend on: whatever releases that dependency (a device about to be destroyed or recreated after a loss)
/// calls <see cref="Drain"/> first, which waits out every build still running and its discard. Every member is safe on
/// any thread.
/// </summary>
public sealed class RetiredBuilds : IDisposable {
    private readonly Lock m_gate = new();
    private readonly HashSet<Task> m_running = [];
    private Exception? m_failure;

    /// <summary>Gets the number of retired builds whose discard has not finished yet.</summary>
    public int Running {
        get {
            lock (m_gate) {
                return m_running.Count;
            }
        }
    }

    /// <summary>Takes a canceled build, which the table discards on a pool thread once it and its cancellation callbacks
    /// return, without waiting for it. The caller hands the build to nothing else.</summary>
    /// <typeparam name="T">The built candidate.</typeparam>
    /// <param name="build">The canceled build (<see cref="BackgroundBuild{T}.Detach"/>); the empty handle is
    /// ignored.</param>
    /// <param name="discard">Releases a result that completed, on a pool thread, or <see langword="null"/> when the result
    /// owns nothing.</param>
    public void Retire<T>(CanceledBuild<T> build, Action<T>? discard) where T : class {
        if (build.IsEmpty) {
            return;
        }

        var discarded = build.DiscardAsync(discard: discard);

        // Entered before the continuation that forgets it is attached, so a build that has already finished cannot be
        // forgotten before it is entered.
        lock (m_gate) {
            _ = m_running.Add(item: discarded);
        }

        _ = discarded.ContinueWith(
            cancellationToken: CancellationToken.None,
            continuationAction: finished => Forget(discarded: finished),
            continuationOptions: TaskContinuationOptions.ExecuteSynchronously,
            scheduler: TaskScheduler.Default
        );
    }
    /// <summary>Blocks until every build retired so far, and every build retired while it waits, has finished and been
    /// discarded, then rethrows the first failure a discard met since the last drain.</summary>
    /// <exception cref="InvalidOperationException">A discard threw; the exception holds the first failure.</exception>
    public void Drain() {
        while (true) {
            Task[] running;

            lock (m_gate) {
                if (m_running.Count == 0) {
                    var failure = m_failure;

                    m_failure = null;

                    if (failure is not null) {
                        throw new InvalidOperationException(message: "Discarding a retired build failed.", innerException: failure);
                    }

                    return;
                }

                running = [.. m_running];
            }

            try {
                Task.WaitAll(tasks: running);
            } catch (AggregateException) {
                // Forget records each failure; the drain rethrows the first once nothing runs.
            }

            // A finished discard is forgotten here too, so the drain never waits on its continuation to do it.
            foreach (var discarded in running) {
                Forget(discarded: discarded);
            }
        }
    }
    /// <summary>Waits out every retired build (<see cref="Drain"/>).</summary>
    public void Dispose() => Drain();

    // Forgets a finished discard once, from its continuation or a drain, whichever comes first, and keeps the first
    // failure for the next drain.
    private void Forget(Task discarded) {
        lock (m_gate) {
            if (m_running.Remove(item: discarded) && (discarded.Exception is { } exception)) {
                m_failure ??= ((exception.InnerExceptions.Count == 1) ? exception.InnerExceptions[0] : exception);
            }
        }
    }
}
