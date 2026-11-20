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
    internal readonly record struct Declared(int Index, bool Active, int Generation, Vector3 BodyColor, byte Kit, byte Look, byte CatalogRig, string? PlacementId) {
        public static Declared Of(in EntitySnapshot entry) => new(Active: entry.Active, BodyColor: entry.BodyColor, CatalogRig: entry.CatalogRig, Generation: entry.Generation, Index: entry.Index, Kit: entry.Kit, Look: entry.Look, PlacementId: entry.PlacementId);
    }

    private Declared[]? m_held;
    private int m_revision;

    public static Declared[] Describe(ReadOnlySpan<EntitySnapshot> entries, Declared[]? previous) {
        if (previous is not null && entries.Length == previous.Length) {
            var same = true;
            for (var i = 0; same && i < entries.Length; i++) { same = previous[i] == Declared.Of(entries[i]); }
            if (same) { return previous; }
        }
        var rows = new Declared[entries.Length];
        for (var i = 0; i < entries.Length; i++) { rows[i] = Declared.Of(entries[i]); }
        return rows;
    }

    public int Observe(Declared[] current) {
        if (!ReferenceEquals(current, m_held)) {
            if (m_held is null || !current.AsSpan().SequenceEqual(m_held)) { m_revision = checked(m_revision + 1); }
            m_held = current;
        }
        return m_revision;
    }
    /// <summary>Forgets the roster the stream was last delivered: the stream stopped observing, so its next delivery
    /// moves the revision whatever it carries.</summary>
    public void Forget() => m_held = null;
}
