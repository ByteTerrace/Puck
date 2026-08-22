namespace Puck.Overlays;

/// <summary>
/// The unified overlay's per-frame frame-slot table: maps each key a <c>Frame</c> HUD element names to one of
/// <see cref="SlotCount"/> combined image-sampler bindings (the compositor's fixed frame-slot descriptor range,
/// immediately after the inner world image's own binding), acquiring the underlying lease through
/// <see cref="IOverlayFrameSources"/> on first use each frame. Owned by <c>UnifiedOverlayNode</c> and driven once per
/// produced frame: <see cref="BeginFrame"/> before the writers run, <see cref="Bind"/> from <c>HudWriter</c> for each
/// visible <c>Frame</c> element, then <see cref="RetirePending"/> once the node's frame fence proves the PREVIOUS
/// frame's sampling pass has retired (mirrors <c>Puck.SdfVm.SdfEngineNode.RetireAndAdoptScreenSourceFrames</c>'s
/// retire-after-the-proving-wait idiom).
/// </summary>
public sealed class OverlayFrameSlots {
    /// <summary>The number of frame-slot bindings the compositor reserves — the widest slot index
    /// <see cref="Bind"/> can hand back is <c>SlotCount - 1</c>.</summary>
    public const int SlotCount = 8;

    private readonly int[] m_keys = new int[SlotCount];
    private readonly OverlayFrameLease[] m_leases = new OverlayFrameLease[SlotCount];
    private readonly OverlayFrameLease[] m_pendingRetireLeases = new OverlayFrameLease[SlotCount];
    private readonly IOverlayFrameSources m_sources;

    private int m_boundCount;
    private int m_pendingRetireCount;

    /// <summary>Initializes a new instance of the <see cref="OverlayFrameSlots"/> class.</summary>
    /// <param name="sources">The host seam leases are acquired through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/> is <see langword="null"/>.</exception>
    public OverlayFrameSlots(IOverlayFrameSources sources) {
        ArgumentNullException.ThrowIfNull(argument: sources);

        m_sources = sources;
    }

    /// <summary>Gets the number of slots bound so far this frame.</summary>
    public int BoundCount => m_boundCount;

    /// <summary>Gets the lease bound at <paramref name="slot"/> this frame.</summary>
    /// <param name="slot">The slot index, <c>0..</c><see cref="BoundCount"/><c>-1</c>.</param>
    /// <returns>The slot's acquired lease.</returns>
    public OverlayFrameLease LeaseAt(int slot) => m_leases[slot];
    /// <summary>Binds <paramref name="key"/> to a slot for this frame: the first bind of a key acquires its lease
    /// through <see cref="IOverlayFrameSources.TryAcquire"/> and takes the next free slot; a repeated key within the
    /// same frame returns the same slot without acquiring again.</summary>
    /// <param name="key">The opaque source id the HUD element names.</param>
    /// <returns>The bound slot index, or -1 when the source has nothing to show this frame or every slot is
    /// already taken — the caller draws nothing for that element, never a placeholder.</returns>
    public int Bind(int key) {
        for (var index = 0; (index < m_boundCount); index++) {
            if (m_keys[index] == key) {
                return index;
            }
        }

        if (
            (m_boundCount >= SlotCount) ||
            !m_sources.TryAcquire(
                key: key,
                lease: out var lease
            )
        ) {
            return -1;
        }

        var slot = m_boundCount;

        m_keys[slot] = key;
        m_leases[slot] = lease;
        m_boundCount++;

        return slot;
    }
    /// <summary>Starts a new produced frame: moves the leases bound over the frame just finished into the
    /// retire-pending set (<see cref="RetirePending"/> releases them once the fence proves that frame's pass
    /// retired) and clears the slot table for this frame's binds.</summary>
    public void BeginFrame() {
        Array.Copy(
            destinationArray: m_pendingRetireLeases,
            length: m_boundCount,
            sourceArray: m_leases,
            sourceIndex: 0,
            destinationIndex: 0
        );
        m_pendingRetireCount = m_boundCount;
        m_boundCount = 0;
    }
    /// <summary>Retires every lease this table currently holds, bound or still pending retirement — the caller's
    /// responsibility to call only after a final fence wait proves no pass can still be sampling them.</summary>
    public void RetireAll() {
        RetirePending();

        for (var index = 0; (index < m_boundCount); index++) {
            m_leases[index].Retire();
            m_leases[index] = default;
        }

        m_boundCount = 0;
    }
    /// <summary>Retires the leases <see cref="BeginFrame"/> moved aside from the previous produced frame. Call once
    /// the node's frame fence wait proves that frame's sampling pass has retired.</summary>
    public void RetirePending() {
        for (var index = 0; (index < m_pendingRetireCount); index++) {
            m_pendingRetireLeases[index].Retire();
            m_pendingRetireLeases[index] = default;
        }

        m_pendingRetireCount = 0;
    }
}
