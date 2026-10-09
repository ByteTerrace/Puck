namespace Puck.SignedDistance.Illumination;

/// <summary>One submission of a finite lighting sweep. A batch never crosses a level boundary.</summary>
/// <param name="Sweep">Zero for direct light from zero, then the feedback sweep ordinal.</param>
/// <param name="ReadGeneration">The preceding complete sweep's generation; unused by the direct sweep.</param>
/// <param name="WriteGeneration">The other generation, including already submitted coarser levels of this sweep.</param>
/// <param name="Level">The level every probe in this batch belongs to.</param>
/// <param name="Probes">The probes in the exact submitted order.</param>
/// <param name="CompletesSweep">Whether committing this batch publishes the entire sweep.</param>
public sealed record IrradianceSolveBatch(int Sweep, int ReadGeneration, int WriteGeneration, int Level,
    IReadOnlyList<IrradianceProbeKey> Probes, bool CompletesSweep);
/// <summary>The CPU reference and residency's shared finite solve order. Coarser levels finish before their finer
/// readers. Planning retains the same immutable batch until submission commits it; only a whole sweep publishes.</summary>
public sealed class IrradianceSolveSchedule {
    private readonly IrradianceProbeKey[][] m_levels;
    private readonly int m_budget;
    private readonly int m_bounces;
    private readonly int m_firstWrite;

    private IrradianceSolveBatch? m_pending;
    private int m_level;
    private int m_offset;

    /// <summary>Captures one solve's probe inventory and submission allowance.</summary>
    /// <param name="levels">Probe keys by level, finest first; caller order within a level is retained.</param>
    /// <param name="bounces">The finite feedback sweeps after the direct sweep.</param>
    /// <param name="probeBudget">The maximum probes in one submission.</param>
    /// <param name="publishedGeneration">The generation readers currently see, or minus one for a cold cache.</param>
    /// <exception cref="ArgumentNullException"><paramref name="levels"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound or generation is outside its domain.</exception>
    /// <exception cref="ArgumentException">There are no levels, or a probe names a different level.</exception>
    public IrradianceSolveSchedule(IReadOnlyList<IReadOnlyList<IrradianceProbeKey>> levels, int bounces, int probeBudget, int publishedGeneration = -1) {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentOutOfRangeException.ThrowIfNegative(bounces);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(probeBudget);
        ArgumentOutOfRangeException.ThrowIfLessThan(publishedGeneration, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(publishedGeneration, 1);
        if (levels.Count == 0) { throw new ArgumentException(message: "A solve has at least one level.", paramName: nameof(levels)); }
        m_levels = new IrradianceProbeKey[levels.Count][];
        for (var level = 0; (level < levels.Count); level++) {
            m_levels[level] = levels[level].ToArray();
            if (m_levels[level].Any(predicate: key => (key.Level != level))) { throw new ArgumentException(message: "A probe must belong to its declared level.", paramName: nameof(levels)); }
        }
        m_budget = probeBudget;
        m_bounces = bounces;
        m_firstWrite = ((publishedGeneration < 0) ? 0 : publishedGeneration ^ 1);
        PublishedGeneration = publishedGeneration;
        m_level = (levels.Count - 1);
    }

    /// <summary>Gets the generation visible to readers; minus one before the first complete sweep.</summary>
    public int PublishedGeneration { get; private set; }
    /// <summary>Gets the complete sweeps this solve has published.</summary>
    public int CompletedSweeps { get; private set; }
    /// <summary>Gets whether direct light and every requested feedback sweep have been submitted.</summary>
    public bool IsComplete => (CompletedSweeps > m_bounces);
    /// <summary>Gets the probe shadings the solve still owes: the rest of the current sweep and every later sweep.</summary>
    public long RemainingProbes {
        get {
            if (IsComplete) { return 0; }
            var total = 0L;
            var done = (long)m_offset;

            for (var level = 0; (level < m_levels.Length); level++) {
                total += m_levels[level].Length;
                if (level > m_level) { done += m_levels[level].Length; }
            }
            return ((((m_bounces + 1L) - CompletedSweeps) * total) - done);
        }
    }

    /// <summary>Returns the pending batch, or null after the finite solve completes. Empty inventories still publish
    /// an empty sweep through <see cref="Submitted"/>, without a GPU dispatch.</summary>
    /// <param name="probeBudget">This batch's probe allowance, at most the solve's; absent uses the solve's. A batch
    /// reads only the preceding complete sweep and earlier batches of coarser levels, so its size never changes what
    /// any probe publishes.</param>
    /// <returns>The retained batch. Its probe inventory cannot be changed through this API.</returns>
    public IrradianceSolveBatch? Plan(int? probeBudget = null) {
        if (IsComplete || (m_pending is not null)) { return m_pending; }
        while ((m_level > 0) && (m_offset == m_levels[m_level].Length)) { m_level--; m_offset = 0; }
        var budget = Math.Clamp(value: (probeBudget ?? m_budget), min: 1, max: m_budget);
        var count = Math.Min(val1: budget, val2: (m_levels[m_level].Length - m_offset));
        var probes = Array.AsReadOnly(array: m_levels[m_level].AsSpan(length: count, start: m_offset).ToArray());
        var last = ((m_offset + count) == m_levels[m_level].Length);

        for (var level = (m_level - 1); ((level >= 0) && last); level--) { last = (m_levels[level].Length == 0); }
        var write = m_firstWrite ^ (CompletedSweeps & 1);

        m_pending = new IrradianceSolveBatch(CompletedSweeps, write ^ 1, write, m_level, probes, last);
        return m_pending;
    }
    /// <summary>Commits only the retained batch after its work is successfully submitted.</summary>
    public void Submitted() {
        if (m_pending is not { } batch) { return; }
        m_offset += batch.Probes.Count;
        if (batch.CompletesSweep) {
            PublishedGeneration = batch.WriteGeneration;
            CompletedSweeps++;
            m_level = (m_levels.Length - 1);
            m_offset = 0;
        }
        m_pending = null;
    }
}
