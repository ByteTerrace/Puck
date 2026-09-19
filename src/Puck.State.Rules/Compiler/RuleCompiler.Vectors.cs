using System.Globalization;
using Puck.Maths;

namespace Puck.State.Rules;

public static partial class RuleCompiler {
    /// <summary>Resolves a vector operand from an authored string spelling. One grammar serves every vector
    /// operand: <c>mix</c>, <c>nearest</c>, <c>remember</c>, and <c>setState.vector</c> read through the same lexer
    /// as <c>dot()</c> and <c>similarity()</c>.</summary>
    /// <param name="operandSpelling">The authored spelling.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="where">Where the operand is spelled, for refusal text.</param>
    /// <param name="expectedSpace">The space the operand must belong to, or <see langword="null"/>.</param>
    /// <returns>The compiled vector operand.</returns>
    public static CompiledVector ResolveVectorOperand(string operandSpelling, RuleCompileContext context, string ruleName, string where, StateSpace? expectedSpace = null) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: operandSpelling);

        if (!ExpressionSpelling.TryParseVector(
            error: out var error,
            text: operandSpelling,
            token: out var token
        )) {
            throw new RuleException(
                detail: $"Vector operand '{operandSpelling}' in {where} {error}",
                refusal: RuleRefusal.VectorOperandNotVector,
                ruleName: ruleName
            );
        }

        return ResolveVectorOperand(
            context: context,
            expectedSpace: expectedSpace,
            ruleName: ruleName,
            token: token,
            where: where
        );
    }
    /// <summary>Resolves a vector operand from a parsed token.</summary>
    /// <param name="token">The parsed operand.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="where">Where the operand is spelled, for refusal text.</param>
    /// <param name="expectedSpace">The space the operand must belong to, or <see langword="null"/>.</param>
    /// <returns>The compiled vector operand.</returns>
    public static CompiledVector ResolveVectorOperand(VectorOperand token, RuleCompileContext context, string ruleName, string where, StateSpace? expectedSpace = null) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: token);

        return (token switch {
            VectorOperand.Cell cell => ResolveVectorCellOperand(
                context: context,
                expectedSpace: expectedSpace,
                key: cell.Key,
                rowName: cell.Name,
                ruleName: ruleName,
                where: where
            ),
            VectorOperand.Literal literal => ResolveVectorLiteralOperand(
                context: context,
                expectedSpace: expectedSpace,
                literal: literal.Value,
                ruleName: ruleName,
                where: where
            ),
            VectorOperand.Embed embed => throw new RuleException(
            detail: $"Embedded literal '{embed.Text}' was not resolved in lock file; run puck embed",
            refusal: RuleRefusal.EffectSourceAmbiguous,
            ruleName: ruleName
        ),
            _ => throw new RuleException(
            detail: $"Unknown vector operand token '{token}' in {where}",
            refusal: RuleRefusal.VectorOperandNotVector,
            ruleName: ruleName
        ),
        });
    }

    // The `vector` field admits both the vector("…") spelling the one lexer reads and the bare base64url payload,
    // which is an identifier to that lexer and so would resolve as a cell.
    private static CompiledVector ResolveVectorLiteral(string spelling, RuleCompileContext context, string ruleName, string where, StateSpace expectedSpace) => ((ExpressionSpelling.TryParseVector(
        error: out _,
        text: spelling,
        token: out var token
    ) && (token is not VectorOperand.Cell))
        ? ResolveVectorOperand(
            context: context,
            expectedSpace: expectedSpace,
            ruleName: ruleName,
            token: token,
            where: where
        )
        : ResolveVectorLiteralOperand(
            context: context,
            expectedSpace: expectedSpace,
            literal: spelling,
            ruleName: ruleName,
            where: where
        )
    );
    private static VectorCallOperand ResolveVectorCallOperand(InstructionPayload.Vector call, ExpressionOp operation, CellKind operandKind, RuleCompileContext context, string ruleName, string verb) {
        CompiledVector left;
        CompiledVector right;

        if (call.Left is VectorOperand.Cell) {
            left = ResolveVectorOperand(
                context: context,
                ruleName: ruleName,
                token: call.Left,
                where: $"{verb} vector {operation}"
            );
            right = ResolveVectorOperand(
                context: context,
                expectedSpace: left.Space,
                ruleName: ruleName,
                token: call.Right,
                where: $"{verb} vector {operation}"
            );
        } else if (call.Right is VectorOperand.Cell) {
            right = ResolveVectorOperand(
                context: context,
                ruleName: ruleName,
                token: call.Right,
                where: $"{verb} vector {operation}"
            );
            left = ResolveVectorOperand(
                context: context,
                expectedSpace: right.Space,
                ruleName: ruleName,
                token: call.Left,
                where: $"{verb} vector {operation}"
            );
        } else {
            var defaultSpace = (context.FindSpace(name: null)
                ?? throw new RuleException(
                detail: $"'{verb}' vector literal has no space; declare a default space or address a typed vector row",
                refusal: RuleRefusal.VectorSpaceMismatch,
                ruleName: ruleName
            ));

            left = ResolveVectorOperand(
                context: context,
                expectedSpace: defaultSpace,
                ruleName: ruleName,
                token: call.Left,
                where: $"{verb} vector {operation}"
            );
            right = ResolveVectorOperand(
                context: context,
                expectedSpace: defaultSpace,
                ruleName: ruleName,
                token: call.Right,
                where: $"{verb} vector {operation}"
            );
        }

        if (
            !string.Equals(
            a: left.Space.Name.Value,
            b: right.Space.Name.Value,
            comparisonType: StringComparison.Ordinal
        ) ||
            !left.Space.HasSameIdentity(other: right.Space)
        ) {
            throw new RuleException(
                detail: $"'{verb}' vector call spaces mismatch: left is '{left.Space.Name}', right is '{right.Space.Name}'",
                refusal: RuleRefusal.VectorSpaceMismatch,
                ruleName: ruleName
            );
        }

        return new VectorCallOperand(
            left: left,
            operation: operation,
            right: right,
            valueKind: operandKind
        );
    }
    private static CompiledVector ResolveVectorCellOperand(string rowName, string? key, RuleCompileContext context, string ruleName, string where, StateSpace? expectedSpace) {
        var row = (context.FindRow(name: rowName)
            ?? throw new RuleException(
            detail: $"{where} addresses unknown row '{rowName}'",
            refusal: RuleRefusal.StateRowUnknown,
            ruleName: ruleName
        ));

        if (row.Kind != CellKind.Vector) {
            throw new RuleException(
                detail: $"{where} addresses row '{rowName}' which is kind={StateSpelling.Kind(kind: row.Kind)}, not Vector",
                refusal: RuleRefusal.VectorOperandNotVector,
                ruleName: ruleName
            );
        }

        var space = (context.FindSpace(name: row.Space)
            ?? throw new RuleException(
            detail: $"Row '{rowName}' names undeclared vector space '{row.Space}'",
            refusal: RuleRefusal.VectorSpaceMismatch,
            ruleName: ruleName
        ));

        if (
            (expectedSpace is not null) &&
            (!string.Equals(
            a: space.Name.Value,
            b: expectedSpace.Name.Value,
            comparisonType: StringComparison.Ordinal
        ) || !space.HasSameIdentity(other: expectedSpace))
        ) {
            throw new RuleException(
                detail: $"{where} vector space mismatch: row '{rowName}' is in space '{space.Name}', but expected space '{expectedSpace.Name}'",
                refusal: RuleRefusal.VectorSpaceMismatch,
                ruleName: ruleName
            );
        }

        var rowOrdinal = ResolveRowOrdinal(
            context: context,
            name: rowName
        );

        if (row.IsSlot) {
            if (
                (key is not null) &&
                !string.Equals(
                a: key,
                b: StateRow.SlotKey.Value,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                throw new RuleException(
                    detail: $"Slot row '{rowName}' cannot be indexed with key '{key}' in {where}",
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName
                );
            }

            return new CompiledVector(
                describe: rowName,
                key: InternKey(
                    context: context,
                    name: StateRow.SlotKey.Value
                ),
                keyFrom: null,
                rowOrdinal: rowOrdinal,
                space: space
            );
        }
        if (key is null) {
            throw new RuleException(
                detail: $"Keyed vector row '{rowName}' requires a cell key in {where}",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
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
            return new CompiledVector(
                describe: $"{rowName}[{key}]",
                key: default,
                keyFrom: dynamicKey,
                rowOrdinal: rowOrdinal,
                space: space
            );
        }
        if (!CellName.TryParse(
            candidate: key,
            name: out var parsed,
            reason: out var reason
        )) {
            throw new RuleException(
                detail: $"Invalid cell key '{key}' on row '{rowName}' in {where}: {reason}",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        return new CompiledVector(
            describe: $"{rowName}[{key}]",
            key: context.Catalog.Keys.Intern(name: parsed),
            keyFrom: null,
            rowOrdinal: rowOrdinal,
            space: space
        );
    }
    private static (StateRow Row, int RowOrdinal, CellKey Key, CompiledCellRef? KeyFrom, string Spelling) ResolveVectorDestination(string intoSpelling, RuleCompileContext context, string ruleName, string where) {
        intoSpelling = intoSpelling.Trim();

        var bracketIndex = intoSpelling.IndexOf(value: '[');
        string? key;
        string rowName;

        if (
            (bracketIndex > 0) &&
            intoSpelling.EndsWith(value: ']')
        ) {
            key = intoSpelling[(bracketIndex + 1)..^1].Trim();
            rowName = intoSpelling[..bracketIndex].Trim();
        } else {
            key = null;
            rowName = intoSpelling;
        }

        var row = (context.FindRow(name: rowName)
            ?? throw new RuleException(
            detail: $"{where} addresses unknown row '{rowName}'",
            refusal: RuleRefusal.StateRowUnknown,
            ruleName: ruleName
        ));

        if (row.Kind != CellKind.Vector) {
            throw new RuleException(
                detail: $"{where} destination row '{rowName}' is kind={StateSpelling.Kind(kind: row.Kind)}, not Vector",
                refusal: RuleRefusal.VectorOperandNotVector,
                ruleName: ruleName
            );
        }

        var rowOrdinal = ResolveRowOrdinal(
            context: context,
            name: rowName
        );

        if (row.IsSlot) {
            if (
                (key is not null) &&
                !string.Equals(
                a: key,
                b: StateRow.SlotKey.Value,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                throw new RuleException(
                    detail: $"Slot row '{rowName}' cannot be indexed with key '{key}' in {where}",
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName
                );
            }

            return (row, rowOrdinal, InternKey(
                context: context,
                name: StateRow.SlotKey.Value
            ), null, rowName);
        }
        if (key is null) {
            throw new RuleException(
                detail: $"Keyed vector destination '{rowName}' requires a cell key in {where}",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
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
            return (row, rowOrdinal, default, dynamicKey, $"{rowName}[{key}]");
        }
        if (!CellName.TryParse(
            candidate: key,
            name: out var parsed,
            reason: out var reason
        )) {
            throw new RuleException(
                detail: $"Invalid destination cell key '{key}' on row '{rowName}' in {where}: {reason}",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        return (row, rowOrdinal, context.Catalog.Keys.Intern(name: parsed), null, $"{rowName}[{key}]");
    }
    private static CompiledVector ResolveVectorLiteralOperand(string literal, RuleCompileContext context, string ruleName, string where, StateSpace? expectedSpace) {
        var space = (expectedSpace
            ?? (context.FindSpace(name: null)
            ?? throw new RuleException(
            detail: $"Vector literal in {where} has no space; declare a default space or address a typed vector row",
            refusal: RuleRefusal.VectorSpaceMismatch,
            ruleName: ruleName
        )));

        if (!StateVector.TryParseBase64Url(
            dimensions: space.Dimensions,
            error: out var error,
            text: literal,
            vector: out var parsed
        )) {
            throw new RuleException(
                detail: $"Vector literal in {where} is invalid: {error}",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        return new CompiledVector(
            constant: parsed!,
            space: space
        );
    }
    private static VectorMeanEffect ResolveVectorMeanTransform(StateTransform.Mean mean, string ruleName, RuleCompileContext context) {
        var fromRow = (context.FindRow(name: mean.From)
            ?? throw new RuleException(
            detail: $"mean from row '{mean.From}' is unknown",
            refusal: RuleRefusal.StateRowUnknown,
            ruleName: ruleName
        ));

        if (
            (fromRow.Kind != CellKind.Vector) ||
            !fromRow.IsKeyed
        ) {
            throw new RuleException(
                detail: $"mean from row '{mean.From}' must be a keyed Vector table",
                refusal: RuleRefusal.VectorOperandNotVector,
                ruleName: ruleName
            );
        }

        var space = (context.FindSpace(name: fromRow.Space)
            ?? throw new RuleException(
            detail: $"Row '{fromRow.Name}' names undeclared vector space '{fromRow.Space}'",
            refusal: RuleRefusal.VectorSpaceMismatch,
            ruleName: ruleName
        ));
        var into = ResolveVectorDestination(
            context: context,
            intoSpelling: mean.Into,
            ruleName: ruleName,
            where: "mean into"
        );
        var intoSpace = (context.FindSpace(name: into.Row.Space)
            ?? throw new RuleException(
            detail: $"Row '{into.Row.Name}' names undeclared vector space '{into.Row.Space}'",
            refusal: RuleRefusal.VectorSpaceMismatch,
            ruleName: ruleName
        ));

        if (
            !string.Equals(
            a: space.Name.Value,
            b: intoSpace.Name.Value,
            comparisonType: StringComparison.Ordinal
        ) ||
            !space.HasSameIdentity(other: intoSpace)
        ) {
            throw new RuleException(
                detail: $"mean from space '{space.Name}' does not match into space '{intoSpace.Name}'",
                refusal: RuleRefusal.VectorSpaceMismatch,
                ruleName: ruleName
            );
        }

        return new VectorMeanEffect(
            describe: $"mean {mean.From} -> {into.Spelling}",
            dimensions: space.Dimensions,
            fromCapacity: fromRow.Capacity.GetValueOrDefault(),
            fromRowOrdinal: ResolveRowOrdinal(
                context: context,
                name: fromRow.Name.Value
            ),
            key: into.Key,
            keyFrom: into.KeyFrom,
            rowOrdinal: into.RowOrdinal,
            whereRowOrdinal: ResolveVectorFilter(
                context: context,
                filter: mean.Where,
                ruleName: ruleName,
                verb: "mean"
            )
        );
    }
    private static VectorMixEffect ResolveVectorMixTransform(StateTransform.Mix mix, string ruleName, RuleCompileContext context) {
        if (mix.Terms is not { Count: >= 1 and <= StateCapacity.MaxMixTerms }) {
            throw new RuleException(
                detail: $"mix transform requires 1 to {StateCapacity.MaxMixTerms} terms, got {(mix.Terms?.Count ?? 0)}",
                refusal: RuleRefusal.VectorMixTerms,
                ruleName: ruleName
            );
        }

        var into = ResolveVectorDestination(
            context: context,
            intoSpelling: mix.Into,
            ruleName: ruleName,
            where: "mix into"
        );
        var space = (context.FindSpace(name: into.Row.Space)
            ?? throw new RuleException(
            detail: $"Row '{into.Row.Name}' names undeclared vector space '{into.Row.Space}'",
            refusal: RuleRefusal.VectorSpaceMismatch,
            ruleName: ruleName
        ));
        var terms = new MixTermFact[mix.Terms.Count];

        for (var index = 0; (index < mix.Terms.Count); index++) {
            var term = mix.Terms[index];

            if (term.Weight is < (-StateCapacity.MaxMixWeight) or > StateCapacity.MaxMixWeight or 0) {
                throw new RuleException(
                    detail: $"mix term [{index}] weight {term.Weight} is invalid; must be in [-{StateCapacity.MaxMixWeight}, {StateCapacity.MaxMixWeight}] and non-zero",
                    refusal: RuleRefusal.VectorMixTerms,
                    ruleName: ruleName
                );
            }

            terms[index] = new MixTermFact(
                Source: ResolveVectorOperand(
                    context: context,
                    expectedSpace: space,
                    operandSpelling: term.From,
                    ruleName: ruleName,
                    where: $"mix term [{index}]"
                ),
                Weight: term.Weight
            );
        }

        return new VectorMixEffect(
            describe: $"mix {into.Spelling}",
            dimensions: space.Dimensions,
            key: into.Key,
            keyFrom: into.KeyFrom,
            rowOrdinal: into.RowOrdinal,
            terms: terms
        );
    }
    private static VectorNearestEffect ResolveVectorNearestTransform(StateTransform.Nearest nearest, string ruleName, RuleCompileContext context) {
        var fromRow = (context.FindRow(name: nearest.From)
            ?? throw new RuleException(
            detail: $"nearest from row '{nearest.From}' is unknown",
            refusal: RuleRefusal.StateRowUnknown,
            ruleName: ruleName
        ));

        if (
            (fromRow.Kind != CellKind.Vector) ||
            !fromRow.IsKeyed
        ) {
            throw new RuleException(
                detail: $"nearest from row '{nearest.From}' must be a keyed Vector table",
                refusal: RuleRefusal.VectorOperandNotVector,
                ruleName: ruleName
            );
        }

        var space = (context.FindSpace(name: fromRow.Space)
            ?? throw new RuleException(
            detail: $"Row '{fromRow.Name}' names undeclared vector space '{fromRow.Space}'",
            refusal: RuleRefusal.VectorSpaceMismatch,
            ruleName: ruleName
        ));
        var query = ResolveVectorOperand(
            context: context,
            expectedSpace: space,
            operandSpelling: nearest.Query,
            ruleName: ruleName,
            where: "nearest query"
        );
        var intoRow = (context.FindRow(name: nearest.Into)
            ?? throw new RuleException(
            detail: $"nearest into row '{nearest.Into}' is unknown",
            refusal: RuleRefusal.StateRowUnknown,
            ruleName: ruleName
        ));
        bool isIntoSlot;

        if (intoRow.Kind == CellKind.Text) {
            if (!intoRow.IsSlot) {
                throw new RuleException(
                    detail: $"nearest into Text row '{nearest.Into}' must be a slot",
                    refusal: RuleRefusal.VectorNearestShape,
                    ruleName: ruleName
                );
            }
            if (nearest.K != 1) {
                throw new RuleException(
                    detail: $"nearest into Text slot '{nearest.Into}' requires k: 1, got k: {nearest.K}",
                    refusal: RuleRefusal.VectorNearestShape,
                    ruleName: ruleName
                );
            }

            isIntoSlot = true;
        } else if (intoRow.Kind is CellKind.Int or CellKind.Fixed) {
            if (
                !intoRow.IsKeyed ||
                (intoRow.Capacity <= 0)
            ) {
                throw new RuleException(
                    detail: $"nearest into '{nearest.Into}' must be a keyed table with declared capacity",
                    refusal: RuleRefusal.VectorNearestShape,
                    ruleName: ruleName
                );
            }

            var maxK = Math.Min(
                val1: intoRow.Capacity.GetValueOrDefault(),
                val2: StateCapacity.MaxNearestResults
            );

            if (
                (nearest.K < 1) ||
                (nearest.K > maxK)
            ) {
                throw new RuleException(
                    detail: $"nearest k must be in [1, {maxK}], got {nearest.K}",
                    refusal: RuleRefusal.VectorNearestShape,
                    ruleName: ruleName
                );
            }

            isIntoSlot = false;
        } else {
            throw new RuleException(
                detail: $"nearest into row '{nearest.Into}' has unsupported kind={StateSpelling.Kind(kind: intoRow.Kind)}; must be Int, Fixed, or Text",
                refusal: RuleRefusal.VectorNearestShape,
                ruleName: ruleName
            );
        }

        long? threshold = null;

        if (nearest.Threshold is { } spelledThreshold) {
            if (intoRow.Kind == CellKind.Int) {
                if (!long.TryParse(
                    spelledThreshold,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedInt
                )) {
                    throw new RuleException(
                        detail: $"nearest threshold '{spelledThreshold}' must be an integer for Int destination '{nearest.Into}'",
                        refusal: RuleRefusal.VectorNearestShape,
                        ruleName: ruleName
                    );
                }

                threshold = parsedInt;
            } else {
                if (!FixedQ4816.TryParse(
                    spelledThreshold,
                    CultureInfo.InvariantCulture,
                    out var parsedFixed
                )) {
                    throw new RuleException(
                        detail: $"nearest threshold '{spelledThreshold}' must be a decimal for Fixed/Text destination '{nearest.Into}'",
                        refusal: RuleRefusal.VectorNearestShape,
                        ruleName: ruleName
                    );
                }

                threshold = parsedFixed.Value;
            }
        }

        var excludeKey = default(CellKey);
        CompiledCellRef? excludeKeyFrom = null;

        if (nearest.Exclude is { } spelledExclude) {
            if (TryResolveDynamicKey(
                cell: out var dynamicExclude,
                context: context,
                key: spelledExclude,
                keyFieldLabel: "exclude",
                ruleName: ruleName,
                verb: "nearest"
            )) {
                excludeKeyFrom = dynamicExclude;
            } else if (CellName.TryParse(
                candidate: spelledExclude,
                name: out var parsedExclude,
                reason: out _
            )) {
                excludeKey = context.Catalog.Keys.Intern(name: parsedExclude);
            } else {
                throw new RuleException(
                    detail: $"nearest exclude '{spelledExclude}' is not a valid key",
                    refusal: RuleRefusal.VectorExcludeKey,
                    ruleName: ruleName
                );
            }
        }

        return new VectorNearestEffect(
            describe: $"nearest {nearest.From} -> {nearest.Into}",
            dimensions: space.Dimensions,
            excludeKey: excludeKey,
            excludeKeyFrom: excludeKeyFrom,
            farthest: nearest.Farthest,
            fromCapacity: fromRow.Capacity.GetValueOrDefault(),
            fromRowOrdinal: ResolveRowOrdinal(
                context: context,
                name: fromRow.Name.Value
            ),
            intoKind: intoRow.Kind,
            isIntoSlot: isIntoSlot,
            k: nearest.K,
            query: query,
            rowOrdinal: ResolveRowOrdinal(
                context: context,
                name: intoRow.Name.Value
            ),
            threshold: threshold,
            whereRowOrdinal: ResolveVectorFilter(
                context: context,
                filter: nearest.Where,
                ruleName: ruleName,
                verb: "nearest"
            )
        );
    }
    private static VectorRememberEffect ResolveVectorRememberTransform(StateTransform.Remember remember, string ruleName, RuleCompileContext context) {
        var intoRow = (context.FindRow(name: remember.Into)
            ?? throw new RuleException(
            detail: $"remember into row '{remember.Into}' is unknown",
            refusal: RuleRefusal.StateRowUnknown,
            ruleName: ruleName
        ));

        if (
            (intoRow.Kind != CellKind.Vector) ||
            !intoRow.IsKeyed ||
            (intoRow.Capacity <= 0)
        ) {
            throw new RuleException(
                detail: $"remember into row '{remember.Into}' must be a keyed Vector table with declared capacity",
                refusal: RuleRefusal.VectorRememberShape,
                ruleName: ruleName
            );
        }

        var space = (context.FindSpace(name: intoRow.Space)
            ?? throw new RuleException(
            detail: $"Row '{intoRow.Name}' names undeclared vector space '{intoRow.Space}'",
            refusal: RuleRefusal.VectorSpaceMismatch,
            ruleName: ruleName
        ));
        var source = ResolveVectorOperand(
            context: context,
            expectedSpace: space,
            operandSpelling: remember.From,
            ruleName: ruleName,
            where: "remember from"
        );

        if (
            !FixedQ4816.TryParse(
            remember.UnlessWithin,
            CultureInfo.InvariantCulture,
            out var unlessWithin
        ) ||
            (unlessWithin.Value < 0L) ||
            (unlessWithin.Value > FixedQ4816.One.Value)
        ) {
            throw new RuleException(
                detail: $"remember unlessWithin '{remember.UnlessWithin}' must be a decimal in [0, 1]",
                refusal: RuleRefusal.VectorRememberShape,
                ruleName: ruleName
            );
        }

        var key = default(CellKey);
        CompiledCellRef? keyFrom = null;

        if (TryResolveDynamicKey(
            cell: out var dynamicKey,
            context: context,
            key: remember.Key,
            keyFieldLabel: "key",
            ruleName: ruleName,
            verb: "remember"
        )) {
            keyFrom = dynamicKey;
        } else if (CellName.TryParse(
            candidate: remember.Key,
            name: out var parsedKey,
            reason: out var reason
        )) {
            key = context.Catalog.Keys.Intern(name: parsedKey);
        } else {
            throw new RuleException(
                detail: $"Invalid remember key '{remember.Key}': {reason}",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        return new VectorRememberEffect(
            capacity: intoRow.Capacity.GetValueOrDefault(),
            describe: $"remember {remember.From} -> {remember.Into}[{remember.Key}]",
            key: key,
            keyFrom: keyFrom,
            rowOrdinal: ResolveRowOrdinal(
                context: context,
                name: intoRow.Name.Value
            ),
            source: source,
            unlessWithinQ16: unlessWithin.Value
        );
    }
    private static int ResolveVectorFilter(string? filter, RuleCompileContext context, string ruleName, string verb) {
        if (filter is not { } name) {
            return -1;
        }

        var row = (context.FindRow(name: name)
            ?? throw new RuleException(
            detail: $"{verb} where row '{name}' is unknown",
            refusal: RuleRefusal.StateRowUnknown,
            ruleName: ruleName
        ));

        if (
            (row.Kind != CellKind.Bool) ||
            !row.IsKeyed
        ) {
            throw new RuleException(
                detail: $"{verb} where row '{name}' must be a keyed Bool table",
                refusal: RuleRefusal.VectorFilterShape,
                ruleName: ruleName
            );
        }

        return ResolveRowOrdinal(
            context: context,
            name: name
        );
    }
}
