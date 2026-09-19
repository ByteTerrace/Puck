using System.Globalization;

namespace Puck.State.Rules;

public static partial class RuleCompiler {
    /// <summary>Compiles a pattern row's value expression for one zone: inside it, a state token keyed
    /// <c>$token</c> reads the current token's cell of a row keyed over <paramref name="tokenDomain"/>.</summary>
    /// <param name="context">The compile context.</param>
    /// <param name="pattern">The pattern row carrying the expression.</param>
    /// <param name="tokenDomain">The zone's token domain.</param>
    /// <param name="ruleName">The rule or console verb the compile answers for.</param>
    /// <param name="tokens">The compiled postfix program, on success.</param>
    /// <param name="reason">The refusal, on failure.</param>
    /// <returns><see langword="true"/> when the expression compiles in the pattern's kind.</returns>
    public static bool TryCompilePatternValue(RuleCompileContext context, PatternRow pattern, string tokenDomain, string ruleName, out CompiledExpressionToken[]? tokens, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: pattern);

        var scope = context.BindingScope;

        context.BindingScope = [BoundKey.Token, BoundKey.Previous];
        try {
            tokens = CompileExpression(
                context: context,
                expression: pattern.Value,
                kind: pattern.Kind,
                ruleName: ruleName,
                verb: $"pattern '{pattern.Name}' value"
            );

            foreach (var token in tokens) {
                if (
                    (token.Operand is IStateAddressedOperand { KeyFrom: { Binding: BoundKey.Token or BoundKey.Previous } } operand) &&
                    ((context.FindRowAt(rowOrdinal: operand.RowOrdinal) is not { } row) || (row.EffectiveDomain is not StateDomain.KeysOf keysOf) || (keysOf.Row.Value != tokenDomain))
                ) {
                    reason = $"pattern '{pattern.Name}' value reads a row by $token or $previous which must be keyed over token domain '{tokenDomain}'";
                    tokens = null;

                    return false;
                }
            }

            reason = string.Empty;

            return true;
        } catch (RuleException exception) {
            reason = exception.Message;
            tokens = null;

            return false;
        } finally {
            context.BindingScope = scope;
        }
    }

    private static ResolvedOperand ResolveBoardOperand(string name, string? key, string ruleName, RuleCompileContext context) {
        RuleException Invalid(string detail) => new(
            detail: detail,
            refusal: RuleRefusal.StateCellUnaddressable,
            ruleName: ruleName
        );
        var tokens = name.Split(separator: ':');

        if (
            (tokens.Length < 3) ||
            ((tokens.Length < 4) && (tokens[1] != "canonical"))
        ) {
            throw Invalid(detail: "board query requires $board:<operation>:<row>:<arguments>");
        }

        var row = context.FindRow(name: tokens[2]);

        if (
            (row?.EffectiveDomain is not StateDomain.CellsOf board) ||
            (context.FindTopology(name: board.Topology) is not { } topology)
        ) {
            throw Invalid(detail: $"'{tokens[2]}' names no discrete board row");
        }

        var kind = (tokens[1] switch {
            "neighbour" => BoardQueryKind.Neighbour,
            "pathCost" => BoardQueryKind.PathCost,
            "jumpDistance" => BoardQueryKind.JumpDistance,
            "mask" => BoardQueryKind.Mask,
            "canonical" => BoardQueryKind.Canonical,
            "offset" => BoardQueryKind.Offset,
            "attacks" => BoardQueryKind.Attacks,
            "component" => BoardQueryKind.Component,
            "boundary" => BoardQueryKind.Boundary,
            "boundaryAt" => BoardQueryKind.BoundaryAt,
            "enclosedAt" => BoardQueryKind.EnclosedAt,
            _ => throw Invalid(detail: $"unknown board operation '{tokens[1]}'"),
        });

        if (
            (kind is BoardQueryKind.Mask) &&
            (topology.CellCount > BoardMask.MaxCells)
        ) {
            throw Invalid(detail: $"{tokens[1]} reads at most {BoardMask.MaxCells} cells as bits; '{board.Topology}' has {topology.CellCount} — a wider board's set algebra is the boardCombine transform");
        }
        if (
            (kind is BoardQueryKind.Offset) &&
            (topology.Kind is not (TopologyKind.Grid or TopologyKind.Hex))
        ) {
            throw Invalid(detail: $"'{tokens[1]}' requires a Grid or Hex topology, not {topology.Kind}");
        }

        CompiledCellRef? keyFrom = null;
        CompiledCellRef? pathTargetFrom = null;
        var literalKey = default(CellKey);

        if (kind is not (BoardQueryKind.Mask or BoardQueryKind.Canonical)) {
            if (TryResolveDynamicKey(
                cell: out var dynamicKey,
                context: context,
                key: key,
                keyFieldLabel: "key",
                ruleName: ruleName,
                verb: "board"
            )) {
                keyFrom = dynamicKey;
            } else if (
                (key is null) ||
                !topology.TryCell(
                cell: out _,
                key: key
            )
            ) {
                throw Invalid(detail: "board query key must name a source cell or use a validated dynamic key");
            } else {
                literalKey = InternKey(
                    context: context,
                    name: key
                );
            }
        } else if (key is not null) {
            throw Invalid(detail: $"{tokens[1]} does not accept key");
        }

        BoardQuery query;

        if (kind == BoardQueryKind.Canonical) {
            if (tokens.Length != 3) {
                throw Invalid(detail: "canonical takes no arguments beyond the row");
            }

            query = new BoardCanonicalQuery(topology: topology);
        } else if (kind == BoardQueryKind.Mask) {
            if (
                (tokens.Length != 5) ||
                (row.Kind is not (CellKind.Int or CellKind.Bool)) ||
                !long.TryParse(
                tokens[3],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var lower
            ) ||
                !long.TryParse(
                tokens[4],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var upper
            ) ||
                (lower > upper)
            ) {
                throw Invalid(detail: "mask requires <min>:<max> integer bounds, min <= max, on an integer or boolean board row");
            }

            query = new BoardMaskQuery(
                lower: lower,
                topology: topology,
                upper: upper
            );
        } else if (kind == BoardQueryKind.JumpDistance) {
            var dynamicTarget = ((tokens.Length == 6) && (tokens[3] == "cell"));
            var target = 0;

            if ((row.Kind is not (CellKind.Int or CellKind.Bool)) ||
                (!dynamicTarget && ((tokens.Length != 4) || !topology.TryCell(tokens[3], out target)))) {
                throw Invalid(detail: "jumpDistance requires <targetCell> or cell:<row>:<key> on an integer or boolean board row");
            }
            if (dynamicTarget) {
                pathTargetFrom = ResolveCellRef(channel: name, context: context, key: tokens[5], row: tokens[4], ruleName: ruleName);
                if (pathTargetFrom.Value.Kind != CellKind.Int) {
                    throw Invalid(detail: "jumpDistance requires an integer cell:<row>:<key> destination ordinal");
                }
            }
            query = new BoardJumpDistanceQuery(topology, target, dynamicTarget);
        } else if (kind == BoardQueryKind.PathCost) {
            if (row.Kind != CellKind.Int) {
                throw Invalid(detail: "pathCost requires <targetCell>:<maxCost>:<maxVisits> or cell:<row>:<key>:<maxCost>:<maxVisits> on an integer terrain row");
            }

            // A dynamic target — 'cell:<row>:<key>' — reads its live integer value as the destination ordinal every
            // evaluation, instead of the literal ordinal a plain target takes at compile time alone.
            var dynamicTarget = ((tokens.Length == 8) && string.Equals(
                a: tokens[3],
                b: "cell",
                comparisonType: StringComparison.Ordinal
            ));
            var argumentsStart = 4;
            var target = 0;

            if (dynamicTarget) {
                argumentsStart = 6;
                pathTargetFrom = ResolveCellRef(
                    channel: name,
                    context: context,
                    key: tokens[5],
                    row: tokens[4],
                    ruleName: ruleName
                );
            } else if (
                (tokens.Length != 6) ||
                !topology.TryCell(
                tokens[3],
                out target
            )
            ) {
                throw Invalid(detail: "pathCost requires <targetCell>:<maxCost>:<maxVisits> or cell:<row>:<key>:<maxCost>:<maxVisits> on an integer terrain row");
            }

            if (
                !long.TryParse(
                tokens[argumentsStart],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var cost
            ) ||
                (cost < 0) ||
                !int.TryParse(
                tokens[(argumentsStart + 1)],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var visits
            ) ||
                (visits < 1) ||
                (visits > topology.CellCount)
            ) {
                throw Invalid(detail: "pathCost requires <targetCell>:<maxCost>:<maxVisits> or cell:<row>:<key>:<maxCost>:<maxVisits> on an integer terrain row");
            }

            query = new BoardPathCostQuery(
                maxCost: cost,
                maxVisits: visits,
                target: target,
                targetIsLive: dynamicTarget,
                topology: topology
            );
        } else if (kind is BoardQueryKind.Component or BoardQueryKind.Boundary or BoardQueryKind.BoundaryAt or BoardQueryKind.EnclosedAt) {
            var boundary = (kind != BoardQueryKind.Component);
            var arity = (boundary
                ? 8
                : 6
            );
            var boundaryLower = 0L;
            var boundaryUpper = 0L;

            if (
                (tokens.Length != arity) ||
                (row.Kind is not (CellKind.Int or CellKind.Bool)) ||
                !long.TryParse(
                tokens[3],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var memberLower
            ) ||
                !long.TryParse(
                tokens[4],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var memberUpper
            ) ||
                (memberLower > memberUpper) ||
                (boundary && (!long.TryParse(
                tokens[5],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out boundaryLower
            ) ||
                    !long.TryParse(
                tokens[6],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out boundaryUpper
            ) || (boundaryLower > boundaryUpper))) ||
                !int.TryParse(
                tokens[(arity - 1)],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var componentVisits
            ) ||
                (componentVisits < 1) ||
                (componentVisits > topology.CellCount)
            ) {
                throw Invalid(detail: (boundary
                    ? $"{tokens[1]} requires <min>:<max>:<boundaryMin>:<boundaryMax>:<maxVisits> on an integer or boolean board row"
                    : "component requires <min>:<max>:<maxVisits> on an integer or boolean board row"));
            }

            query = new BoardComponentQuery(
                boundaryLower: boundaryLower,
                boundaryUpper: boundaryUpper,
                kind: kind,
                lower: memberLower,
                maxVisits: componentVisits,
                topology: topology,
                upper: memberUpper
            );
        } else if (kind == BoardQueryKind.Attacks) {
            if (
                (tokens.Length != 6) ||
                (row.Kind is not (CellKind.Int or CellKind.Bool)) ||
                !long.TryParse(
                tokens[3],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var attackLower
            ) ||
                !long.TryParse(
                tokens[4],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var attackUpper
            ) ||
                (attackLower > attackUpper)
            ) {
                throw Invalid(detail: "attacks requires <min>:<max> integer bounds, min <= max, on an integer or boolean board row");
            }

            var directionNames = tokens[5].Split(separator: ',');

            if (directionNames.Length is < 1 or > 4) {
                throw Invalid(detail: "attacks requires 1..4 comma-separated directions");
            }

            var directions = new int[directionNames.Length];

            for (var index = 0; (index < directionNames.Length); index++) {
                var directionOrdinal = topology.Direction(token: directionNames[index]);

                if (directionOrdinal < 0) {
                    throw Invalid(detail: $"'{directionNames[index]}' is not a direction valid for its topology");
                }

                directions[index] = directionOrdinal;
            }

            query = new BoardAttacksQuery(
                directions: directions,
                lower: attackLower,
                topology: topology,
                upper: attackUpper
            );
        } else if (kind == BoardQueryKind.Offset) {
            if (
                (tokens.Length != 5) ||
                !int.TryParse(
                tokens[3],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var dx
            ) ||
                !int.TryParse(
                tokens[4],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var dz
            )
            ) {
                throw Invalid(detail: "offset requires <dx>:<dz>, both signed integers");
            }

            query = new BoardOffsetQuery(
                dx: dx,
                dz: dz,
                topology: topology
            );
        } else {
            if (tokens.Length != 4) {
                throw Invalid(detail: "neighbour requires exactly one direction token");
            }

            var direction = topology.Direction(token: tokens[3]);

            if (direction < 0) {
                throw Invalid(detail: $"'{tokens[3]}' is not a direction of '{board.Topology}'");
            }

            query = new BoardNeighbourQuery(
                direction: direction,
                topology: topology
            );
        }

        return new ResolvedOperand(
            describe: name,
            operand: new BoardOperand(
                board: query,
                key: literalKey,
                keyFrom: keyFrom,
                rowOrdinal: ResolveRowOrdinal(
                    context: context,
                    name: row.Name.Value
                ),
                targetFrom: pathTargetFrom
            )
        );
    }
    // $match:<pattern>:<row>[:<direction>|:any][:<facet>] — a board source walks the ray from the operand key's
    // origin cell (exclusive) in the named direction, or every direction under `any`; an ordered zone reads the
    // pattern's attribute row in pile order; a keyed row reads its own cells in cell order.
    private static ResolvedOperand ResolvePatternOperand(string name, string? key, string ruleName, RuleCompileContext context) {
        RuleException Invalid(string detail) => new(
            detail: detail,
            refusal: RuleRefusal.StateCellUnaddressable,
            ruleName: ruleName
        );
        var tokens = RuleFacts.SplitChannel(name: name);

        if (tokens.Length is < 3 or > 5) {
            throw Invalid(detail: "pattern match requires $match:<pattern>:<row>[:<direction>|:any][:prefix|:cell|:distance|:mask|:count]");
        }

        var patternRow = (FindPatternRow(
            context: context,
            name: tokens[1]
        ) ?? throw Invalid(detail: $"'{tokens[1]}' names no pattern"));

        if (!context.TryPattern(
            name: tokens[1],
            pattern: out var compiledPattern,
            reason: out var patternReason
        )) {
            throw Invalid(detail: patternReason);
        }

        var facet = MatchFacet.Accept;
        StateRow? row = null;

        if (!TryResolveLiveRow(
            context: context,
            name: tokens[2],
            row: out var live,
            ruleName: ruleName,
            where: $"'{name}' row"
        )) {
            row = (context.FindRow(name: tokens[2]) ?? throw Invalid(detail: $"'{tokens[2]}' names no state row"));
        } else {
            live!.CollectFacts(into: context.Needs);
        }

        var table = live?.Table;
        var domain = ((live is null)
            ? row!.EffectiveDomain
            : ((table is not null)
                ? new StateDomain.KeysOf(
                    Ordered: true,
                    Row: CellName.Parse(candidate: table.TokenDomain)
                )
                : (context.FindRowAt(rowOrdinal: live.Family!.Value.FirstOrdinal)?.EffectiveDomain ?? new StateDomain.Slot())
        ));
        var rowKind = ((live is null)
            ? row!.Kind
            : ((table is not null)
                ? table.Kind
                : (context.FindRowAt(rowOrdinal: live.Family!.Value.FirstOrdinal)?.Kind ?? CellKind.Int)
        ));
        var rowOrdinal = ((live is null)
            ? ResolveRowOrdinal(
                context: context,
                name: tokens[2]
            )
            : -1
        );
        var capacity = ((live is null)
            ? context.RowCapacity(name: tokens[2])
            : 0L
        );
        var attributeOrdinal = -1;
        BoardNeighbourQuery? board = null;
        CellKind kind;
        CompiledCellRef? keyFrom = null;
        var literalKey = default(CellKey);

        if (domain is StateDomain.CellsOf declaredBoard) {
            if (tokens.Length < 4) {
                throw Invalid(detail: "a board source requires a direction or any");
            }

            var topology = (context.FindTopology(name: declaredBoard.Topology) ?? throw Invalid(detail: $"'{tokens[2]}' names no compiled topology"));
            var every = (tokens[3] == "any");
            var direction = (every
                ? -1
                : topology.Direction(token: tokens[3])
            );

            if (
                (direction < 0) &&
                !every
            ) {
                throw Invalid(detail: $"'{tokens[3]}' is not a direction of '{declaredBoard.Topology}'");
            }
            if (tokens.Length == 5) {
                facet = (tokens[4] switch {
                    "prefix" when !every => MatchFacet.Prefix,
                    "cell" when !every => MatchFacet.Cell,
                    "distance" when !every => MatchFacet.Distance,
                    "mask" when every => MatchFacet.DirectionMask,
                    "count" when every => MatchFacet.DirectionCount,
                    _ => throw Invalid(detail: $"'{tokens[4]}' is not a facet for this source: prefix, cell or distance on one direction, mask or count over any"),
                });
            }
            if (TryResolveDynamicKey(
                cell: out var dynamicKey,
                context: context,
                key: key,
                keyFieldLabel: "key",
                ruleName: ruleName,
                verb: "match"
            )) {
                keyFrom = dynamicKey;
            } else if (
                (key is null) ||
                !topology.TryCell(
                cell: out _,
                key: key
            )
            ) {
                throw Invalid(detail: "a board source's key must name the origin cell or use a validated dynamic key");
            } else {
                literalKey = InternKey(
                    context: context,
                    name: key
                );
            }

            board = new BoardNeighbourQuery(
                direction: direction,
                topology: topology
            );
            kind = CellKind.Int;
        } else {
            if (tokens.Length > 4) {
                throw Invalid(detail: "a zone or keyed source takes no direction");
            }
            if (tokens.Length == 4) {
                facet = ((tokens[3] == "prefix")
                    ? MatchFacet.Prefix
                    : throw Invalid(detail: $"'{tokens[3]}' is not a facet for a word source; prefix is")
                );
            }
            if (
                (key is not null) &&
                (domain is StateDomain.Ring)
            ) {
                throw Invalid(detail: "a history source takes no key");
            }
            if (TryResolveDynamicKey(
                cell: out var startKey,
                context: context,
                key: key,
                keyFieldLabel: "key",
                ruleName: ruleName,
                verb: "match"
            )) {
                keyFrom = startKey;
            } else if (key is not null) {
                if (!CellName.TryParse(
                    candidate: key,
                    name: out _,
                    reason: out _
                )) {
                    throw Invalid(detail: "a word source's key must name the token the word starts at, or use a validated dynamic key");
                }

                literalKey = InternKey(
                    context: context,
                    name: key
                );
            }

            if (domain is StateDomain.KeysOf { Ordered: true } zone) {
                if (patternRow.Value is not null) {
                    if (!TryCompilePatternValue(
                        context: context,
                        pattern: patternRow,
                        reason: out var valueReason,
                        ruleName: ruleName,
                        tokenDomain: zone.Row.Value,
                        tokens: out var tokenExpression
                    )) {
                        throw Invalid(detail: valueReason);
                    }

                    return new ResolvedOperand(
                        describe: name,
                        operand: new PatternOperand(
                            attributeOrdinal: -1,
                            board: null,
                            capacity: capacity,
                            key: literalKey,
                            keyFrom: keyFrom,
                            matchFacet: facet,
                            pattern: compiledPattern!,
                            rowFrom: live,
                            rowOrdinal: rowOrdinal,
                            tokenExpression: tokenExpression
                        )
                    );
                }

                var attribute = (patternRow.Attribute ?? throw Invalid(detail: $"pattern '{patternRow.Name}' reads a zone and so needs an attribute row or a value expression"));
                var attributeRow = (context.FindRow(name: attribute) ?? throw Invalid(detail: $"attribute '{attribute}' names no state row"));

                if (
                    (attributeRow.Kind is not (CellKind.Int or CellKind.Fixed)) ||
                    (attributeRow.EffectiveDomain is not StateDomain.KeysOf attributeKeysOf) ||
                    (attributeKeysOf.Row.Value != zone.Row.Value)
                ) {
                    throw Invalid(detail: $"attribute '{attribute}' must be a numeric row keyed over token domain '{zone.Row}'");
                }

                attributeOrdinal = ResolveRowOrdinal(
                    context: context,
                    name: attribute
                );
                kind = attributeRow.Kind;
            } else {
                if (
                    ((live is null) && !row!.IsKeyed && (domain is not StateDomain.Ring)) ||
                    (patternRow.Attribute is not null)
                ) {
                    throw Invalid(detail: $"'{tokens[2]}' must be a keyed or history row read without an attribute");
                }

                kind = ((rowKind == CellKind.Bool)
                    ? CellKind.Int
                    : rowKind
                );
            }
        }

        if (kind != patternRow.Kind) {
            throw Invalid(detail: $"pattern '{patternRow.Name}' reads kind={patternRow.Kind} but the source word is kind={kind}");
        }

        return new ResolvedOperand(
            describe: name,
            operand: new PatternOperand(
                attributeOrdinal: attributeOrdinal,
                board: board,
                capacity: capacity,
                key: literalKey,
                keyFrom: keyFrom,
                matchFacet: facet,
                pattern: compiledPattern!,
                rowFrom: live,
                rowOrdinal: rowOrdinal,
                tokenExpression: null
            )
        );
    }
    private static PatternRow? FindPatternRow(RuleCompileContext context, string name) {
        foreach (var candidate in context.Patterns) {
            if (
                (candidate is not null) &&
                (candidate.Name.Value == name)
            ) {
                return candidate;
            }
        }

        return null;
    }
}
