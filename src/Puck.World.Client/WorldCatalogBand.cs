using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.World.Client;

// Each slot map and its rebuild revision come from the same admitted avatars. Articulation follows the stamps
// the scene actually hands to its pool, including during the boot capacity probe.
//
// `candidates` appends, in ascending order and each once, every body `presents` may answer true for (a scene's active
// bodies, and the seats it may draw while they travel); Prepare asks those alone, never every index of Slots.
internal sealed class WorldCatalogBand(Action<List<int>> candidates, Func<int, bool> presents, Func<int, (byte Rig, WorldLook Look, Vector3 Color)> appearance) {
    private List<(int Body, byte Rig, WorldLook Look, Vector3 Color)> m_image = [];
    private List<(int Body, byte Rig, WorldLook Look, Vector3 Color)> m_scratch = [];
    private (int Body, string Hash, float Scale, WorldLook Look)[] m_articulated = [];

    private readonly List<int> m_bodies = [];
    private readonly List<int> m_candidates = [];

    // A body's first slot in the band, or -1 for a body that holds none.
    public int[] Slots { get; } = Unheld();
    // The bodies holding a slot, in body order, as of the latest Prepare: what a per-frame walk visits instead of every
    // index of Slots. A span, so the walk allocates no enumerator.
    public ReadOnlySpan<int> Bodies => CollectionsMarshal.AsSpan(m_bodies);
    public int SlotCount { get; private set; }
    public int Revision { get; private set; }
    public int ArticulatedRevision { get; private set; }

    // The rig, look and colour a body's avatar is emitted with.
    public (byte Rig, WorldLook Look, Vector3 Color) AppearanceOf(int body) => appearance(body);

    private static int[] Unheld() {
        var slots = new int[WorldBodiesLimits.CapacityCeiling];

        Array.Fill(array: slots, value: -1);

        return slots;
    }

    public void Prepare() {
        m_scratch.Clear();
        foreach (var body in m_bodies) {
            Slots[body] = -1;
        }

        m_bodies.Clear();
        m_candidates.Clear();
        candidates(m_candidates);
        var slot = 0;
        foreach (var body in m_candidates) {
            if (!presents(body)) {
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
