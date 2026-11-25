using Puck.Abstractions.Memory;
using Puck.Abstractions.Collections;

namespace Puck.World.Client;

/// <summary>
/// Which of a band's holders read a state mirror slot that changed: a stamp pool's registrations, or its rigid bodies,
/// each reading through a <see cref="WorldStateLease"/>. The watch indexes every holder's leased slots, and each frame
/// names the holders of the slots the mirror's change log lists (<see cref="WorldStateMirror.TryChangedSince"/>), so a
/// band wakes the holders of what moved and never asks every lease for <see cref="WorldStateLease.Changed"/>.
/// </summary>
/// <remarks>Invariant: a holder that <see cref="Index"/> indexed is named by <see cref="Collect"/> on the first collect
/// after any slot its lease held at that indexing changes. A lease's held set changes only through the holder's own
/// reads, so a band that indexes a holder after each of its reads keeps the index complete; a slot a lease gains
/// between two indexings was read at the value it gained it at. Used on the thread that presents frames.</remarks>
internal sealed class WorldLeaseWatch {
    private readonly List<int> m_changed = [];
    private List<int>?[] m_holdersBySlot = [];
    private int m_installs;
    private WorldStateLease?[] m_leases = [];
    private WorldStateMirror? m_mirror;
    private int m_revision;
    private readonly List<int> m_scratch = [];
    private int[][] m_slots = [];
    private int[] m_versions = [];

    /// <summary>Forgets every holder.</summary>
    public void Clear() {
        foreach (var holders in m_holdersBySlot) {
            holders?.Clear();
        }

        Array.Clear(array: m_leases);
        Array.Fill(array: m_slots, value: []);
    }
    /// <summary>Names the holders of every slot that changed in a mirror since the previous collect.</summary>
    /// <param name="mirror">The mirror the band's leases read.</param>
    /// <param name="woken">The set each such holder is added to.</param>
    /// <returns><see langword="false"/> when the changes cannot be named slot by slot (another mirror than the last
    /// collect read, a document installed since, or more changes than the mirror's log holds): nothing is added and the
    /// band wakes every holder.</returns>
    public bool Collect(WorldStateMirror mirror, MarkedIndexSet woken) {
        if (!ReferenceEquals(objA: mirror, objB: m_mirror) || (mirror.Installs != m_installs)) {
            m_mirror = mirror;
            m_installs = mirror.Installs;
            m_revision = mirror.Revision;

            return false;
        }
        if (mirror.Revision == m_revision) {
            return true;
        }

        m_changed.Clear();

        var named = mirror.TryChangedSince(changed: m_changed, revision: m_revision);

        m_revision = mirror.Revision;
        if (!named) {
            return false;
        }

        foreach (var slot in m_changed) {
            if ((((uint)slot) < ((uint)m_holdersBySlot.Length)) && (m_holdersBySlot[slot] is { } holders)) {
                foreach (var holder in holders) {
                    _ = woken.Add(index: holder);
                }
            }
        }

        return true;
    }
    /// <summary>Forgets one holder's slots.</summary>
    /// <param name="holder">The holder's index in its band.</param>
    public void Forget(int holder) {
        if ((((uint)holder) >= ((uint)m_leases.Length)) || (m_leases[holder] is null)) {
            return;
        }

        foreach (var slot in m_slots[holder]) {
            _ = m_holdersBySlot[slot]!.Remove(item: holder);
        }

        m_leases[holder] = null;
        m_slots[holder] = [];
    }
    /// <summary>Indexes the slots a holder's lease holds now. A holder already indexed under the same lease at the same
    /// <see cref="WorldStateLease.HeldVersion"/> costs two compares.</summary>
    /// <param name="holder">The holder's index in its band.</param>
    /// <param name="lease">The lease the holder reads through.</param>
    public void Index(int holder, WorldStateLease lease) {
        if (holder >= m_leases.Length) {
            var length = Math.Max(val1: (holder + 1), val2: (m_leases.Length * 2));

            Array.Resize(array: ref m_leases, newSize: length);
            Array.Resize(array: ref m_versions, newSize: length);

            var held = m_slots.Length;

            Array.Resize(array: ref m_slots, newSize: length);
            m_slots.AsSpan(start: held).Fill(value: []);
        }
        if (ReferenceEquals(objA: m_leases[holder], objB: lease) && (m_versions[holder] == lease.HeldVersion)) {
            return;
        }

        Forget(holder: holder);
        m_scratch.Clear();
        lease.CopyHeld(slots: m_scratch);
        m_scratch.Sort();

        var slots = new List<int>(capacity: m_scratch.Count);

        for (var index = 0; (index < m_scratch.Count); index++) {
            var slot = m_scratch[index];

            if ((index > 0) && (m_scratch[(index - 1)] == slot)) {
                continue;
            }
            ArrayCapacity.EnsureLength(array: ref m_holdersBySlot, required: (slot + 1));

            (m_holdersBySlot[slot] ??= []).Add(item: holder);
            slots.Add(item: slot);
        }

        m_leases[holder] = lease;
        m_slots[holder] = ((slots.Count == 0) ? [] : [.. slots]);
        m_versions[holder] = lease.HeldVersion;
    }
}
