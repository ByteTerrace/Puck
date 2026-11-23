using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.World.Client;

// Each slot map and its rebuild revision come from the same admitted avatars. Articulation follows the stamps
// the scene actually hands to its pool, including during the boot capacity probe.
//
// `candidates` appends, in ascending order and each once, every body `presents` may answer true for (a scene's active
// bodies, and the seats it may draw while they travel); Prepare asks those alone, never every body.
internal sealed class WorldCatalogBand(Action<List<int>> candidates, Func<int, bool> presents, Func<int, (byte Rig, WorldLook Look, Vector3 Color)> appearance) {
    private List<(int Body, byte Rig, WorldLook Look, Vector3 Color)> m_image = [];
    private List<(int Body, byte Rig, WorldLook Look, Vector3 Color)> m_scratch = [];
    private (int Body, string Hash, float Scale, WorldLook Look)[] m_articulated = [];

    private readonly List<int> m_bodies = [];
    private readonly List<int> m_candidates = [];

    // Each body's first slot in the band, or -1 for a body that holds none, grown by Prepare to its highest candidate.
    private int[] m_slots = [];

    // The bodies holding a slot, in body order, as of the latest Prepare: what a per-frame walk visits instead of every
    // body. A span, so the walk allocates no enumerator.
    public ReadOnlySpan<int> Bodies => CollectionsMarshal.AsSpan(m_bodies);
    // One past the highest body the band has ever held a record for: the span a per-body column sized by it covers.
    public int BodySpan => m_slots.Length;
    public int SlotCount { get; private set; }
    public int Revision { get; private set; }
    public int ArticulatedRevision { get; private set; }

    // The rig, look and colour a body's avatar is emitted with.
    public (byte Rig, WorldLook Look, Vector3 Color) AppearanceOf(int body) => appearance(body);
    // A body's first slot in the band, or -1 for a body that holds none.
    public int SlotOf(int body) => ((((uint)body) < ((uint)m_slots.Length)) ? m_slots[body] : -1);

    public void Prepare() {
        m_scratch.Clear();
        foreach (var body in m_bodies) {
            m_slots[body] = -1;
        }

        m_bodies.Clear();
        m_candidates.Clear();
        candidates(m_candidates);
        var span = m_slots.Length;

        foreach (var body in m_candidates) { span = Math.Max(val1: span, val2: (body + 1)); }
        if (span > m_slots.Length) {
            var from = m_slots.Length;

            Array.Resize(array: ref m_slots, newSize: span);
            m_slots.AsSpan(start: from).Fill(value: -1);
        }
        var slot = 0;
        foreach (var body in m_candidates) {
            if (!presents(body)) {
                continue;
            }

            m_bodies.Add(body);
            m_slots[body] = slot;
            slot += WorldRigCatalog.TransformSlotsPerBody;
            var (rig, look, color) = appearance(body);
            m_scratch.Add((body, rig, look, color));
        }
        SlotCount = slot;
        if (!CollectionsMarshal.AsSpan(m_image).SequenceEqual(CollectionsMarshal.AsSpan(m_scratch))) {
            (m_image, m_scratch) = (m_scratch, m_image);
            Revision++;
        }
    }

    public void ObserveArticulation(IReadOnlyList<WorldCreationInstances.BodyStamp> stamps) {
        var image = stamps.Where(stamp => !WorldRigidAssets.IsRigid(stamp.Creation, stamp.Look))
            .Select(stamp => (stamp.BodyIndex, stamp.Creation.Hash, stamp.Scale, stamp.Look)).ToArray();
        if (!m_articulated.AsSpan().SequenceEqual(image)) {
            m_articulated = image;
            ArticulatedRevision++;
        }
    }
}
