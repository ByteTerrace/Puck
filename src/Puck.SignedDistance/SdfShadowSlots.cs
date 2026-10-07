namespace Puck.SignedDistance;

/// <summary>A frame's stable shadow owners and active incoming handoffs, bounded by its configured policy.</summary>
public sealed class SdfShadowSlots {
    /// <summary>The number of stable visibility bytes in the visibility record.</summary>
    public const int MaxSlots = 4;
    /// <summary>The most incoming visibility channels the frame can reserve.</summary>
    public const int MaxFadeSlots = 2;

    private readonly int[] m_slots = [-1, -1, -1, -1];
    private readonly string?[] m_owners = new string?[MaxSlots];
    private readonly string?[] m_incomingOwners = new string?[MaxFadeSlots];
    private readonly SdfShadowHandoff[] m_handoffs = new SdfShadowHandoff[MaxFadeSlots];

    /// <summary>Gets the configured stable-slot capacity K.</summary>
    public int SlotCount { get; private set; }
    /// <summary>Gets the configured incoming capacity F, independent of active crossings.</summary>
    public int FadeCapacity { get; private set; }
    /// <summary>Gets the active incoming march count, at most <see cref="FadeCapacity"/>.</summary>
    public int FadeCount { get; private set; }
    /// <summary>Gets only the active handoff controls, in incoming-channel order.</summary>
    public ReadOnlySpan<SdfShadowHandoff> Handoffs => m_handoffs.AsSpan(start: 0, length: FadeCount);

    /// <summary>Gets a stable slot's light index, or -1 for a vacant or unconfigured slot.</summary>
    /// <param name="slot">The stable slot, from zero through <see cref="MaxSlots"/> minus one.</param>
    /// <returns>The light index.</returns>
    public int this[int slot] => m_slots[slot];

    /// <summary>Returns the stable light name carried by a slot, or null for an unnamed frame light.</summary>
    /// <param name="slot">The stable slot.</param>
    /// <returns>The owner name. Unnamed slots cannot reuse shadow history.</returns>
    public string? Owner(int slot) => m_owners[slot];
    /// <summary>Returns an incoming channel's exact light name, or null when its identity was not supplied.</summary>
    /// <param name="channel">The incoming channel.</param>
    /// <returns>The owner name; an unnamed channel cannot reuse a light depth map.</returns>
    public string? IncomingOwner(int channel) => m_incomingOwners[channel];
    /// <summary>Publishes a handoff's incoming identity independently of the mutable light-table index.</summary>
    /// <param name="channel">An active incoming channel.</param>
    /// <param name="owner">The exact light name, or null for an unnamed light.</param>
    public void SetIncomingOwner(int channel, string? owner) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: channel, other: FadeCount);
        m_incomingOwners[channel] = owner;
    }
    /// <summary>Sets the stable name independently of its current light-table index.</summary>
    /// <param name="slot">The configured stable slot.</param>
    /// <param name="owner">The exact light name, or null for an unnamed frame light.</param>
    public void SetOwner(int slot, string? owner) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, SlotCount);
        m_owners[slot] = owner;
    }
    /// <summary>Sets the policy and clears the preceding frame's owners and active handoffs.</summary>
    /// <param name="slots">The stable capacity K.</param>
    /// <param name="fadeCapacity">The incoming capacity F.</param>
    /// <exception cref="ArgumentOutOfRangeException">A capacity is outside its supported range.</exception>
    public void Configure(int slots, int fadeCapacity) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: slots);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: slots, other: MaxSlots);
        ArgumentOutOfRangeException.ThrowIfNegative(value: fadeCapacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: fadeCapacity, other: MaxFadeSlots);
        SlotCount = slots;
        FadeCapacity = fadeCapacity;
        FadeCount = 0;
        Array.Fill(array: m_slots, value: -1);
        Array.Clear(array: m_owners);
        Array.Clear(array: m_incomingOwners);
        Array.Clear(array: m_handoffs);
    }
    /// <summary>Assigns the light that owns a stable slot without compacting vacant slots.</summary>
    /// <param name="slot">The configured stable slot.</param>
    /// <param name="light">The frame light index, or -1 when the name has departed.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot or light index is outside its table.</exception>
    public void SetSlot(int slot, int light) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: slot, other: SlotCount);
        ValidateLight(light: light);
        m_slots[slot] = light;
    }
    /// <summary>Copies this frame's active controls, in incoming-channel order.</summary>
    /// <param name="handoffs">At most F active handoffs.</param>
    /// <exception cref="ArgumentOutOfRangeException">A control or count is outside the configured tables.</exception>
    public void SetHandoffs(ReadOnlySpan<SdfShadowHandoff> handoffs) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: handoffs.Length, other: FadeCapacity);
        foreach (var handoff in handoffs) {
            ValidateLight(light: handoff.Outgoing);
            ValidateLight(light: handoff.Incoming);
            ArgumentOutOfRangeException.ThrowIfNegative(value: handoff.Slot);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: handoff.Slot, other: SlotCount);
            if (!float.IsFinite(f: handoff.Weight) || (handoff.Weight < 0f) || (handoff.Weight > 1f)) {
                throw new ArgumentOutOfRangeException(paramName: nameof(handoffs));
            }
        }
        handoffs.CopyTo(destination: m_handoffs);
        m_handoffs.AsSpan(start: handoffs.Length).Clear();
        Array.Clear(array: m_incomingOwners);
        FadeCount = handoffs.Length;
    }
    /// <summary>Copies the policy, stable owners and active controls without sharing mutable storage.</summary>
    /// <param name="source">The source frame's slots.</param>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    public void CopyFrom(SdfShadowSlots source) {
        ArgumentNullException.ThrowIfNull(argument: source);
        source.m_slots.CopyTo(array: m_slots, index: 0);
        source.m_owners.CopyTo(array: m_owners, index: 0);
        source.m_incomingOwners.CopyTo(array: m_incomingOwners, index: 0);
        source.m_handoffs.CopyTo(array: m_handoffs, index: 0);
        SlotCount = source.SlotCount;
        FadeCapacity = source.FadeCapacity;
        FadeCount = source.FadeCount;
    }

    private static void ValidateLight(int light) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: light, other: -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: light, other: SdfLights.MaxLights);
    }
}
