using System.Globalization;
using Puck.Maths;

namespace Puck.State;

public static partial class RuleCompiler {
    /// <summary>Resolves a vector operand from a token into a CompiledVectorOperand.</summary>
    public static CompiledVectorOperand ResolveVectorOperand(
        VectorOperandToken token,
        RuleCompileContext context,
        string ruleName,
        string where,
        StateSpace? expectedSpace = null
    ) {
        ArgumentNullException.ThrowIfNull(argument: token);
        ArgumentNullException.ThrowIfNull(argument: context);

        return token switch {
            VectorOperandToken.Cell cell => ResolveVectorCellOperand(
                rowName: cell.Name,
                key: cell.Key,
                context: context,
                ruleName: ruleName,
                where: where,
                expectedSpace: expectedSpace
            ),
            VectorOperandToken.Literal lit => ResolveVectorLiteralOperand(
                literal: lit.Value,
                context: context,
                ruleName: ruleName,
                where: where,
                expectedSpace: expectedSpace
            ),
            VectorOperandToken.Embed embed => throw new RuleException(
                detail: $"Embedded literal '{embed.Text}' was not resolved in lock file; run puck embed",
                refusal: RuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName
            ),
            _ => throw new RuleException(
                refusal: RuleRefusal.VectorOperandNotVector,
                ruleName: ruleName,
                detail: $"Unknown vector operand token '{token}' in {where}"
            )
        };
    }

    /// <summary>Resolves a vector operand from an authored string spelling (a cell read or vector literal).</summary>
    public static CompiledVectorOperand ResolveVectorOperand(
        string operandSpelling,
        RuleCompileContext context,
        string ruleName,
        string where,
        StateSpace? expectedSpace = null
    ) {
        ArgumentNullException.ThrowIfNull(argument: operandSpelling);
        ArgumentNullException.ThrowIfNull(argument: context);

        operandSpelling = operandSpelling.Trim();

        // Check for vector literal: vector("...") or vector('...')
        if (operandSpelling.StartsWith("vector(", StringComparison.OrdinalIgnoreCase) && operandSpelling.EndsWith(')')) {
            var inner = operandSpelling["vector(".Length..^1].Trim();
            if ((inner.StartsWith('"') && inner.EndsWith('"')) || (inner.StartsWith('\'') && inner.EndsWith('\''))) {
                inner = inner[1..^1];
            }
            return ResolveVectorLiteralOperand(
                literal: inner,
                context: context,
                ruleName: ruleName,
                where: where,
                expectedSpace: expectedSpace
            );
        }

        // Check for embed literal: embed("...") or embed('...')
        if (operandSpelling.StartsWith("embed(", StringComparison.OrdinalIgnoreCase) && operandSpelling.EndsWith(')')) {
            throw new RuleException(
                refusal: RuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName,
                detail: $"Embedded literal '{operandSpelling}' was not resolved in lock file; run puck embed"
            );
        }

        // Check for raw base64url vector literal if expected space (or default space) is known and operand is not a bracketed cell reference
        var candidateLiteral = operandSpelling;
        if ((candidateLiteral.StartsWith('"') && candidateLiteral.EndsWith('"')) || (candidateLiteral.StartsWith('\'') && candidateLiteral.EndsWith('\''))) {
            candidateLiteral = candidateLiteral[1..^1].Trim();
        }
        var literalSpace = expectedSpace ?? context.FindSpace(null);
        if (literalSpace is not null && !candidateLiteral.Contains('[') && (context.FindRow(candidateLiteral) is null) && StateVector.TryParseBase64Url(text: candidateLiteral, dimensions: literalSpace.Dimensions, vector: out var parsedVec, error: out _)) {
            return new CompiledVectorOperand(constant: parsedVec!, space: literalSpace);
        }

        // Cell reference: row[key] or slot row
        string rowName;
        string? key;
        var bracketIndex = operandSpelling.IndexOf('[');
        if (bracketIndex > 0 && operandSpelling.EndsWith(']')) {
            rowName = operandSpelling[..bracketIndex].Trim();
            key = operandSpelling[(bracketIndex + 1)..^1].Trim();
        } else {
            rowName = operandSpelling;
            key = null;
        }

        return ResolveVectorCellOperand(
            rowName: rowName,
            key: key,
            context: context,
            ruleName: ruleName,
            where: where,
            expectedSpace: expectedSpace
        );
    }

    internal static CompiledVectorOperand ResolveVectorLiteralOperand(
        string literal,
        RuleCompileContext context,
        string ruleName,
        string where,
        StateSpace? expectedSpace
    ) {
        var space = expectedSpace ?? context.FindSpace(null);
        if (space is null) {
            throw new RuleException(
                refusal: RuleRefusal.VectorSpaceMismatch,
                ruleName: ruleName,
                detail: $"Vector literal in {where} has no space; declare a default space or address a typed vector row"
            );
        }

        if (!StateVector.TryParseBase64Url(text: literal, dimensions: space.Dimensions, vector: out var vec, error: out var error)) {
            throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"Vector literal in {where} is invalid: {error}"
            );
        }

        return new CompiledVectorOperand(constant: vec!, space: space);
    }

    private static CompiledVectorOperand ResolveVectorCellOperand(
        string rowName,
        string? key,
        RuleCompileContext context,
        string ruleName,
        string where,
        StateSpace? expectedSpace
    ) {
        var row = context.FindRow(name: rowName)
            ?? throw new RuleException(
                refusal: RuleRefusal.StateRowUnknown,
                ruleName: ruleName,
                detail: $"{where} addresses unknown row '{rowName}'"
            );

        if (row.Kind != CellKind.Vector) {
            throw new RuleException(
                refusal: RuleRefusal.VectorOperandNotVector,
                ruleName: ruleName,
                detail: $"{where} addresses row '{rowName}' which is kind={StateSpelling.Kind(row.Kind)}, not Vector"
            );
        }

        var space = context.FindSpace(name: row.Space)
            ?? throw new RuleException(
                refusal: RuleRefusal.VectorSpaceMismatch,
                ruleName: ruleName,
                detail: $"Row '{rowName}' names undeclared vector space '{row.Space}'"
            );

        if (expectedSpace is not null && (!string.Equals(space.Name.Value, expectedSpace.Name.Value, StringComparison.Ordinal) || !space.HasSameIdentity(other: expectedSpace))) {
            throw new RuleException(
                refusal: RuleRefusal.VectorSpaceMismatch,
                ruleName: ruleName,
                detail: $"{where} vector space mismatch: row '{rowName}' is in space '{space.Name}', but expected space '{expectedSpace.Name}'"
            );
        }

        var handle = ResolveHandle(context: context, name: rowName);
        var rowOrdinal = handle.Ordinal;

        if (row.IsSlot) {
            if (key is not null && !string.Equals(key, StateRow.SlotKey.Value, StringComparison.Ordinal)) {
                throw new RuleException(
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName,
                    detail: $"Slot row '{rowName}' cannot be indexed with key '{key}' in {where}"
                );
            }

            return new CompiledVectorOperand(
                rowOrdinal: rowOrdinal,
                handle: handle,
                rowName: rowName,
                key: null,
                keyFrom: null,
                cellKey: StateRow.SlotKey,
                space: space
            );
        }

        if (key is null) {
            throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"Keyed vector row '{rowName}' requires a cell key in {where}"
            );
        }

        if (TryResolveDynamicKey(
            cell: out var dynamicKey,
            context: context,
            key: key,
            keyFieldLabel: "key",
            ruleName: ruleName,
            verb: where
        )) {
            return new CompiledVectorOperand(
                rowOrdinal: rowOrdinal,
                handle: handle,
                rowName: rowName,
                key: key,
                keyFrom: dynamicKey,
                cellKey: default,
                space: space
            );
        }

        var cellKey = CellName.TryParse(candidate: key, name: out var parsedKey, reason: out var reason)
            ? parsedKey
            : throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"Invalid cell key '{key}' on row '{rowName}' in {where}: {reason}"
            );

        return new CompiledVectorOperand(
            rowOrdinal: rowOrdinal,
            handle: handle,
            rowName: rowName,
            key: key,
            keyFrom: null,
            cellKey: cellKey,
            space: space
        );
    }

    private static (StateRow Row, string ResolvedKey, CompiledCellRef? KeyFrom, CellName CellKey) ResolveVectorDestination(
        string intoSpelling,
        RuleCompileContext context,
        string ruleName,
        string where
    ) {
        intoSpelling = intoSpelling.Trim();
        string rowName;
        string? key;
        var bracketIndex = intoSpelling.IndexOf('[');
        if (bracketIndex > 0 && intoSpelling.EndsWith(']')) {
            rowName = intoSpelling[..bracketIndex].Trim();
            key = intoSpelling[(bracketIndex + 1)..^1].Trim();
        } else {
            rowName = intoSpelling;
            key = null;
        }

        var row = context.FindRow(name: rowName)
            ?? throw new RuleException(
                refusal: RuleRefusal.StateRowUnknown,
                ruleName: ruleName,
                detail: $"{where} addresses unknown row '{rowName}'"
            );

        if (row.Kind != CellKind.Vector) {
            throw new RuleException(
                refusal: RuleRefusal.VectorOperandNotVector,
                ruleName: ruleName,
                detail: $"{where} destination row '{rowName}' is kind={StateSpelling.Kind(row.Kind)}, not Vector"
            );
        }

        if (row.IsSlot) {
            if (key is not null && !string.Equals(key, StateRow.SlotKey.Value, StringComparison.Ordinal)) {
                throw new RuleException(
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName,
                    detail: $"Slot row '{rowName}' cannot be indexed with key '{key}' in {where}"
                );
            }
            return (row, StateRow.SlotKey.Value, null, StateRow.SlotKey);
        }

        if (key is null) {
            throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"Keyed vector destination '{rowName}' requires a cell key in {where}"
            );
        }

        if (TryResolveDynamicKey(
            cell: out var dynamicKey,
            context: context,
            key: key,
            keyFieldLabel: "key",
            ruleName: ruleName,
            verb: where
        )) {
            return (row, key, dynamicKey, default);
        }

        var cellKey = CellName.TryParse(candidate: key, name: out var parsedKey, reason: out var reason)
            ? parsedKey
            : throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"Invalid destination cell key '{key}' on row '{rowName}' in {where}: {reason}"
            );

        return (row, key, null, cellKey);
    }

    private static VectorMixEffect ResolveVectorMixTransform(
        StateTransform.Mix mix,
        string ruleName,
        RuleCompileContext context
    ) {
        if (mix.Terms is not { Count: >= 1 and <= 8 }) {
            throw new RuleException(
                refusal: RuleRefusal.VectorMixTerms,
                ruleName: ruleName,
                detail: $"mix transform requires 1 to 8 terms, got {mix.Terms?.Count ?? 0}"
            );
        }

        var (intoRow, intoKey, intoKeyFrom, intoCellKey) = ResolveVectorDestination(
            intoSpelling: mix.Into,
            context: context,
            ruleName: ruleName,
            where: "mix into"
        );

        var space = context.FindSpace(name: intoRow.Space)
            ?? throw new RuleException(
                refusal: RuleRefusal.VectorSpaceMismatch,
                ruleName: ruleName,
                detail: $"Row '{intoRow.Name}' names undeclared vector space '{intoRow.Space}'"
            );

        var terms = new List<MixTermFact>(capacity: mix.Terms.Count);
        for (var i = 0; i < mix.Terms.Count; i++) {
            var term = mix.Terms[i];
            if (term.Weight is < -1000 or > 1000 or 0) {
                throw new RuleException(
                    refusal: RuleRefusal.VectorMixTerms,
                    ruleName: ruleName,
                    detail: $"mix term [{i}] weight {term.Weight} is invalid; must be in [-1000, 1000] and non-zero"
                );
            }

            var operand = ResolveVectorOperand(
                operandSpelling: term.From,
                context: context,
                ruleName: ruleName,
                where: $"mix term [{i}]",
                expectedSpace: space
            );

            terms.Add(new MixTermFact(Source: operand, Weight: term.Weight));
        }

        var handle = ResolveHandle(context: context, name: intoRow.Name);
        return new VectorMixEffect(
            row: intoRow.Name,
            key: intoKey,
            keyFrom: intoKeyFrom,
            handle: handle,
            cellKey: intoCellKey,
            rowOrdinal: handle.Ordinal,
            terms: terms,
            dimensions: space.Dimensions,
            describe: $"mix {intoRow.Name}[{intoKey}]"
        );
    }

    private static VectorMeanEffect ResolveVectorMeanTransform(
        StateTransform.Mean mean,
        string ruleName,
        RuleCompileContext context
    ) {
        var fromRow = context.FindRow(name: mean.From)
            ?? throw new RuleException(
                refusal: RuleRefusal.StateRowUnknown,
                ruleName: ruleName,
                detail: $"mean from row '{mean.From}' is unknown"
            );

        if (fromRow.Kind != CellKind.Vector || !fromRow.IsKeyed) {
            throw new RuleException(
                refusal: RuleRefusal.VectorOperandNotVector,
                ruleName: ruleName,
                detail: $"mean from row '{mean.From}' must be a keyed Vector table"
            );
        }

        var space = context.FindSpace(name: fromRow.Space)
            ?? throw new RuleException(
                refusal: RuleRefusal.VectorSpaceMismatch,
                ruleName: ruleName,
                detail: $"Row '{fromRow.Name}' names undeclared vector space '{fromRow.Space}'"
            );

        var (intoRow, intoKey, intoKeyFrom, intoCellKey) = ResolveVectorDestination(
            intoSpelling: mean.Into,
            context: context,
            ruleName: ruleName,
            where: "mean into"
        );

        var intoSpace = context.FindSpace(name: intoRow.Space)
            ?? throw new RuleException(
                refusal: RuleRefusal.VectorSpaceMismatch,
                ruleName: ruleName,
                detail: $"Row '{intoRow.Name}' names undeclared vector space '{intoRow.Space}'"
            );

        if (!string.Equals(space.Name.Value, intoSpace.Name.Value, StringComparison.Ordinal) || !space.HasSameIdentity(other: intoSpace)) {
            throw new RuleException(
                refusal: RuleRefusal.VectorSpaceMismatch,
                ruleName: ruleName,
                detail: $"mean from space '{space.Name}' does not match into space '{intoSpace.Name}'"
            );
        }

        int? whereOrdinal = null;
        string? whereName = null;
        StateHandle whereHandle = default;
        if (mean.Where is { } whereStr) {
            var whereRow = context.FindRow(name: whereStr)
                ?? throw new RuleException(
                    refusal: RuleRefusal.StateRowUnknown,
                    ruleName: ruleName,
                    detail: $"mean where row '{whereStr}' is unknown"
                );

            if (whereRow.Kind != CellKind.Bool || !whereRow.IsKeyed) {
                throw new RuleException(
                    refusal: RuleRefusal.VectorFilterShape,
                    ruleName: ruleName,
                    detail: $"mean where row '{whereStr}' must be a keyed Bool table"
                );
            }

            whereHandle = ResolveHandle(context: context, name: whereStr);
            whereOrdinal = whereHandle.Ordinal;
            whereName = whereStr;
        }

        var intoHandle = ResolveHandle(context: context, name: intoRow.Name);
        var fromHandle = ResolveHandle(context: context, name: fromRow.Name);

        return new VectorMeanEffect(
            row: intoRow.Name,
            key: intoKey,
            keyFrom: intoKeyFrom,
            handle: intoHandle,
            cellKey: intoCellKey,
            rowOrdinal: intoHandle.Ordinal,
            fromRowOrdinal: fromHandle.Ordinal,
            fromRowName: fromRow.Name,
            fromHandle: fromHandle,
            dimensions: space.Dimensions,
            fromCapacity: fromRow.Capacity.GetValueOrDefault(),
            whereRowOrdinal: whereOrdinal,
            whereRowName: whereName,
            whereHandle: whereHandle,
            describe: $"mean {mean.From} -> {intoRow.Name}[{intoKey}]"
        );
    }

    private static VectorNearestEffect ResolveVectorNearestTransform(
        StateTransform.Nearest nearest,
        string ruleName,
        RuleCompileContext context
    ) {
        var fromRow = context.FindRow(name: nearest.From)
            ?? throw new RuleException(
                refusal: RuleRefusal.StateRowUnknown,
                ruleName: ruleName,
                detail: $"nearest from row '{nearest.From}' is unknown"
            );

        if (fromRow.Kind != CellKind.Vector || !fromRow.IsKeyed) {
            throw new RuleException(
                refusal: RuleRefusal.VectorOperandNotVector,
                ruleName: ruleName,
                detail: $"nearest from row '{nearest.From}' must be a keyed Vector table"
            );
        }

        var space = context.FindSpace(name: fromRow.Space)
            ?? throw new RuleException(
                refusal: RuleRefusal.VectorSpaceMismatch,
                ruleName: ruleName,
                detail: $"Row '{fromRow.Name}' names undeclared vector space '{fromRow.Space}'"
            );

        var query = ResolveVectorOperand(
            operandSpelling: nearest.Query,
            context: context,
            ruleName: ruleName,
            where: "nearest query",
            expectedSpace: space
        );

        var intoRow = context.FindRow(name: nearest.Into)
            ?? throw new RuleException(
                refusal: RuleRefusal.StateRowUnknown,
                ruleName: ruleName,
                detail: $"nearest into row '{nearest.Into}' is unknown"
            );

        bool isIntoSlot;
        if (intoRow.Kind == CellKind.Text) {
            if (!intoRow.IsSlot) {
                throw new RuleException(
                    refusal: RuleRefusal.VectorNearestShape,
                    ruleName: ruleName,
                    detail: $"nearest into Text row '{nearest.Into}' must be a slot"
                );
            }
            if (nearest.K != 1) {
                throw new RuleException(
                    refusal: RuleRefusal.VectorNearestShape,
                    ruleName: ruleName,
                    detail: $"nearest into Text slot '{nearest.Into}' requires k: 1, got k: {nearest.K}"
                );
            }
            isIntoSlot = true;
        } else if (intoRow.Kind is CellKind.Int or CellKind.Fixed) {
            if (!intoRow.IsKeyed || intoRow.Capacity <= 0) {
                throw new RuleException(
                    refusal: RuleRefusal.VectorNearestShape,
                    ruleName: ruleName,
                    detail: $"nearest into '{nearest.Into}' must be a keyed table with declared capacity"
                );
            }
            var maxK = Math.Min(intoRow.Capacity.GetValueOrDefault(), 64);
            if (nearest.K < 1 || nearest.K > maxK) {
                throw new RuleException(
                    refusal: RuleRefusal.VectorNearestShape,
                    ruleName: ruleName,
                    detail: $"nearest k must be in [1, {maxK}], got {nearest.K}"
                );
            }
            isIntoSlot = false;
        } else {
            throw new RuleException(
                refusal: RuleRefusal.VectorNearestShape,
                ruleName: ruleName,
                detail: $"nearest into row '{nearest.Into}' has unsupported kind={intoRow.Kind}; must be Int, Fixed, or Text"
            );
        }

        long? threshold = null;
        if (nearest.Threshold is { } thStr) {
            if (intoRow.Kind == CellKind.Int) {
                if (!long.TryParse(thStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt)) {
                    throw new RuleException(
                        refusal: RuleRefusal.VectorNearestShape,
                        ruleName: ruleName,
                        detail: $"nearest threshold '{thStr}' must be an integer for Int destination '{nearest.Into}'"
                    );
                }
                threshold = parsedInt;
            } else {
                if (!FixedQ4816.TryParse(thStr, CultureInfo.InvariantCulture, out var parsedFixed)) {
                    throw new RuleException(
                        refusal: RuleRefusal.VectorNearestShape,
                        ruleName: ruleName,
                        detail: $"nearest threshold '{thStr}' must be a decimal for Fixed/Text destination '{nearest.Into}'"
                    );
                }
                threshold = parsedFixed.Value;
            }
        }

        int? whereOrdinal = null;
        string? whereName = null;
        StateHandle whereHandle = default;
        if (nearest.Where is { } whereStr) {
            var whereRow = context.FindRow(name: whereStr)
                ?? throw new RuleException(
                    refusal: RuleRefusal.StateRowUnknown,
                    ruleName: ruleName,
                    detail: $"nearest where row '{whereStr}' is unknown"
                );

            if (whereRow.Kind != CellKind.Bool || !whereRow.IsKeyed) {
                throw new RuleException(
                    refusal: RuleRefusal.VectorFilterShape,
                    ruleName: ruleName,
                    detail: $"nearest where row '{whereStr}' must be a keyed Bool table"
                );
            }

            whereHandle = ResolveHandle(context: context, name: whereStr);
            whereOrdinal = whereHandle.Ordinal;
            whereName = whereStr;
        }

        var excludeKey = nearest.Exclude;
        CompiledCellRef? excludeKeyFrom = null;
        CellName excludeCellKey = default;
        if (nearest.Exclude is { } exStr) {
            if (TryResolveDynamicKey(
                cell: out var exDynamic,
                context: context,
                key: exStr,
                keyFieldLabel: "exclude",
                ruleName: ruleName,
                verb: "nearest"
            )) {
                excludeKeyFrom = exDynamic;
            } else if (CellName.TryParse(candidate: exStr, name: out var parsedEx, reason: out _)) {
                excludeCellKey = parsedEx;
            } else {
                throw new RuleException(
                    refusal: RuleRefusal.VectorExcludeKey,
                    ruleName: ruleName,
                    detail: $"nearest exclude '{exStr}' is not a valid key"
                );
            }
        }

        var intoHandle = ResolveHandle(context: context, name: intoRow.Name);
        var fromHandle = ResolveHandle(context: context, name: fromRow.Name);

        return new VectorNearestEffect(
            intoRowOrdinal: intoHandle.Ordinal,
            intoRowName: intoRow.Name,
            intoHandle: intoHandle,
            intoKind: intoRow.Kind,
            isIntoSlot: isIntoSlot,
            fromRowOrdinal: fromHandle.Ordinal,
            fromRowName: fromRow.Name,
            fromHandle: fromHandle,
            dimensions: space.Dimensions,
            fromCapacity: fromRow.Capacity.GetValueOrDefault(),
            query: query,
            k: nearest.K,
            threshold: threshold,
            farthest: nearest.Farthest,
            whereRowOrdinal: whereOrdinal,
            whereRowName: whereName,
            whereHandle: whereHandle,
            excludeKey: excludeKey,
            excludeKeyFrom: excludeKeyFrom,
            excludeCellKey: excludeCellKey,
            describe: $"nearest {nearest.From} -> {intoRow.Name}"
        );
    }

    private static VectorRememberEffect ResolveVectorRememberTransform(
        StateTransform.Remember remember,
        string ruleName,
        RuleCompileContext context
    ) {
        var intoRow = context.FindRow(name: remember.Into)
            ?? throw new RuleException(
                refusal: RuleRefusal.StateRowUnknown,
                ruleName: ruleName,
                detail: $"remember into row '{remember.Into}' is unknown"
            );

        if (intoRow.Kind != CellKind.Vector || !intoRow.IsKeyed || intoRow.Capacity <= 0) {
            throw new RuleException(
                refusal: RuleRefusal.VectorRememberShape,
                ruleName: ruleName,
                detail: $"remember into row '{remember.Into}' must be a keyed Vector table with declared capacity"
            );
        }

        var space = context.FindSpace(name: intoRow.Space)
            ?? throw new RuleException(
                refusal: RuleRefusal.VectorSpaceMismatch,
                ruleName: ruleName,
                detail: $"Row '{intoRow.Name}' names undeclared vector space '{intoRow.Space}'"
            );

        var source = ResolveVectorOperand(
            operandSpelling: remember.From,
            context: context,
            ruleName: ruleName,
            where: "remember from",
            expectedSpace: space
        );

        if (!FixedQ4816.TryParse(remember.UnlessWithin, CultureInfo.InvariantCulture, out var unlessWithinFixed) ||
            unlessWithinFixed.Value < 0L || unlessWithinFixed.Value > FixedQ4816.One.Value) {
            throw new RuleException(
                refusal: RuleRefusal.VectorRememberShape,
                ruleName: ruleName,
                detail: $"remember unlessWithin '{remember.UnlessWithin}' must be a decimal in [0, 1]"
            );
        }
        var unlessWithinQ16 = unlessWithinFixed.Value;

        CompiledCellRef? keyFrom = null;
        CellName cellKey = default;
        if (TryResolveDynamicKey(
            cell: out var dynamicKey,
            context: context,
            key: remember.Key,
            keyFieldLabel: "key",
            ruleName: ruleName,
            verb: "remember"
        )) {
            keyFrom = dynamicKey;
        } else if (CellName.TryParse(candidate: remember.Key, name: out var parsedKey, reason: out var reason)) {
            cellKey = parsedKey;
        } else {
            throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"Invalid remember key '{remember.Key}': {reason}"
            );
        }

        var intoHandle = ResolveHandle(context: context, name: intoRow.Name);

        return new VectorRememberEffect(
            row: intoRow.Name,
            key: remember.Key,
            keyFrom: keyFrom,
            handle: intoHandle,
            cellKey: cellKey,
            rowOrdinal: intoHandle.Ordinal,
            dimensions: space.Dimensions,
            capacity: intoRow.Capacity.GetValueOrDefault(),
            source: source,
            unlessWithinQ16: unlessWithinQ16,
            describe: $"remember {remember.From} -> {intoRow.Name}[{remember.Key}]"
        );
    }
}
