using System.Runtime.InteropServices;
using Puck.Maths;

namespace Puck.State;

public sealed partial class RuleEvaluator {
    private bool FireVectorCopy(VectorCopyEffect copy, string ruleName, ulong tick, bool preflight) {
        var destinationCellKey = (copy.KeyFrom is null)
            ? copy.CellKey
            : default;
        var destinationKey = (destinationCellKey != default)
            ? destinationCellKey.Value
            : ResolveKey(
                key: copy.Key,
                keyFrom: copy.KeyFrom,
                tick: tick,
                engineTick: EngineTick
            );

        if (destinationCellKey == default && !CellName.TryParse(candidate: destinationKey, name: out destinationCellKey, reason: out _)) {
            return false;
        }

        var sourceVector = copy.Source.Constant;
        if (sourceVector is null) {
            if (!copy.Source.TryReadSpan(reader: m_host, out var span)) {
                return false;
            }

            sourceVector = StateVector.CreateUnchecked(components: span);
        }

        if (m_traceEntry is not null) {
            m_traceEffectValue = sourceVector.ToBase64Url();
        }

        return Apply(
            effect: copy,
            ruleName: ruleName,
            mutation: new StateMutation.UpsertCell(
                Row: copy.Row,
                Key: destinationKey,
                Value: 0L,
                Write: StateWriteKind.Set,
                Text: null,
                Handle: copy.Handle,
                CellKey: destinationCellKey,
                Vector: sourceVector
            ),
            tick: tick,
            preflight: preflight
        );
    }

    private bool FireVectorMix(VectorMixEffect mix, string ruleName, ulong tick, bool preflight) {
        var destinationCellKey = (mix.KeyFrom is null)
            ? mix.CellKey
            : default;
        var destinationKey = (destinationCellKey != default)
            ? destinationCellKey.Value
            : ResolveKey(
                key: mix.Key,
                keyFrom: mix.KeyFrom,
                tick: tick,
                engineTick: EngineTick
            );

        if (destinationCellKey == default && !CellName.TryParse(candidate: destinationKey, name: out destinationCellKey, reason: out _)) {
            return false;
        }

        var resolvedTerms = new List<ResolvedMixTerm>(capacity: mix.Terms.Count);
        for (var i = 0; i < mix.Terms.Count; i++) {
            var term = mix.Terms[i];
            var src = term.Source;
            if (src.Constant is { } constant) {
                resolvedTerms.Add(new ResolvedMixTerm(
                    SourceRowOrdinal: null,
                    SourceRowName: null,
                    SourceKey: default,
                    Vector: constant,
                    Weight: (int)term.Weight
                ));
            } else {
                var srcCellKey = (src.KeyFrom is null)
                    ? src.CellKey
                    : default;
                var srcKey = (srcCellKey != default)
                    ? srcCellKey.Value
                    : ResolveKey(
                        key: src.Key,
                        keyFrom: src.KeyFrom,
                        tick: tick,
                        engineTick: EngineTick
                    );

                if (srcCellKey == default && !CellName.TryParse(candidate: srcKey, name: out srcCellKey, reason: out _)) {
                    return false;
                }

                resolvedTerms.Add(new ResolvedMixTerm(
                    SourceRowOrdinal: src.RowOrdinal,
                    SourceRowName: src.RowName,
                    SourceKey: srcCellKey,
                    Vector: null,
                    Weight: (int)term.Weight
                ));
            }
        }

        if (m_traceEntry is not null) {
            m_traceEffectValue = destinationKey;
        }

        var transform = new ResolvedVectorTransform.Mix(
            TargetRowOrdinal: mix.RowOrdinal,
            TargetRowName: mix.Row,
            TargetKey: destinationCellKey,
            Terms: resolvedTerms
        );

        return Apply(
            effect: mix,
            ruleName: ruleName,
            mutation: new StateMutation.ApplyVector(Transform: transform),
            tick: tick,
            preflight: preflight
        );
    }

    private bool FireVectorMean(VectorMeanEffect mean, string ruleName, ulong tick, bool preflight) {
        var destinationCellKey = (mean.KeyFrom is null)
            ? mean.CellKey
            : default;
        var destinationKey = (destinationCellKey != default)
            ? destinationCellKey.Value
            : ResolveKey(
                key: mean.Key,
                keyFrom: mean.KeyFrom,
                tick: tick,
                engineTick: EngineTick
            );

        if (destinationCellKey == default && !CellName.TryParse(candidate: destinationKey, name: out destinationCellKey, reason: out _)) {
            return false;
        }

        if (m_traceEntry is not null) {
            m_traceEffectValue = destinationKey;
        }

        var transform = new ResolvedVectorTransform.Mean(
            TargetRowOrdinal: mean.RowOrdinal,
            TargetRowName: mean.Row,
            TargetKey: destinationCellKey,
            FromRowOrdinal: mean.FromRowOrdinal,
            FromRowName: mean.FromRowName,
            WhereRowOrdinal: mean.WhereRowOrdinal,
            WhereRowName: mean.WhereRowName
        );

        return Apply(
            effect: mean,
            ruleName: ruleName,
            mutation: new StateMutation.ApplyVector(Transform: transform),
            tick: tick,
            preflight: preflight
        );
    }

    private bool FireVectorNearest(VectorNearestEffect nearest, string ruleName, ulong tick, bool preflight) {
        CellName targetKey;
        if (nearest.IsIntoSlot) {
            targetKey = StateRow.SlotKey;
        } else {
            targetKey = default;
        }

        int? queryRowOrdinal = null;
        string? queryRowName = null;
        CellName queryKey = default;
        StateVector? queryVector = null;

        if (nearest.Query.Constant is { } constant) {
            queryVector = constant;
        } else {
            queryRowOrdinal = nearest.Query.RowOrdinal;
            queryRowName = nearest.Query.RowName;
            queryKey = (nearest.Query.KeyFrom is null)
                ? nearest.Query.CellKey
                : default;
            if (queryKey == default) {
                var qKeyStr = ResolveKey(
                    key: nearest.Query.Key,
                    keyFrom: nearest.Query.KeyFrom,
                    tick: tick,
                    engineTick: EngineTick
                );
                if (!CellName.TryParse(candidate: qKeyStr, name: out queryKey, reason: out _)) {
                    return false;
                }
            }
        }

        CellName? exclude = null;
        if (nearest.ExcludeKey is not null) {
            if (nearest.ExcludeKeyFrom is not null) {
                var exStr = ResolveKey(
                    key: nearest.ExcludeKey,
                    keyFrom: nearest.ExcludeKeyFrom,
                    tick: tick,
                    engineTick: EngineTick
                );
                if (CellName.TryParse(candidate: exStr, name: out var parsedEx, reason: out _)) {
                    exclude = parsedEx;
                } else {
                    return false;
                }
            } else if (nearest.ExcludeCellKey != default) {
                exclude = nearest.ExcludeCellKey;
            } else if (CellName.TryParse(candidate: nearest.ExcludeKey, name: out var parsedStatic, reason: out _)) {
                exclude = parsedStatic;
            } else {
                return false;
            }
        }

        if (m_traceEntry is not null) {
            ReadOnlySpan<sbyte> qSpan = default;
            if (queryVector is not null) {
                qSpan = queryVector.Components;
            } else {
                _ = nearest.Query.TryReadSpan(reader: m_host, span: out qSpan);
            }

            if (qSpan.Length > 0 && nearest.FromRowOrdinal < m_host.Store.Rows.Count) {
                var fromRow = m_host.Store.Rows[nearest.FromRowOrdinal];
                var fromCells = fromRow.Cells;
                if (fromCells is not null && fromCells.Count > 0) {
                    var candidateList = new List<NearestCandidate>(capacity: fromCells.Count);
                    for (var i = 0; i < fromCells.Count; i++) {
                        var cell = fromCells[i];
                        if (!m_host.Store.TryStoredVector(rowOrdinal: nearest.FromRowOrdinal, key: cell.Key, components: out var cSpan)) {
                            continue;
                        }

                        var admitted = true;
                        if (nearest.WhereRowOrdinal.HasValue) {
                            if (!m_host.Store.TryStored(rowOrdinal: nearest.WhereRowOrdinal.Value, key: cell.Key, value: out var boolVal, text: out _) || boolVal == 0L) {
                                admitted = false;
                            }
                        }

                        candidateList.Add(new NearestCandidate(Key: cell.Key, Components: cSpan.ToArray(), Admitted: admitted));
                    }

                    var matches = new VectorTransforms.NearestMatch[nearest.K];
                    var isFixedScore = (nearest.IntoKind is CellKind.Fixed or CellKind.Text);
                    var matchCount = VectorTransforms.SelectNearest(
                        candidates: CollectionsMarshal.AsSpan(candidateList),
                        query: qSpan,
                        isFixedScore: isFixedScore,
                        k: nearest.K,
                        threshold: nearest.Threshold,
                        excludeKey: exclude,
                        farthest: nearest.Farthest,
                        results: matches
                    );

                    if (nearest.IsIntoSlot) {
                        m_traceEffectValue = (matchCount > 0) ? matches[0].Key.Value : "(none)";
                    } else if (matchCount == 0) {
                        m_traceEffectValue = "(none)";
                    } else if (nearest.IntoKind == CellKind.Fixed) {
                        m_traceEffectValue = string.Join(", ", matches.Take(matchCount).Select(m => $"{m.Key}: {FixedQ4816.FromRawBits(m.Score)}"));
                    } else {
                        m_traceEffectValue = string.Join(", ", matches.Take(matchCount).Select(m => $"{m.Key}: {m.Score}"));
                    }
                } else {
                    m_traceEffectValue = "(none)";
                }
            } else {
                m_traceEffectValue = "(none)";
            }
        }

        var transform = new ResolvedVectorTransform.Nearest(
            TargetRowOrdinal: nearest.IntoRowOrdinal,
            TargetRowName: nearest.IntoRowName,
            TargetKey: targetKey,
            FromRowOrdinal: nearest.FromRowOrdinal,
            FromRowName: nearest.FromRowName,
            QueryRowOrdinal: queryRowOrdinal,
            QueryRowName: queryRowName,
            QueryKey: queryKey,
            QueryVector: queryVector,
            K: nearest.K,
            Threshold: nearest.Threshold,
            WhereRowOrdinal: nearest.WhereRowOrdinal,
            WhereRowName: nearest.WhereRowName,
            Exclude: exclude,
            Farthest: nearest.Farthest,
            IntoKind: nearest.IntoKind
        );

        return Apply(
            effect: nearest,
            ruleName: ruleName,
            mutation: new StateMutation.ApplyVector(Transform: transform),
            tick: tick,
            preflight: preflight
        );
    }

    private bool FireVectorRemember(VectorRememberEffect remember, string ruleName, ulong tick, bool preflight) {
        var destinationCellKey = (remember.KeyFrom is null)
            ? remember.CellKey
            : default;
        var destinationKey = (destinationCellKey != default)
            ? destinationCellKey.Value
            : ResolveKey(
                key: remember.Key,
                keyFrom: remember.KeyFrom,
                tick: tick,
                engineTick: EngineTick
            );

        if (destinationCellKey == default && !CellName.TryParse(candidate: destinationKey, name: out destinationCellKey, reason: out _)) {
            return false;
        }

        int? fromRowOrdinal = null;
        string? fromRowName = null;
        CellName fromKey = default;
        StateVector? fromVector = null;

        if (remember.Source.Constant is { } constant) {
            fromVector = constant;
        } else {
            fromRowOrdinal = remember.Source.RowOrdinal;
            fromRowName = remember.Source.RowName;
            fromKey = (remember.Source.KeyFrom is null)
                ? remember.Source.CellKey
                : default;
            if (fromKey == default) {
                var srcKeyStr = ResolveKey(
                    key: remember.Source.Key,
                    keyFrom: remember.Source.KeyFrom,
                    tick: tick,
                    engineTick: EngineTick
                );
                if (!CellName.TryParse(candidate: srcKeyStr, name: out fromKey, reason: out _)) {
                    return false;
                }
            }
        }

        if (m_traceEntry is not null) {
            ReadOnlySpan<sbyte> vecSpan = default;
            if (fromVector is not null) {
                vecSpan = fromVector.Components;
            } else {
                _ = remember.Source.TryReadSpan(reader: m_host, span: out vecSpan);
            }

            if (vecSpan.Length > 0 && remember.RowOrdinal < m_host.Store.Rows.Count) {
                var intoRow = m_host.Store.Rows[remember.RowOrdinal];
                var existingCells = intoRow.Cells;
                if (existingCells is not null && existingCells.Count > 0) {
                    var candidateList = new List<NearestCandidate>(capacity: existingCells.Count);
                    for (var i = 0; i < existingCells.Count; i++) {
                        var cell = existingCells[i];
                        if (m_host.Store.TryStoredVector(rowOrdinal: remember.RowOrdinal, key: cell.Key, components: out var cSpan)) {
                            candidateList.Add(new NearestCandidate(Key: cell.Key, Components: cSpan.ToArray(), Admitted: true));
                        }
                    }

                    if (VectorTransforms.TryRemember(
                        existingCells: CollectionsMarshal.AsSpan(candidateList),
                        key: destinationCellKey,
                        vector: vecSpan,
                        unlessWithinQ16: remember.UnlessWithinQ16,
                        matchingKey: out var matchingKey
                    )) {
                        m_traceEffectValue = "stored";
                    } else {
                        m_traceEffectValue = $"matched {matchingKey}";
                    }
                } else {
                    m_traceEffectValue = "stored";
                }
            } else {
                m_traceEffectValue = "stored";
            }
        }

        var transform = new ResolvedVectorTransform.Remember(
            IntoRowOrdinal: remember.RowOrdinal,
            IntoRowName: remember.Row,
            Key: destinationCellKey,
            FromRowOrdinal: fromRowOrdinal,
            FromRowName: fromRowName,
            FromKey: fromKey,
            FromVector: fromVector,
            UnlessWithinQ16: remember.UnlessWithinQ16
        );

        return Apply(
            effect: remember,
            ruleName: ruleName,
            mutation: new StateMutation.ApplyVector(Transform: transform),
            tick: tick,
            preflight: preflight
        );
    }
}
