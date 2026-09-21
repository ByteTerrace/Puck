using Puck.Maths;

namespace Puck.State;

/// <summary>One retained undo group's load-time plan.</summary>
/// <param name="Name">The stable rule-group name.</param>
/// <param name="Rows">The complete catalog row closure the group may rewind.</param>
/// <param name="Depth">The most committed turns retained.</param>
public sealed record ArenaUndoPlan(string Name, int[] Rows, int Depth);
/// <summary>A typed, checkpoint-safe snapshot of an arena's retained undo history.</summary>
/// <param name="Groups">The configured groups in declaration order.</param>
public sealed record ArenaUndoSnapshot(IReadOnlyList<ArenaUndoGroupSnapshot> Groups);
/// <summary>One undo group's retained ring and optional turn still spanning ticks.</summary>
public sealed record ArenaUndoGroupSnapshot(string Name, int Depth, int[] Rows, IReadOnlyList<ArenaUndoSegmentSnapshot> Segments, ArenaUndoSegmentSnapshot? Pending);
/// <summary>One closed or pending retained turn.</summary>
public sealed record ArenaUndoSegmentSnapshot(bool Rewindable, IReadOnlyList<ArenaUndoEntrySnapshot> Entries);
/// <summary>One retained overwritten arena position.</summary>
public sealed record ArenaUndoEntrySnapshot(ArenaColumn Column, int Index, long Number, string? Text, StateVisibility? Visibility, StateObservation? Observation, string? MemberKey, sbyte[]? Components);
public sealed partial class StateArena {
    private Dictionary<string, ArenaUndoGroup>? m_undo;
    private ArenaUndoGroup? m_undoWriter;

    /// <summary>Configures retained turn journals. Repeating the same plan preserves history; a changed plan replaces
    /// that group's history. An empty plan releases all undo storage.</summary>
    public void ConfigureUndo(IReadOnlyList<ArenaUndoPlan>? plans) {
        if (m_undoWriter is not null) {
            throw new InvalidOperationException(message: "Undo plans cannot change while a group pass is executing.");
        }
        if (plans is not { Count: > 0 }) {
            m_undo = null;
            m_undoWriter = null;
            return;
        }

        var next = new Dictionary<string, ArenaUndoGroup>(capacity: plans.Count, comparer: StringComparer.Ordinal);
        var retainedBytes = 0L;

        foreach (var plan in plans) {
            ArgumentNullException.ThrowIfNull(plan);
            if (string.IsNullOrWhiteSpace(value: plan.Name) || (plan.Depth < 1) || (plan.Rows.Length == 0)) {
                throw new ArgumentException(message: "An undo plan requires a name, at least one row, and positive depth.", paramName: nameof(plans));
            }
            var rows = plan.Rows.Distinct().Order().ToArray();

            if (rows.Any(predicate: row => (((uint)row) >= ((uint)m_layout.RowCount)))) {
                throw new ArgumentException(message: $"Undo group '{plan.Name}' names a row outside the arena.", paramName: nameof(plans));
            }
            if (rows.Any(predicate: row => m_layout[row].HostOwned)) {
                throw new ArgumentException(message: $"Undo group '{plan.Name}' names host-owned state, which the arena cannot rewind.", paramName: nameof(plans));
            }
            retainedBytes = checked((retainedBytes + EstimateUndoGroupBytes(catalog: m_catalog, depth: plan.Depth, layout: m_layout, rows: rows)));
            if (retainedBytes > ArenaCapacity.MaxJournalBytes) {
                throw new ArgumentException(message: $"Undo group '{plan.Name}' depth {plan.Depth} exceeds the {ArenaCapacity.MaxJournalBytes}-byte journal ceiling at its worst case.", paramName: nameof(plans));
            }
            if (next.ContainsKey(key: plan.Name)) {
                throw new ArgumentException(message: $"Undo group '{plan.Name}' is configured more than once.", paramName: nameof(plans));
            }
            if ((m_undo is not null) && m_undo.TryGetValue(key: plan.Name, value: out var existing) && existing.Matches(rows, plan.Depth)) {
                next.Add(key: plan.Name, value: existing);
            } else {
                next.Add(key: plan.Name, value: new ArenaUndoGroup(plan.Name, rows, plan.Depth, UndoEntryCapacity(catalog: m_catalog, layout: m_layout, rows: rows), UndoComponentCapacity(layout: m_layout, rows: rows)));
            }
        }
        m_undo = next;
    }
    /// <summary>Begins a logical turn which may span ordinary journal scopes and simulation ticks.</summary>
    public void BeginUndoTurn(string group) {
        var state = RequireUndo(group: group);

        if (state.Pending is not null) {
            throw new InvalidOperationException(message: $"Undo group '{group}' already has a turn in progress.");
        }
        state.Begin();
    }
    /// <summary>Gets whether the named group has a logical turn spanning scopes or ticks.</summary>
    public bool UndoTurnPending(string group) => (RequireUndo(group: group).Pending is not null);
    /// <summary>Attributes committed writes to one undo group's current pass.</summary>
    public void BeginUndoPass(string group) {
        if (m_undoWriter is not null) {
            throw new InvalidOperationException(message: "An undo group pass is already executing.");
        }
        m_undoWriter = RequirePendingUndo(group: group);
    }
    /// <summary>Ends the current undo-group write attribution.</summary>
    public void EndUndoPass(string group) {
        if (!ReferenceEquals(objA: m_undoWriter, objB: RequireUndo(group: group))) {
            throw new InvalidOperationException(message: $"Undo group '{group}' is not the executing pass.");
        }
        m_undoWriter = null;
    }
    /// <summary>Closes and retains the logical turn in the group's bounded ring.</summary>
    public void CommitUndoTurn(string group) {
        var state = RequirePendingUndo(group: group);

        state.CommitPending();
    }
    /// <summary>Abandons a logical turn's retained record while keeping its already committed state writes.</summary>
    public void CancelUndoTurn(string group) {
        var state = RequirePendingUndo(group: group);

        if (ReferenceEquals(objA: state, objB: m_undoWriter)) {
            throw new InvalidOperationException(message: $"Undo group '{group}' cannot cancel while its pass is executing.");
        }
        state.CancelPending();
    }
    /// <summary>Restores and removes the newest rewindable turn.</summary>
    public bool TryRewindTurn(string group, out string reason) {
        if (m_journal.Scopes != 0) {
            reason = $"undo group '{group}' cannot rewind inside an open arena scope";
            return false;
        }
        if (m_undoWriter is not null) {
            reason = $"undo group '{group}' cannot rewind while another undo pass is executing";
            return false;
        }
        var state = RequireUndo(group: group);

        if (state.Pending is not null) {
            reason = $"undo group '{group}' has an unsettled turn";
            return false;
        }
        if (!state.TryPeek(segment: out var segment)) {
            reason = $"undo group '{group}' has no retained turn";
            return false;
        }
        if (!segment.Rewindable) {
            reason = $"undo group '{group}' newest turn wrote outside its declared rows";
            return false;
        }
        RestoreUndo(owner: state, segment: segment);
        state.Pop();
        reason = string.Empty;
        return true;
    }
    /// <summary>Exports all retained and in-progress turn records for a typed checkpoint.</summary>
    public ArenaUndoSnapshot ExportUndoSnapshot() => new(Groups: ((m_undo is null)
        ? []
        : m_undo.Values.Select(selector: group => group.Snapshot()).ToArray()));
    /// <summary>Restores a typed undo checkpoint against the already configured plans.</summary>
    public bool TryImportUndoSnapshot(ArenaUndoSnapshot snapshot, out string reason) {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (m_journal.Scopes != 0) {
            throw new InvalidOperationException(message: "Undo history is imported only between ordinary arena scopes.");
        }
        if (!ValidateUndoSnapshot(reason: out reason, snapshot: snapshot)) {
            return false;
        }
        if (snapshot.Groups.Count == 0) {
            return true;
        }
        foreach (var item in snapshot.Groups) {
            m_undo![item.Name].Restore(keys: m_keys, snapshot: item);
        }
        reason = string.Empty;
        return true;
    }
    /// <summary>Checks a typed undo checkpoint against the configured plans without changing the arena.</summary>
    public bool ValidateUndoSnapshot(ArenaUndoSnapshot snapshot, out string reason) {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Groups is null) {
            reason = "undo checkpoint contains a null group list";
            return false;
        }
        if ((snapshot.Groups.Count == 0) && (m_undo is null)) {
            reason = string.Empty;
            return true;
        }
        if ((m_undo is null) || (snapshot.Groups.Count != m_undo.Count)) {
            reason = "undo checkpoint does not match the arena's configured groups";
            return false;
        }
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var group in snapshot.Groups) {
            if ((group is null) || (group.Name is null) || (group.Rows is null) || (group.Segments is null)) {
                reason = "undo checkpoint contains a null group, row list, or segment list";
                return false;
            }
            if (!names.Add(item: group.Name) || !m_undo.TryGetValue(key: group.Name, value: out var configured) || !configured.Matches(group.Rows, group.Depth) || (group.Segments.Count > group.Depth)) {
                reason = $"undo checkpoint group '{group.Name}' does not match its configured plan";
                return false;
            }
            if (group.Segments.Any(predicate: segment => (segment is null))) {
                reason = $"undo checkpoint group '{group.Name}' contains a null retained segment";
                return false;
            }
            foreach (var segment in group.Segments.Append(element: group.Pending).Where(predicate: segment => (segment is not null))) {
                if (segment!.Entries is null) {
                    reason = $"undo checkpoint group '{group.Name}' contains a null entry list";
                    return false;
                }
                var positions = new HashSet<long>();

                foreach (var entry in segment.Entries) {
                    if (entry is null) {
                        reason = $"undo checkpoint group '{group.Name}' contains a null journal entry";
                        return false;
                    }
                    if (!Enum.IsDefined(value: entry.Column) || (entry.Index < 0) || (entry.Index >= m_layout.Size(column: entry.Column))) {
                        reason = $"undo checkpoint group '{group.Name}' carries an out-of-range journal position";
                        return false;
                    }
                    var row = m_layout.RowOf(column: entry.Column, index: entry.Index);
                    var identity = (((long)((byte)entry.Column)) << 32) | ((uint)entry.Index);

                    if ((row < 0) || !configured.Contains(row: row) || !positions.Add(item: identity)) {
                        reason = $"undo checkpoint group '{group.Name}' carries an undeclared or duplicate journal position";
                        return false;
                    }
                    if (entry.Column == ArenaColumn.Vector) {
                        if ((row < 0) || (entry.Components?.Length != m_layout[row].Dimensions)) {
                            reason = $"undo checkpoint group '{group.Name}' carries a vector snapshot of the wrong width";
                            return false;
                        }
                    } else if (entry.Components is not null) {
                        reason = $"undo checkpoint group '{group.Name}' carries vector components for a non-vector column";
                        return false;
                    }
                    var payloadMatches = entry.Column switch {
                        ArenaColumn.Text or ArenaColumn.Provenance => ((entry.Visibility is null) && (entry.Observation is null)),
                        ArenaColumn.Visibility => ((entry.Text is null) && (entry.Observation is null)),
                        ArenaColumn.Observation => ((entry.Text is null) && (entry.Visibility is null)),
                        _ => ((entry.Text is null) && (entry.Visibility is null) && (entry.Observation is null)),
                    };

                    if (!payloadMatches) {
                        reason = $"undo checkpoint group '{group.Name}' carries a payload incompatible with its column";
                        return false;
                    }
                    if (((entry.Column == ArenaColumn.MemberKey) && ((entry.MemberKey is null) != (entry.Number == -1L))) || ((entry.Column != ArenaColumn.MemberKey) && (entry.MemberKey is not null))) {
                        reason = $"undo checkpoint group '{group.Name}' carries a member-key payload on the wrong column";
                        return false;
                    }
                    if ((entry.MemberKey is not null) && (!CellName.TryParse(candidate: entry.MemberKey, name: out var memberName, reason: out _) || !m_keys.TryResolve(key: out _, name: memberName))) {
                        reason = $"undo checkpoint group '{group.Name}' names an unknown retained member key '{entry.MemberKey}'";
                        return false;
                    }
                    if (!ValidateUndoEntry(entry: entry, reason: out var entryReason, row: row)) {
                        reason = $"undo checkpoint group '{group.Name}' {entryReason}";
                        return false;
                    }
                }
            }
            if (!ValidateUndoChain(configured: configured, reason: out var chainReason, snapshot: group)) {
                reason = $"undo checkpoint group '{group.Name}' {chainReason}";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    private bool ValidateUndoChain(ArenaUndoGroup configured, ArenaUndoGroupSnapshot snapshot, out string reason) {
        var projected = new Dictionary<long, ArenaUndoEntrySnapshot>();
        var segments = ((snapshot.Pending is null) ? snapshot.Segments.ToArray() : snapshot.Segments.Append(element: snapshot.Pending).ToArray());

        for (var segmentIndex = (segments.Length - 1); (segmentIndex >= 0); segmentIndex--) {
            foreach (var entry in segments[segmentIndex].Entries) {
                projected[(((long)((byte)entry.Column)) << 32) | ((uint)entry.Index)] = entry;
            }
            foreach (var row in configured.Rows) {
                ref readonly var layout = ref m_layout[row];

                if (layout.Shape is not (RowShape.Keyed or RowShape.Ordered)) {
                    continue;
                }
                var names = new HashSet<string>(comparer: StringComparer.Ordinal);
                var occupied = 0L;

                for (var position = 0; (position < layout.CellCapacity); position++) {
                    var slot = (layout.CellStart + position);
                    var member = ProjectedMemberName(projected: projected, slot: slot);
                    var present = (ProjectedNumber(column: ArenaColumn.Presence, index: slot, projected: projected) != 0L);

                    if ((member is null) != !present) {
                        reason = $"would restore row '{m_rows[row].Name.Value}' with membership and presence disagreeing";
                        return false;
                    }
                    if (member is not null) {
                        if (m_catalog.IsPoolRow(rowOrdinal: row) && (!TryPoolKey(key: out var poolKey, slot: position) || !string.Equals(a: member, b: m_keys[poolKey].Value, comparisonType: StringComparison.Ordinal))) {
                            reason = $"would restore pool row '{m_rows[row].Name.Value}' with a member outside its fixed identity slot";
                            return false;
                        }
                        occupied++;
                        if (!names.Add(item: member)) {
                            reason = $"would restore duplicate member '{member}' in row '{m_rows[row].Name.Value}'";
                            return false;
                        }
                    }
                }
                if (ProjectedNumber(column: ArenaColumn.MemberCount, index: row, projected: projected) != occupied) {
                    reason = $"would restore row '{m_rows[row].Name.Value}' with a member count inconsistent with its slots";
                    return false;
                }
            }
            foreach (var pool in m_catalog.Pools) {
                if (!configured.Contains(row: pool.GenerationRowOrdinal)) {
                    continue;
                }
                var live = ProjectedRowNumbers(row: pool.DomainRowOrdinal, projected: projected);
                var generations = ProjectedRowNumbers(row: pool.GenerationRowOrdinal, projected: projected);

                foreach (var pair in generations) {
                    if (pair.Value < 0L) {
                        reason = $"would restore pool '{pool.Name.Value}' with a negative generation";
                        return false;
                    }
                }
                foreach (var pair in live) {
                    if (!generations.TryGetValue(key: pair.Key, value: out var generation) || (generation != pair.Value)) {
                        reason = $"would restore pool '{pool.Name.Value}' with a live lifetime different from its generation row";
                        return false;
                    }
                }
                foreach (var field in pool.Fields) {
                    var fields = ProjectedRowNumbers(row: field.RowOrdinal, projected: projected);

                    if ((fields.Count != live.Count) || fields.Keys.Any(predicate: key => !live.ContainsKey(key: key))) {
                        reason = $"would restore pool '{pool.Name.Value}' with field '{field.Name.Value}' live keys different from its domain";
                        return false;
                    }
                }
            }
        }
        reason = string.Empty;
        return true;
    }
    private long ProjectedNumber(ArenaColumn column, int index, IReadOnlyDictionary<long, ArenaUndoEntrySnapshot> projected) => (projected.TryGetValue(key: (((long)((byte)column)) << 32) | ((uint)index), value: out var entry)
        ? entry.Number
        : ReadNumberRaw(column: column, index: index));
    private string? ProjectedMemberName(IReadOnlyDictionary<long, ArenaUndoEntrySnapshot> projected, int slot) {
        if (projected.TryGetValue(key: (((long)((byte)ArenaColumn.MemberKey)) << 32) | ((uint)slot), value: out var entry)) {
            return entry.MemberKey;
        }
        var ordinal = ReadNumberRaw(column: ArenaColumn.MemberKey, index: slot);

        return ((ordinal < 0L) ? null : m_keys.Names[((int)ordinal)].Value);
    }
    private Dictionary<string, long> ProjectedRowNumbers(int row, IReadOnlyDictionary<long, ArenaUndoEntrySnapshot> projected) {
        ref readonly var layout = ref m_layout[row];
        var values = new Dictionary<string, long>(capacity: layout.CellCapacity, comparer: StringComparer.Ordinal);

        for (var position = 0; (position < layout.CellCapacity); position++) {
            var slot = (layout.CellStart + position);
            var name = ProjectedMemberName(projected: projected, slot: slot);

            if ((name is not null) && (ProjectedNumber(column: ArenaColumn.Presence, index: slot, projected: projected) != 0L)) {
                values.Add(key: name, value: ProjectedNumber(column: ArenaColumn.Number, index: slot, projected: projected));
            }
        }
        return values;
    }
    private bool ValidateUndoEntry(ArenaUndoEntrySnapshot entry, int row, out string reason) {
        ref readonly var layout = ref m_layout[row];

        if (UndoPositionCount(catalog: m_catalog, column: entry.Column, layout: layout, row: row) == 0) {
            reason = $"carries column '{entry.Column}' which row '{m_rows[row].Name.Value}' cannot store";
            return false;
        }
        if (((entry.Column is (ArenaColumn.Vector or ArenaColumn.Text or ArenaColumn.Provenance or ArenaColumn.Visibility or ArenaColumn.Observation)) && (entry.Number != 0L)) || ((entry.Column == ArenaColumn.MemberKey) && (entry.Number is not (-1L or 0L)))) {
            reason = $"carries a noncanonical number for column '{entry.Column}'";
            return false;
        }
        if (entry.Column == ArenaColumn.Number) {
            var declaration = m_rows[row];
            StateEnum? symbols = null;

            if (declaration.Enum is { } enumName) {
                _ = m_catalog.TryGetEnum(name: enumName, symbols: out symbols);
            }
            if (!declaration.TryAdmitWrite(current: 0L, operand: entry.Number, write: StateWriteKind.Set, stored: out var admitted, reason: out _, symbols: symbols) || (admitted != entry.Number)) {
                reason = $"carries a number outside row '{declaration.Name.Value}' admission";
                return false;
            }
        } else if ((entry.Column is (ArenaColumn.Presence or ArenaColumn.ClockSet or ArenaColumn.LaneRoster)) && (entry.Number is not (0L or 1L))) {
            reason = "carries a non-bit value in a bit column";
            return false;
        } else if ((entry.Column == ArenaColumn.MemberCount) && ((entry.Number < 0L) || (entry.Number > layout.CellCapacity))) {
            reason = "carries a member count outside its row capacity";
            return false;
        } else if ((entry.Column == ArenaColumn.Behavior) && ((entry.Number < byte.MinValue) || (entry.Number > byte.MaxValue) || !Enum.IsDefined(value: ((StateCellBehavior)((byte)entry.Number))))) {
            reason = "carries an unknown cell behavior";
            return false;
        } else if ((entry.Column == ArenaColumn.Text) && ((entry.Text?.Length ?? 0) > StateCapacity.MaxTextValueLength)) {
            reason = "carries text past the state text ceiling";
            return false;
        } else if ((entry.Column == ArenaColumn.Provenance) && ((entry.Text?.Length ?? 0) > StateCapacity.MaxProvenanceLength)) {
            reason = "carries provenance past its length ceiling";
            return false;
        } else if ((entry.Column == ArenaColumn.Visibility) && !StateVisibilityStorage.TryMeasure(value: entry.Visibility, bytes: out _, reason: out reason)) {
            return false;
        }
        reason = string.Empty;
        return true;
    }

    internal void RetainCommittedEntries(int mark) {
        if (m_undo is null) {
            return;
        }
        for (var index = mark; (index < m_journal.Length); index++) {
            var entry = m_journal[index];
            var changeSlot = (m_layout.ChangeBase(column: entry.Column) + entry.Index);

            RetainWrite(entry, ((entry.Column == ArenaColumn.Vector) ? m_journal.Components(entry: entry) : default), differs: m_changeDiffers[changeSlot]);
        }
    }

    private void RetainDirectWrite(ArenaColumn column, int index, long number, object? reference = null, ReadOnlySpan<sbyte> components = default) {
        if (m_undo is null) {
            return;
        }
        RetainWrite(new ArenaJournalEntry(Column: column, Index: index, Number: number, Reference: reference), components, differs: null);
    }
    private void RetainWrite(ArenaJournalEntry entry, ReadOnlySpan<sbyte> components, bool? differs) {
        if (m_undo is null) {
            return;
        }
        var row = m_layout.RowOf(column: entry.Column, index: entry.Index);

        foreach (var group in m_undo.Values) {
            if (ReferenceEquals(objA: group, objB: m_undoWriter)) {
                if ((row < 0) || !group.Contains(row: row)) {
                    group.Pending!.Rewindable = false;
                } else if (UndoPositionCount(catalog: m_catalog, column: entry.Column, layout: m_layout[row], row: row) == 0) {
                    // The compact plan admits only mutation columns reachable for this row. If a future write door
                    // reaches another one, preserve the hard storage bound and refuse this turn's rewind. Member
                    // insertion and compaction reset every materialized cell column, including irrelevant zero
                    // metadata. Compare the first pre-scope value with the final value so those resets remain a
                    // no-op, while a real excluded-column change still makes the turn unrewindable.
                    if (differs ?? Differs(entry: entry)) {
                        group.Pending!.Rewindable = false;
                    }
                } else {
                    var memberKey = (((entry.Column == ArenaColumn.MemberKey) && (entry.Number >= 0)) ? m_keys.Names[((int)entry.Number)].Value : null);

                    group.Add(components: components, entry: entry, memberKey: memberKey);
                }
            } else if ((row >= 0) && group.Contains(row: row) && (group.Pending is not null)) {
                group.Pending.Rewindable = false;
            } else if ((row >= 0) && group.Contains(row: row)) {
                group.InvalidateLatest();
            }
        }
    }

    internal void ClearUndo() {
        m_undo = null;
        m_undoWriter = null;
    }

    /// <summary>Returns the retained journal reservation for all configured groups, including one pending segment
    /// per group beside its closed ring.</summary>
    public static long EstimateUndoBytes(StateCatalog catalog, ArenaLayout layout, IReadOnlyList<ArenaUndoPlan> plans) {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(plans);
        var bytes = 0L;

        foreach (var plan in plans) {
            var rows = plan.Rows.Distinct().Order().ToArray();

            bytes = checked((bytes + EstimateUndoGroupBytes(catalog: catalog, depth: plan.Depth, layout: layout, rows: rows)));
        }
        return bytes;
    }

    private static long EstimateUndoGroupBytes(StateCatalog catalog, ArenaLayout layout, int[] rows, int depth) {
        var entryCapacity = UndoEntryCapacity(catalog: catalog, layout: layout, rows: rows);
        var componentCapacity = UndoComponentCapacity(layout: layout, rows: rows);
        // The group owns its rows and segment-reference array, plus one dictionary reused for whichever segment is
        // currently pending. Its bucket and entry slack is charged once, not once per retained segment.
        var sharedBytes = (((256L + (sizeof(int) * ((long)rows.Length))) + (sizeof(long) * (depth + 1L))) + (64L * entryCapacity));
        var segmentBytes = ((96L + (48L * entryCapacity)) + componentCapacity);

        foreach (var row in rows) {
            ref readonly var rowLayout = ref layout[row];

            foreach (var column in ArenaColumns.All) {
                var positions = UndoPositionCount(catalog: catalog, column: column, layout: rowLayout, row: row);

                if (column == ArenaColumn.Text) {
                    segmentBytes = checked((segmentBytes + (((long)positions) * (32L + (2L * StateCapacity.MaxTextValueLength)))));
                } else if (column == ArenaColumn.Provenance) {
                    segmentBytes = checked((segmentBytes + (((long)positions) * (32L + (2L * StateCapacity.MaxProvenanceLength)))));
                } else if (column == ArenaColumn.Visibility) {
                    var visibilityBytes = ((((64L + 32L) + (2L * SafeName.MaxLength)) + 64L) + (((long)StateCapacity.MaxVisibilityReaders) * ((sizeof(long) + 32L) + (2L * StateCapacity.MaxVisibilityReaderLength))));

                    segmentBytes = checked((segmentBytes + (((long)positions) * visibilityBytes)));
                } else if (column == ArenaColumn.Observation) {
                    segmentBytes = checked((segmentBytes + (((long)positions) * 32L)));
                }
            }
        }
        return checked((sharedBytes + (segmentBytes * (depth + 1L))));
    }
    private static int UndoEntryCapacity(StateCatalog catalog, ArenaLayout layout, int[] rows) {
        var count = 0;

        foreach (var row in rows) {
            ref readonly var value = ref layout[row];

            foreach (var column in ArenaColumns.All) {
                count = checked((count + UndoPositionCount(catalog: catalog, column: column, layout: value, row: row)));
            }
        }
        return count;
    }
    private static int UndoPositionCount(StateCatalog catalog, ArenaColumn column, ArenaRowLayout layout, int row) {
        var poolRow = catalog.IsPoolRow(rowOrdinal: row);
        var poolFieldRow = catalog.IsPoolFieldRow(rowOrdinal: row);

        return (column switch {
            ArenaColumn.Number => ((layout.Kind is (CellKind.Int or CellKind.Fixed or CellKind.Bool)) ? layout.CellCapacity : 0),
            ArenaColumn.Text => ((layout.Kind == CellKind.Text) ? layout.CellCapacity : 0),
            ArenaColumn.Vector => ((layout.Kind == CellKind.Vector) ? layout.CellCapacity : 0),
            ArenaColumn.Presence => layout.CellCapacity,
            ArenaColumn.MemberKey => ((layout.Shape is (RowShape.Keyed or RowShape.Ordered)) ? layout.CellCapacity : 0),
            ArenaColumn.MemberCount => ((layout.Shape is (RowShape.Keyed or RowShape.Ordered)) ? 1 : 0),
            // A pool snapshot may carry a persisted clock even when its field declares no advancing trait, and a
            // later load may introduce one without changing the layout. Reserve every pool field's clock columns.
            ArenaColumn.ClockEpochTick or ArenaColumn.ClockEpochEngineTick or ArenaColumn.ClockY0 or ArenaColumn.ClockV0 or ArenaColumn.ClockSubstepTicks or ArenaColumn.ClockSet => ((!poolRow || poolFieldRow) ? layout.CellCapacity : 0),
            ArenaColumn.Provenance or ArenaColumn.Behavior or ArenaColumn.Visibility or ArenaColumn.Observation => (poolRow ? 0 : layout.CellCapacity),
            ArenaColumn.HistoryCursor => ((layout.Shape == RowShape.Ring) ? 1 : 0),
            ArenaColumn.DrawCursor => ((layout.MaskWordStart >= 0) ? 1 : 0),
            ArenaColumn.DrawnMaskWord => (layout.MaskCount * 4),
            ArenaColumn.PhaseSequence => ((catalog.Rows[row].Phase is not null) ? 1 : 0),
            ArenaColumn.LaneNumber => layout.LaneCapacity,
            ArenaColumn.LaneRoster => 0,
            _ => 0,
        });
    }
    private static int UndoComponentCapacity(ArenaLayout layout, int[] rows) {
        var count = 0;

        foreach (var row in rows) {
            ref readonly var value = ref layout[row];

            count = checked((count + (value.CellCapacity * value.Dimensions)));
        }
        return count;
    }
    private ArenaUndoGroup RequireUndo(string group) => (((m_undo is not null) && m_undo.TryGetValue(key: group, value: out var state))
        ? state
        : throw new InvalidOperationException(message: $"Arena has no undo group '{group}'."));
    private ArenaUndoGroup RequirePendingUndo(string group) {
        var state = RequireUndo(group: group);

        return ((state.Pending is not null) ? state : throw new InvalidOperationException(message: $"Undo group '{group}' has no turn in progress."));
    }
    private void RestoreUndo(ArenaUndoGroup owner, ArenaUndoSegment segment) {
        var epoch = ++m_reindexEpoch;

        m_reindexCount = 0;
        for (var index = (segment.Count - 1); (index >= 0); index--) {
            var retained = segment[index];
            var entry = retained.Entry;

            if (entry.Column == ArenaColumn.MemberKey) {
                entry = entry with { Number = ((retained.MemberKey is null) ? -1L : (m_keys.TryResolve(CellName.Parse(candidate: retained.MemberKey), out var memberKey) ? memberKey.Ordinal : throw new InvalidOperationException(message: $"Retained member key '{retained.MemberKey}' is absent from the arena ledger."))) };
            }
            if (retained.ComponentLength > 0) {
                VectorSpan(slot: entry.Index).Clear();
                segment.Components(entry: retained).CopyTo(destination: VectorSpan(slot: entry.Index));
            } else {
                Restore(entry: entry);
            }
            BumpGeneration(column: entry.Column, index: entry.Index, tailPush: false);
            MarkReindex(column: entry.Column, epoch: epoch, index: entry.Index);
            var changedRow = m_layout.RowOf(column: entry.Column, index: entry.Index);

            if (changedRow >= 0) {
                foreach (var group in m_undo!.Values) {
                    if (!ReferenceEquals(objA: group, objB: owner) && group.Contains(row: changedRow)) {
                        if (group.Pending is not null) {
                            group.Pending.Rewindable = false;
                        } else {
                            group.InvalidateLatest();
                        }
                    }
                }
                m_rowVersions[changedRow]++;
            }
        }
        for (var index = 0; (index < m_reindexCount); index++) {
            var row = m_reindexRow[index];

            Reindex(layout: m_layout[row], rowOrdinal: row);
        }
        m_reindexCount = 0;
    }

    internal void AddUndoTo(ref Fnv1aHash hash) {
        if (m_undo is null) {
            return;
        }
        hash.Add(value: ((ulong)m_undo.Count));
        foreach (var group in m_undo.Values) {
            group.AddTo(hash: ref hash);
        }
    }

    private sealed class ArenaUndoGroup {
        private readonly ArenaUndoSegment[] m_segments;
        private readonly Dictionary<long, int> m_seen;

        private int m_count;
        private int m_head;
        private bool m_pending;

        public ArenaUndoGroup(string name, int[] rows, int depth, int entryCapacity, int componentCapacity) {
            Name = name;
            Rows = rows;
            Depth = depth;
            m_segments = new ArenaUndoSegment[(depth + 1)];
            m_seen = new Dictionary<long, int>(capacity: entryCapacity);
            for (var index = 0; (index < m_segments.Length); index++) {
                m_segments[index] = new ArenaUndoSegment(componentCapacity: componentCapacity, entryCapacity: entryCapacity);
            }
        }

        public int Depth { get; }
        public string Name { get; }
        public ArenaUndoSegment? Pending => (m_pending ? m_segments[Depth] : null);
        public int[] Rows { get; }

        public bool Contains(int row) => (Array.BinarySearch(array: Rows, value: row) >= 0);
        public bool Matches(int[] rows, int depth) => ((Depth == depth) && Rows.AsSpan().SequenceEqual(other: rows));
        public void Begin() {
            m_segments[Depth].Reset(rewindable: true);
            m_seen.Clear();
            m_pending = true;
        }
        public void Add(ArenaJournalEntry entry, ReadOnlySpan<sbyte> components, string? memberKey) {
            var identity = (((long)((byte)entry.Column)) << 32) | ((uint)entry.Index);

            if (m_seen.ContainsKey(key: identity)) {
                return;
            }
            m_seen.Add(key: identity, value: m_seen.Count);
            Pending!.Add(components: components, entry: entry, memberKey: memberKey);
        }
        public void CommitPending() {
            if ((Pending!.Count == 0) && Pending.Rewindable) {
                CancelPending();
                return;
            }
            var target = ((m_head + m_count) % Depth);

            if (m_count == Depth) {
                target = m_head;
                m_head = ((m_head + 1) % Depth);
            } else {
                m_count++;
            }
            (m_segments[target], m_segments[Depth]) = (m_segments[Depth], m_segments[target]);
            m_segments[Depth].Reset(rewindable: true);
            m_seen.Clear();
            m_pending = false;
        }
        public void CancelPending() {
            if ((Pending!.Count > 0) || !Pending.Rewindable) {
                InvalidateLatest();
            }
            m_segments[Depth].Reset(rewindable: true);
            m_seen.Clear();
            m_pending = false;
        }
        public void InvalidateLatest() {
            if (m_count == 0) {
                return;
            }
            m_segments[(((m_head + m_count) - 1) % Depth)].Rewindable = false;
        }
        public bool TryPeek(out ArenaUndoSegment segment) {
            if (m_count == 0) {
                segment = null!;
                return false;
            }
            segment = m_segments[(((m_head + m_count) - 1) % Depth)];
            return true;
        }
        public void Pop() {
            m_segments[(((m_head + m_count) - 1) % Depth)].Reset(rewindable: true);
            m_count--;
        }
        public ArenaUndoGroupSnapshot Snapshot() {
            var snapshots = new ArenaUndoSegmentSnapshot[m_count];

            for (var index = 0; (index < m_count); index++) {
                snapshots[index] = m_segments[((m_head + index) % Depth)].Snapshot();
            }
            return new ArenaUndoGroupSnapshot(Name, Depth, Rows.ToArray(), snapshots, Pending?.Snapshot());
        }
        public void Restore(ArenaUndoGroupSnapshot snapshot, CellKeyTable keys) {
            m_head = 0;
            m_count = snapshot.Segments.Count;
            for (var index = 0; (index < m_segments.Length); index++) {
                m_segments[index].Reset(rewindable: true);
            }
            for (var index = 0; (index < m_count); index++) {
                m_segments[index].Restore(snapshot: snapshot.Segments[index], keys: keys);
            }
            m_pending = (snapshot.Pending is not null);
            if (snapshot.Pending is not null) {
                m_segments[Depth].Restore(snapshot: snapshot.Pending, keys: keys);
                m_seen.Clear();
                for (var index = 0; (index < m_segments[Depth].Count); index++) {
                    var entry = m_segments[Depth][index].Entry;

                    m_seen.Add(key: (((long)((byte)entry.Column)) << 32) | ((uint)entry.Index), value: index);
                }
            } else {
                m_seen.Clear();
            }
        }
        public void AddTo(ref Fnv1aHash hash) {
            hash.Add(value: Fnv1aHash.Compute(values: Name.AsSpan()));
            hash.Add(value: ((ulong)Depth));
            hash.Add(value: ((ulong)Rows.Length));
            foreach (var row in Rows) {
                hash.Add(value: ((ulong)row));
            }
            hash.Add(value: ((ulong)m_count));
            for (var index = 0; (index < m_count); index++) {
                m_segments[((m_head + index) % Depth)].AddTo(hash: ref hash);
            }
            hash.Add(value: ((Pending is null) ? 0UL : 1UL));
            Pending?.AddTo(hash: ref hash);
        }
    }
    private sealed class ArenaUndoSegment {
        private readonly sbyte[] m_components;
        private readonly ArenaUndoEntry[] m_entries;

        private int m_componentCount;
        private int m_count;

        public ArenaUndoSegment(int entryCapacity, int componentCapacity) {
            m_entries = new ArenaUndoEntry[entryCapacity];
            m_components = new sbyte[componentCapacity];
        }

        public int Count => m_count;
        public bool Rewindable { get; set; }

        public ArenaUndoEntry this[int index] => m_entries[index];

        public ReadOnlySpan<sbyte> Components(ArenaUndoEntry entry) => m_components.AsSpan(entry.ComponentOffset, entry.ComponentLength);
        public void Reset(bool rewindable) {
            Array.Clear(array: m_entries, index: 0, length: m_count);
            m_count = 0;
            m_componentCount = 0;
            Rewindable = rewindable;
        }
        public void Add(ArenaJournalEntry entry, ReadOnlySpan<sbyte> components, string? memberKey = null) {
            components.CopyTo(destination: m_components.AsSpan(start: m_componentCount));
            m_entries[m_count++] = new ArenaUndoEntry(entry, m_componentCount, components.Length, memberKey);
            m_componentCount += components.Length;
        }
        public ArenaUndoSegmentSnapshot Snapshot() {
            var entries = new ArenaUndoEntrySnapshot[m_count];

            for (var index = 0; (index < m_count); index++) {
                var value = m_entries[index];
                var number = value.Entry.Column switch {
                    ArenaColumn.MemberKey => ((value.MemberKey is null) ? -1L : 0L),
                    ArenaColumn.Vector => 0L,
                    _ => value.Entry.Number,
                };

                entries[index] = new ArenaUndoEntrySnapshot(value.Entry.Column, value.Entry.Index, number, (value.Entry.Reference as string), (value.Entry.Reference as StateVisibility), (value.Entry.Reference as StateObservation), value.MemberKey, ((value.ComponentLength == 0) ? null : Components(entry: value).ToArray()));
            }
            return new ArenaUndoSegmentSnapshot(Rewindable, entries);
        }
        public void Restore(ArenaUndoSegmentSnapshot snapshot, CellKeyTable keys) {
            Reset(rewindable: snapshot.Rewindable);
            foreach (var entry in snapshot.Entries) {
                object? reference = entry.Column switch {
                    ArenaColumn.Text or ArenaColumn.Provenance => entry.Text,
                    ArenaColumn.Visibility => entry.Visibility,
                    ArenaColumn.Observation => entry.Observation,
                    _ => null,
                };

                // Retain the ledger-owned name, not a separate decoded string per historical entry.
                // Validation already proved the key exists; this keeps import within the same reservation.
                string? memberName = null;

                if (entry.MemberKey is { } spelling) {
                    if (!keys.TryResolve(CellName.Parse(candidate: spelling), out var key)) {
                        throw new InvalidOperationException(message: "Validated retained member key disappeared during import.");
                    }
                    memberName = keys.Names[key.Ordinal].Value;
                }
                Add(new ArenaJournalEntry(Column: entry.Column, Index: entry.Index, Number: entry.Number, Reference: reference), (entry.Components ?? []), memberName);
            }
        }
        public void AddTo(ref Fnv1aHash hash) {
            hash.Add(value: (Rewindable ? 1UL : 0UL));
            hash.Add(value: ((ulong)m_count));
            for (var index = 0; (index < m_count); index++) {
                var value = m_entries[index];

                hash.Add(value: ((ulong)value.Entry.Column));
                hash.Add(value: ((ulong)value.Entry.Index));
                var number = value.Entry.Column switch {
                    ArenaColumn.MemberKey => ((value.MemberKey is null) ? -1L : 0L),
                    ArenaColumn.Vector => 0L,
                    _ => value.Entry.Number,
                };

                hash.Add(value: ((ulong)number));
                FoldText(hash: ref hash, text: value.MemberKey);
                if (value.ComponentLength > 0) {
                    hash.Add(values: System.Runtime.InteropServices.MemoryMarshal.Cast<sbyte, byte>(span: Components(entry: value)));
                } else if (value.Entry.Reference is StateVisibility visibility) {
                    StateVisibilityHash.Append(hash: ref hash, visibility: visibility);
                } else if (value.Entry.Reference is StateObservation observation) {
                    hash.Add(value: ((ulong)observation.Tick));
                    hash.Add(value: (observation.Visible ? 1UL : 0UL));
                } else {
                    FoldText(hash: ref hash, text: (value.Entry.Reference as string));
                }
            }
        }
    }
    private readonly record struct ArenaUndoEntry(ArenaJournalEntry Entry, int ComponentOffset, int ComponentLength, string? MemberKey);
}
