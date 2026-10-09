using Puck.Testing;
using Xunit;

namespace Puck.Cli.Testing;

/// <summary>Holds a cohort together until every worker enters, and holds its final admission until every worker ends.
/// Admission precedes completion callbacks, so an extra admission cannot hide behind a worker finishing quickly.</summary>
internal sealed class SchedulingCohorts(int bound, int cohorts) : IDisposable {
    private readonly CountdownEvent[] m_entered = [.. Enumerable.Range(count: cohorts, start: 0).Select(selector: _ => new CountdownEvent(initialCount: bound))];
    private readonly CountdownEvent[] m_ended = [.. Enumerable.Range(count: cohorts, start: 0).Select(selector: _ => new CountdownEvent(initialCount: bound))];

    private int m_admitted;
    private int m_reported;
    private int m_admissionPeak;
    private int m_workers;
    private int m_inside;
    private int m_peak;

    public int AdmissionPeak => m_admissionPeak;
    public int Peak => m_peak;

    public void Started() {
        m_admitted++;
        m_admissionPeak = Math.Max(val1: m_admissionPeak, val2: (m_admitted - m_reported));
        if ((m_admitted % bound) == 0) {
            Assert.True(condition: m_ended[((m_admitted / bound) - 1)].Wait(timeout: TestLiveness.Bound), userMessage: "The admitted cohort never ended.");
        }
    }
    public void Run() {
        var cohort = ((Interlocked.Increment(location: ref m_workers) - 1) / bound);
        var inside = Interlocked.Increment(location: ref m_inside);
        int peak;

        do {
            peak = Volatile.Read(location: ref m_peak);
        } while ((inside > peak) && (Interlocked.CompareExchange(comparand: peak, location1: ref m_peak, value: inside) != peak));

        try {
            m_entered[cohort].Signal();
            Assert.True(condition: m_entered[cohort].Wait(timeout: TestLiveness.Bound), userMessage: "The runner never overlapped a full cohort.");
        } finally {
            Interlocked.Decrement(location: ref m_inside);
            m_ended[cohort].Signal();
        }
    }
    public void Completed() => m_reported++;
    public void Dispose() {
        foreach (var signal in m_entered.Concat(second: m_ended)) {
            signal.Dispose();
        }
    }
}
