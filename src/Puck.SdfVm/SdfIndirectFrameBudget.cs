namespace Puck.SdfVm;

/// <summary>One residency's produced-frame admission. Whole chunks retain their identity and order when deferred.</summary>
public sealed class SdfIndirectFrameBudget {
    private readonly HashSet<object> m_admitted = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>One cache-producing submission plus one submission's allowance for auxiliary depth and receiver work.</summary>
    public const long CostLimit = (SdfIndirectCost.SubmissionCostLimit + SdfIndirectCost.SubmissionCostLimit);

    /// <summary>Gets the produced-frame ordinal, independent of transport or lighting publication.</summary>
    public long Frame { get; private set; }
    /// <summary>Gets the total reserved instruction-visit estimate in this frame.</summary>
    public long Cost { get; private set; }

    /// <summary>Renews admission at a produced-frame boundary; no pending solve cursor is changed.</summary>
    public void BeginFrame() {
        Frame = checked((Frame + 1));
        Cost = 0;
        m_admitted.Clear();
    }
    /// <summary>Reserves a whole chunk once by reference identity. Repeated passes of that chunk share admission.
    /// A refused chunk stays with its owner until a later frame admits it.</summary>
    /// <param name="chunk">The immutable pending chunk, or the owner of one shared frame allowance.</param>
    /// <param name="cost">Its complete conservative instruction-visit estimate.</param>
    /// <returns>Whether this frame admits the chunk.</returns>
    public bool TryAdmit(object chunk, long cost) {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentOutOfRangeException.ThrowIfNegative(cost);
        if (m_admitted.Contains(item: chunk)) { return true; }
        if (cost > (CostLimit - Cost)) { return false; }
        Cost += cost;
        m_admitted.Add(item: chunk);
        return true;
    }
}
