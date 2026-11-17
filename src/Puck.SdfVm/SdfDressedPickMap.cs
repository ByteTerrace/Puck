namespace Puck.SdfVm;

// Pose-independent metadata shared by every piece of one copied surface.
internal sealed record SdfMeshCopySurface(object Identity, int FirstInstance);

/// <summary>Resolves retained row addresses through their owner provenance, wherever the host run lies.</summary>
public sealed class SdfDressedPickMap(ISdfPickMap inner, SdfMeshRows rows) : ISdfPickMap {
    /// <summary>Gets the immutable host map this reader extends.</summary>
    public ISdfPickMap Inner => inner;

    /// <inheritdoc/>
    public object? Resolve(uint identity) {
        if (SdfVisibility.KindOf(identity: identity) == SdfVisibilityKind.Mesh) {
            var row = Source(row: ((int)SdfVisibility.SourceOf(identity: identity)));

            if (row < 0) { return null; }
            if (rows[row].Identity is SdfMeshCopySurface copy) { return copy.Identity; }
        }
        return inner.Resolve(identity: Translate(identity: identity));
    }
    /// <inheritdoc/>
    public string? MaterialName(uint identity, int material) => inner.MaterialName(identity: Translate(identity: identity), material: material);

    private int Source(int row) {
        if (rows.Occupancy(row: row) == 0) { return -1; }
        var copied = rows.OwnerOf(row: row).CopiedRow;

        return ((copied < 0) ? row : ((rows.Occupancy(row: copied) == 0) ? -1 : copied));
    }
    private uint Translate(uint identity) {
        if (SdfVisibility.KindOf(identity: identity) != SdfVisibilityKind.Mesh) { return identity; }
        var row = Source(row: ((int)SdfVisibility.SourceOf(identity: identity)));

        if (row < 0) { return 0; }
        var owner = rows.OwnerOf(row: row);

        return ((owner.Host >= 0) ? SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Mesh, source: ((uint)owner.Host)) :
            ((owner.Instance >= 0) ? SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Sdf, source: ((uint)(owner.Instance + 1))) : 0));
    }
}
/// <summary>The row identity epoch under which one mesh target was recorded.</summary>
public readonly record struct SdfMeshRowStamp(SdfMeshRows? Rows, long Revision);
