namespace Puck.SdfVm;

/// <summary>
/// The measured GPU time one unit of a kind of background job costs (a ray, a probe, a field evaluation), learned from
/// the timestamps of the kind's completed jobs (<see cref="SdfGpuWorkReadback.GpuSeconds"/>): what a client of the
/// background GPU work (<see cref="SdfGpuWork"/>) charges a job before its timestamps measure it, and how many units it
/// puts in one job so that the job fits background work's share of a frame (<see cref="SdfGpuWork.FrameShareSeconds"/>).
/// <para>The rate rises to a higher measurement at once and falls toward a lower one by <see cref="Fall"/> of the gap a
/// measurement, so a kind whose units vary in cost (a probe ray toward one sun or toward eighty lamps) is sized for its
/// costly jobs until cheaper ones have run for a while.</para>
/// <para>Reads and measurements may come from different threads (a coordinator sizing a job, the recording thread
/// answering one); the rate is one double read and written whole.</para>
/// </summary>
public sealed class SdfGpuWorkRate {
    /// <summary>The share of the gap to a lower measurement the rate falls by with each measurement.</summary>
    public const double Fall = 0.125;

    private double m_secondsPerUnit;

    /// <summary>Initializes a new instance of the <see cref="SdfGpuWorkRate"/> class at a prior rate, which the first
    /// measurement replaces.</summary>
    /// <param name="priorSecondsPerUnit">The GPU time, in seconds, one unit is assumed to cost before any job of the kind
    /// has been measured, and on a queue without timestamps; positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="priorSecondsPerUnit"/> is not positive.</exception>
    public SdfGpuWorkRate(double priorSecondsPerUnit) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: priorSecondsPerUnit);
        m_secondsPerUnit = priorSecondsPerUnit;
    }

    /// <summary>Gets whether a completed job has been measured since the prior.</summary>
    public bool Measured { get; private set; }
    /// <summary>Gets the GPU time, in seconds, one unit is estimated to cost.</summary>
    public double SecondsPerUnit => Volatile.Read(location: ref m_secondsPerUnit);

    /// <summary>Returns the GPU time, in seconds, a job of <paramref name="units"/> units is estimated to take.</summary>
    /// <param name="units">The job's units.</param>
    /// <returns>The estimate, in seconds.</returns>
    public double SecondsOf(long units) => (units * SecondsPerUnit);
    /// <summary>Returns the most units a job may hold whose estimate fits <paramref name="seconds"/>: at least one, so a
    /// unit costlier than the whole span still runs alone, and at most <paramref name="most"/>.</summary>
    /// <param name="seconds">The GPU time, in seconds, the job is to fit.</param>
    /// <param name="most">The most units a job holds whatever its estimate, at least one.</param>
    /// <returns>The units.</returns>
    public long UnitsWithin(double seconds, long most) => Math.Clamp(
        max: Math.Max(val1: 1L, val2: most),
        min: 1L,
        value: (long)Math.Min(val1: (seconds / SecondsPerUnit), val2: long.MaxValue)
    );
    /// <summary>Folds a completed job's measured GPU time into the rate.</summary>
    /// <param name="units">The job's units; a job of none measures nothing.</param>
    /// <param name="seconds">The job's measured GPU time, in seconds.</param>
    public void Observe(long units, double seconds) {
        if ((units <= 0) || !(seconds > 0.0)) { return; }

        var measured = (seconds / units);
        var current = SecondsPerUnit;

        Volatile.Write(location: ref m_secondsPerUnit, value: ((measured >= current) || !Measured) ? measured : (current + ((measured - current) * Fall)));
        Measured = true;
    }
}
