namespace Puck.State;

public sealed partial class StateArena {
    private int m_poolMutationDepth;

    /// <summary>Attempts to claim the lowest free slot in a compiled pool.</summary>
    public bool TryClaim(int poolOrdinal, out StateInstanceHandle handle, out string reason) => TryClaim(poolOrdinal: poolOrdinal, time: ArenaTime.Origin, handle: out handle, reason: out reason);
    /// <summary>Attempts to claim the lowest free slot, birthing timed fields at <paramref name="time"/>.</summary>
    public bool TryClaim(int poolOrdinal, in ArenaTime time, out StateInstanceHandle handle, out string reason) {
        m_poolMutationDepth++;
        try { return TryClaimCore(handle: out handle, poolOrdinal: poolOrdinal, reason: out reason, time: in time); } finally { m_poolMutationDepth--; }
    }

    private bool TryClaimCore(int poolOrdinal, in ArenaTime time, out StateInstanceHandle handle, out string reason) {
        handle = default;
        if (!TryPool(pool: out var pool, poolOrdinal: poolOrdinal, reason: out reason)) {
            return false;
        }
        if (pool.IsPair) {
            reason = $"State pool '{pool.Name.Value}' is a pair pool; use TryClaimPair.";
            return false;
        }

        var slot = FirstFreePoolSlot(pool: pool);

        if (slot < 0) {
            reason = $"State pool '{pool.Name.Value}' holds its {pool.Capacity} instances.";
            return false;
        }
        if (!TryPoolKey(key: out var key, slot: slot)) {
            reason = $"State pool '{pool.Name.Value}' slot {slot} has no compiled key.";
            return false;
        }
        if (!TryRead(rowOrdinal: pool.GenerationRowOrdinal, key: key, value: out var generationValue)) {
            reason = $"State pool '{pool.Name.Value}' slot {slot} has no persistent generation.";
            return false;
        }

        var generation = generationValue.AsInt;
        var position = slot;

        var mark = BeginScope();

        InsertPoolCell(rowOrdinal: pool.DomainRowOrdinal, position: position, key: key, value: CellValue.Int(value: generation));
        for (var fieldOrdinal = 0; (fieldOrdinal < pool.Fields.Count); fieldOrdinal++) {
            var field = pool.Fields[fieldOrdinal];

            InsertPoolCell(rowOrdinal: field.RowOrdinal, position: position, key: key, value: field.Default);
            if ((field.Declaration.Advance is not null) && !TryWriteClock(rowOrdinal: field.RowOrdinal, key: key, epochTick: unchecked((long)time.Tick), epochEngineTick: unchecked((long)time.EngineTick), y0: 0L, v0: 0L, substepTicks: 0L, reason: out reason)) {
                Rewind(mark: mark);
                return false;
            }
        }
        if (m_journal.OverCeiling) {
            reason = $"State pool '{pool.Name.Value}' claim exceeds the {ArenaCapacity.MaxJournalBytes}-byte journal ceiling.";
            Rewind(mark: mark);
            return false;
        }

        Commit(mark: mark);
        handle = m_catalog.CreateInstanceHandle(generation: generation, poolOrdinal: poolOrdinal, slot: slot);
        reason = string.Empty;
        return true;
    }

    /// <summary>Attempts to release one live generation-checked pool instance.</summary>
    public bool TryRelease(StateInstanceHandle handle, out string reason) {
        m_poolMutationDepth++;
        try { return TryReleaseCore(handle: handle, reason: out reason); } finally { m_poolMutationDepth--; }
    }

    private bool TryReleaseCore(StateInstanceHandle handle, out string reason) {
        if (!TryResolve(handle: handle, position: out var position)) {
            reason = "The state pool instance is absent or stale.";
            return false;
        }

        var pool = m_catalog.Pools[handle.PoolOrdinal];

        if (handle.Generation == long.MaxValue) {
            reason = $"State pool '{pool.Name.Value}' slot {handle.Slot} exhausted its generation range.";
            return false;
        }
        if (!TryPoolKey(slot: handle.Slot, key: out var key)) {
            reason = $"State pool '{pool.Name.Value}' slot {handle.Slot} has no compiled key.";
            return false;
        }

        var mark = BeginScope();

        {
            for (var pairPoolOrdinal = 0; (pairPoolOrdinal < m_catalog.Pools.Count); pairPoolOrdinal++) {
                var pairPool = m_catalog.Pools[pairPoolOrdinal];

                if (!pairPool.IsPair || ((pairPool.LeftPoolOrdinal != handle.PoolOrdinal) && (pairPool.RightPoolOrdinal != handle.PoolOrdinal))) {
                    continue;
                }
                var rightCapacity = m_catalog.Pools[pairPool.RightPoolOrdinal].Capacity;

                for (var pairSlot = NextOccupiedPoolSlot(poolOrdinal: pairPool.Ordinal, start: 0); (pairSlot >= 0); pairSlot = NextOccupiedPoolSlot(poolOrdinal: pairPool.Ordinal, start: (pairSlot + 1))) {
                    _ = TryPoolKey(key: out var pairKey, slot: pairSlot);

                    if (((pairPool.LeftPoolOrdinal != handle.PoolOrdinal) || ((pairSlot / rightCapacity) != handle.Slot)) && ((pairPool.RightPoolOrdinal != handle.PoolOrdinal) || ((pairSlot % rightCapacity) != handle.Slot))) {
                        continue;
                    }
                    _ = TryRead(rowOrdinal: pairPool.DomainRowOrdinal, key: pairKey, value: out var pairGeneration);
                    if (!TryReleaseCore(handle: m_catalog.CreateInstanceHandle(poolOrdinal: pairPool.Ordinal, slot: pairSlot, generation: pairGeneration.AsInt), reason: out reason)) {
                        Rewind(mark: mark);
                        return false;
                    }
                }
            }
        }
        for (var fieldOrdinal = 0; (fieldOrdinal < pool.Fields.Count); fieldOrdinal++) {
            var field = pool.Fields[fieldOrdinal];

            RemovePoolCell(rowOrdinal: field.RowOrdinal, position: position);
        }
        RemovePoolCell(rowOrdinal: pool.DomainRowOrdinal, position: position);
        if (
            !TryWrite(rowOrdinal: pool.GenerationRowOrdinal, key: key, value: CellValue.Int(value: (handle.Generation + 1L)), reason: out reason)
        ) {
            Rewind(mark: mark);
            return false;
        }
        if (m_journal.OverCeiling) {
            reason = $"State pool '{pool.Name.Value}' release exceeds the {ArenaCapacity.MaxJournalBytes}-byte journal ceiling.";
            Rewind(mark: mark);
            return false;
        }

        Commit(mark: mark);
        reason = string.Empty;
        return true;
    }

    /// <summary>Attempts to claim the pair identified by two live endpoint lifetimes.</summary>
    public bool TryClaimPair(int poolOrdinal, StateInstanceHandle leftHandle, StateInstanceHandle rightHandle, out StateInstanceHandle handle, out string reason) => TryClaimPair(poolOrdinal: poolOrdinal, leftHandle: leftHandle, rightHandle: rightHandle, time: ArenaTime.Origin, handle: out handle, reason: out reason);
    /// <summary>Claims a pair and births its timed fields at <paramref name="time"/>.</summary>
    public bool TryClaimPair(int poolOrdinal, StateInstanceHandle leftHandle, StateInstanceHandle rightHandle, in ArenaTime time, out StateInstanceHandle handle, out string reason) {
        handle = default;
        if (!TryPool(pool: out var pool, poolOrdinal: poolOrdinal, reason: out reason) || !pool.IsPair) {
            reason = $"State pool ordinal {poolOrdinal} is not a pair pool.";
            return false;
        }
        if ((leftHandle.PoolOrdinal != pool.LeftPoolOrdinal) || (rightHandle.PoolOrdinal != pool.RightPoolOrdinal) || !TryResolve(handle: leftHandle, position: out _) || !TryResolve(handle: rightHandle, position: out _)) {
            reason = $"State pair pool '{pool.Name.Value}' requires live endpoints from its declared pools.";
            return false;
        }
        var leftSlot = leftHandle.Slot;
        var rightSlot = rightHandle.Slot;

        if (!pool.AllowSelf && (pool.LeftPoolOrdinal == pool.RightPoolOrdinal) && (leftSlot == rightSlot)) {
            reason = $"State pair pool '{pool.Name.Value}' does not allow self pairs.";
            return false;
        }
        if (!pool.Directed && (leftSlot > rightSlot)) {
            (leftSlot, rightSlot) = (rightSlot, leftSlot);
        }
        if (CellCount(rowOrdinal: pool.DomainRowOrdinal) >= pool.MaxLive) {
            reason = $"State pair pool '{pool.Name.Value}' holds its {pool.MaxLive} live pairs.";
            return false;
        }
        var slot = checked(((leftSlot * m_catalog.Pools[pool.RightPoolOrdinal].Capacity) + rightSlot));

        if (!TryPoolKey(key: out var key, slot: slot) || TryRead(rowOrdinal: pool.DomainRowOrdinal, key: key, value: out _)) {
            reason = $"State pair pool '{pool.Name.Value}' already holds that pair.";
            return false;
        }
        if (!TryRead(rowOrdinal: pool.GenerationRowOrdinal, key: key, value: out var generationValue)) {
            reason = $"State pair pool '{pool.Name.Value}' pair slot {slot} has no persistent generation.";
            return false;
        }
        var position = slot;
        var mark = BeginScope();

        m_poolMutationDepth++;
        try {
            InsertPoolCell(rowOrdinal: pool.DomainRowOrdinal, position: position, key: key, value: generationValue);
            for (var fieldOrdinal = 0; (fieldOrdinal < pool.Fields.Count); fieldOrdinal++) {
                var field = pool.Fields[fieldOrdinal];

                InsertPoolCell(rowOrdinal: field.RowOrdinal, position: position, key: key, value: field.Default);
                if ((field.Declaration.Advance is not null) && !TryWriteClock(rowOrdinal: field.RowOrdinal, key: key, epochTick: unchecked((long)time.Tick), epochEngineTick: unchecked((long)time.EngineTick), y0: 0L, v0: 0L, substepTicks: 0L, reason: out reason)) {
                    Rewind(mark: mark);
                    return false;
                }
            }
            if (m_journal.OverCeiling) {
                reason = $"State pair pool '{pool.Name.Value}' claim exceeds the {ArenaCapacity.MaxJournalBytes}-byte journal ceiling.";
                Rewind(mark: mark);
                return false;
            }
            Commit(mark: mark);
        } finally {
            m_poolMutationDepth--;
        }
        handle = m_catalog.CreateInstanceHandle(poolOrdinal: poolOrdinal, slot: slot, generation: generationValue.AsInt);
        reason = string.Empty;
        return true;
    }
    /// <summary>Resolves a live generation-checked instance to its fixed identity-slot storage position.</summary>
    public bool TryResolve(StateInstanceHandle handle, out int position) {
        var addresses = m_poolAddresses;

        if (
            handle.BelongsTo(catalogIdentity: m_catalog.Identity) &&
            (((uint)handle.PoolOrdinal) < ((uint)addresses.Length))
        ) {
            ref readonly var address = ref addresses[handle.PoolOrdinal];
            var slot = handle.Slot;

            if (
                (((uint)slot) < ((uint)address.Capacity)) &&
                ((address.Occupancy[(slot >> 6)] & (1UL << (slot & 63))) != 0UL) &&
                (m_numbers[(address.DomainCellStart + slot)] == handle.Generation)
            ) {
                position = slot;
                return true;
            }
        }
        position = -1;
        return false;
    }
    /// <summary>Resolves the currently live lifetime occupying one pool slot.</summary>
    public bool TryResolvePoolSlot(int poolOrdinal, int slot, out StateInstanceHandle handle) {
        var addresses = m_poolAddresses;

        if (((uint)poolOrdinal) < ((uint)addresses.Length)) {
            ref readonly var address = ref addresses[poolOrdinal];

            if (
                (((uint)slot) < ((uint)address.Capacity)) &&
                ((address.Occupancy[(slot >> 6)] & (1UL << (slot & 63))) != 0UL)
            ) {
                handle = m_catalog.CreateInstanceHandle(generation: m_numbers[(address.DomainCellStart + slot)], poolOrdinal: poolOrdinal, slot: slot);
                return true;
            }
        }
        handle = default;
        return false;
    }
    /// <summary>Attempts to read one field of a live pool instance.</summary>
    public bool TryRead(StateInstanceHandle handle, int fieldOrdinal, out CellValue value) {
        if (TryFieldSlot(cell: out var cell, field: out var field, fieldOrdinal: fieldOrdinal, handle: handle)) {
            value = ValueAt(layout: in m_layout[field.RowOrdinal], slot: cell);
            return true;
        }
        value = default;
        return false;
    }
    /// <summary>Attempts to read one field's effective value at <paramref name="time"/>.</summary>
    public bool TryReadLive(StateInstanceHandle handle, int fieldOrdinal, in ArenaTime time, out CellValue value) {
        if (!TryFieldSlot(cell: out var cell, field: out var field, fieldOrdinal: fieldOrdinal, handle: handle)) {
            value = default;
            return false;
        }
        if (field.Read != ArenaPoolFieldRead.Traited) {
            value = ValueAt(layout: in m_layout[field.RowOrdinal], slot: cell);
            return true;
        }
        _ = TryPoolKey(key: out var key, slot: handle.Slot);
        value = NumericValue(kind: m_layout[field.RowOrdinal].Kind, raw: LiveNumberAt(rowOrdinal: field.RowOrdinal, slot: cell, key: key, time: in time));
        return true;
    }
    /// <summary>Attempts to read one numeric field's stored number without materializing a carrier.</summary>
    /// <param name="handle">The generation-checked instance.</param>
    /// <param name="fieldOrdinal">The field's ordinal in the pool's record.</param>
    /// <param name="raw">The stored number, or zero when the read fails.</param>
    /// <returns><see langword="true"/> when the instance is live and the field is numeric.</returns>
    /// <remarks>The number is raw in the field's own kind, exactly as
    /// <see cref="TryReadRaw(int, CellKey, out long)"/> answers for a row. A text or vector field holds no number
    /// and answers <see langword="false"/>.</remarks>
    public bool TryReadRaw(StateInstanceHandle handle, int fieldOrdinal, out long raw) {
        if (
            TryFieldSlot(cell: out var cell, field: out var field, fieldOrdinal: fieldOrdinal, handle: handle) &&
            (field.Read != ArenaPoolFieldRead.Carrier)
        ) {
            raw = m_numbers[cell];
            return true;
        }
        raw = 0L;
        return false;
    }
    /// <summary>Attempts to read one numeric field's effective number at <paramref name="time"/> without
    /// materializing a carrier.</summary>
    /// <param name="handle">The generation-checked instance.</param>
    /// <param name="fieldOrdinal">The field's ordinal in the pool's record.</param>
    /// <param name="time">The evaluation time a traited field advances to.</param>
    /// <param name="raw">The effective number, or zero when the read fails.</param>
    /// <returns><see langword="true"/> when the instance is live and the field is numeric.</returns>
    public bool TryReadLiveRaw(StateInstanceHandle handle, int fieldOrdinal, in ArenaTime time, out long raw) {
        if (TryFieldSlot(cell: out var cell, field: out var field, fieldOrdinal: fieldOrdinal, handle: handle)) {
            if (field.Read == ArenaPoolFieldRead.Stored) {
                raw = m_numbers[cell];
                return true;
            }
            if (
                (field.Read == ArenaPoolFieldRead.Traited) &&
                TryPoolKey(key: out var key, slot: handle.Slot)
            ) {
                raw = LiveNumberAt(rowOrdinal: field.RowOrdinal, slot: cell, key: key, time: in time);
                return true;
            }
        }
        raw = 0L;
        return false;
    }

    // Resolves a handle and one of its fields to the field's storage cell. Every check reads the arena's own
    // flat address table, so a read costs array loads and no catalog walk.
    private bool TryFieldSlot(StateInstanceHandle handle, int fieldOrdinal, out ArenaPoolFieldAddress field, out int cell) {
        if (TryResolve(handle: handle, position: out var slot)) {
            var fields = m_poolAddresses[handle.PoolOrdinal].Fields;

            if (((uint)fieldOrdinal) < ((uint)fields.Length)) {
                field = fields[fieldOrdinal];
                cell = (field.CellStart + slot);
                if (((m_presence[(cell >> 6)] >> (cell & 63)) & 1UL) != 0UL) {
                    return true;
                }
            }
        }
        field = default;
        cell = -1;
        return false;
    }
    private static ArenaPoolAddress[] BuildPoolAddresses(StateCatalog catalog, ArenaLayout layout, ulong[][] occupancy) {
        var addresses = new ArenaPoolAddress[catalog.Pools.Count];

        foreach (var pool in catalog.Pools) {
            var fields = new ArenaPoolFieldAddress[pool.Fields.Count];

            for (var index = 0; (index < fields.Length); index++) {
                var rowOrdinal = pool.Fields[index].RowOrdinal;
                ref readonly var row = ref layout[rowOrdinal];

                fields[index] = new ArenaPoolFieldAddress(
                    CellStart: row.CellStart,
                    Read: ((row.Kind is (CellKind.Text or CellKind.Vector))
                        ? ArenaPoolFieldRead.Carrier
                        : (row.HasTraits ? ArenaPoolFieldRead.Traited : ArenaPoolFieldRead.Stored)),
                    RowOrdinal: rowOrdinal
                );
            }
            addresses[pool.Ordinal] = new ArenaPoolAddress(
                Capacity: pool.Capacity,
                DomainCellStart: layout[pool.DomainRowOrdinal].CellStart,
                Fields: fields,
                Occupancy: occupancy[pool.Ordinal]
            );
        }
        return addresses;
    }

    // How a pool field answers a number: straight from storage, through its row's traits, or not at all.
    private enum ArenaPoolFieldRead : byte {
        Stored,
        Traited,
        Carrier,
    }
    private readonly record struct ArenaPoolFieldAddress(int CellStart, int RowOrdinal, ArenaPoolFieldRead Read);
    private readonly record struct ArenaPoolAddress(int Capacity, int DomainCellStart, ulong[] Occupancy, ArenaPoolFieldAddress[] Fields);

    /// <summary>Attempts to read one vector field of a live pool instance.</summary>
    /// <remarks>The returned view is invalidated by an arena relayout or a structural edit of the pool.</remarks>
    public bool TryReadVector(StateInstanceHandle handle, int fieldOrdinal, out ReadOnlySpan<sbyte> components) {
        components = default;
        if (!TryResolve(handle: handle, position: out var position) ||
            (((uint)fieldOrdinal) >= ((uint)m_catalog.Pools[handle.PoolOrdinal].Fields.Count))) {
            return false;
        }
        ref readonly var layout = ref m_layout[m_catalog.Pools[handle.PoolOrdinal].Fields[fieldOrdinal].RowOrdinal];

        if ((layout.Kind != CellKind.Vector) || !Bit(words: m_presence, index: (layout.CellStart + position))) {
            return false;
        }
        components = VectorSpan(slot: (layout.CellStart + position));
        return true;
    }
    /// <summary>Attempts to write one field of a live pool instance.</summary>
    public bool TryWrite(StateInstanceHandle handle, int fieldOrdinal, CellValue value, out string reason) {
        m_poolMutationDepth++;
        try { return TryWritePoolFieldCore(fieldOrdinal: fieldOrdinal, handle: handle, reason: out reason, value: value); } finally { m_poolMutationDepth--; }
    }

    private bool TryWritePoolFieldCore(StateInstanceHandle handle, int fieldOrdinal, CellValue value, out string reason) {
        if (!TryResolve(handle: handle, position: out _)) {
            reason = "The state pool instance is absent or stale.";
            return false;
        }
        var pool = m_catalog.Pools[handle.PoolOrdinal];

        if (((uint)fieldOrdinal) >= ((uint)pool.Fields.Count)) {
            reason = $"State pool '{pool.Name.Value}' has no field ordinal {fieldOrdinal}.";
            return false;
        }
        if (!TryPoolKey(slot: handle.Slot, key: out var key)) {
            reason = $"State pool '{pool.Name.Value}' slot {handle.Slot} has no compiled key.";
            return false;
        }
        return TryWrite(rowOrdinal: pool.Fields[fieldOrdinal].RowOrdinal, key: key, value: value, reason: out reason);
    }

    /// <summary>Attempts to write one vector field of a live pool instance.</summary>
    public bool TryWriteVector(StateInstanceHandle handle, int fieldOrdinal, ReadOnlySpan<sbyte> components, out string reason) {
        m_poolMutationDepth++;
        try {
            if (!TryResolve(handle: handle, position: out _)) {
                reason = "The state pool instance is absent or stale.";
                return false;
            }
            var pool = m_catalog.Pools[handle.PoolOrdinal];

            if (((uint)fieldOrdinal) >= ((uint)pool.Fields.Count)) {
                reason = $"State pool '{pool.Name.Value}' has no field ordinal {fieldOrdinal}.";
                return false;
            }
            if (!TryPoolKey(slot: handle.Slot, key: out var key)) {
                reason = $"State pool '{pool.Name.Value}' slot {handle.Slot} has no compiled key.";
                return false;
            }
            return TryWriteVector(
                rowOrdinal: pool.Fields[fieldOrdinal].RowOrdinal,
                key: key,
                components: components,
                reason: out reason
            );
        } finally { m_poolMutationDepth--; }
    }
    /// <summary>Attempts a numeric set or add against one field of a live pool instance.</summary>
    public bool TryWrite(StateInstanceHandle handle, int fieldOrdinal, long operand, StateWriteKind write, out string reason) {
        m_poolMutationDepth++;
        try {
            if (!TryResolve(handle: handle, position: out _)) {
                reason = "The state pool instance is absent or stale.";
                return false;
            }
            var pool = m_catalog.Pools[handle.PoolOrdinal];

            if (((uint)fieldOrdinal) >= ((uint)pool.Fields.Count)) {
                reason = $"State pool '{pool.Name.Value}' has no field ordinal {fieldOrdinal}.";
                return false;
            }
            if (!TryPoolKey(slot: handle.Slot, key: out var key)) {
                reason = $"State pool '{pool.Name.Value}' slot {handle.Slot} has no compiled key.";
                return false;
            }
            return TryWrite(rowOrdinal: pool.Fields[fieldOrdinal].RowOrdinal, key: key, operand: operand, write: write, reason: out reason);
        } finally { m_poolMutationDepth--; }
    }
    /// <summary>Attempts a numeric set or add against a field's effective value, rebasing its clock.</summary>
    public bool TryWriteLive(StateInstanceHandle handle, int fieldOrdinal, long operand, StateWriteKind write, in ArenaTime time, out string reason) {
        m_poolMutationDepth++;
        try {
            if (!TryResolve(handle: handle, position: out _)) { reason = "The state pool instance is absent or stale."; return false; }
            var pool = m_catalog.Pools[handle.PoolOrdinal];

            if (((uint)fieldOrdinal) >= ((uint)pool.Fields.Count)) { reason = $"State pool '{pool.Name.Value}' has no field ordinal {fieldOrdinal}."; return false; }
            if (!TryPoolKey(slot: handle.Slot, key: out var key)) { reason = $"State pool '{pool.Name.Value}' slot {handle.Slot} has no compiled key."; return false; }
            return TryWriteLive(rowOrdinal: pool.Fields[fieldOrdinal].RowOrdinal, key: key, operand: operand, write: write, time: in time, reason: out reason);
        } finally { m_poolMutationDepth--; }
    }
    /// <summary>Returns a stable snapshot of the pool's live handles in ascending slot order.</summary>
    public IReadOnlyList<StateInstanceHandle> SnapshotPool(int poolOrdinal) {
        if (!TryPool(pool: out var pool, poolOrdinal: poolOrdinal, reason: out var reason)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(poolOrdinal), actualValue: poolOrdinal, message: reason);
        }

        var handles = new StateInstanceHandle[CellCount(rowOrdinal: pool.DomainRowOrdinal)];

        _ = CopyPoolSnapshotCore(destination: handles, pool: pool);
        return Array.AsReadOnly(array: handles);
    }
    /// <summary>Copies live handles in ascending slot order into caller-owned storage.</summary>
    /// <returns>The number copied, or the required count as a negative value when the destination is too short.</returns>
    public int CopyPoolSnapshot(int poolOrdinal, Span<StateInstanceHandle> destination) {
        if (!TryPool(pool: out var pool, poolOrdinal: poolOrdinal, reason: out _)) {
            return 0;
        }
        var count = CellCount(rowOrdinal: pool.DomainRowOrdinal);

        if (destination.Length < count) {
            return -count;
        }
        return CopyPoolSnapshotCore(destination: destination, pool: pool);
    }

    private int CopyPoolSnapshotCore(StatePoolDescriptor pool, Span<StateInstanceHandle> destination) {
        ref readonly var layout = ref m_layout[pool.DomainRowOrdinal];
        var count = 0;

        var occupancy = m_poolOccupancy[pool.Ordinal];

        for (var word = 0; (word < occupancy.Length); word++) {
            var occupied = occupancy[word];

            while (occupied != 0UL) {
                var slot = ((word << 6) + System.Numerics.BitOperations.TrailingZeroCount(value: occupied));

                destination[count++] = m_catalog.CreateInstanceHandle(
                    poolOrdinal: pool.Ordinal,
                    slot: slot,
                    generation: m_numbers[(layout.CellStart + slot)]
                );
                occupied &= (occupied - 1UL);
            }
        }
        return count;
    }

    /// <summary>Exports every pool declaration with its complete live allocator continuation.</summary>
    public IReadOnlyList<StatePool> ToPools() {
        var ordinary = m_catalog.Pools.Where(predicate: static pool => !pool.IsPair).ToArray();
        var pools = new StatePool[ordinary.Length];

        foreach (var pool in ordinary) {
            var generations = new long[pool.Capacity];

            for (var slot = 0; (slot < pool.Capacity); slot++) {
                _ = TryPoolKey(key: out var key, slot: slot);
                _ = TryRead(rowOrdinal: pool.GenerationRowOrdinal, key: key, value: out var generation);
                generations[slot] = generation.AsInt;
            }

            var live = new List<StatePoolSeed>();

            foreach (var handle in SnapshotPool(poolOrdinal: pool.Ordinal)) {
                var values = new StatePoolValue[pool.Fields.Count];

                for (var field = 0; (field < values.Length); field++) {
                    _ = TryRead(fieldOrdinal: field, handle: handle, value: out var value);
                    values[field] = PoolValue(handle: handle, field: pool.Fields[field], value: value);
                }
                live.Add(item: new StatePoolSeed(Slot: handle.Slot, Values: values));
            }

            pools[pool.Ordinal] = new StatePool(
                Name: pool.Name,
                Record: pool.Record,
                Capacity: pool.Capacity,
                Initial: null,
                Snapshot: new StatePoolSnapshot(Generations: generations, Live: live)
            );
        }
        return Array.AsReadOnly(array: pools);
    }
    /// <summary>Exports every pair pool declaration with its complete allocator continuation.</summary>
    public IReadOnlyList<StatePairPool> ToPairPools() {
        var pairs = m_catalog.Pools.Where(predicate: static pool => pool.IsPair).ToArray();
        var result = new StatePairPool[pairs.Length];

        for (var index = 0; (index < pairs.Length); index++) {
            var pool = pairs[index];
            var generations = new long[pool.Capacity];

            for (var slot = 0; (slot < pool.Capacity); slot++) { _ = TryPoolKey(key: out var key, slot: slot); _ = TryRead(rowOrdinal: pool.GenerationRowOrdinal, key: key, value: out var generation); generations[slot] = generation.AsInt; }
            var live = new List<StatePoolSeed>();

            foreach (var handle in SnapshotPool(poolOrdinal: pool.Ordinal)) {
                var values = new StatePoolValue[pool.Fields.Count];

                for (var field = 0; (field < values.Length); field++) { _ = TryRead(fieldOrdinal: field, handle: handle, value: out var value); values[field] = PoolValue(handle: handle, field: pool.Fields[field], value: value); }
                live.Add(item: new StatePoolSeed(Slot: handle.Slot, Values: values));
            }
            result[index] = new StatePairPool(Name: pool.Name, Record: pool.Record, LeftPool: m_catalog.Pools[pool.LeftPoolOrdinal].Name, RightPool: m_catalog.Pools[pool.RightPoolOrdinal].Name, MaxLive: pool.MaxLive, Directed: pool.Directed, AllowSelf: pool.AllowSelf, Snapshot: new StatePoolSnapshot(Generations: generations, Live: live));
        }
        return Array.AsReadOnly(array: result);
    }

    private StatePoolValue PoolValue(StateInstanceHandle handle, StatePoolFieldDescriptor field, CellValue value) {
        _ = TryPoolKey(slot: handle.Slot, key: out var key);
        _ = TryReadClock(rowOrdinal: field.RowOrdinal, key: key, epochTick: out var epochTick, epochEngineTick: out var epochEngineTick, y0: out var y0, v0: out var v0, substepTicks: out var substepTicks, set: out var set);
        return new StatePoolValue(
            Field: field.Name,
            Value: value,
            Clock: (set ? new StateCellClock(EpochEngineTick: epochEngineTick, EpochTick: epochTick, SubstepTicks: substepTicks, V0: v0, Y0: y0) : null));
    }
    private bool TryPool(int poolOrdinal, out StatePoolDescriptor pool, out string reason) {
        if (((uint)poolOrdinal) < ((uint)m_catalog.Pools.Count)) {
            pool = m_catalog.Pools[poolOrdinal];
            reason = string.Empty;
            return true;
        }
        pool = null!;
        reason = $"State pool ordinal {poolOrdinal} is outside the catalog.";
        return false;
    }

    /// <summary>Attempts to resolve the catalog-seeded numeric key for one pool identity slot.</summary>
    public bool TryPoolKey(int slot, out CellKey key) => m_catalog.TryGetPoolKey(key: out key, slot: slot);

    private bool PoolSlotOccupied(int poolOrdinal, int slot) => ((m_poolOccupancy[poolOrdinal][(slot >> 6)] & (1UL << (slot & 63))) != 0UL);
    private int FirstFreePoolSlot(StatePoolDescriptor pool) {
        var occupancy = m_poolOccupancy[pool.Ordinal];

        for (var word = 0; (word < occupancy.Length); word++) {
            var free = ~occupancy[word];

            if (free == 0UL) {
                continue;
            }
            var slot = ((word << 6) + System.Numerics.BitOperations.TrailingZeroCount(value: free));

            if (slot < pool.Capacity) {
                return slot;
            }
        }
        return -1;
    }
    // Fixed slot positions need no row reindex. Only the presence column changes the occupancy cache,
    // including when ordinary scopes or retained turns restore that column.
    private void InsertPoolCell(int rowOrdinal, int position, CellKey key, CellValue value) {
        ref readonly var layout = ref m_layout[rowOrdinal];
        var slot = (layout.CellStart + position);

        ClearCell(rowOrdinal: rowOrdinal, slot: slot, tailPush: false);
        WriteNumber(column: ArenaColumn.MemberKey, index: slot, value: key.Ordinal);
        WriteNumber(column: ArenaColumn.MemberCount, index: rowOrdinal, value: (m_memberCounts[rowOrdinal] + 1));
        StoreValueRaw(layout: layout, slot: slot, tailPush: false, value: value);
        RecomputeMembership(rowOrdinal: rowOrdinal);
    }
    private void RemovePoolCell(int rowOrdinal, int position) {
        ClearCell(rowOrdinal: rowOrdinal, slot: (m_layout[rowOrdinal].CellStart + position), tailPush: false);
        WriteNumber(column: ArenaColumn.MemberCount, index: rowOrdinal, value: (m_memberCounts[rowOrdinal] - 1));
        RecomputeMembership(rowOrdinal: rowOrdinal);
    }
    private int NextOccupiedPoolSlot(int poolOrdinal, int start) {
        var occupancy = m_poolOccupancy[poolOrdinal];
        var word = (start >> 6);

        if (word >= occupancy.Length) {
            return -1;
        }
        var occupied = occupancy[word] & (ulong.MaxValue << (start & 63));

        while (true) {
            if (occupied != 0UL) {
                return ((word << 6) + System.Numerics.BitOperations.TrailingZeroCount(value: occupied));
            }
            if (++word >= occupancy.Length) {
                return -1;
            }
            occupied = occupancy[word];
        }
    }
}
