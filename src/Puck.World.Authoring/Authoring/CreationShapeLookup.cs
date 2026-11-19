namespace Puck.World.Authoring;

// A creation's shapes found by name or by reference, the first authored occurrence winning, as the client resolves a
// name. Names are read only as far as a lookup needs: later unresolved document identifiers must not make an earlier
// successful match throw. Both indexes live as long as the lookup, one validation or one rig compile.
internal sealed class CreationShapeLookup(IReadOnlyList<ShapeDocument> shapes) {
    private int m_firstUnnamed = -1;
    private Dictionary<string, int>? m_names;
    private Dictionary<ShapeDocument, int>? m_references;
    private int m_scannedNames;
    private int m_scannedReferences;

    public int Find(string? name, bool includeUnnamed = false) {
        if (name is null) {
            if (!includeUnnamed) { return -1; }
            if (m_firstUnnamed >= 0) { return m_firstUnnamed; }
        } else if (
            (m_names is not null) &&
            m_names.TryGetValue(
            key: name,
            value: out var found
        )
        ) {
            return found;
        }

        while (m_scannedNames < shapes.Count) {
            var index = m_scannedNames;
            var candidate = shapes[index].Name?.Value;

            m_scannedNames++;
            if (candidate is null) {
                if (m_firstUnnamed < 0) { m_firstUnnamed = index; }
            } else {
                m_names ??= new Dictionary<string, int>(comparer: StringComparer.Ordinal);
                m_names.TryAdd(
                    key: candidate,
                    value: index
                );
            }
            if (string.Equals(
                a: candidate,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return index;
            }
        }
        return -1;
    }
    public int FindReference(ShapeDocument shape) {
        if (
            (m_references is not null) &&
            m_references.TryGetValue(
            key: shape,
            value: out var found
        )
        ) {
            return found;
        }
        while (m_scannedReferences < shapes.Count) {
            var index = m_scannedReferences++;
            var candidate = shapes[index];

            if (candidate is not null) {
                m_references ??= new Dictionary<ShapeDocument, int>(comparer: ReferenceEqualityComparer.Instance);
                m_references.TryAdd(
                    key: candidate,
                    value: index
                );
            }
            if (ReferenceEquals(
                objA: candidate,
                objB: shape
            )) {
                return index;
            }
        }
        return shapes.Count;
    }
}
