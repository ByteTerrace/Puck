using System.Numerics;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One delivery stream's declared-set revision (<see cref="WorldSnapshot.Revision"/>): one more each time the
/// declared fields of the bodies the stream is delivered (which bodies, their generations, colors, kits, looks, catalog
/// rigs and placements) differ from the ones it was last delivered, and on the first delivery after it starts or is
/// released. A body redacted from the stream throughout never moves it, and neither does motion, so a recipient rebuilds
/// what it draws from the roster only when the roster it sees changed, never on an ordinary tick.</summary>
internal sealed class WorldDeclaredRoster {
    // The declared fields of one delivered body, in delivery order.
    private readonly record struct Declared(int Index, bool Active, int Generation, Vector3 BodyColor, byte Kit, byte Look, byte CatalogRig, string? PlacementId) {
        public static Declared Of(in EntitySnapshot entry) => new(Active: entry.Active, BodyColor: entry.BodyColor, CatalogRig: entry.CatalogRig, Generation: entry.Generation, Index: entry.Index, Kit: entry.Kit, Look: entry.Look, PlacementId: entry.PlacementId);
    }

    private Declared[] m_held = [];
    private int m_count;
    private int m_revision;
    private bool m_seeded;

    /// <summary>Returns the revision a delivery of these bodies carries, moving it when their declared fields differ from
    /// the last delivery's.</summary>
    /// <param name="entries">The bodies the delivery carries, as redacted for the stream.</param>
    /// <returns>The stream's declared-set revision, one or more once any delivery was observed.</returns>
    public int Observe(ReadOnlySpan<EntitySnapshot> entries) {
        var same = (m_seeded && (entries.Length == m_count));

        for (var index = 0; (same && (index < entries.Length)); index++) {
            same = (m_held[index] == Declared.Of(entry: in entries[index]));
        }
        if (same) {
            return m_revision;
        }
        if (m_held.Length < entries.Length) {
            m_held = new Declared[entries.Length];
        }
        for (var index = 0; (index < entries.Length); index++) {
            m_held[index] = Declared.Of(entry: in entries[index]);
        }
        m_count = entries.Length;
        m_seeded = true;

        return (m_revision = checked(m_revision + 1));
    }
    /// <summary>Forgets the roster the stream was last delivered: the stream stopped observing, so its next delivery
    /// moves the revision whatever it carries.</summary>
    public void Forget() => m_seeded = false;
}
