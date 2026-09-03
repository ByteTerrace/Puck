using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Reduces full-rate authority snapshots to an independently authored remote-projection cadence. Skipped
/// field writes are coalesced by cell and field, the latest write winning; one-shot teleport/correction hints are
/// retained by entity generation. The returned image is therefore self-contained for the elapsed interval rather
/// than merely the last tick sampled.</summary>
public sealed class WorldProjectionSampler {
    private ulong m_updateTicks;
    private readonly Dictionary<long, FieldCellDelta> m_pendingFields = [];
    private readonly EntityContinuity[] m_pendingContinuity = new EntityContinuity[WorldBodiesLimits.CapacityCeiling];
    private readonly int[] m_pendingContinuityGeneration = new int[WorldBodiesLimits.CapacityCeiling];
    private readonly bool[] m_hasPendingContinuity = new bool[WorldBodiesLimits.CapacityCeiling];
    private ulong m_accumulatedStepTicks;
    private bool m_hasDeliveredSnapshot;
    private bool m_hasObservedSnapshot;
    private bool m_pendingFieldsFull;
    private int m_pendingContinuityCount;
    private ulong m_lastObservedTick;
    private float m_updateSeconds = float.NaN;

    /// <summary>Creates a sampler. Zero delivers every snapshot; a positive interval delivers the first snapshot
    /// immediately and then the first snapshot whose accumulated engine time reaches the interval.</summary>
    /// <param name="updateSeconds">The remote projection interval in seconds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="updateSeconds"/> is non-finite, negative, or
    /// greater than <see cref="WorldObserverDisclosure.MaximumUpdateSeconds"/>.</exception>
    public WorldProjectionSampler(float updateSeconds) {
        SetUpdateSeconds(updateSeconds: updateSeconds);
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
            throw new ArgumentOutOfRangeException(nameof(updateSeconds));
        }
        if (updateSeconds == m_updateSeconds) {
            return;
        }

        m_updateTicks = ((updateSeconds > 0f)
            ? FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: updateSeconds))
            : 0UL);
        m_updateSeconds = updateSeconds;
    }

    /// <summary>Accumulates one borrowed authority snapshot and returns the next projection image when due.</summary>
    /// <param name="snapshot">The next full-rate snapshot, in increasing tick order.</param>
    /// <param name="projected">The self-contained sampled image when due; otherwise the default value.</param>
    /// <returns><see langword="true"/> when <paramref name="projected"/> should be delivered.</returns>
    public bool TryProject(in WorldSnapshot snapshot, out WorldSnapshot projected) {
        if (m_hasObservedSnapshot && snapshot.Tick <= m_lastObservedTick) {
            throw new ArgumentException(message: $"projection snapshot tick {snapshot.Tick} does not follow {m_lastObservedTick}.", paramName: nameof(snapshot));
        }
        m_hasObservedSnapshot = true;
        m_lastObservedTick = snapshot.Tick;
        Accumulate(snapshot: in snapshot);
        if (
            m_hasDeliveredSnapshot &&
            (m_updateTicks != 0UL) &&
            (m_accumulatedStepTicks < m_updateTicks)
        ) {
            projected = default;
            return false;
        }

        projected = Compose(snapshot: in snapshot);
        m_accumulatedStepTicks = 0UL;
        m_hasDeliveredSnapshot = true;
        ClearAccumulation();
        return true;
    }

    private void Accumulate(in WorldSnapshot snapshot) {
        m_accumulatedStepTicks = ((ulong.MaxValue - m_accumulatedStepTicks < snapshot.StepTicks)
            ? ulong.MaxValue
            : (m_accumulatedStepTicks + snapshot.StepTicks));

        if (snapshot.FieldsFull) {
            m_pendingFields.Clear();
            m_pendingFieldsFull = true;
        }
        foreach (var delta in snapshot.FieldCells.Span) {
            var key = ((checked((long)delta.Cell) << 8) | delta.Field);
            m_pendingFields[key] = delta;
        }

        foreach (var entry in snapshot.Entries.Span) {
            if (
                (entry.Continuity.Kind == EntityContinuityKind.Continuous) ||
                ((uint)entry.Index >= (uint)m_hasPendingContinuity.Length)
            ) {
                continue;
            }
            if (!m_hasPendingContinuity[entry.Index]) {
                m_pendingContinuityCount++;
            }
            m_hasPendingContinuity[entry.Index] = true;
            m_pendingContinuity[entry.Index] = entry.Continuity;
            m_pendingContinuityGeneration[entry.Index] = entry.Generation;
        }
    }

    private WorldSnapshot Compose(in WorldSnapshot snapshot) {
        var entries = snapshot.Entries;
        if (m_pendingContinuityCount != 0) {
            var patched = snapshot.Entries.ToArray();
            for (var ordinal = 0; ordinal < patched.Length; ordinal++) {
                ref var entry = ref patched[ordinal];
                if (
                    ((uint)entry.Index < (uint)m_hasPendingContinuity.Length) &&
                    m_hasPendingContinuity[entry.Index] &&
                    (m_pendingContinuityGeneration[entry.Index] == entry.Generation)
                ) {
                    entry = entry with { Continuity = m_pendingContinuity[entry.Index] };
                }
            }
            entries = patched;
        }

        var fields = snapshot.FieldCells;
        if (m_pendingFields.Count != 0) {
            var combined = m_pendingFields.Values.ToArray();
            Array.Sort(combined, static (left, right) => {
                var cell = left.Cell.CompareTo(right.Cell);
                return (cell != 0 ? cell : left.Field.CompareTo(right.Field));
            });
            fields = combined;
        }

        return snapshot with {
            Entries = entries,
            FieldCells = fields,
            FieldsFull = m_pendingFieldsFull,
            StepTicks = m_accumulatedStepTicks,
        };
    }

    private void ClearAccumulation() {
        m_pendingFields.Clear();
        m_pendingFieldsFull = false;
        if (m_pendingContinuityCount == 0) {
            return;
        }
        Array.Clear(array: m_hasPendingContinuity);
        m_pendingContinuityCount = 0;
    }
}
