using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

/// <summary>One produced frame's transport chunks: at most one chunk of each kind, in place, classify and trace order,
/// each following every earlier chunk of its plan. Its reference identity is its frame admission, so a deferred step
/// keeps its chunks and order.</summary>
/// <param name="Place">The placement chunk, or null.</param>
/// <param name="Classify">The partition chunk, or null.</param>
/// <param name="Trace">The trace chunk, or null.</param>
/// <param name="Cost">The step's instruction-visit estimate, at most one submission's cap.</param>
/// <param name="End">The plan's chunk index following the step's last chunk.</param>
internal sealed record SdfIndirectTransportStep(SdfIndirectChunk? Place, SdfIndirectChunk? Classify, SdfIndirectChunk? Trace, long Cost, int End);

public sealed partial class SdfIndirectCache {
    private const int PlaceKind = 0;
    private const int ClassifyKind = 1;
    private const int TraceKind = 2;

    private readonly List<(int Kind, SdfIndirectChunk Chunk)> m_transportChunks = [];
    private readonly HashSet<IrradianceBrickKey> m_planPlaced = [];
    private readonly HashSet<IrradianceBrickKey> m_planClassified = [];

    private int m_transportCursor;
    private int m_lastPlaceChunk = -1;
    private int m_lastClassifyChunk = -1;
    private SdfIndirectTransportStep? m_step;

    /// <summary>Gets the pending plan's transport chunks not yet submitted, including the selected step's.</summary>
    public int PendingTransportChunks => (m_transportChunks.Count - m_transportCursor);
    /// <summary>Gets the selected step's estimated instruction visits, zero without a step.</summary>
    public long TransportStepCost => (m_step?.Cost ?? 0);

    /// <summary>Gets the selected step's chunk of one transport pass.</summary>
    /// <param name="part">The place, classify or trace pass.</param>
    /// <returns>The chunk, or null when the pass has none this step.</returns>
    public SdfIndirectChunk? TransportChunk(string part) => part switch {
        SdfWorldPackage.IndirectPlace => m_step?.Place,
        SdfWorldPackage.IndirectClassify => m_step?.Classify,
        SdfWorldPackage.IndirectTrace => m_step?.Trace,
        _ => null,
    };
    /// <summary>Gets a transport pass's admission unit.</summary>
    /// <param name="part">The place, classify or trace pass.</param>
    /// <returns>The pass's unit.</returns>
    public static SdfIndirectUnits TransportUnits(string part) => part switch {
        SdfWorldPackage.IndirectPlace => SdfIndirectCost.PlaceUnits,
        SdfWorldPackage.IndirectClassify => SdfIndirectCost.ClassifyUnits,
        _ => SdfIndirectCost.TraceUnits,
    };
    /// <summary>The schedule's field-evaluation allowance for one plan: one submission's worth of queries, and never
    /// less than one whole item of each kind, so a field too heavy for a whole item still makes progress in chunks.</summary>
    /// <param name="instructionCount">The complete field's instruction count.</param>
    /// <returns>The plan's evaluation allowance.</returns>
    public static int PlanEvaluations(int instructionCount) => ((int)Math.Max(val1: (SdfIndirectCost.SubmissionCostLimit / Math.Max(val1: 1, val2: instructionCount)),
        val2: ((IrradianceSchedule.PlaceEvaluations + IrradianceSchedule.ClassifyEvaluations) + IrradianceSchedule.TraceEvaluations)));

    private static SdfIndirectUnits UnitsOf(int kind) => kind switch {
        PlaceKind => SdfIndirectCost.PlaceUnits,
        ClassifyKind => SdfIndirectCost.ClassifyUnits,
        _ => SdfIndirectCost.TraceUnits,
    };
    private void BeginTransportChunks(IrradianceFramePlan plan, int instructionCount) {
        ClearTransportChunks();
        m_planPlaced.UnionWith(other: plan.Placed);
        m_planClassified.UnionWith(other: plan.Classified);
        AppendTransportChunks(firstUnits: [0, 0, 0], instructionCount: instructionCount, plan: plan);
        PlanTransportStep();
    }
    // A plan outlives a program upload. Its unsubmitted units are admitted again against the field the next step
    // dispatches, from each kind's first unsubmitted unit, so no step prices an earlier field.
    private void RechunkTransport(int instructionCount) {
        var plan = m_pending!;
        var counts = new[] { plan.Placed.Count, plan.Classified.Count, plan.Traces.Count };
        var firstUnits = new int[3];

        for (var kind = PlaceKind; (kind <= TraceKind); kind++) { firstUnits[kind] = (counts[kind] * UnitsOf(kind: kind).UnitsPerItem); }
        for (var index = m_transportCursor; (index < m_transportChunks.Count); index++) {
            var (kind, chunk) = m_transportChunks[index];

            firstUnits[kind] = Math.Min(val1: firstUnits[kind], val2: ((chunk.ItemFirst * UnitsOf(kind: kind).UnitsPerItem) + chunk.UnitFirst));
        }
        m_transportChunks.RemoveRange(count: (m_transportChunks.Count - m_transportCursor), index: m_transportCursor);
        m_step = null;
        InstructionCount = instructionCount;
        AppendTransportChunks(firstUnits: firstUnits, instructionCount: instructionCount, plan: plan);
    }
    private void AppendTransportChunks(IrradianceFramePlan plan, int[] firstUnits, int instructionCount) {
        if (Add(kind: PlaceKind, count: plan.Placed.Count)) { m_lastPlaceChunk = (m_transportChunks.Count - 1); }
        if (Add(kind: ClassifyKind, count: plan.Classified.Count)) { m_lastClassifyChunk = (m_transportChunks.Count - 1); }
        _ = Add(kind: TraceKind, count: plan.Traces.Count);

        bool Add(int kind, int count) {
            var chunks = SdfIndirectCost.Admit(count: count, firstUnit: firstUnits[kind], instructionCount: instructionCount, units: UnitsOf(kind: kind));

            foreach (var chunk in chunks) { m_transportChunks.Add(item: (kind, chunk)); }
            return (chunks.Count != 0);
        }
    }
    // Selects the next frame's chunks once the previous step has been submitted. The brick table this step uploads
    // lists newly placed bricks only once their placement is complete by the step's place pass, and flags their
    // partitions only once the step's classify pass completes them; a placement chunk still finds its own brick's
    // record by slot, outside the sorted directory every reader searches.
    private void PlanTransportStep() {
        if ((m_pending is null) || (m_step is not null) || Frozen) { return; }
        var picks = new SdfIndirectChunk?[3];
        var cost = 0L;
        var kind = -1;
        var end = m_transportCursor;

        for (; (end < m_transportChunks.Count); end++) {
            var (next, chunk) = m_transportChunks[end];
            var price = UnitsOf(kind: next).CostOf(chunk: chunk, instructionCount: InstructionCount);

            if ((next <= kind) || ((end > m_transportCursor) && (price > (SdfIndirectCost.SubmissionCostLimit - cost)))) { break; }
            picks[next] = chunk;
            cost += price;
            kind = next;
        }
        m_step = new SdfIndirectTransportStep(Classify: picks[ClassifyKind], Cost: cost, End: end, Place: picks[PlaceKind], Trace: picks[TraceKind]);
        WriteBrickTable(flagPlanned: (m_lastClassifyChunk < end), listPlanned: (m_lastPlaceChunk < end));
    }
    private bool SubmitTransportStep(out bool completes) {
        completes = false;
        if (m_step is not { } step) { return false; }
        m_transportCursor = step.End;
        m_step = null;
        completes = (m_transportCursor >= m_transportChunks.Count);
        return true;
    }
    private void ClearTransportChunks() {
        m_transportChunks.Clear();
        m_planPlaced.Clear();
        m_planClassified.Clear();
        m_transportCursor = 0;
        m_lastPlaceChunk = -1;
        m_lastClassifyChunk = -1;
        m_step = null;
    }
    private void WriteBrickTable(bool listPlanned, bool flagPlanned) {
        ClearBricks();
        foreach (var (key, slot) in m_slots) {
            if (!m_placed.Contains(item: key) || (!listPlanned && m_planPlaced.Contains(item: key))) { continue; }
            var classified = (m_schedule.IsClassified(key: key) && (flagPlanned || !m_planClassified.Contains(item: key)));

            Write(m_bricks, slot, key.X, key.Y, key.Z, key.Level | (classified ? SdfIndirectLayout.BrickClassified : 0));
        }
        SdfIndirectBrickTable.Index(m_bricks, Layout.BrickCapacity);
        if (!listPlanned) {
            foreach (var key in m_planPlaced) { Write(m_bricks, m_slots[key], key.X, key.Y, key.Z, key.Level); }
        }
        Regions[0].Write(bytes: m_bricks, offset: 0);
    }
}
