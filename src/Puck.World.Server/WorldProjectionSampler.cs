using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Reduces full-rate authority snapshots to an independently authored remote-projection cadence. Skipped
/// field writes are coalesced by cell and field, the latest write winning; one-shot teleport/correction hints are
/// retained by entity generation. The returned image is therefore self-contained for the elapsed interval rather
/// than merely the last tick sampled.</summary>
public sealed class WorldProjectionSampler {
    private ulong m_accumulatedStepTicks;
    private bool m_hasDeliveredSnapshot;
    private bool m_hasObservedSnapshot;
    private ulong m_lastObservedTick;
    private bool m_pendingFieldsFull;
    private ulong m_updateTicks;

    private readonly Dictionary<(string Region, string Name, int Row, int Cell, byte Field), FieldCellDelta> m_pendingFields = [];
    private readonly Dictionary<int, (int Generation, EntityContinuity Hint)> m_pendingContinuity = [];
    private float m_updateSeconds = float.NaN;

    /// <summary>Creates a sampler. Zero delivers every snapshot; a positive interval delivers the first snapshot
    /// immediately and then the first snapshot whose accumulated engine time reaches the interval.</summary>
    /// <param name="updateSeconds">The remote projection interval in seconds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="updateSeconds"/> is non-finite, negative, or
    /// greater than <see cref="WorldObserverDisclosure.MaximumUpdateSeconds"/>.</exception>
    public WorldProjectionSampler(float updateSeconds) {
        SetUpdateSeconds(updateSeconds: updateSeconds);
    }

    private void Accumulate(in WorldSnapshot snapshot) {
        m_accumulatedStepTicks = m_accumulatedStepTicks.AddSaturating(right: snapshot.StepTicks);

        if (snapshot.FieldsFull) {
            m_pendingFields.Clear();
            m_pendingFieldsFull = true;
        }
        foreach (var delta in snapshot.FieldCells.Span) {
            if ((delta.Row >= 0) && (delta.Cell == -1)) {
                foreach (var pending in m_pendingFields.Where(predicate: pair => (pair.Value.Row == delta.Row)).Select(selector: pair => pair.Key).ToArray()) { m_pendingFields.Remove(key: pending); }
            }
            var key = (delta.Region, delta.FieldName, delta.Row, delta.Cell, delta.Field);

            m_pendingFields[key] = delta;
        }
    }

    private void AccumulateContinuity(in WorldSnapshot snapshot, WorldBodyView? view) {
        var events = view is { } recipient ? recipient.Frame.Continuity : snapshot.Entries.Span;
        foreach (ref readonly var entry in events) {
            if (
                (entry.Continuity.Kind == EntityContinuityKind.Continuous) ||
                (view is { } bodyView && !bodyView.Contains(entry.Index, entry.Generation)) ||
                (uint)entry.Index >= WorldBodiesLimits.CapacityCeiling
            ) { continue; }
            var hint = m_pendingContinuity.TryGetValue(entry.Index, out var held) && held.Generation == entry.Generation
                ? held.Hint.Then(entry.Continuity) : entry.Continuity;
            m_pendingContinuity[entry.Index] = (entry.Generation, hint);
        }
    }
    private void ClearAccumulation() {
        m_pendingFields.Clear();
        m_pendingFieldsFull = false;
        m_pendingContinuity.Clear();
    }
    private WorldSnapshot Compose(in WorldSnapshot snapshot, WorldBodyView? view) {
        var entries = snapshot.Entries;
        if (m_pendingContinuity.Count != 0) {
            entries = view is { } recipient
                ? recipient.Frame.ApplyContinuity(entries, m_pendingContinuity)
                : ApplyContinuity(entries, m_pendingContinuity);
        }

        var fields = snapshot.FieldCells;

        if (m_pendingFields.Count != 0) {
            var combined = m_pendingFields.Values.ToArray();

            Array.Sort(
                array: combined,
                comparison: static (left, right) => {
                    var region = StringComparer.Ordinal.Compare(x: left.Region, y: right.Region);

                    if (region != 0) { return region; }
                    var row = left.Row.CompareTo(value: right.Row);

                    if (row != 0) { return row; }
                    return LexicographicOrder.Compare(
                        leftPrimary: left.Cell,
                        leftSecondary: left.Field,
                        rightPrimary: right.Cell,
                        rightSecondary: right.Field
                    );
                }
            );
            fields = combined;
        }

        return snapshot with {
            Entries = entries,
            FieldCells = fields,
            FieldsFull = m_pendingFieldsFull,
            StepTicks = m_accumulatedStepTicks,
        };
    }

    internal static ReadOnlyMemory<EntitySnapshot> ApplyContinuity(ReadOnlyMemory<EntitySnapshot> entries,
        Dictionary<int, (int Generation, EntityContinuity Hint)> hints) {
        EntitySnapshot[]? patched = null;
        for (var i = 0; i < entries.Length; i++) {
            ref readonly var entry = ref entries.Span[i];
            if (hints.TryGetValue(entry.Index, out var held) && held.Generation == entry.Generation) {
                patched ??= entries.ToArray();
                patched[i] = entry with { Continuity = held.Hint.Then(entry.Continuity) };
            }
        }
        return patched is null ? entries : patched;
    }

    /// <summary>Changes the cadence without discarding the interval already accumulated. This lets a live
    /// <c>bodies.disclosure</c> edit take effect on an existing projection without losing skipped field writes or
    /// continuity hints.</summary>
    /// <param name="updateSeconds">The new remote projection interval in seconds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="updateSeconds"/> is non-finite, negative, or
    /// greater than <see cref="WorldObserverDisclosure.MaximumUpdateSeconds"/>.</exception>
    public void SetUpdateSeconds(float updateSeconds) {
        if (
            !float.IsFinite(f: updateSeconds) ||
            (updateSeconds < 0f) ||
            (updateSeconds > WorldObserverDisclosure.MaximumUpdateSeconds)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(updateSeconds));
        }
        if (updateSeconds == m_updateSeconds) {
            return;
        }

        m_updateTicks = ((updateSeconds > 0f)
            ? FixedTickConversion.DurationEngineTicks(seconds: ((decimal)updateSeconds))
            : 0UL
        );
        m_updateSeconds = updateSeconds;
    }
    /// <summary>Accumulates one borrowed authority snapshot and returns the next projection image when due.</summary>
    /// <param name="snapshot">The next full-rate snapshot, in increasing tick order.</param>
    /// <param name="projected">The self-contained sampled image when due; otherwise the default value.</param>
    /// <param name="due">Whether the image is owed now whatever the cadence, as the primer of a stream's new generation
    /// is.</param>
    /// <returns><see langword="true"/> when <paramref name="projected"/> should be delivered.</returns>
    public bool TryProject(in WorldSnapshot snapshot, out WorldSnapshot projected, bool due = false) => TryProject(snapshot, out projected, due, null);

    internal bool TryProject(in WorldSnapshot snapshot, out WorldSnapshot projected, bool due, WorldBodyView? view) {
        if (
            m_hasObservedSnapshot &&
            (snapshot.Tick <= m_lastObservedTick)
        ) {
            throw new ArgumentException(
                message: $"projection snapshot tick {snapshot.Tick} does not follow {m_lastObservedTick}.",
                paramName: nameof(snapshot)
            );
        }
        m_hasObservedSnapshot = true;
        m_lastObservedTick = snapshot.Tick;
        Accumulate(snapshot: in snapshot);
        if (
            !due &&
            m_hasDeliveredSnapshot &&
            snapshot.Cues.IsEmpty &&
            snapshot.MusicBoundary == 0 &&
            snapshot.MusicSenses == 0 &&
            (m_updateTicks != 0UL) &&
            (m_accumulatedStepTicks < m_updateTicks)
        ) {
            AccumulateContinuity(snapshot, view);
            projected = default;
            return false;
        }

        var disclosed = view is { } recipient ? snapshot with { Entries = recipient.Project() } : snapshot;
        projected = Compose(disclosed, view);
        m_accumulatedStepTicks = 0UL;
        m_hasDeliveredSnapshot = true;
        ClearAccumulation();
        return true;
    }
}
