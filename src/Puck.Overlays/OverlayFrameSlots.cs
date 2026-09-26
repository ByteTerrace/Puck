namespace Puck.Overlays;

using Puck.Hosting;
using Puck.Shaders;

/// <summary>
/// The overlay's per-frame frame-slot table: maps each key a <c>Frame</c> HUD element names to one of
/// <see cref="SlotCount"/> sampled-image bindings (the overlay pass group's frame slots, after its source image),
/// acquiring the underlying lease through <see cref="IOverlayFrameSources"/> on first use each frame. Owned by
/// <see cref="OverlayFrameComposer"/> and driven once per recording: <see cref="BeginFrame"/> before the writers run,
/// <see cref="Bind"/> from <c>HudWriter</c> for each visible <c>Frame</c> element, then <see cref="MoveTo"/> hands every
/// held lease to the recording's <see cref="LeaseRetireList"/>, which retires it after the submission that sampled it.
/// </summary>
public sealed class OverlayFrameSlots {
    /// <summary>The number of frame-slot bindings the compositor reserves — the widest slot index
    /// <see cref="Bind"/> can hand back is <c>SlotCount - 1</c>.</summary>
    public const int SlotCount = RenderGraphPackageCatalog.OverlayFrameSlotCount;

    private readonly IOverlayFrameSources m_sources;

    private int m_boundCount;
    private bool m_capacityExceeded;

    private readonly int[] m_keys = new int[SlotCount];
    private readonly GpuImageLease[] m_leases = new GpuImageLease[SlotCount];
    private readonly LeaseRetireList m_pendingRetire = new(capacity: SlotCount);

    /// <summary>Initializes a new instance of the <see cref="OverlayFrameSlots"/> class.</summary>
    /// <param name="sources">The host seam leases are acquired through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/> is <see langword="null"/>.</exception>
    public OverlayFrameSlots(IOverlayFrameSources sources) {
        ArgumentNullException.ThrowIfNull(argument: sources);

        m_sources = sources;
    }

    /// <summary>Gets the number of slots bound so far this frame.</summary>
    public int BoundCount => m_boundCount;
    /// <summary>Gets whether a distinct source binding was refused this frame because all <see cref="SlotCount"/>
    /// slots were already occupied. The table does not acquire an over-capacity source merely to probe its
    /// availability.</summary>
    public bool CapacityExceeded => m_capacityExceeded;

    /// <summary>Starts a new produced frame: holds any lease still bound from the frame before, which <see cref="MoveTo"/>
    /// hands on with this frame's, and clears the slot table for this frame's binds.</summary>
    public void BeginFrame() {
        HoldBound();
        m_capacityExceeded = false;
    }
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

        if (m_boundCount >= SlotCount) {
            m_capacityExceeded = true;

            return -1;
        }

        if (
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
    /// <summary>Gets the lease bound at <paramref name="slot"/> this frame.</summary>
    /// <param name="slot">The slot index, <c>0..</c><see cref="BoundCount"/><c>-1</c>.</param>
    /// <returns>The slot's acquired lease.</returns>
    public GpuImageLease LeaseAt(int slot) => m_leases[slot];
    /// <summary>Retires every lease this table currently holds, bound or held from an earlier frame. Call it only for
    /// leases no submission samples: ones a recording bound but never handed to its frame.</summary>
    public void RetireAll() {
        HoldBound();
        m_pendingRetire.RetireAll();
    }
    /// <summary>Moves every lease the table holds, bound this frame or still pending retirement, into
    /// <paramref name="destination"/> and empties the table: a recorder hands them to its instance's frame, whose slot
    /// retires them after the submission that sampled them.</summary>
    /// <param name="destination">The list that holds the leases from now on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    public void MoveTo(LeaseRetireList destination) {
        ArgumentNullException.ThrowIfNull(argument: destination);

        HoldBound();
        m_pendingRetire.MoveTo(destination: destination);
    }

    // Moves the bound leases to the retire-pending list, after any still pending, and empties the slot table.
    private void HoldBound() {
        for (var index = 0; (index < m_boundCount); index++) {
            m_pendingRetire.Hold(lease: in m_leases[index]);
            m_leases[index] = default;
        }

        m_boundCount = 0;
    }
}
