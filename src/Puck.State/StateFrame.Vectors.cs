using Puck.Maths;

namespace Puck.State;

public sealed partial class StateFrame {
    private sbyte[] m_vectors = [];
    private sbyte[] m_byteJournal = [];
    private int m_byteJournalLength;
    private StateVector?[] m_loadedVectors = [];

    /// <summary>Exposes the frame's vector byte values in layout order.</summary>
    public ReadOnlySpan<sbyte> VectorValues => m_vectors;

    private void InitializeVectors(FrameLayout layout, IReadOnlyList<StateRow> rows) {
        if (layout.VectorLength > 0) {
            m_vectors = new sbyte[layout.VectorLength];
            m_byteJournal = new sbyte[layout.VectorLength];
            m_loadedVectors = new StateVector?[layout.VectorCellCount];
        } else {
            m_vectors = [];
            m_byteJournal = [];
            m_loadedVectors = [];
        }
    }

    private void CopyVectorsFrom(StateFrame other) {
        if (m_vectors.Length > 0) {
            other.m_vectors.AsSpan().CopyTo(destination: m_vectors);
        }
    }

    private void ClearVectorJournal() {
        m_byteJournalLength = 0;
    }

    private void RestoreVectorJournalEntry(JournalEntry entry) {
        var byteJournalOffset = -1 - entry.Index;
        var packed = entry.Previous;
        var offset = (int)(packed >> 32);
        var dimensions = (int)packed;

        m_byteJournal.AsSpan(start: byteJournalOffset, length: dimensions)
            .CopyTo(destination: m_vectors.AsSpan(start: offset, length: dimensions));

        var rowOrdinal = Layout.RowOfVectorOffset(offset: offset);
        m_rowVersions[rowOrdinal]++;
    }

    private void LoadVectorRow(FrameRowLayout layout, StateRow row, int rowOrdinal, StateStore source) {
        var dimensions = layout.Dimensions;

        if (row.IsSlot) {
            var cacheIndex = layout.VectorCellStart;
            var offset = layout.VectorOffset;

            if (source.TryStoredVector(rowOrdinal: rowOrdinal, key: StateRow.SlotKey, out var comps)) {
                if (source.TryStored(rowOrdinal: rowOrdinal, key: StateRow.SlotKey, out _, out _, out var cell) && (cell?.Vector is { } vec)) {
                    if (!ReferenceEquals(objA: m_loadedVectors[cacheIndex], objB: vec)) {
                        comps.CopyTo(destination: m_vectors.AsSpan(start: offset, length: dimensions));
                        m_loadedVectors[cacheIndex] = vec;
                        m_rowVersions[rowOrdinal]++;
                    }
                } else {
                    comps.CopyTo(destination: m_vectors.AsSpan(start: offset, length: dimensions));
                    m_loadedVectors[cacheIndex] = null;
                    m_rowVersions[rowOrdinal]++;
                }
            } else {
                m_vectors.AsSpan(start: offset, length: dimensions).Clear();
                m_loadedVectors[cacheIndex] = null;
                m_rowVersions[rowOrdinal]++;
            }
        } else if (row.Cells is { } cells) {
            for (var index = 0; index < cells.Count; index++) {
                var cell = cells[index];
                var cacheIndex = layout.VectorCellStart + index;
                var offset = layout.VectorOffset + (index * dimensions);

                if (source.TryStoredVector(rowOrdinal: rowOrdinal, key: cell.Key, out var comps)) {
                    if (source.TryStored(rowOrdinal: rowOrdinal, key: cell.Key, out _, out _, out var sourceCell) && (sourceCell?.Vector is { } vec)) {
                        if (!ReferenceEquals(objA: m_loadedVectors[cacheIndex], objB: vec)) {
                            comps.CopyTo(destination: m_vectors.AsSpan(start: offset, length: dimensions));
                            m_loadedVectors[cacheIndex] = vec;
                            m_rowVersions[rowOrdinal]++;
                        }
                    } else {
                        comps.CopyTo(destination: m_vectors.AsSpan(start: offset, length: dimensions));
                        m_loadedVectors[cacheIndex] = null;
                        m_rowVersions[rowOrdinal]++;
                    }
                } else {
                    m_vectors.AsSpan(start: offset, length: dimensions).Clear();
                    m_loadedVectors[cacheIndex] = null;
                    m_rowVersions[rowOrdinal]++;
                }
            }
        }
    }

    /// <inheritdoc/>
    public override bool TryStoredVector(StateRow row, CellName key, out ReadOnlySpan<sbyte> components) {
        if (Layout.TryOrdinal(name: row.Name.Value, ordinal: out var ordinal)) {
            var layout = Layout[ordinal];

            if (layout.Kind == FrameRowKind.Vector) {
                return TryStoredVector(rowOrdinal: ordinal, key: key, out components);
            }
        }

        return base.TryStoredVector(row: row, key: key, out components);
    }

    /// <inheritdoc/>
    public override bool TryStoredVector(int rowOrdinal, CellName key, out ReadOnlySpan<sbyte> components) {
        if (((uint)rowOrdinal) < ((uint)Layout.RowCount)) {
            var layout = Layout[rowOrdinal];

            if (layout.Kind == FrameRowKind.Vector) {
                var dimensions = layout.Dimensions;
                int cellIndex;

                if (m_rows[rowOrdinal].IsSlot) {
                    cellIndex = 0;
                } else {
                    var ordinals = DomainOrdinals(rowOrdinal: rowOrdinal);

                    if (!ordinals.TryGetValue(key: key, out cellIndex)) {
                        components = default;

                        return false;
                    }
                }

                var offset = layout.VectorOffset + (cellIndex * dimensions);
                components = m_vectors.AsSpan(start: offset, length: dimensions);

                return true;
            }

            return base.TryStoredVector(rowOrdinal: rowOrdinal, key: key, out components);
        }

        components = default;

        return false;
    }

    /// <summary>Writes a vector cell into the frame's byte buffer.</summary>
    public bool TryWriteVector(StateRow row, CellName key, ReadOnlySpan<sbyte> components, out string reason) {
        if (Layout.TryOrdinal(name: row.Name.Value, ordinal: out var ordinal)) {
            return TryWriteVector(rowOrdinal: ordinal, key: key, components: components, reason: out reason);
        }

        reason = $"Row '{row.Name}' not found in frame.";

        return false;
    }

    /// <summary>Writes a vector cell into the frame's byte buffer by row ordinal.</summary>
    public bool TryWriteVector(int rowOrdinal, CellName key, ReadOnlySpan<sbyte> components, out string reason) {
        if (((uint)rowOrdinal) >= ((uint)Layout.RowCount)) {
            reason = "Row ordinal out of range.";

            return false;
        }

        var layout = Layout[rowOrdinal];

        if (layout.Kind != FrameRowKind.Vector) {
            reason = "Row is not framed as a vector.";

            return false;
        }

        if (components.Length != layout.Dimensions) {
            reason = $"Vector length {components.Length} does not match row dimensions {layout.Dimensions}.";

            return false;
        }

        int cellIndex;

        if (m_rows[rowOrdinal].IsSlot) {
            cellIndex = 0;
        } else {
            var ordinals = DomainOrdinals(rowOrdinal: rowOrdinal);

            if (!ordinals.TryGetValue(key: key, out cellIndex)) {
                reason = "Key not found in vector row.";

                return false;
            }
        }

        var offset = layout.VectorOffset + (cellIndex * layout.Dimensions);
        var targetSpan = m_vectors.AsSpan(start: offset, length: layout.Dimensions);

        if (targetSpan.SequenceEqual(other: components)) {
            reason = string.Empty;

            return true;
        }

        if (m_journalScopes > 0) {
            EnsureByteJournalCapacity(additional: layout.Dimensions);
            targetSpan.CopyTo(destination: m_byteJournal.AsSpan(start: m_byteJournalLength, length: layout.Dimensions));

            if (m_journalLength >= m_journal.Length) {
                Array.Resize(array: ref m_journal, newSize: (m_journal.Length * 2));
            }

            var packed = ((long)offset << 32) | (uint)layout.Dimensions;
            m_journal[m_journalLength++] = new JournalEntry(
                Index: -1 - m_byteJournalLength,
                Previous: packed
            );
            m_byteJournalLength += layout.Dimensions;
            m_journalTouches++;
        }

        components.CopyTo(destination: targetSpan);
        m_rowVersions[rowOrdinal]++;
        reason = string.Empty;

        return true;
    }

    private void EnsureByteJournalCapacity(int additional) {
        if ((m_byteJournalLength + additional) > m_byteJournal.Length) {
            var newCap = Math.Max(val1: (m_byteJournal.Length * 2), val2: (m_byteJournalLength + additional + 64));
            Array.Resize(array: ref m_byteJournal, newSize: newCap);
        }
    }

    /// <summary>Applies a pre-resolved vector transform on the frame.</summary>
    public bool TryApplyVector(ResolvedVectorTransform transform, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: transform);

        switch (transform) {
            case ResolvedVectorTransform.Mix mix: {
                if (((uint)mix.TargetRowOrdinal) >= ((uint)Layout.RowCount)) {
                    reason = "Target row ordinal out of range.";
                    return false;
                }

                var targetLayout = Layout[mix.TargetRowOrdinal];
                if (targetLayout.Kind != FrameRowKind.Vector) {
                    reason = $"Target row '{mix.TargetRowName}' is not a framed vector row.";
                    return false;
                }

                var dimensions = targetLayout.Dimensions;
                var terms = mix.Terms;
                if ((terms.Count == 0) || (terms.Count > StateCapacity.MaxMixTerms)) {
                    reason = "Mix term count out of bounds.";
                    return false;
                }

                Span<long> sum = stackalloc long[dimensions];
                sum.Clear();

                for (var index = 0; index < terms.Count; index++) {
                    var term = terms[index];
                    var weight = term.Weight;
                    if ((weight < -StateCapacity.MaxMixWeight) || (weight > StateCapacity.MaxMixWeight) || (weight == 0)) {
                        reason = "Mix term weight out of bounds.";
                        return false;
                    }

                    ReadOnlySpan<sbyte> termSpan;
                    if (term.Vector is { } vec) {
                        if (vec.Dimensions != dimensions) {
                            reason = $"Mix term vector dimensions {vec.Dimensions} mismatch target {dimensions}.";
                            return false;
                        }
                        termSpan = vec.Components;
                    } else if (term.SourceRowOrdinal.HasValue) {
                        if (!TryStoredVector(rowOrdinal: term.SourceRowOrdinal.Value, key: term.SourceKey, out termSpan) || (termSpan.Length != dimensions)) {
                            reason = $"Vector cell '{term.SourceRowName}[{term.SourceKey}]' not found or dimensions mismatch.";
                            return false;
                        }
                    } else {
                        reason = "Mix term has no vector or source cell.";
                        return false;
                    }

                    for (var d = 0; d < dimensions; d++) {
                        sum[d] += (long)weight * termSpan[d];
                    }
                }

                var isAllZero = true;
                for (var d = 0; d < dimensions; d++) {
                    if (sum[d] != 0L) {
                        isAllZero = false;
                        break;
                    }
                }

                if (isAllZero) {
                    reason = RuleRefusal.VectorMixZero.ToString();
                    return false;
                }

                Span<sbyte> destination = stackalloc sbyte[dimensions];
                if (!SignedByteVectorFunctions.TryNormalize(components: sum, destination: destination)) {
                    reason = RuleRefusal.VectorMixZero.ToString();
                    return false;
                }

                return TryWriteVector(rowOrdinal: mix.TargetRowOrdinal, key: mix.TargetKey, components: destination, reason: out reason);
            }

            case ResolvedVectorTransform.Mean mean: {
                if (((uint)mean.TargetRowOrdinal) >= ((uint)Layout.RowCount)) {
                    reason = "Target row ordinal out of range.";
                    return false;
                }

                var targetLayout = Layout[mean.TargetRowOrdinal];
                if (targetLayout.Kind != FrameRowKind.Vector) {
                    reason = $"Target row '{mean.TargetRowName}' is not a framed vector row.";
                    return false;
                }

                if (((uint)mean.FromRowOrdinal) >= ((uint)Layout.RowCount)) {
                    reason = "Source row ordinal out of range.";
                    return false;
                }

                var dimensions = targetLayout.Dimensions;
                var fromRow = m_rows[mean.FromRowOrdinal];
                if (fromRow.Cells is not { } cells || (cells.Count == 0)) {
                    reason = "Mean from table has no cells.";
                    return false;
                }

                Span<long> sum = stackalloc long[dimensions];
                sum.Clear();
                var count = 0;

                for (var index = 0; index < cells.Count; index++) {
                    var cellKey = cells[index].Key;

                    if (mean.WhereRowOrdinal.HasValue) {
                        if (!TryStored(rowOrdinal: mean.WhereRowOrdinal.Value, key: cellKey, value: out var boolVal, text: out _) || (boolVal == 0)) {
                            continue;
                        }
                    }

                    if (TryStoredVector(rowOrdinal: mean.FromRowOrdinal, key: cellKey, out var comps) && (comps.Length == dimensions)) {
                        for (var d = 0; d < dimensions; d++) {
                            sum[d] += comps[d];
                        }
                        count++;
                    }
                }

                if (count == 0) {
                    reason = RuleRefusal.VectorMeanEmpty.ToString();
                    return false;
                }

                var isAllZero = true;
                for (var d = 0; d < dimensions; d++) {
                    if (sum[d] != 0L) {
                        isAllZero = false;
                        break;
                    }
                }

                if (isAllZero) {
                    reason = RuleRefusal.VectorMixZero.ToString();
                    return false;
                }

                Span<sbyte> destination = stackalloc sbyte[dimensions];
                if (!SignedByteVectorFunctions.TryNormalize(components: sum, destination: destination)) {
                    reason = RuleRefusal.VectorMixZero.ToString();
                    return false;
                }

                return TryWriteVector(rowOrdinal: mean.TargetRowOrdinal, key: mean.TargetKey, components: destination, reason: out reason);
            }

            case ResolvedVectorTransform.Nearest:
            case ResolvedVectorTransform.Remember:
                reason = $"{transform.GetType().Name} transforms always take the cross-row path.";
                return false;

            default:
                reason = $"Unsupported vector transform '{transform.GetType().Name}'.";
                return false;
        }
    }

    /// <summary>Applies a pre-resolved vector transform on the frame, returning the written vector.</summary>
    public bool TryApplyVector(ResolvedVectorTransform transform, out StateVector? resultVector, out string reason) {
        if (!TryApplyVector(transform: transform, reason: out reason)) {
            resultVector = null;
            return false;
        }

        var (targetOrd, targetKey) = transform switch {
            ResolvedVectorTransform.Mix m => (m.TargetRowOrdinal, m.TargetKey),
            ResolvedVectorTransform.Mean me => (me.TargetRowOrdinal, me.TargetKey),
            _ => (-1, StateRow.SlotKey)
        };

        if ((targetOrd >= 0) && TryStoredVector(rowOrdinal: targetOrd, key: targetKey, out var span)) {
            resultVector = StateVector.CreateUnchecked(components: span);
            return true;
        }

        resultVector = null;
        return true;
    }
}
