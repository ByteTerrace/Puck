using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.World.Client;

// Each slot map and its rebuild revision come from the same admitted avatars. Articulation follows the stamps
// the scene actually hands to its pool, including during the boot capacity probe.
internal sealed class WorldCatalogBand(Func<int, bool> presents, Func<int, (byte Rig, WorldLook Look, Vector3 Color)> appearance) {
    private List<(int Body, byte Rig, WorldLook Look, Vector3 Color)> m_image = [];
    private List<(int Body, byte Rig, WorldLook Look, Vector3 Color)> m_scratch = [];
    private (int Body, string Hash, float Scale, WorldLook Look)[] m_articulated = [];

    private readonly List<int> m_bodies = [];

    public int[] Slots { get; } = new int[WorldBodiesLimits.CapacityCeiling];
    // The bodies holding a slot, in body order, as of the latest Prepare: what a per-frame walk visits instead of every
    // index of Slots. A span, so the walk allocates no enumerator.
    public ReadOnlySpan<int> Bodies => CollectionsMarshal.AsSpan(m_bodies);
    public int SlotCount { get; private set; }
    public int Revision { get; private set; }
    public int ArticulatedRevision { get; private set; }

    public void Prepare() {
        m_scratch.Clear();
        m_bodies.Clear();
        var slot = 0;
        for (var body = 0; body < Slots.Length; body++) {
            if (!presents(body)) {
                Slots[body] = -1;
                continue;
            }

            m_bodies.Add(body);
            Slots[body] = slot;
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
