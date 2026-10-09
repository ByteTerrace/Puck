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
            return ((cache.ShadeChunk is not { } shade) || IndirectFrameBudget.TryAdmit(chunk: shade, cost: cache.ShadeChunkCost));
        }
        return IndirectFrameBudget.TryAdmit(chunk: (cache.TransportBatch ?? m_emptyTransport), cost: cache.TransportStepCost);
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
