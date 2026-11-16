namespace Puck.SdfVm;

// The same place-based material mapping serves live variants and the store's canonical palette.
internal static class SdfMeshMaterials {
    internal static uint Renumber(uint material, int[] from, int[] to) {
        var place = Array.IndexOf(array: from, value: unchecked((int)material));

        return (((place >= 0) && (place < to.Length)) ? unchecked((uint)to[place]) : material);
    }

    // The renumbering a decode applies to the triangle ids it writes, from the entry's canonical ids to the taking group's,
    // or null when the ids are held as stored: they are the same, or `to` names two places by one id and could not be
    // renumbered back by place.
    internal static Remap? RemapOf(int[] from, int[]? to) => (((to is null) || from.AsSpan().SequenceEqual(other: to) || (to.Distinct().Count() != to.Length)) ? null : new Remap(from: from, to: to));

    // Writes ids renumbered by place as Renumber does, and notes whether a mesh can be held in `to`'s ids and renumbered
    // from them again: it cannot when an id outside `from` (one no shape of the group owns, which renumbering keeps) is one
    // of `to`'s, where it would read as a place. A decode that meets one holds the entry's ids instead.
    internal sealed class Remap(int[] from, int[] to) {
        /// <summary>Gets whether an id outside the entry's palette is one of the taking group's.</summary>
        public bool Collided { get; private set; }

        /// <summary>Renumbers ids in place.</summary>
        /// <param name="ids">The ids a decode wrote, read as the entry's.</param>
        public void Apply(Span<uint> ids) {
            var cached = false;
            var last = 0u;
            var renumbered = 0u;

            for (var index = 0; (index < ids.Length); index++) {
                var id = ids[index];

                if (!cached || (id != last)) {
                    var place = Array.IndexOf(array: from, value: unchecked((int)id));

                    if (place < 0) { Collided |= (Array.IndexOf(array: to, value: unchecked((int)id)) >= 0); }
                    renumbered = (((place >= 0) && (place < to.Length)) ? unchecked((uint)to[place]) : id);
                    last = id;
                    cached = true;
                }
                ids[index] = renumbered;
            }
        }
    }

    // Shapes are keyed by material place, never by a program's palette registration. Keep ids no shape owns
    // unchanged, and reserve them before choosing canonical ids so a load cannot mistake one for a place.
    internal static int[] Canonical(IReadOnlyList<SdfMesh> pieces, int[] materials) {
        if (materials.Length == 0) { return []; }
        HashSet<uint>? outside = null;

        void Reserve(ReadOnlySpan<uint> values) {
            foreach (var value in values) {
                if (Array.IndexOf(array: materials, value: unchecked((int)value)) < 0) { (outside ??= []).Add(item: value); }
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
