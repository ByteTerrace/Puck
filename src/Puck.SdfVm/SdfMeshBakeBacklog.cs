namespace Puck.SdfVm;

/// <summary>
/// The bake pool's backlog measured as time: how long its workers take to drain what is queued, against how long a
/// newly admitted group takes to reach them. A group that starts its final lattice holds that lattice until its first
/// stage runs, so admitting one whose stage would only wait in the queue holds memory for nothing, and admitting none
/// while the queue drains before the next would arrive leaves workers idle. Admission therefore waits exactly while
/// the stages queued and still on their way keep every worker busy for the lead a new admission takes
/// (<see cref="Holds"/>).
/// <para>
/// Both sides are measured in seconds of wall time, from the pool's own work: the time one queued stage takes a worker
/// (<see cref="ObserveStage"/>) and the time from a group's admission to its first queued stage
/// (<see cref="ObserveLead"/>, the GPU rounds its lattice takes). So the answer does not depend on when it is asked: a
/// take right after a burst of answers, on a host whose next take is a slow frame away, still admits what the workers
/// will finish before that take's groups arrive. Until either is measured the two are equal, and the rule is a round
/// of the workers.
/// </para>
/// <para>Measurements and reads may come from different threads; each is one double read and written whole
/// (<see cref="SdfGpuWorkRate"/>).</para>
/// </summary>
public sealed class SdfMeshBakeBacklog {
    /// <summary>The seconds a stage and a lead are each assumed to take before one is measured: a frame at the 60 Hz
    /// frame bar.</summary>
    public const double PriorSeconds = (1.0 / 60.0);
    /// <summary>The most rounds of the workers the pool holds before admission waits, however long the lead: sixteen,
    /// so a stalled answer cannot admit without bound.</summary>
    public const int MostRounds = 16;

    private readonly SdfGpuWorkRate m_stage = new(priorSecondsPerUnit: PriorSeconds);
    private readonly SdfGpuWorkRate m_lead = new(priorSecondsPerUnit: PriorSeconds);

    /// <summary>Gets the wall time, in seconds, one queued stage is estimated to take a worker.</summary>
    public double StageSeconds => m_stage.SecondsPerUnit;
    /// <summary>Gets the wall time, in seconds, a group is estimated to take from its admission to its first queued
    /// stage.</summary>
    public double LeadSeconds => m_lead.SecondsPerUnit;

    /// <summary>Folds the wall time a worker spent on one queued stage into the estimate.</summary>
    /// <param name="seconds">The stage's time, in seconds; zero or less measures nothing.</param>
    public void ObserveStage(double seconds) => m_stage.Observe(seconds: seconds, units: 1L);
    /// <summary>Folds the wall time from one group's admission to its first queued stage into the estimate.</summary>
    /// <param name="seconds">The lead, in seconds; zero or less measures nothing.</param>
    public void ObserveLead(double seconds) => m_lead.Observe(seconds: seconds, units: 1L);
    /// <summary>Returns the stages the pool holds, queued or on their way, at which admission waits: what the workers
    /// drain in one lead, at least a round of them and at most <see cref="MostRounds"/>.</summary>
    /// <param name="workers">The pool's workers, at least one.</param>
    /// <returns>The stages.</returns>
    public int Round(int workers) {
        var round = Math.Max(val1: 1, val2: workers);

        return ((int)Math.Clamp(max: (((double)MostRounds) * round), min: round, value: Math.Ceiling(a: (round * (LeadSeconds / StageSeconds)))));
    }
    /// <summary>Returns whether admission waits: the pool's stages, queued and on their way, keep every worker busy for
    /// the lead a newly admitted group takes.</summary>
    /// <param name="stages">The stages queued, and the groups admitted whose first stage has not been queued yet.</param>
    /// <param name="workers">The pool's workers.</param>
    /// <returns><see langword="true"/> when a group admitted now would only wait in the queue.</returns>
    public bool Holds(int stages, int workers) => (stages >= Round(workers: workers));
}
