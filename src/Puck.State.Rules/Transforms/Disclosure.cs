namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // Collect intrinsic dependencies as well as the compiler's selector/set dependencies. Written rows can also
    // be read: sorting reveals their values, moving reveals membership, and remembering tests prior contents.
    internal static void CollectAccesses(ArenaTransform transform, StateCatalog catalog, in ArenaTransformBinding binding, List<int> reads, List<int> writes) {
        Span<int> readRows = stackalloc int[catalog.Rows.Count];
        Span<int> writeRows = stackalloc int[catalog.Rows.Count];
        var accesses = new DisclosureAccesses(catalog: catalog, reads: readRows, writes: writeRows);
        accesses.Collect(transform: transform, binding: in binding);
        foreach (var row in accesses.Reads) { reads.Add(item: row); }
        foreach (var row in accesses.Writes) { writes.Add(item: row); }
    }

    // At most MaxRows entries in each span. The firing owns these spans; no cached mutable binding or heap
    // collection survives a call, and the compiler and raw arena callers use exactly the same access walk.
    private ref struct DisclosureAccesses(StateCatalog catalog, Span<int> reads, Span<int> writes) {
        private readonly Span<int> m_reads = reads;
        private readonly Span<int> m_writes = writes;
        private int m_readCount;
        private int m_writeCount;
        public readonly ReadOnlySpan<int> Reads => m_reads[..m_readCount];
        public readonly ReadOnlySpan<int> Writes => m_writes[..m_writeCount];

        private void Read(int row) {
            if ((uint)row < (uint)catalog.Rows.Count && !Reads.Contains(value: row)) { m_reads[m_readCount++] = row; }
        }
        private void Write(int row, bool read = false) {
            if ((uint)row < (uint)catalog.Rows.Count && !Writes.Contains(value: row)) { m_writes[m_writeCount++] = row; }
            if (read) { Read(row: row); }
        }
        private void Vector(VectorSource source) { if (source.IsCell) { Read(row: source.Address.RowOrdinal); } }
        private void Named(CellName name) { if (catalog.TryResolve(lane: StateLane.Document, name: name, handle: out var handle)) { Read(row: handle.Ordinal); } }
        private void Set(CellSetExpression? set) {
            switch (set) {
                case CellSetExpression.Board board: Named(name: board.Row); break;
                case CellSetExpression.Zone zone: Named(name: zone.Row); break;
                case CellSetExpression.Family family when catalog.TryGetFamily(name: family.Name, family: out var range):
                    foreach (var ordinal in range.Ordinals()) {
                        Read(row: ordinal);
                        if (catalog.Rows[ordinal].EffectiveDomain is StateDomain.KeysOf domain) { Named(name: domain.Row); }
                    }
                    break;
                case CellSetExpression.Any any: for (var index = 0; index < any.Items.Count; index++) { Set(set: any.Items[index]); } break;
                case CellSetExpression.Both both: for (var index = 0; index < both.Items.Count; index++) { Set(set: both.Items[index]); } break;
                case CellSetExpression.Complement complement: Set(set: complement.Item); break;
            }
        }
        public void Collect(ArenaTransform transform, in ArenaTransformBinding binding) {
            for (var index = 0; index < transform.DisclosureReads.Count; index++) { Read(row: transform.DisclosureReads[index]); }
            switch (transform) {
                case ArenaTransform.Arrange value:
                    Write(row: value.RowOrdinal, read: true); Read(row: value.FromRowOrdinal); Read(row: value.DomainRowOrdinal); break;
                case ArenaTransform.BoardCombine value:
                    Write(row: value.RowOrdinal); Read(row: value.LeftRowOrdinal); Read(row: value.RightRowOrdinal);
                    Set(set: value.LeftSet?.Set); Set(set: value.RightSet?.Set); break;
                case ArenaTransform.ClearEnclosed value:
                    Write(row: value.RowOrdinal, read: true); break;
                case ArenaTransform.Mean value:
                    Write(row: value.IntoRowOrdinal); Read(row: value.FromRowOrdinal); Read(row: value.WhereRowOrdinal); break;
                case ArenaTransform.Copy value:
                    Write(row: value.IntoRowOrdinal); Vector(source: value.From); break;
                case ArenaTransform.Mix value:
                    Write(row: value.IntoRowOrdinal);
                    for (var index = 0; index < value.Terms.Count; index++) { Vector(source: value.Terms[index].Source); }
                    break;
                case ArenaTransform.Nearest value:
                    Write(row: value.IntoRowOrdinal); Read(row: value.FromRowOrdinal); Read(row: value.WhereRowOrdinal); Vector(source: value.Query); break;
                case ArenaTransform.PushRay value:
                    var pool = catalog.Pools[value.PoolOrdinal];
                    Read(row: pool.DomainRowOrdinal);
                    Write(row: pool.Fields[value.CellFieldOrdinal].RowOrdinal, read: true);
                    Read(row: pool.Fields[value.ValueFieldOrdinal].RowOrdinal);
                    break;
                case ArenaTransform.Remember value:
                    Write(row: value.IntoRowOrdinal, read: true); Vector(source: value.From); break;
                case ArenaTransform.SetRay value:
                    Write(row: value.RowOrdinal, read: true); break;
                case ArenaTransform.Shuffle value:
                    Write(row: value.RowOrdinal, read: true); Write(row: value.DrawRowOrdinal, read: true); break;
                case ArenaTransform.SortKeyed value:
                    Write(row: value.RowOrdinal, read: true); break;
                case ArenaTransform.SortZone value:
                    Write(row: value.RowOrdinal, read: true);
                    for (var index = 0; index < value.By.Count; index++) { Read(row: value.By[index].RowOrdinal); }
                    break;
                case ArenaTransform.Transfer value:
                    Write(row: binding.FromOr(own: value.FromRowOrdinal), read: true);
                    Write(row: binding.ToOr(own: value.ToRowOrdinal), read: true);
                    Write(row: value.DrawRowOrdinal, read: true); break;
                case ArenaTransform.WriteSet value:
                    Write(row: value.RowOrdinal); Read(row: value.SetRowOrdinal); Set(set: value.Set?.Set); break;
                }
        }
    }

    private static bool TryEncloseAudience(StateArena arena, ArenaTransform transform, in ArenaTransformBinding binding, out EffectRefusal refusal) {
        // Observe is the declared knowledge projection. Copy is the explicit setState vector write, not a transform.
        if (transform is ArenaTransform.Observe or ArenaTransform.Copy) {
            refusal = EffectRefusal.None;
            return true;
        }
        Span<int> readRows = stackalloc int[arena.Rows.Count];
        Span<int> writeRows = stackalloc int[arena.Rows.Count];
        var accesses = new DisclosureAccesses(catalog: arena.Catalog, reads: readRows, writes: writeRows);
        accesses.Collect(transform: transform, binding: in binding);
        foreach (var target in accesses.Writes) {
            foreach (var source in accesses.Reads) {
                if (!arena.ReadAudienceEncloses(rowOrdinal: source, audience: arena.Rows[target].Visibility)) {
                    refusal = EffectRefusal.Of(code: RuleRefusal.TransformWidensAudience,
                        reason: $"TransformWidensAudience: '{arena.Rows[source].Name}' currently withholds state from readers of '{arena.Rows[target].Name}'; confine the destination audience or disclose through a rule's own write");
                    return false;
                }
            }
        }
        refusal = EffectRefusal.None;
        return true;
    }
}
