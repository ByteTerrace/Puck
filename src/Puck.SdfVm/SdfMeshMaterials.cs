namespace Puck.SdfVm;

// The same place-based material mapping serves live variants and the store's canonical palette.
internal static class SdfMeshMaterials {
    internal static uint Renumber(uint material, int[] from, int[] to) {
        var place = Array.IndexOf(array: from, value: unchecked((int)material));

        return (((place >= 0) && (place < to.Length)) ? unchecked((uint)to[place]) : material);
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
