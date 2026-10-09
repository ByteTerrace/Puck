using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

// Admission prices each kind's unit by the field instruction visits the kernels counted for it. Every indirect kernel
// adds its lanes' visits and units to monotonic counters in the cache buffer (SdfIndirectLayout.Cost*); the transport
// recorder copies them after each trace dispatch, and the residency's produced-frame boundary reads every completed
// copy, in submission order, as differences. A kind is priced at the largest of its last PriceWindow measurements,
// never above the conservative price (every query against the complete program), which still prices a kind no
// submission has measured yet. Prices change only at a produced-frame boundary: a pending plan's unsubmitted units are
// then admitted again at the new prices from their first unsubmitted unit, so no chunk is recorded at two prices.
public sealed partial class SdfIndirectCache {
    /// <summary>The measurements a kind's price is the largest of.</summary>
    public const int PriceWindow = 4;

    private readonly long[] m_prices = new long[(SdfIndirectLayout.CostKinds * PriceWindow)];
    private readonly int[] m_priceCursor = new int[SdfIndirectLayout.CostKinds];
    private readonly uint[] m_costBaseline = new uint[SdfIndirectLayout.CostWords];
    private readonly List<SdfIndirectCostReadback> m_costReadbacks = [];

    private long m_costGeneration = 1;
    private long m_baselineGeneration;
    private long m_baselineSequence;
    private long m_transportPrices;

    /// <summary>Gets the revision of the measured prices, advanced whenever a measurement changes one.</summary>
    public long PriceRevision { get; private set; }
    /// <summary>Gets the counter generation: every clear of the cache buffer restarts the device counters.</summary>
    public long CostGeneration => m_costGeneration;

    /// <summary>Gets a kind's measured field instruction visits per unit, or null before a submission measured it.</summary>
    /// <param name="kind">The measured kind (<see cref="SdfIndirectLayout.CostPlace"/> through <see cref="SdfIndirectLayout.CostReceiver"/>).</param>
    /// <returns>The largest of the kind's recent measurements, or null.</returns>
    public long? MeasuredFieldCost(int kind) {
        ArgumentOutOfRangeException.ThrowIfNegative(kind);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(kind, SdfIndirectLayout.CostKinds);
        var largest = 0L;

        for (var entry = 0; (entry < PriceWindow); entry++) { largest = Math.Max(val1: largest, val2: m_prices[((kind * PriceWindow) + entry)]); }
        return ((largest == 0) ? null : largest);
    }
    /// <summary>Gets a transport pass's admission unit at the current prices.</summary>
    /// <param name="part">The place, classify or trace pass.</param>
    /// <returns>The pass's unit.</returns>
    public SdfIndirectUnits TransportUnits(string part) => part switch {
        SdfWorldPackage.IndirectPlace => UnitsOf(kind: PlaceKind),
        SdfWorldPackage.IndirectClassify => UnitsOf(kind: ClassifyKind),
        _ => UnitsOf(kind: TraceKind),
    };
    /// <summary>Gets one admitted receiver's unit at the current prices.</summary>
    public SdfIndirectUnits ReceiverUnits => (SdfIndirectCost.ReceiverUnits with { MeasuredFieldCost = MeasuredFieldCost(kind: SdfIndirectLayout.CostReceiver) });
    /// <summary>Prices one plan: one submission's allowance, and never less than one whole item of each kind, so a field
    /// too heavy for a whole item still makes progress in chunks.</summary>
    /// <param name="instructionCount">The complete field's instruction count.</param>
    /// <returns>The plan's prices.</returns>
    public IrradiancePlanPrices PlanPrices(int instructionCount) {
        var place = UnitsOf(kind: PlaceKind).ItemCost(instructionCount: instructionCount);
        var classify = UnitsOf(kind: ClassifyKind).ItemCost(instructionCount: instructionCount);
        var trace = UnitsOf(kind: TraceKind).ItemCost(instructionCount: instructionCount);

        return new IrradiancePlanPrices(Allowance: Math.Max(val1: SdfIndirectCost.SubmissionCostLimit, val2: checked(((place + classify) + trace))),
            Classify: Math.Max(val1: 1L, val2: classify), Place: Math.Max(val1: 1L, val2: place), Trace: Math.Max(val1: 1L, val2: trace));
    }
    /// <summary>Reads every completed counter copy at a produced-frame boundary. Call on the rendering owner thread
    /// before the frame plans.</summary>
    public void BeginFrame() {
        foreach (var readback in m_costReadbacks) { readback.Poll(cache: this); }
    }

    internal void AttachCostReadback(SdfIndirectCostReadback readback) => m_costReadbacks.Add(item: readback);
    internal void DetachCostReadback(SdfIndirectCostReadback readback) => m_costReadbacks.Remove(item: readback);
    /// <summary>Records one completed copy of the device's cost counters. Copies of a cleared buffer's earlier generation
    /// and copies older than the baseline are discarded; the first copy of a generation is its baseline, and each later
    /// copy prices every kind whose units advanced at the visits per unit since the previous copy.</summary>
    /// <param name="generation">The <see cref="CostGeneration"/> the copy was recorded under.</param>
    /// <param name="sequence">The copy's submission order.</param>
    /// <param name="counters">The <see cref="SdfIndirectLayout.CostWords"/> counter words.</param>
    public void ObserveCost(long generation, long sequence, ReadOnlySpan<uint> counters) {
        if ((generation != m_costGeneration) || ((generation == m_baselineGeneration) && (sequence <= m_baselineSequence))) { return; }
        if (generation == m_baselineGeneration) {
            var changed = false;

            for (var kind = 0; (kind < SdfIndirectLayout.CostKinds); kind++) {
                var visits = unchecked(counters[(2 * kind)] - m_costBaseline[(2 * kind)]);
                var units = unchecked(counters[((2 * kind) + 1)] - m_costBaseline[((2 * kind) + 1)]);

                if (units == 0) { continue; }
                var price = Math.Max(val1: 1L, val2: ((((long)visits) + units) - 1) / units);
                var slot = ((kind * PriceWindow) + m_priceCursor[kind]);

                changed |= (m_prices[slot] != price);
                m_prices[slot] = price;
                m_priceCursor[kind] = ((m_priceCursor[kind] + 1) % PriceWindow);
            }
            if (changed) { PriceRevision++; }
        }
        counters[..SdfIndirectLayout.CostWords].CopyTo(destination: m_costBaseline);
        m_baselineGeneration = generation;
        m_baselineSequence = sequence;
    }

    private SdfIndirectUnits UnitsOf(int kind) => kind switch {
        PlaceKind => SdfIndirectCost.PlaceUnits with { MeasuredFieldCost = MeasuredFieldCost(kind: SdfIndirectLayout.CostPlace) },
        ClassifyKind => SdfIndirectCost.ClassifyUnits with { MeasuredFieldCost = MeasuredFieldCost(kind: SdfIndirectLayout.CostClassify) },
        _ => SdfIndirectCost.TraceUnits with { MeasuredFieldCost = MeasuredFieldCost(kind: SdfIndirectLayout.CostTrace) },
    };
    private SdfIndirectUnits ShadeUnitsOf(SdfFrame frame) =>
        (SdfIndirectCost.ShadeUnits(frame: frame, layout: Layout) with { MeasuredFieldCost = MeasuredFieldCost(kind: SdfIndirectLayout.CostShade) });
    // Every clear of the cache buffer zeroes the device counters; the next copy restarts the baseline.
    private void ClearCostCounters() => m_costGeneration++;
}
