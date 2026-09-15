using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Puck.Maths;

namespace Puck.World.Server;

public static partial class WorldStateTransforms {
    private static bool TryMix(WorldDefinition definition, StateTransform.Mix mix, out string reason, WorldStateRow[] rows) {
        if (!TryParseCellRef(spelling: mix.Into, key: out var intoKey, rowName: out var intoRowName)) {
            reason = $"invalid destination '{mix.Into}'";
            return false;
        }

        var targetRow = WorldDefinitionRows.FindStateRow(rows: rows, name: intoRowName);
        if (targetRow is null) {
            reason = $"no state row named '{intoRowName}'";
            return false;
        }

        if (targetRow.Kind != CellKind.Vector) {
            reason = $"destination row '{intoRowName}' is not a vector row";
            return false;
        }

        if (!WorldStateSpaces.TryResolveSpace(spaces: definition.Spaces, row: targetRow, space: out var space, reason: out reason)) {
            return false;
        }

        if (mix.Terms is null || mix.Terms.Count == 0 || mix.Terms.Count > StateCapacity.MaxMixTerms) {
            reason = $"mix terms count must be 1..{StateCapacity.MaxMixTerms}";
            return false;
        }

        var vectors = new ReadOnlyMemory<sbyte>[mix.Terms.Count];
        var weights = new int[mix.Terms.Count];

        for (var i = 0; i < mix.Terms.Count; i++) {
            var term = mix.Terms[i];
            if (term is null) {
                reason = "mix term cannot be null";
                return false;
            }

            if (term.Weight < -StateCapacity.MaxMixWeight || term.Weight > StateCapacity.MaxMixWeight || term.Weight == 0) {
                reason = $"mix weight {term.Weight} must be non-zero and within [-{StateCapacity.MaxMixWeight}, {StateCapacity.MaxMixWeight}]";
                return false;
            }

            weights[i] = term.Weight;

            if (!TryGetVectorOperand(rows: rows, operand: term.From, vector: out var vec, reason: out reason, spaces: definition.Spaces, operandSpace: out var termSpace)) {
                return false;
            }

            if (termSpace is not null) {
                if (!string.Equals(termSpace.Name.Value, space.Name.Value, StringComparison.Ordinal) || !termSpace.HasSameIdentity(space)) {
                    reason = $"spaces '{termSpace.Name}' and '{space.Name}' do not match";
                    return false;
                }
            } else if (vec.Dimensions != space.Dimensions) {
                reason = $"term '{term.From}' vector dimensions {vec.Dimensions} do not match destination space '{space.Name}' dimensions {space.Dimensions}";
                return false;
            }

            vectors[i] = vec.Memory;
        }

        Span<sbyte> destination = stackalloc sbyte[space.Dimensions];
        if (!VectorTransforms.TryMix(vectors: vectors, weights: weights, destination: destination, refusal: out var refusal)) {
            reason = refusal?.ToString() ?? "mix operation failed";
            return false;
        }

        if (!StateVector.TryCreate(components: destination, vector: out var resultVector, error: out var createError)) {
            reason = createError;
            return false;
        }
        if (!StateCellWriter.TryComposeVectorCell(
            cells: out var nextCells,
            evictedKey: out _,
            key: intoKey,
            reason: out reason,
            row: targetRow,
            vector: resultVector
        )) {
            return false;
        }

        ReplaceRow(rows: rows, next: targetRow with { Cells = nextCells });
        reason = string.Empty;
        return true;
    }

    private static bool TryMean(WorldDefinition definition, StateTransform.Mean mean, out string reason, WorldStateRow[] rows) {
        var fromRow = WorldDefinitionRows.FindStateRow(rows: rows, name: mean.From);
        if (fromRow is null) {
            reason = $"no state row named '{mean.From}'";
            return false;
        }

        if (fromRow.Kind != CellKind.Vector) {
            reason = $"source row '{mean.From}' is not a vector row";
            return false;
        }

        if (!TryParseCellRef(spelling: mean.Into, key: out var intoKey, rowName: out var intoRowName)) {
            reason = $"invalid destination '{mean.Into}'";
            return false;
        }

        var intoRow = WorldDefinitionRows.FindStateRow(rows: rows, name: intoRowName);
        if (intoRow is null) {
            reason = $"no state row named '{intoRowName}'";
            return false;
        }

        if (intoRow.Kind != CellKind.Vector) {
            reason = $"destination row '{intoRowName}' is not a vector row";
            return false;
        }

        if (!WorldStateSpaces.TryResolveSpace(spaces: definition.Spaces, row: fromRow, space: out var fromSpace, reason: out reason)) {
            return false;
        }

        if (!WorldStateSpaces.TryResolveSpace(spaces: definition.Spaces, row: intoRow, space: out var intoSpace, reason: out reason)) {
            return false;
        }

        if (!string.Equals(fromSpace.Name.Value, intoSpace.Name.Value, StringComparison.Ordinal) || !fromSpace.HasSameIdentity(intoSpace)) {
            reason = $"spaces '{fromSpace.Name}' and '{intoSpace.Name}' do not match";
            return false;
        }

        WorldStateRow? whereRow = null;
        if (!string.IsNullOrWhiteSpace(value: mean.Where)) {
            whereRow = WorldDefinitionRows.FindStateRow(rows: rows, name: mean.Where);
            if (whereRow is null) {
                reason = $"no state row named '{mean.Where}'";
                return false;
            }
            if (whereRow.Kind != CellKind.Bool) {
                reason = $"where row '{mean.Where}' must be kind bool";
                return false;
            }
            if (!whereRow.IsKeyed) {
                reason = $"where row '{mean.Where}' must be a keyed Bool table";
                return false;
            }
        }

        var candidates = new List<ReadOnlyMemory<sbyte>>();
        foreach (var cell in (fromRow.Cells ?? [])) {
            if (cell.Vector is null) {
                continue;
            }

            if (whereRow is not null) {
                var whereCell = StateRows.FindCell(cells: whereRow.Cells, key: cell.Key);
                if (whereCell is null || whereCell.Value == 0L) {
                    continue;
                }
            }

            candidates.Add(item: cell.Vector.Memory);
        }

        if (candidates.Count == 0) {
            reason = RuleRefusal.VectorMeanEmpty.ToString();
            return false;
        }

        Span<sbyte> destination = stackalloc sbyte[intoSpace.Dimensions];
        if (!VectorTransforms.TryMean(candidates: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: candidates), destination: destination, refusal: out var refusal)) {
            reason = refusal?.ToString() ?? "mean operation failed";
            return false;
        }

        if (!StateVector.TryCreate(components: destination, vector: out var resultVector, error: out var createError)) {
            reason = createError;
            return false;
        }
        if (!StateCellWriter.TryComposeVectorCell(
            cells: out var nextCells,
            evictedKey: out _,
            key: intoKey,
            reason: out reason,
            row: intoRow,
            vector: resultVector
        )) {
            return false;
        }

        ReplaceRow(rows: rows, next: intoRow with { Cells = nextCells });
        reason = string.Empty;
        return true;
    }

    private static bool TryNearest(WorldDefinition definition, StateTransform.Nearest nearest, out string reason, WorldStateRow[] rows) {
        var fromRow = WorldDefinitionRows.FindStateRow(rows: rows, name: nearest.From);
        if (fromRow is null) {
            reason = $"no state row named '{nearest.From}'";
            return false;
        }

        if (fromRow.Kind != CellKind.Vector) {
            reason = $"source row '{nearest.From}' is not a vector row";
            return false;
        }
        if (!fromRow.IsKeyed) {
            reason = $"source row '{nearest.From}' must be a keyed Vector table";
            return false;
        }

        if (!WorldStateSpaces.TryResolveSpace(spaces: definition.Spaces, row: fromRow, space: out var fromSpace, reason: out reason)) {
            return false;
        }

        var intoRow = WorldDefinitionRows.FindStateRow(rows: rows, name: nearest.Into);
        if (intoRow is null) {
            reason = $"no state row named '{nearest.Into}'";
            return false;
        }

        if (!TryGetVectorOperand(rows: rows, operand: nearest.Query, vector: out var queryVector, reason: out reason, spaces: definition.Spaces, operandSpace: out var querySpace)) {
            return false;
        }

        if (querySpace is not null) {
            if (!string.Equals(querySpace.Name.Value, fromSpace.Name.Value, StringComparison.Ordinal) || !querySpace.HasSameIdentity(fromSpace)) {
                reason = $"spaces '{querySpace.Name}' and '{fromSpace.Name}' do not match";
                return false;
            }
        } else if (queryVector.Dimensions != fromSpace.Dimensions) {
            reason = $"query vector dimensions {queryVector.Dimensions} do not match from space dimensions {fromSpace.Dimensions}";
            return false;
        }

        if (intoRow.Kind is not (CellKind.Int or CellKind.Fixed or CellKind.Text)) {
            reason = $"into row '{nearest.Into}' must be Int, Fixed, or Text kind";
            return false;
        }

        if (intoRow.Kind == CellKind.Text) {
            if (!intoRow.IsSlot) {
                reason = $"nearest into Text row '{nearest.Into}' must be a slot";
                return false;
            }
            if (nearest.K != 1) {
                reason = "nearest into Text requires k = 1";
                return false;
            }
        } else {
            if (!intoRow.IsKeyed) {
                reason = $"nearest into '{nearest.Into}' must be a keyed table";
                return false;
            }
            if (intoRow.Capacity is null || intoRow.Capacity <= 0) {
                reason = $"row '{nearest.Into}' is keyed but declares no capacity";
                return false;
            }
        }

        var effectiveCapacity = (intoRow.Capacity ?? (intoRow.IsSlot ? 1 : 0));
        var maxK = Math.Min(val1: effectiveCapacity, val2: 64);
        if (nearest.K < 1 || nearest.K > maxK) {
            reason = $"nearest k must be 1..{maxK}";
            return false;
        }

        long? threshold = null;
        if (!string.IsNullOrWhiteSpace(value: nearest.Threshold)) {
            if (intoRow.Kind == CellKind.Int) {
                if (long.TryParse(s: nearest.Threshold, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var tInt)) {
                    threshold = tInt;
                } else {
                    reason = $"invalid integer threshold '{nearest.Threshold}'";
                    return false;
                }
            } else {
                if (FixedQ4816.TryParse(s: nearest.Threshold, provider: CultureInfo.InvariantCulture, result: out var tFix)) {
                    threshold = tFix.Value;
                } else {
                    reason = $"invalid fixed threshold '{nearest.Threshold}'";
                    return false;
                }
            }
        }

        CellName? excludeKey = null;
        if (!string.IsNullOrWhiteSpace(value: nearest.Exclude)) {
            if (CellName.TryParse(candidate: nearest.Exclude, name: out var ex, reason: out _)) {
                excludeKey = ex;
            } else {
                reason = $"invalid exclude key '{nearest.Exclude}'";
                return false;
            }
        }

        WorldStateRow? whereRow = null;
        if (!string.IsNullOrWhiteSpace(value: nearest.Where)) {
            whereRow = WorldDefinitionRows.FindStateRow(rows: rows, name: nearest.Where);
            if (whereRow is null) {
                reason = $"no state row named '{nearest.Where}'";
                return false;
            }
            if (whereRow.Kind != CellKind.Bool) {
                reason = $"where row '{nearest.Where}' must be kind bool";
                return false;
            }
            if (!whereRow.IsKeyed) {
                reason = $"where row '{nearest.Where}' must be a keyed Bool table";
                return false;
            }
        }

        var sourceCells = (fromRow.Cells ?? []);
        var candidateList = new List<NearestCandidate>(capacity: sourceCells.Count);

        for (var i = 0; i < sourceCells.Count; i++) {
            var cell = sourceCells[i];
            if (cell.Vector is null) {
                continue;
            }

            var admitted = true;
            if (whereRow is not null) {
                var whereCell = StateRows.FindCell(cells: whereRow.Cells, key: cell.Key);
                if (whereCell is null || whereCell.Value == 0L) {
                    admitted = false;
                }
            }

            candidateList.Add(item: new NearestCandidate(Key: cell.Key, Components: cell.Vector.Memory, Admitted: admitted));
        }

        var matches = new VectorTransforms.NearestMatch[nearest.K];
        var isFixedScore = (intoRow.Kind is CellKind.Fixed or CellKind.Text);
        var count = VectorTransforms.SelectNearest(
            candidates: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: candidateList),
            query: queryVector.Components,
            isFixedScore: isFixedScore,
            k: nearest.K,
            threshold: threshold,
            excludeKey: excludeKey,
            farthest: nearest.Farthest,
            results: matches
        );

        if (intoRow.Kind == CellKind.Text) {
            var bestKey = ((count > 0) ? matches[0].Key.Value : string.Empty);
            if (!StateCellWriter.TryComposeTextCell(
                cells: out var textCells,
                evictedKey: out _,
                key: StateRow.SlotKey,
                reason: out reason,
                row: intoRow,
                text: bestKey
            )) {
                return false;
            }

            ReplaceRow(rows: rows, next: intoRow with { Cells = textCells });
            reason = string.Empty;
            return true;
        }

        var newCells = new List<StateCell>(capacity: count);
        for (var i = 0; i < count; i++) {
            newCells.Add(item: new StateCell(Key: matches[i].Key, Value: matches[i].Score));
        }

        ReplaceRow(rows: rows, next: intoRow with { Cells = newCells });
        reason = string.Empty;
        return true;
    }

    private static bool TryRemember(WorldDefinition definition, StateTransform.Remember remember, out string reason, WorldStateRow[] rows) {
        var intoRow = WorldDefinitionRows.FindStateRow(rows: rows, name: remember.Into);
        if (intoRow is null) {
            reason = $"no state row named '{remember.Into}'";
            return false;
        }

        if (intoRow.Kind != CellKind.Vector) {
            reason = $"destination row '{remember.Into}' is not a vector row";
            return false;
        }

        if (!CellName.TryParse(candidate: remember.Key, name: out var destKey, reason: out var keyReason)) {
            reason = $"invalid destination key '{remember.Key}': {keyReason}";
            return false;
        }

        if (!WorldStateSpaces.TryResolveSpace(spaces: definition.Spaces, row: intoRow, space: out var space, reason: out reason)) {
            return false;
        }

        if (!TryGetVectorOperand(rows: rows, operand: remember.From, vector: out var fromVector, reason: out reason, spaces: definition.Spaces, operandSpace: out var fromSpace)) {
            return false;
        }

        if (fromSpace is not null) {
            if (!string.Equals(fromSpace.Name.Value, space.Name.Value, StringComparison.Ordinal) || !fromSpace.HasSameIdentity(space)) {
                reason = $"spaces '{fromSpace.Name}' and '{space.Name}' do not match";
                return false;
            }
        } else if (fromVector.Dimensions != space.Dimensions) {
            reason = $"source vector dimensions {fromVector.Dimensions} do not match destination space '{space.Name}' dimensions {space.Dimensions}";
            return false;
        }

        if (!FixedQ4816.TryParse(s: remember.UnlessWithin, provider: CultureInfo.InvariantCulture, result: out var unlessWithin)) {
            reason = $"invalid unlessWithin threshold '{remember.UnlessWithin}'";
            return false;
        }

        if (unlessWithin < FixedQ4816.Zero || unlessWithin > FixedQ4816.One) {
            reason = "unlessWithin threshold must be within [0, 1]";
            return false;
        }

        var existingCells = (intoRow.Cells ?? []);
        var candidates = new NearestCandidate[existingCells.Count];
        for (var i = 0; i < existingCells.Count; i++) {
            var c = existingCells[i];
            candidates[i] = new NearestCandidate(Key: c.Key, Components: (c.Vector?.Memory ?? ReadOnlyMemory<sbyte>.Empty), Admitted: true);
        }

        if (!VectorTransforms.TryRemember(
            existingCells: candidates,
            key: destKey,
            vector: fromVector.Components,
            unlessWithinQ16: unlessWithin.Value,
            matchingKey: out _
        )) {
            reason = string.Empty;
            return true;
        }

        if (!StateCellWriter.TryComposeVectorCell(
            cells: out var nextCells,
            evictedKey: out _,
            key: destKey,
            reason: out reason,
            row: intoRow,
            vector: fromVector
        )) {
            return false;
        }

        ReplaceRow(rows: rows, next: intoRow with { Cells = nextCells });
        reason = string.Empty;
        return true;
    }

    private static bool TryParseCellRef(string spelling, out CellName key, out string rowName) {
        spelling = spelling.Trim();
        key = StateRow.SlotKey;

        if (spelling.EndsWith(value: ']')) {
            var bracket = spelling.IndexOf(value: '[');
            if (bracket > 0) {
                rowName = spelling[..bracket].Trim();
                var keyStr = spelling[(bracket + 1)..^1].Trim();
                return CellName.TryParse(candidate: keyStr, name: out key, reason: out _);
            }
        }

        rowName = spelling;
        return true;
    }

    private static bool TryGetVectorOperand(
        WorldStateRow[] rows,
        string operand,
        [NotNullWhen(true)] out StateVector? vector,
        out string reason
    ) => TryGetVectorOperand(rows: rows, operand: operand, vector: out vector, reason: out reason, spaces: null, operandSpace: out _);

    private static bool TryGetVectorOperand(
        WorldStateRow[] rows,
        string operand,
        [NotNullWhen(true)] out StateVector? vector,
        out string reason,
        IReadOnlyList<StateSpace>? spaces,
        out StateSpace? operandSpace
    ) {
        operandSpace = null;
        operand = operand.Trim();
        if (operand.StartsWith(value: "vector(\"", comparisonType: StringComparison.Ordinal) && operand.EndsWith(value: "\")", comparisonType: StringComparison.Ordinal)) {
            var base64 = operand[8..^2];
            if (!StateVector.TryParseBase64Url(text: base64, vector: out vector, error: out var err)) {
                reason = (err ?? "invalid vector literal");
                return false;
            }

            reason = string.Empty;
            return true;
        }

        if (!TryParseCellRef(spelling: operand, key: out var key, rowName: out var rowName)) {
            vector = null;
            reason = $"invalid vector operand '{operand}'";
            return false;
        }

        var row = WorldDefinitionRows.FindStateRow(rows: rows, name: rowName);
        if (row is null) {
            vector = null;
            reason = $"no state row named '{rowName}'";
            return false;
        }

        if (row.Kind != CellKind.Vector) {
            vector = null;
            reason = $"row '{rowName}' is not a vector row";
            return false;
        }

        if (spaces is not null && !WorldStateSpaces.TryResolveSpace(spaces: spaces, row: row, space: out operandSpace, reason: out reason)) {
            vector = null;
            return false;
        }

        var cell = StateRows.FindCell(cells: row.Cells, key: key);
        if (cell is null || cell.Vector is null) {
            vector = null;
            reason = $"vector cell '{rowName}[{key.Value}]' not found";
            return false;
        }

        vector = cell.Vector;
        reason = string.Empty;
        return true;
    }

    private static void ReplaceRow(WorldStateRow[] rows, WorldStateRow next) {
        for (var i = 0; i < rows.Length; i++) {
            if (string.Equals(a: rows[i].Name.Value, b: next.Name.Value, comparisonType: StringComparison.Ordinal)) {
                rows[i] = next;
                return;
            }
        }
    }
}
