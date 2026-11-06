using System.Diagnostics;
using System.Globalization;

namespace Puck.Cli.Sweep;

/// <summary>The time a whole sweep may take and the caller's cancellation, one token every wait of the sweep honours:
/// a World's call, a boot, a lease wait and the scenario process.</summary>
public sealed class SweepBudget : IDisposable {
    private readonly CancellationTokenSource m_cancellation;
    private readonly CancellationToken m_caller;
    private readonly Stopwatch m_clock = Stopwatch.StartNew();

    private TimeSpan m_total;

    /// <summary>Initializes a new instance of the <see cref="SweepBudget"/> class and starts its clock.</summary>
    /// <param name="total">The most time the sweep may take.</param>
    /// <param name="caller">The caller's cancellation, such as an interrupt.</param>
    public SweepBudget(TimeSpan total, CancellationToken caller) {
        m_caller = caller;
        m_cancellation = CancellationTokenSource.CreateLinkedTokenSource(token: caller);
        m_total = total;
        m_cancellation.CancelAfter(delay: total);
    }

    /// <summary>Gets the token that is cancelled when the time passes or the caller cancels.</summary>
    public CancellationToken Token => m_cancellation.Token;
    /// <summary>Gets whether the sweep must stop: its time has passed or the caller cancelled.</summary>
    public bool Expired => m_cancellation.IsCancellationRequested;
    /// <summary>Gets the time left, never negative.</summary>
    public TimeSpan Remaining => TimeSpan.FromTicks(value: Math.Max(val1: 0L, val2: (m_total - m_clock.Elapsed).Ticks));
    /// <summary>Gets why the sweep must stop, or <see langword="null"/> while it may go on.</summary>
    public string? Reason => (Expired
        ? (m_caller.IsCancellationRequested ? "the sweep was cancelled" : string.Create(provider: CultureInfo.InvariantCulture, handler: $"the sweep's --timeout-seconds of {m_total.TotalSeconds:0} passed"))
        : null);

    /// <summary>Returns the shorter of a wait and the time left.</summary>
    /// <param name="wait">The wait.</param>
    /// <returns>The wait, or what remains when that is less.</returns>
    public TimeSpan Bound(TimeSpan wait) => ((wait < Remaining) ? wait : Remaining);
    /// <summary>Stops the budget's clock while the sweep waits in a machine-wide lease queue, which is time spent on other
    /// runs' work and never part of what the sweep's own time measures. Time already past stays past: pausing an
    /// expired budget changes nothing.</summary>
    /// <returns>A scope that resumes the clock when disposed; the time paused is added back to the budget.</returns>
    public IDisposable Pause() {
        var paused = Stopwatch.StartNew();

        m_cancellation.CancelAfter(delay: Timeout.InfiniteTimeSpan);

        return new Resume(onDispose: () => {
            m_total += paused.Elapsed;
            m_cancellation.CancelAfter(delay: Remaining);
        });
    }
    /// <inheritdoc/>
    public void Dispose() => m_cancellation.Dispose();

    private sealed class Resume(Action onDispose) : IDisposable {
        private Action? m_onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(location1: ref m_onDispose, value: null)?.Invoke();
    }
}
