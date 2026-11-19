namespace Puck.SdfVm;

/// <summary>
/// The measured GPU time one unit of a kind of background job costs (a ray, a probe, a field evaluation), learned from
/// the timestamps of the kind's completed jobs (<see cref="SdfGpuWorkReadback.GpuSeconds"/>): what a client of the
/// background GPU work (<see cref="SdfGpuWork"/>) charges a job before its timestamps measure it, and how many units it
/// puts in one job so that the job fits one frame's allowance (<see cref="SdfGpuWorkBudget.FrameAllowanceSeconds"/>).
/// <para>The rate falls toward a lower measurement by <see cref="Fall"/> of the gap a measurement, and rises toward a
/// higher one by its <c>rise</c>: at once by default, so a kind whose units vary in cost (a probe ray toward one sun or
/// toward eighty lamps) is sized for its costly jobs until cheaper ones have run for a while. A kind whose jobs each mix
/// units of many costs (a bake batch of several groups' lattices) follows the mean instead, rising by <see cref="Fall"/>
/// too, since sizing every job for the costliest it has seen shrinks them all.</para>
/// <para>Reads and measurements may come from different threads (a coordinator sizing a job, the recording thread
/// answering one); the rate is one double read and written whole.</para>
/// </summary>
public sealed class SdfGpuWorkRate {
    /// <summary>The share of the gap to a lower measurement the rate falls by with each measurement.</summary>
    public const double Fall = 0.125;

    private readonly double m_rise;
    private double m_secondsPerUnit;

    /// <summary>Initializes a new instance of the <see cref="SdfGpuWorkRate"/> class at a prior rate, which the first
    /// measurement replaces.</summary>
    /// <param name="priorSecondsPerUnit">The GPU time, in seconds, one unit is assumed to cost before any job of the kind
    /// has been measured, and on a queue without timestamps; positive.</param>
    /// <param name="rise">The share of the gap to a higher measurement the rate rises by with each measurement, above
    /// zero and at most one: one rises at once, <see cref="Fall"/> follows the mean.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="priorSecondsPerUnit"/> is not positive, or
    /// <paramref name="rise"/> is not above zero and at most one.</exception>
    public SdfGpuWorkRate(double priorSecondsPerUnit, double rise = 1.0) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: priorSecondsPerUnit);
        if (!((rise > 0.0) && (rise <= 1.0))) { throw new ArgumentOutOfRangeException(paramName: nameof(rise), actualValue: rise, message: "A rate rises by a share above zero and at most one."); }
        m_secondsPerUnit = priorSecondsPerUnit;
        m_rise = rise;
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
    /// unit costlier than the whole span still runs alone, and at most <paramref name="most"/>, which an unbounded span
    /// (<see cref="double.PositiveInfinity"/>, a held frame's own work) gives.</summary>
    /// <param name="seconds">The GPU time, in seconds, the job is to fit.</param>
    /// <param name="most">The most units a job holds whatever its estimate, at least one.</param>
    /// <returns>The units.</returns>
    public long UnitsWithin(double seconds, long most) {
        var bound = Math.Max(val1: 1L, val2: most);
        var units = (seconds / SecondsPerUnit);

        return ((units >= bound) ? bound : Math.Max(val1: 1L, val2: (long)units));
    }
    /// <summary>Folds a completed job's measured GPU time into the rate.</summary>
    /// <param name="units">The job's units; a job of none measures nothing.</param>
    /// <param name="seconds">The job's measured GPU time, in seconds.</param>
    public void Observe(long units, double seconds) {
        if ((units <= 0) || !(seconds > 0.0)) { return; }

        var measured = (seconds / units);
        var current = SecondsPerUnit;
        var share = ((measured >= current) ? m_rise : Fall);

        Volatile.Write(location: ref m_secondsPerUnit, value: Measured ? (current + ((measured - current) * share)) : measured);
        Measured = true;
    }
}
