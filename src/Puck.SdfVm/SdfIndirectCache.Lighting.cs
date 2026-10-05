using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

public sealed partial class SdfIndirectCache {
    private IrradianceSolveSchedule? m_solve;
    private IrradianceSolveBatch? m_shade;
    private uint m_nextLightingStamp;
    private uint m_writeLightingStamp;
    private readonly byte[] m_shadeUpdates;

    /// <summary>Gets the submitted generation visible to receivers, or minus one before a complete sweep.</summary>
    public int PublishedGeneration { get; private set; } = -1;
    /// <summary>Gets the exact stamp every probe of the visible sweep carries; zero means no publication.</summary>
    public uint PublishedStamp { get; private set; }
    /// <summary>Gets the exact preceding whole bank's stamp for the current published source. Zero means that
    /// source has no preceding complete sweep; a different source never supplies Near feedback.</summary>
    public uint PreviousPublishedStamp { get; private set; }
    /// <summary>Gets the completed sweep depth of the visible publication, retained while a later solve starts;
    /// zero means no published lighting. Its first sweep contains direct sources and later sweeps add feedback.</summary>
    public int PublishedSweeps { get; private set; }
    /// <summary>Gets the complete sweeps of the current finite solve.</summary>
    public int CompletedSweeps => m_solve?.CompletedSweeps ?? 0;
    /// <summary>Gets whether the current direct and feedback sweeps have all been submitted.</summary>
    public bool LightingComplete => m_solve?.IsComplete ?? false;
    /// <summary>Gets the retained shade batch's probe count.</summary>
    public int ShadeCount => m_shade?.Probes.Count ?? 0;
    /// <summary>Gets the immutable actual source of the current finite solve, absent before its first admission.</summary>
    public SdfIndirectLightingSnapshot? LightingSource => HasLightingCycle && Lighting?.AwaitingEnvironment != true ? Lighting?.Snapshot : null;
    /// <summary>Gets the source that produced the visible complete sweep. While a later solve writes its other bank,
    /// this keeps the preceding source instead of relabeling the still visible irradiance.</summary>
    public SdfIndirectLightingSnapshot? PublishedLightingSource { get; private set; }

    internal bool CanBeginLighting => IsComplete && !Frozen && (m_solve is null || m_solve.IsComplete);
    internal bool HasLightingCycle => m_solve is not null;
    internal IrradianceSolveBatch? ShadeBatch => m_shade;
    internal uint WriteLightingStamp => m_writeLightingStamp;
    internal SdfWorldTables.PinnedIndirectLighting? Lighting { get; set; }

    /// <summary>Starts a finite direct and feedback solve after its source tables have been pinned. Every allocated
    /// probe participates; its shader class decides whether it contributes, without a host classification guess.</summary>
    /// <exception cref="InvalidOperationException">Transport or a prior solve is incomplete, or admission is frozen.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The captured bounce request lies outside the supported tier limits.</exception>
    public void BeginLighting() {
        if (!CanBeginLighting) { throw new InvalidOperationException("An indirect solve starts only after transport completes and the previous solve finishes."); }
        var levels = new IReadOnlyList<IrradianceProbeKey>[Layout.Levels.Count];
        for (var level = 0; level < levels.Length; level++) {
            var probes = new List<IrradianceProbeKey>();
            foreach (var brick in m_slots.Keys.Where(key => key.Level == level).Order()) {
                for (var z = 0; z < 4; z++) {
                    for (var y = 0; y < 4; y++) {
                        for (var x = 0; x < 4; x++) {
                            probes.Add(new IrradianceProbeKey(level, brick.X * 4 + x, brick.Y * 4 + y, brick.Z * 4 + z));
                        }
                    }
                }
            }
            levels[level] = probes;
        }
        var frame = Lighting?.Frame;
        var bounces = frame?.IndirectBounces ?? Layout.BounceLimit;
        ArgumentOutOfRangeException.ThrowIfNegative(bounces);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bounces, SdfIndirectLayout.MaximumBounces);
        if (frame is not null && ((frame.IndirectSources & SdfIndirectSources.Feedback) == 0 || frame.IndirectGains.Feedback == 0f)) { bounces = 0; }
        m_solve = new IrradianceSolveSchedule(levels, Math.Min(bounces, Layout.BounceLimit), Layout.ShadeBudget, PublishedGeneration);
        m_writeLightingStamp = checked(++m_nextLightingStamp);
        Count(name: "indirect.sweeps.restarted", amount: 1);
    }

    /// <summary>Retains the next bounded shade batch until submission. A frozen cache admits no new batch.</summary>
    public void PlanLighting() {
        if (m_shade is not null || Frozen || m_solve is null || m_changedGeometry is not null) { return; }
        m_shade = m_solve.Plan();
        if (m_shade is not { } batch) { return; }
        for (var row = 0; row < batch.Probes.Count; row++) {
            Write(m_shadeUpdates, row, ProbeSlot(batch.Probes[row]), 0, batch.Level, 0);
        }
        Regions[4].Write(offset: 0, bytes: m_shadeUpdates.AsSpan(0, batch.Probes.Count * 16));
    }

    /// <summary>Commits the retained shade batch after its GPU submission; only a whole sweep moves the visible bank.</summary>
    public void SubmittedLighting() {
        if (m_shade is not { } batch) { return; }
        m_solve!.Submitted();
        if (batch.CompletesSweep) {
            PreviousPublishedStamp = ReferenceEquals(PublishedLightingSource, LightingSource) ? PublishedStamp : 0u;
            PublishedGeneration = batch.WriteGeneration;
            PublishedStamp = m_writeLightingStamp;
            PublishedSweeps = m_solve.CompletedSweeps;
            PublishedLightingSource = LightingSource;
            LightingPublication++;
            Count(name: "indirect.sweeps.completed", amount: 1);
            if (!m_solve.IsComplete) { m_writeLightingStamp = checked(++m_nextLightingStamp); }
        }
        m_shade = null;
    }

    // An evicted slot or changed transport cannot retain a lighting publication whose source it no longer owns.
    private void InvalidateLighting() {
        m_solve = null;
        m_shade = null;
        PublishedGeneration = -1;
        PublishedStamp = 0;
        PreviousPublishedStamp = 0;
        PublishedSweeps = 0;
        PublishedLightingSource = null;
        LightingPublication++;
    }

    // Capture restarts radiance from direct sources without evicting any reusable transport. Publication stamps
    // prevent either old bank from being read before the new whole sweep overwrites and publishes its payload.
    internal void ResetLightingForCapture() {
        InvalidateLighting();
        Lighting?.InvalidateSource();
        m_completedLighting = default;
    }
}
