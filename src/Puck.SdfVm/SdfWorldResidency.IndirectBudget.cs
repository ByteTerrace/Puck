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
            return ((cache.ShadeChunk is not { } shade) || IndirectFrameBudget.TryAdmitDevice(chunk: shade, cost: cache.ShadeChunkCost));
        }
        return IndirectFrameBudget.TryAdmitDevice(chunk: (cache.TransportBatch ?? m_emptyTransport), cost: cache.TransportStepCost);
    }
    internal bool AdmitLightView() => IndirectFrameBudget.TryAdmit(chunk: IndirectLightViews,
        cost: SdfIndirectCost.EstimateCost(IndirectLightViews.EstimatedQueries, Frame!.Program.InstructionCount));
    internal int AdmitReceiverProofs(SdfIndirectCache cache) {
        var count = (cache.Frozen ? 0 : cache.ReceiverProofBudget);

        return (IndirectFrameBudget.TryAdmit(chunk: m_receiverBudget, cost: ReceiverCost(cache: cache, count: count)) ? count : 0);
    }
    internal void LogReceiverProofs(SdfIndirectCache cache) {
        var count = AdmitReceiverProofs(cache: cache);
        var logged = (m_receiverBudgetLogged ? 0 : count);
        var queries = (((long)logged) * SdfIndirectCost.ReceiverQueries);

        LogIndirectSubmission("receiver-proofs", queries, cache.InstructionCount, cache.Frame,
            (ReceiverCost(cache: cache, count: logged) - SdfIndirectCost.EstimateCost(queries: queries, instructionCount: cache.InstructionCount)));
        m_receiverBudgetLogged = true;
    }

    // The shared allowance at the admitted receiver's measured price.
    private static long ReceiverCost(SdfIndirectCache cache, int count) => checked((((long)count) * cache.ReceiverUnits.ItemCost(instructionCount: cache.InstructionCount)));
}
