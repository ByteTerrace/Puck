namespace Puck.SdfVm;

// The same place-based material mapping serves live variants and the store's canonical palette.
internal static class SdfMeshMaterials {
    // The renumbering a decode applies to the triangle ids it writes, from the entry's canonical ids to the taking group's,
    // or null when the ids are held as stored: they are the same, or `to` names two places by one id and could not be
    // renumbered back by place.
    internal static Remap? RemapOf(int[] from, int[]? to) => (((to is null) || from.AsSpan().SequenceEqual(other: to) || (to.Distinct().Count() != to.Length)) ? null : new Remap(from, to, detectCollision: true));

    // Writes ids renumbered by place as Renumber does, and notes whether a mesh can be held in `to`'s ids and renumbered
    // from them again: it cannot when an id outside `from` (one no shape of the group owns, which renumbering keeps) is one
    // of `to`'s, where it would read as a place. A decode that meets one holds the entry's ids instead.
    internal sealed class Remap {
        private readonly Dictionary<uint, uint> m_ids;
        private readonly HashSet<uint>? m_targets;

        public Remap(int[] from, int[] to, bool detectCollision = false) {
            m_ids = new(from.Length);
            for (var place = 0; place < from.Length; place++) {
                var id = unchecked((uint)from[place]);
                m_ids.TryAdd(id, place < to.Length ? unchecked((uint)to[place]) : id);
            }
            if (detectCollision) { m_targets = [.. to.Select(static id => unchecked((uint)id))]; }
        }

        public uint Renumber(uint id) => m_ids.GetValueOrDefault(id, id);
        public bool Collided { get; private set; }

        public void Apply(Span<uint> ids) {
            foreach (ref var id in ids) {
                if (m_ids.TryGetValue(id, out var mapped)) { id = mapped; }
                else if (m_targets!.Contains(id)) { Collided = true; }
            }
        }
    }

    // Shapes are keyed by material place, never by a program's palette registration. Keep ids no shape owns
    // unchanged, and reserve them before choosing canonical ids so a load cannot mistake one for a place.
    internal static int[] Canonical(IReadOnlyList<SdfMesh> pieces, int[] materials) {
        if (materials.Length == 0) { return []; }
        var owned = materials.ToHashSet();
        HashSet<uint>? outside = null;

        void Reserve(ReadOnlySpan<uint> values) {
            foreach (var value in values) {
                if (!owned.Contains(unchecked((int)value))) { (outside ??= []).Add(item: value); }
            }
        }

        foreach (var piece in pieces) {
            Reserve(values: piece.TriangleMaterials.Span);
            foreach (var level in piece.Levels) { Reserve(values: level.TriangleMaterials.Span); }
        }

        var canonical = new int[materials.Length];
        var next = 0U;

        for (var place = 0; (place < canonical.Length); place++) {
            while (outside?.Contains(item: next) ?? false) { next++; }
            canonical[place] = unchecked((int)next++);
        }

        return canonical;
    }
}
