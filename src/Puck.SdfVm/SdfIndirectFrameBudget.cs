namespace Puck.SdfVm;

/// <summary>One residency's produced-frame admission. Whole chunks retain their identity and order when deferred.
/// Two allowances renew each frame: the device slice the cache's transport and lighting solve share
/// (<see cref="SdfIndirectCache.FrameCost"/>), and one submission's auxiliary allowance for light views and receiver
/// proofs.</summary>
public sealed class SdfIndirectFrameBudget {
    private readonly HashSet<object> m_admitted = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>The auxiliary allowance for light views and receiver proofs: one submission's cap.</summary>
    public const long AuxiliaryLimit = SdfIndirectCost.SubmissionCostLimit;

    /// <summary>Gets the produced-frame ordinal, independent of transport or lighting publication.</summary>
    public long Frame { get; private set; }
    /// <summary>Gets the total reserved instruction-visit estimate in this frame, both allowances together.</summary>
    public long Cost => (DeviceCost + AuxiliaryCost);

    /// <summary>Gets the device slice this frame admits transport and lighting chunks within.</summary>
    public long DeviceLimit { get; private set; } = SdfIndirectCost.SubmissionCostLimit;

    /// <summary>Gets the transport and lighting visits reserved in this frame.</summary>
    public long DeviceCost { get; private set; }
    /// <summary>Gets the light-view and receiver visits reserved in this frame.</summary>
    public long AuxiliaryCost { get; private set; }

    /// <summary>Renews admission at a produced-frame boundary; no pending solve cursor is changed.</summary>
    /// <param name="deviceLimit">The frame's device slice for transport and lighting.</param>
    public void BeginFrame(long deviceLimit = SdfIndirectCost.SubmissionCostLimit) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceLimit);
        Frame = checked((Frame + 1));
        DeviceLimit = deviceLimit;
        DeviceCost = 0;
        AuxiliaryCost = 0;
        m_admitted.Clear();
    }
    /// <summary>Reserves a transport or lighting chunk within the device slice, once by reference identity. The frame's
    /// first device chunk is admitted whatever its cost, so an indivisible unit larger than the slice still advances, one
    /// chunk a frame; a later chunk waits for a frame with room. A refused chunk stays with its owner.</summary>
    /// <param name="chunk">The immutable pending chunk.</param>
    /// <param name="cost">Its complete measured or conservative instruction-visit estimate.</param>
    /// <returns>Whether this frame admits the chunk.</returns>
    public bool TryAdmitDevice(object chunk, long cost) {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentOutOfRangeException.ThrowIfNegative(cost);
        if (m_admitted.Contains(item: chunk)) { return true; }
        if ((DeviceCost > 0) && (cost > (DeviceLimit - DeviceCost))) { return false; }
        DeviceCost = checked((DeviceCost + cost));
        m_admitted.Add(item: chunk);
        return true;
    }
    /// <summary>Reserves a light-view or receiver allowance within the auxiliary allowance, once by reference identity.
    /// Repeated passes of that owner share admission. A refused owner stays with its owner until a later frame admits it.</summary>
    /// <param name="chunk">The owner of one shared frame allowance.</param>
    /// <param name="cost">Its complete conservative instruction-visit estimate.</param>
    /// <returns>Whether this frame admits the allowance.</returns>
    public bool TryAdmit(object chunk, long cost) {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentOutOfRangeException.ThrowIfNegative(cost);
        if (m_admitted.Contains(item: chunk)) { return true; }
        if (cost > (AuxiliaryLimit - AuxiliaryCost)) { return false; }
        AuxiliaryCost += cost;
        m_admitted.Add(item: chunk);
        return true;
    }
}
