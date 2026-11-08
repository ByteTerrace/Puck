using System.Diagnostics;
using System.Globalization;

namespace Puck.Cli;

/// <summary>The time a whole run may take and the caller's cancellation, one token every wait of the run honours:
/// a World's call, a boot, a lease wait and the scenario process.</summary>
public sealed class SuiteBudget : IDisposable {
    private readonly CancellationTokenSource m_cancellation;
    private readonly CancellationToken m_caller;
    private readonly Stopwatch m_clock;
    private readonly Lock m_gate = new();
    private int m_pauses;
    private bool m_wasRunning;

    private readonly TimeSpan m_total;

    /// <summary>Initializes a new instance of the <see cref="SuiteBudget"/> class and starts its clock.</summary>
    /// <param name="total">The most time the run may take.</param>
    /// <param name="clock">An existing suite clock, or null to start a new one.</param>
    /// <param name="caller">The caller's cancellation, such as an interrupt.</param>
    public SuiteBudget(TimeSpan total, CancellationToken caller = default, Stopwatch? clock = null) {
        m_clock = clock ?? Stopwatch.StartNew();
        m_caller = caller;
        m_cancellation = CancellationTokenSource.CreateLinkedTokenSource(token: caller);
        m_total = total;
        m_cancellation.CancelAfter(delay: Remaining);
    }

    /// <summary>Gets the token that is cancelled when the time passes or the caller cancels.</summary>
    public CancellationToken Token => m_cancellation.Token;
    /// <summary>Gets whether the run must stop: its time has passed or the caller cancelled.</summary>
    public bool Expired => m_cancellation.IsCancellationRequested;
    /// <summary>Gets the time left, never negative.</summary>
    public TimeSpan Remaining => TimeSpan.FromTicks(value: Math.Max(val1: 0L, val2: CliProcess.RemainingBudget(budget: m_total, clock: m_clock).Ticks));
    /// <summary>Gets why the run must stop, or <see langword="null"/> while it may go on.</summary>
    public string? Reason => (Expired
        ? (m_caller.IsCancellationRequested ? "the run was cancelled" : string.Create(provider: CultureInfo.InvariantCulture, handler: $"the run's --timeout-seconds of {m_total.TotalSeconds:0} passed"))
        : null);

    /// <summary>Returns the shorter of a wait and the time left.</summary>
    /// <param name="wait">The wait.</param>
    /// <returns>The wait, or what remains when that is less.</returns>
    public TimeSpan Bound(TimeSpan wait) => ((wait < Remaining) ? wait : Remaining);
    /// <summary>Stops the budget's clock while the run waits in a machine-wide lease queue, which is time spent on other
    /// runs' work and never part of what the run's own time measures. Pausing never clears expiry or caller
    /// cancellation.</summary>
    /// <returns>A scope that resumes the clock after the last pause ends.</returns>
    public IDisposable Pause() {
        lock (m_gate) {
            if (m_pauses++ == 0) {
                if (Remaining == TimeSpan.Zero) { m_cancellation.Cancel(); }
                m_wasRunning = m_clock.IsRunning;
                m_clock.Stop();
                m_cancellation.CancelAfter(delay: Timeout.InfiniteTimeSpan);
            }
        }
        return new Resume(onDispose: () => {
            lock (m_gate) {
                if (--m_pauses == 0) {
                    if (m_wasRunning) { m_clock.Start(); }
                    m_cancellation.CancelAfter(delay: Remaining);
                }
            }
        });
    }
    /// <inheritdoc/>
    public void Dispose() => m_cancellation.Dispose();

    private sealed class Resume(Action onDispose) : IDisposable {
        private Action? m_onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(location1: ref m_onDispose, value: null)?.Invoke();
    }
}
