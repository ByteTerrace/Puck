namespace Puck.World;

/// <summary>A private copy of one identity's record store that claims, releases and field writes land in, each
/// admitted by the writer that asks (<see cref="WorldIdentityWriter.Admits"/>), until
/// <see cref="WorldIdentity.Apply"/> installs it whole. Dropping a stage leaves the identity as it was, so a rule
/// firing that rewinds writes nothing to the identity.</summary>
/// <remarks>An instance is named by its pool, slot and generation: the arena chooses the lowest free slot and advances
/// the slot's generation on release, so a reference a player arranged or a world kept names exactly one lifetime and
/// reads absent once that lifetime ends. Rows are never keyed by a generated name, so the arena's append-only key table
/// does not grow with what players make.</remarks>
public sealed class WorldIdentityStage {
    internal WorldIdentityStage(WorldIdentity identity, WorldStateSection source, StateArena arena) {
        Identity = identity;
        Source = source;
        Arena = arena;
    }

    /// <summary>Gets the identity this stage was opened over.</summary>
    public WorldIdentity Identity { get; }
    /// <summary>Gets a value indicating whether a write has landed in the stage since it opened.</summary>
    public bool Dirty { get; private set; }

    internal StateArena Arena { get; private set; }
    internal WorldStateSection Source { get; }

    internal void MarkApplied() => Dirty = false;

    /// <summary>Claims the lowest free slot of a pool and writes its initial fields, all or nothing.</summary>
    /// <param name="writer">The writer asking.</param>
    /// <param name="pool">The pool's name on the identity.</param>
    /// <param name="values">The fields to write after the record's defaults, in order.</param>
    /// <param name="slot">The claimed slot, on success.</param>
    /// <param name="generation">The claimed lifetime's generation, on success.</param>
    /// <param name="reason">The refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the instance was claimed with every field written.</returns>
    public bool TryClaim(WorldIdentityWriter writer, CellName pool, ReadOnlySpan<WorldIdentityField> values, out int slot, out long generation, out string reason) {
        slot = -1;
        generation = 0L;
        if (!TryAdmitPool(descriptor: out var descriptor, pair: false, pool: pool, reason: out reason, writer: writer)) {
            return false;
        }
        var mark = Arena.BeginScope();

        if (!Arena.TryClaim(handle: out var handle, poolOrdinal: descriptor.Ordinal, reason: out reason) || !TryWriteFields(descriptor: descriptor, handle: handle, reason: out reason, values: values)) {
            Arena.Rewind(mark: mark);
            reason = $"identity '{Identity.Id}' pool '{pool}': {reason}";
            return false;
        }
        Arena.Commit(mark: mark);
        slot = handle.Slot;
        generation = handle.Generation;
        Dirty = true;
        return true;
    }
    /// <summary>Claims a pair joining two live instances and writes its initial fields, all or nothing.</summary>
    /// <param name="writer">The writer asking.</param>
    /// <param name="pairPool">The pair pool's name on the identity.</param>
    /// <param name="leftSlot">The left endpoint's slot in its pool.</param>
    /// <param name="rightSlot">The right endpoint's slot in its pool.</param>
    /// <param name="values">The fields to write after the record's defaults, in order.</param>
    /// <param name="slot">The claimed pair slot, on success.</param>
    /// <param name="generation">The claimed pair's generation, on success.</param>
    /// <param name="reason">The refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the pair was claimed with every field written.</returns>
    public bool TryClaimPair(WorldIdentityWriter writer, CellName pairPool, int leftSlot, int rightSlot, ReadOnlySpan<WorldIdentityField> values, out int slot, out long generation, out string reason) {
        slot = -1;
        generation = 0L;
        if (!TryAdmitPool(descriptor: out var descriptor, pair: true, pool: pairPool, reason: out reason, writer: writer)) {
            return false;
        }
        if (!Arena.TryResolvePoolSlot(handle: out var left, poolOrdinal: descriptor.LeftPoolOrdinal, slot: leftSlot) ||
            !Arena.TryResolvePoolSlot(handle: out var right, poolOrdinal: descriptor.RightPoolOrdinal, slot: rightSlot)) {
            reason = $"identity '{Identity.Id}' pair pool '{pairPool}': an endpoint slot ({leftSlot}, {rightSlot}) holds no live instance";
            return false;
        }
        var mark = Arena.BeginScope();

        if (!Arena.TryClaimPair(handle: out var handle, leftHandle: left, poolOrdinal: descriptor.Ordinal, reason: out reason, rightHandle: right) || !TryWriteFields(descriptor: descriptor, handle: handle, reason: out reason, values: values)) {
            Arena.Rewind(mark: mark);
            reason = $"identity '{Identity.Id}' pair pool '{pairPool}': {reason}";
            return false;
        }
        Arena.Commit(mark: mark);
        slot = handle.Slot;
        generation = handle.Generation;
        Dirty = true;
        return true;
    }
    /// <summary>Releases the live instance in a slot, and every pair it is an endpoint of.</summary>
    /// <param name="writer">The writer asking.</param>
    /// <param name="pool">The pool's name on the identity.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="generation">The lifetime the caller names, or <see langword="null"/> for whichever is live.</param>
    /// <param name="reason">The refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the instance was released.</returns>
    public bool TryRelease(WorldIdentityWriter writer, CellName pool, int slot, long? generation, out string reason) {
        if (!TryAdmitPool(descriptor: out var descriptor, pair: null, pool: pool, reason: out reason, writer: writer) ||
            !TryLive(descriptor: descriptor, generation: generation, handle: out var handle, reason: out reason, slot: slot)) {
            return false;
        }
        if (!Arena.TryRelease(handle: handle, reason: out reason)) {
            reason = $"identity '{Identity.Id}' pool '{pool}' slot {slot}: {reason}";
            return false;
        }
        Dirty = true;
        return true;
    }
    /// <summary>Writes one field of the live instance in a slot, under the field's own kind and bounds.</summary>
    /// <param name="writer">The writer asking.</param>
    /// <param name="pool">The pool's name on the identity.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="generation">The lifetime the caller names, or <see langword="null"/> for whichever is live.</param>
    /// <param name="field">The record field.</param>
    /// <param name="value">The value.</param>
    /// <param name="reason">The refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the field was written.</returns>
    public bool TryWrite(WorldIdentityWriter writer, CellName pool, int slot, long? generation, CellName field, CellValue value, out string reason) {
        if (!TryAdmitPool(descriptor: out var descriptor, pair: null, pool: pool, reason: out reason, writer: writer) ||
            !TryLive(descriptor: descriptor, generation: generation, handle: out var handle, reason: out reason, slot: slot)) {
            return false;
        }
        if (!TryWriteFields(descriptor: descriptor, handle: handle, reason: out reason, values: [new WorldIdentityField(Field: field, Value: value)])) {
            reason = $"identity '{Identity.Id}' pool '{pool}' slot {slot}: {reason}";
            return false;
        }
        Dirty = true;
        return true;
    }
    /// <summary>Returns the kind a pool's record declares for one field.</summary>
    /// <param name="pool">The pool's name on the identity.</param>
    /// <param name="field">The record field.</param>
    /// <param name="kind">The field's kind, on success.</param>
    /// <param name="reason">Why there is none, or empty on success.</param>
    /// <returns><see langword="true"/> when the store holds the pool and its record the field.</returns>
    public bool TryFieldKind(CellName pool, CellName field, out CellKind kind, out string reason) {
        kind = default;
        if (!Arena.Catalog.TryGetPool(name: pool, pool: out var descriptor)) {
            reason = $"identity '{Identity.Id}' holds no pool '{pool}'";
            return false;
        }
        foreach (var member in descriptor.Fields) {
            if (member.Name == field) {
                kind = member.Kind;
                reason = string.Empty;
                return true;
            }
        }
        reason = $"identity '{Identity.Id}' pool '{pool}' record '{descriptor.Record}' has no field '{field}'";
        return false;
    }
    /// <summary>Reads one field of the live instance in a slot, as the stage holds it.</summary>
    /// <param name="pool">The pool's name on the identity.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="field">The record field.</param>
    /// <param name="value">The value, on success.</param>
    /// <returns><see langword="true"/> when the slot holds a live instance with that field.</returns>
    public bool TryRead(CellName pool, int slot, CellName field, out CellValue value) => TryReadField(arena: Arena, field: field, pool: pool, slot: slot, value: out value);

    // Replaces one pool's live instances and generations with the ones a carried section holds for the same pool, when
    // both declare it alike. Used only to adopt what a colocated traveller carried home.
    internal bool TryReplacePool(WorldStateSection carried, CellName pool, out string reason) {
        if ((carried.Pools?.FirstOrDefault(predicate: candidate => (candidate.Name == pool)) is not { } incoming) ||
            (Source.Pools?.FirstOrDefault(predicate: candidate => (candidate.Name == pool)) is not { } declared) ||
            (incoming.Record != declared.Record) || (incoming.Capacity != declared.Capacity) ||
            !SameRecord(left: carried.Records?.FirstOrDefault(predicate: record => (record.Name == incoming.Record)), right: Source.Records?.FirstOrDefault(predicate: record => (record.Name == declared.Record)))) {
            reason = "the carried pool is not declared as this identity declares it";
            return false;
        }
        var pairs = Arena.ToPairPools();
        var section = Source with {
            Pools = Arena.ToPools().Select(selector: live => ((live.Name == pool) ? incoming : live)).ToArray(),
            PairPools = ((pairs.Count > 0) ? pairs : null),
        };
        var time = ArenaTime.Origin;

        if (!StateArena.TryCreate(arena: out var arena, catalog: StateCatalog.Compile(section: section), options: null, reason: out reason, section: section, time: in time)) {
            return false;
        }
        Arena = arena;
        Dirty = true;
        return true;
    }

    // Two record declarations hold the same fields in the same order, each of the same kind and bounds.
    private static bool SameRecord(StateRecord? left, StateRecord? right) {
        if ((left is null) || (right is null)) {
            return false;
        }
        var leftFields = (left.Fields ?? []);
        var rightFields = (right.Fields ?? []);

        if (leftFields.Count != rightFields.Count) {
            return false;
        }
        for (var index = 0; (index < leftFields.Count); index++) {
            var (a, b) = (leftFields[index], rightFields[index]);

            if ((a.Name != b.Name) || (a.Kind != b.Kind) || (a.Min != b.Min) || (a.Max != b.Max) || (a.Enum != b.Enum)) {
                return false;
            }
        }
        return true;
    }

    internal static bool TryReadField(StateArena arena, CellName pool, int slot, CellName field, out CellValue value) {
        value = default;
        if (!arena.Catalog.TryGetPool(name: pool, pool: out var descriptor) || !arena.TryResolvePoolSlot(handle: out var handle, poolOrdinal: descriptor.Ordinal, slot: slot)) {
            return false;
        }
        foreach (var member in descriptor.Fields) {
            if (member.Name == field) {
                return arena.TryRead(fieldOrdinal: member.Ordinal, handle: handle, value: out value);
            }
        }
        return false;
    }

    private bool TryAdmitPool(WorldIdentityWriter writer, CellName pool, bool? pair, out StatePoolDescriptor descriptor, out string reason) {
        descriptor = null!;
        if (!Arena.Catalog.TryGetPool(name: pool, pool: out var found)) {
            reason = $"identity '{Identity.Id}' holds no pool '{pool}'";
            return false;
        }
        if ((pair is { } wantsPair) && (found.IsPair != wantsPair)) {
            reason = $"identity '{Identity.Id}' pool '{pool}' {(found.IsPair ? "is a pair pool" : "is not a pair pool")}";
            return false;
        }
        if (!writer.Admits(fact: false, identity: Identity.Document?.Identity, name: pool, reason: out reason)) {
            return false;
        }
        descriptor = found;
        return true;
    }
    private bool TryLive(StatePoolDescriptor descriptor, int slot, long? generation, out StateInstanceHandle handle, out string reason) {
        if (!Arena.TryResolvePoolSlot(handle: out handle, poolOrdinal: descriptor.Ordinal, slot: slot) || ((generation is { } named) && (handle.Generation != named))) {
            reason = $"identity '{Identity.Id}' pool '{descriptor.Name}' slot {slot} holds no live instance{((generation is { } wanted) ? $" of generation {wanted}" : string.Empty)}";
            return false;
        }
        reason = string.Empty;
        return true;
    }
    private bool TryWriteFields(StatePoolDescriptor descriptor, StateInstanceHandle handle, ReadOnlySpan<WorldIdentityField> values, out string reason) {
        foreach (var value in values) {
            var ordinal = -1;

            foreach (var member in descriptor.Fields) {
                if (member.Name == value.Field) {
                    ordinal = member.Ordinal;
                    break;
                }
            }
            if (ordinal < 0) {
                reason = $"record '{descriptor.Record}' has no field '{value.Field}'";
                return false;
            }
            if (!Arena.TryWrite(fieldOrdinal: ordinal, handle: handle, reason: out reason, value: value.Value)) {
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }
}
/// <summary>One record field and the value a write gives it.</summary>
/// <param name="Field">The record field.</param>
/// <param name="Value">The value.</param>
public readonly record struct WorldIdentityField(CellName Field, CellValue Value);
