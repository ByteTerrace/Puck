using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldResidency {
    private readonly object m_receiverBudget = new();
    private readonly object m_emptyTransport = new();

    private bool m_receiverBudgetLogged;

    /// <summary>Gets this residency's aggregate admission for the actual produced frame.</summary>
    public SdfIndirectFrameBudget IndirectFrameBudget { get; } = new();

    internal bool AdmitIndirect(string part, SdfIndirectCache cache) {
        if (part == SdfWorldPackage.IndirectShade) {
            if (cache.ShadeBatch is not { } shade) { return true; }
            var source = cache.Lighting!.Frame;
            var cost = (SdfIndirectCost.EstimateCost((((long)shade.Probes.Count) * SdfIndirectCost.ShadeQueries(cache.Layout, source)), source.Program.InstructionCount)
                + (shade.Probes.Count * SdfIndirectCost.ShadeCacheCost(layout: cache.Layout)));

            return IndirectFrameBudget.TryAdmit(chunk: shade, cost: cost);
        }
        var queries = (((((long)cache.PlaceCount) * SdfIndirectCost.PlaceQueries)
            + (((long)cache.ClassifyCount) * SdfIndirectCost.ClassifyQueries)) + (((long)cache.TraceCount) * SdfIndirectCost.TraceQueries));

        return IndirectFrameBudget.TryAdmit(chunk: (cache.TransportBatch ?? m_emptyTransport), cost: SdfIndirectCost.EstimateCost(queries, cache.InstructionCount));
    }
    internal bool AdmitLightView() => IndirectFrameBudget.TryAdmit(chunk: IndirectLightViews,
        cost: SdfIndirectCost.EstimateCost(IndirectLightViews.EstimatedQueries, Frame!.Program.InstructionCount));
    internal int AdmitReceiverProofs(SdfIndirectCache cache) {
        var count = (cache.Frozen ? 0 : cache.ReceiverProofBudget);

        return (IndirectFrameBudget.TryAdmit(chunk: m_receiverBudget, cost: SdfIndirectCost.EstimateCost((((long)count) * SdfIndirectCost.ReceiverQueries), cache.InstructionCount)) ? count : 0);
    }
    internal void LogReceiverProofs(SdfIndirectCache cache) {
        var count = AdmitReceiverProofs(cache: cache);

        LogIndirectSubmission("receiver-proofs", (((long)(m_receiverBudgetLogged ? 0 : count)) * SdfIndirectCost.ReceiverQueries), cache.InstructionCount, cache.Frame);
        m_receiverBudgetLogged = true;
    }
}
