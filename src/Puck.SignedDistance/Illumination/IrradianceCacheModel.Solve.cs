namespace Puck.SignedDistance.Illumination;

public sealed partial class IrradianceCacheModel {
    private IrradianceSolveSchedule? m_solve;

    /// <summary>Gets whether every batch of the current finite lighting solve has completed.</summary>
    public bool SolveComplete => (m_solve?.IsComplete ?? false);

    /// <summary>Captures the traced probe order for a finite solve that may pause between whole batches.
    /// Geometry and source functions must remain unchanged until it completes.</summary>
    /// <param name="bounces">The feedback sweeps after the direct sweep.</param>
    /// <param name="reverseOrder">Whether each level's probes are visited in reverse order.</param>
    /// <param name="probesPerStep">The probe allowance per batch; zero admits a whole level.</param>
    /// <exception cref="InvalidOperationException">A preceding finite solve is incomplete.</exception>
    public void BeginSolve(int bounces, bool reverseOrder = false, int probesPerStep = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(bounces);
        if (m_solve is { IsComplete: false }) { throw new InvalidOperationException(message: "The preceding finite lighting solve must complete first."); }
        m_published = -1;
        var levels = new IReadOnlyList<IrradianceProbeKey>[m_levels.Count];

        for (var level = 0; (level < levels.Length); level++) {
            var keys = ProbesOf(level: level).Where(predicate: static probe => (probe.Hits is not null)).Select(selector: static probe => probe.Key).ToArray();

            if (reverseOrder) { Array.Reverse(array: keys); }
            levels[level] = keys;
        }
        m_solve = new IrradianceSolveSchedule(levels, bounces, ((probesPerStep > 0) ? probesPerStep : int.MaxValue));
    }
    /// <summary>Returns the retained next batch, or null before a solve starts or after it completes.</summary>
    public IrradianceSolveBatch? PlanSolve() => m_solve?.Plan();
    /// <summary>Evaluates and commits the retained batch. Only its final whole-sweep chunk publishes the bank.</summary>
    public void SubmittedSolve() {
        if (m_solve?.Plan() is not { } batch) { return; }
        foreach (var key in batch.Probes) { ShadeProbe(feedback: (batch.Sweep > 0), probe: m_probes[key], write: batch.WriteGeneration); }
        m_solve.Submitted();
        if (batch.CompletesSweep) { m_published = m_solve.PublishedGeneration; SweepsPublished++; }
    }
}
