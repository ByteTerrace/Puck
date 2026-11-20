namespace Puck.SdfVm;

/// <summary>Orders (priority, index) pairs by higher priority first, then by lower index: the order the bake takes
/// groups in and the mesh region lands owed meshes in, each by the pixels a metre covers at its demand box and the
/// plan's or the list's order among equals. A struct, so a span sort through it inlines the comparison.</summary>
internal readonly struct SdfMeshPriorityOrder : IComparer<(float Priority, int Index)> {
    /// <summary>Compares two pairs.</summary>
    /// <param name="x">The first pair.</param>
    /// <param name="y">The second pair.</param>
    /// <returns>Negative when <paramref name="x"/> comes first, positive when <paramref name="y"/> does, zero when they are the same pair.</returns>
    public int Compare((float Priority, int Index) x, (float Priority, int Index) y) =>
        ((y.Priority.CompareTo(value: x.Priority) is var order and not 0) ? order : x.Index.CompareTo(value: y.Index));
}
