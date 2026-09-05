using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>Compiles authored <see cref="WorldRule"/> rows against a candidate <see cref="WorldDefinition"/> —
/// construction at the document/mutation boundary, exactly where <c>BodyActionSpecFactory.Compile</c> sits for a kit's
/// per-body actions. Called twice by design: once (wrapped, per rule) inside <c>WorldDefinitionValidator</c> so a
/// malformed rule refuses the mutation or the boot by name instead of throwing later, and once more (unwrapped —
/// validation already proved success) inside the server's install path to obtain the live array the tick
/// evaluates.</summary>
public static partial class WorldRuleCompiler {
    // Shared by rules and interactions: an empty or absent effect list is refused in the subject's own noun.
    private static CompiledWorldEffect[] CompileEffects(IReadOnlyList<ActionEffect>? effects, string ruleName, WorldDefinition definition, string subject, bool allowTransaction = true) {
        if (effects is not { Count: > 0 }) {
            throw new WorldRuleException(
                detail: $"{((subject == "rule")
                    ? "a"
                    : "an")} {subject} must carry a non-empty effect list",
                refusal: WorldRuleRefusal.EffectKindInadmissible,
                ruleName: ruleName,
                subject: subject
            );
        }
        if (effects.Count > WorldRuleCapacity.MaxEffectsPerRule) {
            throw new WorldRuleException(
                detail: $"a {subject} carries {effects.Count} effects, exceeding the {WorldRuleCapacity.MaxEffectsPerRule}-effect ceiling",
                refusal: WorldRuleRefusal.EffectKindInadmissible,
                ruleName: ruleName,
                subject: subject
            );
        }

        var compiled = new CompiledWorldEffect[effects.Count];

        for (var index = 0; (index < compiled.Length); index++) {
            if (!allowTransaction && effects[index] is ActionEffect.Transaction) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.EffectKindInadmissible,
                    ruleName: ruleName,
                    detail: "a transaction cannot contain another transaction"
                );
            }

            compiled[index] = CompileEffect(
                effect: effects[index],
                ruleName: ruleName,
                definition: definition
            );
        }

        return compiled;
    }
    // SetState/AddState/CountdownState/Generate lift, and — riding the same "admit an existing WorldMutation kind" seam Generate
    // proved — so do upsertHudPanel/removeHudPanel/upsertPlacement/removePlacement: the rest of ActionEffect writes a
    // body's own kinematic or register state, which a world rule has none of. save admits on its OWN terms — not an
    // existing mutation kind at all, but engine I/O with no document effect (see ActionEffect.Save's remarks) —
    // compiling to a fixed, argument-free CompiledWorldEffect since it addresses no row.
    private static CompiledWorldEffect CompileEffect(ActionEffect? effect, string ruleName, WorldDefinition definition) => effect switch {
        null => throw new WorldRuleException(
        refusal: WorldRuleRefusal.EffectKindInadmissible,
        ruleName: ruleName,
        detail: "an effect row is null"
    ),
        ActionEffect.TransformState transform => ResolveStateTransform(transform.Transform, ruleName, definition),
        ActionEffect.SetState set => ResolveWrite(
        rowName: set.State,
        key: set.Key,
        target: set.Target,
        write: WorldDocumentWriteKind.Set,
        value: set.Value,
        fromState: set.FromState,
        fromKey: set.FromKey,
        valueSeconds: set.ValueSeconds,
        text: set.Text,
        expression: set.Expression,
        ruleName: ruleName,
        definition: definition,
        verb: "setState"
    ),
        ActionEffect.PushState push => ResolvePush(push, ruleName, definition),
        ActionEffect.AddState add => ResolveWrite(
        rowName: add.State,
        key: add.Key,
        target: add.Target,
        write: WorldDocumentWriteKind.Add,
        value: add.Value,
        fromState: add.FromState,
        fromKey: add.FromKey,
        valueSeconds: add.ValueSeconds,
        text: null,
        expression: add.Expression,
        ruleName: ruleName,
        definition: definition,
        verb: "addState"
    ),
        ActionEffect.CountdownState countdown => ResolveCountdown(
        definition: definition,
        effect: countdown,
        ruleName: ruleName
    ),
        ActionEffect.Generate generate => ResolveGenerate(
        definition: definition,
        generate: generate,
        ruleName: ruleName
    ),
        ActionEffect.UpsertHudPanel upsertHud => ResolveUpsertHudPanel(
        effect: upsertHud,
        ruleName: ruleName
    ),
        ActionEffect.RemoveHudPanel removeHud => ResolveRemoveHudPanel(
        effect: removeHud,
        ruleName: ruleName
    ),
        ActionEffect.UpsertPlacement upsertPlacement => ResolveUpsertPlacement(
        effect: upsertPlacement,
        ruleName: ruleName
    ),
        ActionEffect.RemovePlacement removePlacement => ResolveRemovePlacement(
        effect: removePlacement,
        ruleName: ruleName
    ),
        ActionEffect.Save => new CompiledWorldEffect(SaveEffect.Instance),
        ActionEffect.Pose pose => ResolvePose(
        definition: definition,
        effect: pose,
        ruleName: ruleName
    ),
        ActionEffect.RemoveStateCell remove => ResolveRemoveStateCell(
        definition: definition,
        effect: remove,
        ruleName: ruleName
    ),
        ActionEffect.ScheduleState schedule => ResolveScheduleState(
        definition: definition,
        effect: schedule,
        ruleName: ruleName
    ),
        ActionEffect.Transaction transaction => ResolveTransaction(
        definition: definition,
        effect: transaction,
        ruleName: ruleName
    ),
        ActionEffect.EmitCue cue => ResolveCue(
        definition: definition,
        effect: cue,
        ruleName: ruleName
    ),
        ActionEffect.SetBodyVerticalVelocity body => ResolveBodyVerticalVelocity(
        definition: definition,
        key: body.Key,
        operation: BodyMotionOp.SetVerticalVelocity,
        ruleName: ruleName,
        value: body.Velocity,
        verb: "setBodyVerticalVelocity"
    ),
        ActionEffect.ScaleBodyVerticalVelocity body => ResolveBodyVerticalVelocity(
        definition: definition,
        key: body.Key,
        operation: BodyMotionOp.ScaleVerticalVelocity,
        ruleName: ruleName,
        value: body.Factor,
        verb: "scaleBodyVerticalVelocity"
    ),
        ActionEffect.ApplyBodyImpulse impulse => ResolveBodyImpulse(
        definition: definition,
        effect: impulse,
        ruleName: ruleName
    ),
        ActionEffect.DesignateBody designate => ResolveBodyDesignation(
        definition: definition,
        effect: designate,
        ruleName: ruleName
    ),
        ActionEffect.PaintField paint => ResolveFieldPaint(
        definition: definition,
        effect: paint,
        ruleName: ruleName
    ),
        _ => throw new WorldRuleException(
        refusal: WorldRuleRefusal.EffectKindInadmissible,
        ruleName: ruleName,
        detail: $"'{effect.GetType().Name}' has no world-scope meaning"
    ),
    };
    private static CompiledWorldEffect ResolvePose(ActionEffect.Pose effect, string ruleName, WorldDefinition definition) {
        CompiledCellRef? keyFrom = null;
        var index = -1;

        if (TryResolveDynamicKey(
            definition: definition,
            key: effect.Key,
            ruleName: ruleName,
            verb: "pose",
            keyFieldLabel: "key",
            cell: out var dynamicKey
        )) {
            keyFrom = dynamicKey;
        } else if (
            !int.TryParse(
            s: effect.Key,
            style: System.Globalization.NumberStyles.Integer,
            provider: System.Globalization.CultureInfo.InvariantCulture,
            result: out index
        ) ||
            (index < 0)
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.SpatialChannelMalformed,
                ruleName: ruleName,
                detail: $"'pose' names body '{effect.Key}', which is not a non-negative integer"
            );
        }

        if (index >= definition.Population.Capacity) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.BodyIndexUnknown,
                ruleName: ruleName,
                detail: $"'pose' names body {index}, which is outside the document's declared entity-table capacity ({definition.Population.Capacity})"
            );
        }

        var bodyText = ((keyFrom is null)
            ? index.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
            : effect.Key
        );
        var spawnPoint = (effect.SpawnPoint ?? string.Empty);

        if ((spawnPoint.Length > 0) == (effect.Position is not null)) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.PoseAmbiguous,
                ruleName: ruleName,
                detail: "'pose' authors exactly one of 'spawnPoint' and 'position'"
            );
        }

        if (
            (effect.Position is null) &&
            (
                (effect.YawDegrees != 0f) ||
                (effect.PitchDegrees != 0f) ||
                (effect.RollDegrees != 0f)
            )
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.PoseAmbiguous,
                ruleName: ruleName,
                detail: "'pose' angles are only legal with a literal 'position'; a spawnPoint supplies its own yaw and zero pitch/roll"
            );
        }

        if (effect.Position is { } position) {
            if (
                !float.IsFinite(f: position.X) ||
                !float.IsFinite(f: position.Y) ||
                !float.IsFinite(f: position.Z) ||
                !float.IsFinite(f: effect.YawDegrees) ||
                !float.IsFinite(f: effect.PitchDegrees) ||
                !float.IsFinite(f: effect.RollDegrees)
            ) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.PoseAmbiguous,
                    ruleName: ruleName,
                    detail: "'pose' position and angles must be finite"
                );
            }

            const double DegreesToRadians = (Math.PI / 180.0);

            return new CompiledWorldEffect(new PoseEffect(
                spawnPoint: string.Empty,
                key: effect.Key,
                keyFrom: keyFrom,
                pose: new CompiledWorldPose(
                    Position: new FixedVector3(
                        X: FixedQ4816.FromDouble(value: position.X),
                        Y: FixedQ4816.FromDouble(value: position.Y),
                        Z: FixedQ4816.FromDouble(value: position.Z)
                    ),
                    YawRadians: FixedQ4816.FromDouble(value: (effect.YawDegrees * DegreesToRadians)),
                    PitchRadians: FixedQ4816.FromDouble(value: (effect.PitchDegrees * DegreesToRadians)),
                    RollRadians: FixedQ4816.FromDouble(value: (effect.RollDegrees * DegreesToRadians))
                ),
                describe: $"pose body:{bodyText} at ({position.X}, {position.Y}, {position.Z}) yaw={effect.YawDegrees} pitch={effect.PitchDegrees} roll={effect.RollDegrees}"
            ));
        }

        if (WorldDefinitionRows.FindSpawnPoint(
            spawnPoints: definition.SpawnPoints,
            id: spawnPoint
        ) is null) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.SpawnPointUnknown,
                ruleName: ruleName,
                detail: $"'pose' names spawnPoint '{spawnPoint}', which the 'spawnPoints' section does not declare"
            );
        }

        return new CompiledWorldEffect(new PoseEffect(
            spawnPoint: spawnPoint,
            key: effect.Key,
            keyFrom: keyFrom,
            pose: null,
            describe: $"pose body:{bodyText} at {spawnPoint}"
        ));
    }
    private static string DescribeCellKind(CellKind kind) => kind.ToString().ToLowerInvariant();
    private static string DescribeComparison(ActionStateComparison comparison) => comparison switch {
        ActionStateComparison.Equal => "==",
        ActionStateComparison.NotEqual => "!=",
        ActionStateComparison.Less => "<",
        ActionStateComparison.LessOrEqual => "<=",
        ActionStateComparison.Greater => ">",
        _ => ">=",
    };
    // Builds the world.rule.compile refusal detail for a 'valueSeconds' that is not an exact whole engine-tick
    // count — names the authored value, the arithmetic that proves it inexact, and the nearest EXACT durations on
    // either side (as engine-tick counts, which are always exact integers, plus an approximate seconds gloss for
    // orientation — 1 engine tick is 1/50400 s, which itself has no terminating decimal spelling, so the gloss is
    // never claimed exact). A negative duration is refused on separate, simpler terms: there is no "nearest exact"
    // either side of a value that is not a duration at all.
    private static string DescribeInexactDuration(string verb, string rowName, decimal literalSeconds) {
        var secondsText = literalSeconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture);

        if (literalSeconds < 0m) {
            return $"'{verb}' authors {rowName} 'valueSeconds' {secondsText} — a duration must be non-negative.";
        }

        var scaledTicks = (literalSeconds * FixedTickConversion.TicksPerSecond);
        var lowerTicks = decimal.Floor(d: scaledTicks);
        var upperTicks = (lowerTicks + 1m);
        var lowerSeconds = (lowerTicks / FixedTickConversion.TicksPerSecond);
        var upperSeconds = (upperTicks / FixedTickConversion.TicksPerSecond);

        return ((((((string)$"'{verb}' authors {rowName} 'valueSeconds' {secondsText} — {secondsText}s * {FixedTickConversion.TicksPerSecond} engine ticks/s = {scaledTicks.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} ticks, not a whole number, so no exact engine-tick duration exists for it; ")
            + $"the nearest EXACT durations are {lowerTicks.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} engine ticks ")
            + $"(≈{lowerSeconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}s) and {upperTicks.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} engine ticks ")
            + $"(≈{upperSeconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}s) — author one of those as 'valueSeconds', or (when no terminating decimal spells the ")
            + "intended duration exactly) author the raw whole engine-tick count directly via 'value' on the row and its companion decrement rule.");
    }
    // Emits a bounded postfix Boolean program: leaf comparisons push, All/Any consume their child count, Not flips
    // one result. The runtime therefore preserves arbitrary nesting without recursive evaluation or per-tick
    // allocation.
    private static void FlattenPredicate(ActionPredicate? predicate, List<CompiledWorldPredicate> gate, string ruleName, WorldDefinition definition, int depth = 0) {
        if (depth >= WorldRuleCapacity.MaxPredicateTokens) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.PredicateKindInadmissible,
                ruleName: ruleName,
                detail: $"a gate is nested past the {WorldRuleCapacity.MaxPredicateTokens}-token ceiling"
            );
        }

        switch (predicate) {
            case null:
                break;
            case ActionPredicate.All all:
                if (all.Predicates is null) {
                    throw new WorldRuleException(
                        refusal: WorldRuleRefusal.PredicateKindInadmissible,
                        ruleName: ruleName,
                        detail: "an 'all' gate must carry a non-null predicate list"
                    );
                }

                foreach (var inner in all.Predicates) {
                    if (inner is null) {
                        throw new WorldRuleException(
                            refusal: WorldRuleRefusal.PredicateKindInadmissible,
                            ruleName: ruleName,
                            detail: "an 'all' gate contains a null predicate row"
                        );
                    }

                    FlattenPredicate(
                        definition: definition,
                        gate: gate,
                        predicate: inner,
                        ruleName: ruleName,
                        depth: (depth + 1)
                    );
                }

                gate.Add(item: Logical(CompiledWorldPredicateKind.All, all.Predicates.Count, "all"));

                break;
            case ActionPredicate.Any any:
                if (any.Predicates is not { Count: > 0 }) {
                    throw new WorldRuleException(
                        refusal: WorldRuleRefusal.PredicateKindInadmissible,
                        ruleName: ruleName,
                        detail: "an 'any' gate must carry at least one predicate"
                    );
                }

                foreach (var inner in any.Predicates) {
                    if (inner is null) {
                        throw new WorldRuleException(
                            refusal: WorldRuleRefusal.PredicateKindInadmissible,
                            ruleName: ruleName,
                            detail: "an 'any' gate contains a null predicate row"
                        );
                    }

                    FlattenPredicate(predicate: inner, gate: gate, ruleName: ruleName, definition: definition, depth: (depth + 1));
                }

                gate.Add(item: Logical(CompiledWorldPredicateKind.Any, any.Predicates.Count, "any"));

                break;
            case ActionPredicate.Not not:
                if (not.Predicate is null) {
                    throw new WorldRuleException(
                        refusal: WorldRuleRefusal.PredicateKindInadmissible,
                        ruleName: ruleName,
                        detail: "a 'not' gate must carry one non-null predicate"
                    );
                }

                FlattenPredicate(predicate: not.Predicate, gate: gate, ruleName: ruleName, definition: definition, depth: (depth + 1));
                gate.Add(item: Logical(CompiledWorldPredicateKind.Not, 1, "not"));

                break;
            case ActionPredicate.CompareValue expression:
                if (expression.Kind is not (CellKind.Int or CellKind.Fixed) || !Enum.IsDefined(expression.Comparison)) {
                    throw new WorldRuleException(WorldRuleRefusal.PredicateKindInadmissible, ruleName, "compareValue requires Int or Fixed and a defined comparison");
                }
                gate.Add(new(null, expression.Comparison, 0, expression.Kind, null, $"compareValue {expression.Kind} {expression.Comparison}",
                    LeftExpression: CompileExpression(expression.Left, expression.Kind, ruleName, "compareValue left", definition),
                    RightExpression: CompileExpression(expression.Right, expression.Kind, ruleName, "compareValue right", definition)));
                break;
            case ActionPredicate.CompareState compare:
                gate.Add(item: ResolvePredicate(
                    compare: compare,
                    definition: definition,
                    ruleName: ruleName
                ));

                break;
            default:
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.PredicateKindInadmissible,
                    ruleName: ruleName,
                detail: $"'{predicate.GetType().Name}' has no world-scope meaning — world gates admit 'compareState', 'compareValue', 'all', 'any', and 'not'"
                );
        }

        static CompiledWorldPredicate Logical(CompiledWorldPredicateKind kind, int arity, string describe) => new(
            Left: null,
            Comparison: default,
            Value: 0L,
            ValueKind: default,
            Comparand: null,
            Describe: describe,
            Kind: kind,
            Arity: arity
        );
    }
    private static bool HasRegion(WorldDefinition definition, string placementId) {
        foreach (var placement in definition.Placements) {
            if (
                (placement.Region is not null) &&
                string.Equals(
                a: placement.Id,
                b: placementId,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return true;
            }
        }

        return false;
    }
    // Structural only — whether the screen index a $machine: channel names is DECLARED, mirroring HasRegion's own
    // minimal bar. Which SOURCE the screen carries (machine vs. camera vs. view) is not checked here: a screen can be
    // re-sourced live, and a screen with no booted machine simply reads as 0 at evaluation time (WorldServer.Machines
    // .TryPeek), the same "reads as zero rather than throwing" precedent ReadStateCell already follows.
    private static bool HasScreen(WorldDefinition definition, int index) {
        foreach (var screen in definition.Screens) {
            if (screen.Index == index) {
                return true;
            }
        }

        return false;
    }
    // Resolves a $channel: name to its declared document-order ordinal — the same ordinal WorldChannelTable.Compile
    // assigns each channels[] row, since no compiled table exists yet at rule-compile time — or -1 when the document
    // declares no such channel.
    private static int ResolveChannelOrdinal(WorldDefinition definition, string name) {
        var channels = definition.Channels;

        for (var ordinal = 0; (ordinal < channels.Count); ordinal++) {
            if (
                (channels[ordinal] is { } channel) &&
                string.Equals(a: channel.Name, b: name, comparisonType: StringComparison.Ordinal)
            ) {
                return ordinal;
            }
        }

        return -1;
    }
    private static void RefuseKeyOnReservedChannel(string? key, string ruleName, string name, string keyFieldLabel) {
        if (key is not null) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"reserved channel '{name}' is a single quantity and carries no cells — drop the '{keyFieldLabel}'"
            );
        }
    }
    // Parses ONE body-reference token pair (tokens[start], tokens[start+1]) — "body:<n>" (a literal 0-based index,
    // bounded against the document's OWN declared entity-table capacity) or "argmax:<row>"/"argmin:<row>" (a
    // reduction-derived body key, resolved through the same ResolveNumericRow(requireKeyed: true) door the standalone
    // $argmax:/$argmin: channel uses) — the shared grammar $distance:/$los: spend both their halves on.
    private static CompiledBodyRef ResolveBodyRefToken(string[] tokens, int start, string ruleName, WorldDefinition definition, string channel) {
        var kind = tokens[start];

        if ((BindingOfBodyToken(token: kind) is var bound) && (bound != RuleBinding.None)) {
            if (bound == RuleBinding.Token) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName,
                    detail: $"'{channel}' names 'token', which binds a cell key inside a pattern value expression and never a body"
                );
            }
            RequireBindingInScope(
                binding: bound,
                ruleName: ruleName,
                spelled: kind,
                where: $"'{channel}'"
            );

            return new CompiledBodyRef(
                Index: ((int)bound),
                Kind: CompiledBodyRefKind.Binding,
                Row: null
            );
        }

        if ((start + 1) >= tokens.Length) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.SpatialChannelMalformed,
                ruleName: ruleName,
                detail: $"'{channel}' names '{kind}' with no value — a body reference is {s_bodyRefVocabulary}"
            );
        }

        var value = tokens[(start + 1)];

        if (string.Equals(
            a: kind,
            b: "cell",
            comparisonType: StringComparison.Ordinal
        )) {
            if ((start + 2) >= tokens.Length) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.SpatialChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{channel}' names 'cell:{value}' with no key — spell 'cell:<row>:<key>'"
                );
            }

            var cell = ResolveCellRef(
                channel: channel,
                definition: definition,
                key: tokens[(start + 2)],
                row: value,
                ruleName: ruleName
            );

            return new CompiledBodyRef(
                Index: -1,
                Kind: CompiledBodyRefKind.Cell,
                Row: cell.Row,
                Key: cell.Key,
                Handle: cell.Handle
            );
        }

        if (string.Equals(
            a: kind,
            b: "body",
            comparisonType: StringComparison.Ordinal
        )) {
            if (
                !int.TryParse(
                s: value,
                style: System.Globalization.NumberStyles.Integer,
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var index
            ) ||
                (index < 0)
            ) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.SpatialChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{channel}' names 'body:{value}', which is not a non-negative integer"
                );
            }

            if (index >= definition.Population.Capacity) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.BodyIndexUnknown,
                    ruleName: ruleName,
                    detail: $"'{channel}' names 'body:{index}', which is outside the document's declared entity-table capacity ({definition.Population.Capacity})"
                );
            }

            return new CompiledBodyRef(
                Index: index,
                Kind: CompiledBodyRefKind.Literal,
                Row: null
            );
        }

        if (
            string.Equals(
            a: kind,
            b: "argmax",
            comparisonType: StringComparison.Ordinal
        ) ||
            string.Equals(
            a: kind,
            b: "argmin",
            comparisonType: StringComparison.Ordinal
        )
        ) {
            if (string.IsNullOrEmpty(value: value)) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.SpatialChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{channel}' names '{kind}:' with no row"
                );
            }

            _ = ResolveNumericRow(
                channel: channel,
                definition: definition,
                malformed: WorldRuleRefusal.SpatialChannelMalformed,
                name: value,
                requireKeyed: true,
                ruleName: ruleName
            );

            return new CompiledBodyRef(
                Index: -1,
                Kind: ((kind == "argmax")
                ? CompiledBodyRefKind.ArgMax
                : CompiledBodyRefKind.ArgMin),
                Row: value,
                Handle: ResolveWorldStateHandle(definition: definition, name: value)
            );
        }

        throw new WorldRuleException(
            refusal: WorldRuleRefusal.SpatialChannelMalformed,
            ruleName: ruleName,
            detail: $"'{channel}' names body-reference token '{kind}:{value}' — expected 'body:<n>' or 'argmax:<row>'/'argmin:<row>'"
        );
    }
    private static CompiledWorldEffect ResolveCountdown(ActionEffect.CountdownState effect, string ruleName, WorldDefinition definition) {
        var row = (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: effect.State
        )
            ?? throw new WorldRuleException(
            refusal: WorldRuleRefusal.StateRowUnknown,
            ruleName: ruleName,
            detail: $"'countdownState' names no state row '{effect.State}' — declare it with world.row.set state <json> first"
        ));

        if (
            (row.Kind != CellKind.Int) ||
            !row.NonNegative
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"state row '{effect.State}' is kind={DescribeCellKind(kind: row.Kind)} nonNegative={row.NonNegative.ToString().ToLowerInvariant()} — 'countdownState' requires kind=int nonNegative=true so its computed final partial step can saturate at zero"
            );
        }

        CompiledCellRef? keyFrom = null;
        string resolvedKey;

        if (TryResolveDynamicKey(
            definition: definition,
            key: effect.Key,
            ruleName: ruleName,
            verb: "countdownState",
            keyFieldLabel: "key",
            cell: out var dynamicKey
        )) {
            keyFrom = dynamicKey;
            resolvedKey = effect.Key!;
        } else {
            resolvedKey = ResolveKey(
                row: row,
                key: effect.Key,
                ruleName: ruleName,
                verb: "countdownState",
                keyFieldLabel: "key"
            );
        }

        return new CompiledWorldEffect(new CountdownEffect(
            row: effect.State,
            key: resolvedKey,
            keyFrom: keyFrom,
            describe: $"countdownState {effect.State}.{resolvedKey} by runtime step"
        ));
    }
    // A 'generate' effect names ONE thing: the SITE to redraw. The source is the site's own facet (named or
    // inlined), so there is no second row to resolve and no key to address — a draw site is a scalar slot by
    // construction. Timing is the one refusal this compile adds: a boot-timed site draws once at first fill and can
    // never be redrawn, and an author sees that here rather than at the first tick the rule fires.
    private static CompiledWorldEffect ResolveGenerate(ActionEffect.Generate generate, string ruleName, WorldDefinition definition) {
        var row = (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: generate.Row
        )
            ?? throw new WorldRuleException(
            refusal: WorldRuleRefusal.StateRowUnknown,
            ruleName: ruleName,
            detail: $"'generate' names no state row '{generate.Row}'"
        ));

        // A lattice row painted by a draw fill is a generate target too: one whole-field pass per firing, with no
        // timing of its own, resolved through the same source walk a slot site takes.
        var draw = (row.Draw ?? ((WorldLatticeFill.FindDraw(trait: row.Field) is { } fill)
            ? new WorldDraw(Source: fill.Source, Generator: fill.Generator, Timing: WorldDrawTiming.Event)
            : null));

        if (draw is null) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.GeneratorUnknown,
                ruleName: ruleName,
                detail: $"state row '{generate.Row}' declares no draw — 'generate' redraws a draw site or a field row painted by a draw fill"
            );
        }

        if (draw.Timing == WorldDrawTiming.Boot) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.GeneratorUnknown,
                ruleName: ruleName,
                detail: $"state row '{generate.Row}' declares timing=boot — it draws once at first fill and is never redrawn"
            );
        }

        if (!WorldGeneratorEngine.TryResolveSource(
            generators: definition.Generators,
            draw: draw,
            generator: out var generator,
            reason: out var resolveReason
        )) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.GeneratorUnknown,
                ruleName: ruleName,
                detail: $"state row '{generate.Row}' {resolveReason}"
            );
        }

        // The ONE kind predicate, asked here at rule COMPILE time so an author sees a mismatch before the effect ever
        // fires — the same call the fire-time door makes, never a second reading of it.
        if (!WorldGeneratorEngine.TryCheckTargetKind(
            source: generator.Source,
            targetKind: row.Kind,
            reason: out var kindReason
        )) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"state row '{generate.Row}': {kindReason}"
            );
        }

        return new CompiledWorldEffect(new GenerateEffect(
            row: generate.Row,
            generator: generate.Row,
            describe: $"generate {generate.Row}"
        ));
    }

    // The (row, key) PAIR rule: a null key means the row's slot cell, and WorldStateRow.IsKeyed — never "declares a
    // capacity", and never !IsSlot — is the discriminator (a capacity-free row carrying several author-keyed cells
    // has no slot either, while a row with NO cells is legitimately slot-addressable: the first write mints its slot
    // cell, exactly as world.state.cell.set does).
    // A '$cell:<row>:<key>' indirection: the named cell must exist on a declared int row, since its VALUE is read
    // as a key every evaluation.
    private static CompiledCellRef ResolveCellRef(string row, string key, string ruleName, WorldDefinition definition, string channel) {
        var declared = ResolveNumericRow(
            channel: channel,
            definition: definition,
            malformed: WorldRuleRefusal.StateCellUnaddressable,
            name: row,
            requireKeyed: false,
            ruleName: ruleName
        );

        if (declared.Kind != CellKind.Int) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"'{channel}' reads row '{row}' as a body index, but it is kind={DescribeCellKind(kind: declared.Kind)} — an index cell is kind=int"
            );
        }

        var resolvedKey = ResolveKey(
            key: key,
            keyFieldLabel: "key",
            row: declared,
            ruleName: ruleName,
            verb: channel
        );

        if (!declared.HasCell(key: resolvedKey)) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUndeclared,
                ruleName: ruleName,
                detail: $"'{channel}' reads cell '{row}'.'{resolvedKey}' as a key, which the row does not declare"
            );
        }

        return new CompiledCellRef(
            Key: resolvedKey,
            Row: row,
            Handle: ResolveWorldStateHandle(definition: definition, name: row)
        );
    }
    private static bool TryResolveDynamicKey(string? key, string ruleName, WorldDefinition definition, string verb, string keyFieldLabel, out CompiledCellRef cell) {
        if ((BindingOfKeyToken(key: key) is var bound) && (bound != RuleBinding.None)) {
            RequireBindingInScope(
                binding: bound,
                ruleName: ruleName,
                spelled: key!,
                where: $"'{verb}' {keyFieldLabel}"
            );
            cell = new CompiledCellRef(
                Binding: bound,
                Key: string.Empty,
                Row: string.Empty
            );

            return true;
        }

        if (
            (key is not null) &&
            key.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.PairKeyPrefix
        )
        ) {
            var pairTokens = key[WorldRuleFacts.PairKeyPrefix.Length..].Split(separator: ':');
            var pairWidthA = BodyRefTokenWidth(start: 0, tokens: pairTokens);

            if (pairTokens.Length != (pairWidthA + BodyRefTokenWidth(start: pairWidthA, tokens: pairTokens))) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.PairKeyMalformed,
                    ruleName: ruleName,
                    detail: $"'{verb}' {keyFieldLabel} '{key}' does not spell '{WorldRuleFacts.PairKeyPrefix}<bodyRefA>:<bodyRefB>' (each {s_bodyRefVocabulary})"
                );
            }

            var channel = $"{verb} {keyFieldLabel} '{key}'";

            cell = new CompiledCellRef(
                Row: string.Empty,
                Key: string.Empty,
                PairBodyA: ResolveBodyRefToken(channel: channel, definition: definition, ruleName: ruleName, start: 0, tokens: pairTokens),
                PairBodyB: ResolveBodyRefToken(channel: channel, definition: definition, ruleName: ruleName, start: pairWidthA, tokens: pairTokens)
            );

            return true;
        }

        if (
            (key is null) ||
            !key.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: RuleFacts.CellKeyPrefix
        )
        ) {
            cell = default;

            return false;
        }

        var tokens = key[RuleFacts.CellKeyPrefix.Length..].Split(separator: ':');

        if (tokens.Length != 2) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"'{verb}' {keyFieldLabel} '{key}' does not spell '{RuleFacts.CellKeyPrefix}<row>:<key>'"
            );
        }

        cell = ResolveCellRef(
            channel: $"{verb} {keyFieldLabel} '{key}'",
            definition: definition,
            key: tokens[1],
            row: tokens[0],
            ruleName: ruleName
        );

        return true;
    }
    private static string ResolveKey(WorldStateRow row, string? key, string ruleName, string verb, string keyFieldLabel) {
        if (key is { } authored) {
            return (CellName.TryParse(
                candidate: authored,
                name: out var parsed,
                reason: out var reason
            )
                ? parsed.Value
                : throw new WorldRuleException(
                    refusal: WorldRuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName,
                    detail: $"'{verb}' {keyFieldLabel} '{authored}' {reason}"
                )
            );
        }

        return (row.IsKeyed
            ? throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"'{verb}' names keyed row '{row.Name}' without a '{keyFieldLabel}' — a keyed row has no single cell, so name the one you mean"
            )
            : WorldStateRow.SlotKey.Value
        );
    }
    // The declared-row resolution a $reduce:/$argmax:/$argmin: channel shares: the row must exist and must not be
    // kind=text (a reduction/extremum is numeric, exactly like the ordinary declared-row path below). requireKeyed
    // additionally demands the row be POSITIVELY keyed (WorldStateRow.IsKeyed) — an argmax/argmin yields a BODY, and
    // a slot row's one cell carries the engine-minted $value key rather than a body index, so a slot row is refused
    // there (ArgRowNotKeyed) but admitted for an ordinary reduction (a slot row's max/min/sum trivially equals its
    // one cell; count is 1).
    private static WorldStateRow ResolveNumericRow(string name, string ruleName, WorldDefinition definition, bool requireKeyed, WorldRuleRefusal malformed, string channel) {
        var row = (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: name
        )
            ?? throw new WorldRuleException(
            refusal: malformed,
            ruleName: ruleName,
            detail: $"'{channel}' names row '{name}', which the document does not declare"
        ));

        if (row.Kind == CellKind.Text) {
            throw new WorldRuleException(
                refusal: malformed,
                ruleName: ruleName,
                detail: $"'{channel}' names row '{name}', which is kind=text — a reduction/extremum is numeric, never text"
            );
        }

        if (
            requireKeyed &&
            !row.IsKeyed
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.ArgRowNotKeyed,
                ruleName: ruleName,
                detail: $"'{channel}' names row '{name}', which is not keyed — an argmax/argmin yields a body, and a slot row's cell carries no body-index key; author a keyed row whose cell keys ARE body indices"
            );
        }

        return row;
    }
    // Resolves ANY read operand — a compareState's primary (State, Key) pair, its comparand (ComparandState,
    // ComparandKey) pair, or a setState/addState's live copy source (FromState, FromKey) — through the same
    // reserved-channel/state-row walk, so no two of them can drift into different readings of the same name.
    // verb/fieldLabel/keyFieldLabel name the AUTHORED spelling in refusal text, since every caller is refused by the
    // same shapes under different-sounding names ("state"/"key" vs "comparandState"/"comparandKey" vs "fromState"/
    // "fromKey"): a refusal that quoted one caller's spelling at another's author would name a field they never wrote.
    // Reserved channels ($tick/$population/$region:/$machine:) are all integer-valued; a declared row carries its own
    // kind. The $machine: channel is resolved here too, so every caller reaches a live machine byte on the same terms.
    // $symmetry:<function>[:<argument>]:<row> — the row is the LAST token (a row name is colon-free by construction),
    // the function the first, and whatever sits between is the argument the function takes. The source cell resolves
    // through the ordinary row/key walk, so every key rule (a keyed row needs a key, $each/$cell: indirections, the
    // declared-cell requirement of a read) holds for it unchanged; a cell argument resolves the same way.
    private static ResolvedOperand ResolveSymmetryOperand(string name, string? key, string ruleName, WorldDefinition definition, string verb, string fieldLabel, string keyFieldLabel, string describe) {
        var tokens = name[RuleFacts.SymmetryPrefix.Length..].Split(separator: ':');

        static WorldRuleException Malformed(string ruleName, string name, string detail) => new(
            refusal: WorldRuleRefusal.SymmetryChannelMalformed,
            ruleName: ruleName,
            detail: $"'{name}' {detail} — a symmetry channel spells '{RuleFacts.SymmetryPrefix}<ring|antipode|canonicalRay|cycle:<steps>|reflect:<node|cell:<row>[.<key>]>|orthogonal:<node|cell:<row>[.<key>]>|innerProduct:<node|cell:<row>[.<key>]>|projectionX|projectionY>:<row>'"
        );

        if (tokens.Length < 2) {
            throw Malformed(ruleName: ruleName, name: name, detail: "names no source row");
        }

        var rowName = tokens[^1];
        var function = tokens[0] switch {
            "ring" => WorldSymmetryFunction.Ring,
            "antipode" => WorldSymmetryFunction.Antipode,
            "canonicalRay" => WorldSymmetryFunction.CanonicalRay,
            "cycle" => WorldSymmetryFunction.Cycle,
            "reflect" => WorldSymmetryFunction.Reflect,
            "orthogonal" => WorldSymmetryFunction.Orthogonal,
            "innerProduct" => WorldSymmetryFunction.InnerProduct,
            "projectionX" => WorldSymmetryFunction.ProjectionX,
            "projectionY" => WorldSymmetryFunction.ProjectionY,
            _ => throw Malformed(ruleName: ruleName, name: name, detail: $"names no symmetry function '{tokens[0]}'"),
        };
        var argument = string.Join(separator: ':', values: tokens[1..^1]);
        var takesArgument = (function is WorldSymmetryFunction.Cycle or WorldSymmetryFunction.Reflect or WorldSymmetryFunction.Orthogonal or WorldSymmetryFunction.InnerProduct);

        if (takesArgument == (argument.Length == 0)) {
            throw Malformed(ruleName: ruleName, name: name, detail: (takesArgument ? $"'{tokens[0]}' needs an argument" : $"'{tokens[0]}' takes no argument"));
        }

        var source = ResolveOperand(
            allowText: false,
            definition: definition,
            fieldLabel: fieldLabel,
            key: key,
            keyFieldLabel: keyFieldLabel,
            name: rowName,
            ruleName: ruleName,
            verb: verb
        );

        if (!source.Operand.TryGetValue(out StateCellOperand? sourceCell)) {
            throw Malformed(ruleName: ruleName, name: name, detail: $"names '{rowName}', which is not a state row — the source of a symmetry read is a declared row's cell");
        }

        var literal = 0L;
        CompiledCellRef? other = null;

        if (takesArgument) {
            if (function == WorldSymmetryFunction.Cycle) {
                if (!long.TryParse(s: argument, style: System.Globalization.NumberStyles.AllowLeadingSign, provider: System.Globalization.CultureInfo.InvariantCulture, result: out literal)) {
                    throw Malformed(ruleName: ruleName, name: name, detail: $"'cycle' needs a whole number of ring steps, not '{argument}'");
                }
            }
            else if (argument.StartsWith(value: "cell:", comparisonType: StringComparison.Ordinal)) {
                var reference = argument["cell:".Length..];
                var dot = reference.IndexOf(value: '.');
                var otherRow = ((dot < 0) ? reference : reference[..dot]);
                var otherKey = ((dot < 0) ? null : reference[(dot + 1)..]);
                var resolved = ResolveOperand(
                    allowText: false,
                    definition: definition,
                    fieldLabel: fieldLabel,
                    key: otherKey,
                    keyFieldLabel: keyFieldLabel,
                    name: otherRow,
                    ruleName: ruleName,
                    verb: verb
                );

                if (!resolved.Operand.TryGetValue(out StateCellOperand? otherCell) || (otherCell.KeyFrom is not null)) {
                    throw Malformed(ruleName: ruleName, name: name, detail: $"argument '{argument}' does not name a declared row's cell by a literal key");
                }

                other = new CompiledCellRef(Row: otherCell.Row, Key: (otherCell.Key ?? string.Empty), Handle: otherCell.StateHandle);
            }
            else if (!long.TryParse(s: argument, style: System.Globalization.NumberStyles.None, provider: System.Globalization.CultureInfo.InvariantCulture, result: out literal) || (literal >= SymmetryLattice.NodeCount)) {
                throw Malformed(ruleName: ruleName, name: name, detail: $"argument '{argument}' is neither a node 0..{SymmetryLattice.NodeCount - 1} nor 'cell:<row>[.<key>]'");
            }
        }

        var symmetryValueKind = ((function is WorldSymmetryFunction.ProjectionX or WorldSymmetryFunction.ProjectionY) ? CellKind.Fixed : CellKind.Int);

        return new ResolvedOperand(
            Operand: new CompiledWorldOperand(SymmetryOperand.FromStateCell(
                source: sourceCell,
                symmetry: function,
                symmetryArgument: literal,
                symmetryOtherCell: other,
                valueKind: symmetryValueKind
            )),
            ValueKind: symmetryValueKind,
            Describe: describe
        );
    }
    private static ResolvedOperand ResolveOperand(string name, string? key, string ruleName, WorldDefinition definition, string verb, string fieldLabel, string keyFieldLabel, bool allowText = false) {
        if (name.StartsWith("$phase:", StringComparison.Ordinal)) {
            return ResolvePhaseOperand(name, key, ruleName, definition);
        }
        if (name.StartsWith("$board:", StringComparison.Ordinal)) {
            return ResolveBoardOperand(name, key, ruleName, definition);
        }
        if (name.StartsWith(RuleFacts.MatchPrefix, StringComparison.Ordinal)) {
            return ResolvePatternOperand(name, key, ruleName, definition);
        }
        if (name.StartsWith(RuleFacts.HistoryPrefix, StringComparison.Ordinal)) {
            return ResolveHistoryOperand(name, key, ruleName, definition);
        }
        if (name.StartsWith(WorldRuleFacts.ClockPrefix, StringComparison.Ordinal)) {
            return ResolveClockOperand(name, key, ruleName, definition);
        }
        if (name.StartsWith(RuleFacts.BindPrefix, StringComparison.Ordinal)) {
            return ResolveBindingOperand(name, key, ruleName, keyFieldLabel);
        }
        if (name.StartsWith(RuleFacts.TablePrefix, StringComparison.Ordinal)) {
            return ResolveTableOperand(name, key, ruleName, definition, keyFieldLabel);
        }
        var describe = $"{name}{((key is { } spelledKey)
            ? $".{spelledKey}"
            : string.Empty)}";

        if (string.Equals(
            a: name,
            b: RuleFacts.Tick,
            comparisonType: StringComparison.Ordinal
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(TickOperand.Instance),
                ValueKind: CellKind.Int,
                Describe: describe
            );
        }

        if (string.Equals(
            a: name,
            b: WorldRuleFacts.Population,
            comparisonType: StringComparison.Ordinal
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(PopulationOperand.Instance),
                ValueKind: CellKind.Int,
                Describe: describe
            );
        }

        if (string.Equals(
            a: name,
            b: WorldRuleFacts.PhysicsQuiescent,
            comparisonType: StringComparison.Ordinal
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(PhysicsQuiescentOperand.Instance),
                ValueKind: CellKind.Bool,
                Describe: describe
            );
        }

        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.RegionPrefix
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var placementId = name[WorldRuleFacts.RegionPrefix.Length..];

            if (
                string.IsNullOrEmpty(value: placementId) ||
                !HasRegion(
                definition: definition,
                placementId: placementId
            )
            ) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.RegionUnknown,
                    ruleName: ruleName,
                    detail: $"'{name}' names no placement carrying a region facet"
                );
            }

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(new RegionOccupancyOperand(placementId)),
                ValueKind: CellKind.Int,
                Describe: describe
            );
        }

        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.MachinePrefix
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var suffix = name[WorldRuleFacts.MachinePrefix.Length..];
            var separator = suffix.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: ':'
            );

            if (
                (separator < 0) ||
                !int.TryParse(
                s: suffix[..separator],
                style: System.Globalization.NumberStyles.Integer,
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var screen
            ) ||
                !int.TryParse(
                s: suffix[(separator + 1)..],
                style: System.Globalization.NumberStyles.Integer,
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var address
            ) ||
                (screen < 0) ||
                (address < 0)
            ) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.MachineChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' does not spell '{WorldRuleFacts.MachinePrefix}<screen>:<address>' with non-negative integers"
                );
            }

            if (!HasScreen(
                definition: definition,
                index: screen
            )) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.ScreenUnknown,
                    ruleName: ruleName,
                    detail: $"'{name}' names screen {screen}, which the document does not declare"
                );
            }

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(new MachineMemoryOperand(screen, address)),
                ValueKind: CellKind.Int,
                Describe: describe
            );
        }

        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: RuleFacts.ReducePrefix
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var suffix = name[RuleFacts.ReducePrefix.Length..];
            var separator = suffix.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: ':'
            );

            if (
                (separator < 0) ||
                !TryParseReduceOp(
                text: suffix[..separator],
                op: out var op
            ) ||
                string.IsNullOrEmpty(value: suffix[(separator + 1)..])
            ) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.ReduceChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' does not spell '{RuleFacts.ReducePrefix}<max|min|sum|count>:<row>'"
                );
            }

            var rowAndFilter = suffix[(separator + 1)..];
            const string WhereMarker = ":where:";
            var where = rowAndFilter.IndexOf(value: WhereMarker, comparisonType: StringComparison.Ordinal);
            var rowName = ((where < 0) ? rowAndFilter : rowAndFilter[..where]);
            var filterRowName = ((where < 0) ? null : rowAndFilter[(where + WhereMarker.Length)..]);

            if ((where >= 0) && string.IsNullOrEmpty(value: filterRowName)) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.ReduceChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' carries ':where:' without a filter row"
                );
            }
            var reduceRow = ResolveNumericRow(
                channel: name,
                definition: definition,
                malformed: WorldRuleRefusal.ReduceChannelMalformed,
                name: rowName,
                requireKeyed: false,
                ruleName: ruleName
            );
            WorldStateHandle filterHandle = default;
            if (filterRowName is not null) {
                _ = ResolveNumericRow(
                    channel: name,
                    definition: definition,
                    malformed: WorldRuleRefusal.ReduceChannelMalformed,
                    name: filterRowName,
                    requireKeyed: true,
                    ruleName: ruleName
                );
                if (!reduceRow.IsKeyed) {
                    throw new WorldRuleException(
                        refusal: WorldRuleRefusal.ReduceChannelMalformed,
                        ruleName: ruleName,
                        detail: $"'{name}' applies a keyed filter to non-keyed row '{rowName}'"
                    );
                }
                filterHandle = ResolveWorldStateHandle(definition: definition, name: filterRowName);
            }
            var reduceValueKind = ((op == WorldStateReduceOp.Count)
                ? CellKind.Int
                : reduceRow.Kind
            );

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(new ReductionOperand(
                    row: rowName,
                    stateHandle: ResolveWorldStateHandle(definition: definition, name: rowName),
                    reduce: op,
                    filterRow: filterRowName,
                    filterHandle: filterHandle,
                    valueKind: reduceValueKind
                )),
                ValueKind: reduceValueKind,
                Describe: describe
            );
        }

        if (
            name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.ArgMaxPrefix
        ) ||
            name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.ArgMinPrefix
        )
        ) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var isMax = name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.ArgMaxPrefix
            );
            var rowAndFilter = name[(isMax
                ? WorldRuleFacts.ArgMaxPrefix.Length
                : WorldRuleFacts.ArgMinPrefix.Length)..];
            const string WhereMarker = ":where:";
            var where = rowAndFilter.IndexOf(value: WhereMarker, comparisonType: StringComparison.Ordinal);
            var rowName = ((where < 0) ? rowAndFilter : rowAndFilter[..where]);
            var filterRowName = ((where < 0) ? null : rowAndFilter[(where + WhereMarker.Length)..]);

            if (string.IsNullOrEmpty(value: rowName) || ((where >= 0) && string.IsNullOrEmpty(value: filterRowName))) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.ArgChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' does not spell '{(isMax
                    ? WorldRuleFacts.ArgMaxPrefix
                    : WorldRuleFacts.ArgMinPrefix)}<row>'"
                );
            }

            _ = ResolveNumericRow(
                channel: name,
                definition: definition,
                malformed: WorldRuleRefusal.ArgChannelMalformed,
                name: rowName,
                requireKeyed: true,
                ruleName: ruleName
            );
            WorldStateHandle filterHandle = default;
            if (filterRowName is not null) {
                _ = ResolveNumericRow(
                    channel: name,
                    definition: definition,
                    malformed: WorldRuleRefusal.ArgChannelMalformed,
                    name: filterRowName,
                    requireKeyed: true,
                    ruleName: ruleName
                );
                filterHandle = ResolveWorldStateHandle(definition: definition, name: filterRowName);
            }

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(new ArgBodyOperand(
                    row: rowName,
                    stateHandle: ResolveWorldStateHandle(definition: definition, name: rowName),
                    reduce: (isMax ? WorldStateReduceOp.Max : WorldStateReduceOp.Min),
                    filterRow: filterRowName,
                    filterHandle: filterHandle
                )),
                ValueKind: CellKind.Int,
                Describe: describe
            );
        }

        if (
            name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.DistancePrefix
        ) ||
            name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.LineOfSightPrefix
        )
        ) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var isDistance = name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.DistancePrefix
            );
            var suffix = name[(isDistance
                ? WorldRuleFacts.DistancePrefix.Length
                : WorldRuleFacts.LineOfSightPrefix.Length)..];
            var tokens = suffix.Split(separator: ':');
            var widthA = BodyRefTokenWidth(start: 0, tokens: tokens);

            if (tokens.Length != (widthA + BodyRefTokenWidth(start: widthA, tokens: tokens))) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.SpatialChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' does not spell '{(isDistance
                    ? WorldRuleFacts.DistancePrefix
                    : WorldRuleFacts.LineOfSightPrefix)}<bodyRefA>:<bodyRefB>' (each {s_bodyRefVocabulary})"
                );
            }

            var bodyA = ResolveBodyRefToken(
                channel: name,
                definition: definition,
                ruleName: ruleName,
                start: 0,
                tokens: tokens
            );
            var bodyB = ResolveBodyRefToken(
                channel: name,
                definition: definition,
                ruleName: ruleName,
                start: widthA,
                tokens: tokens
            );
            var spatialValueKind = (isDistance
                ? CellKind.Fixed
                : CellKind.Bool
            );

            return new ResolvedOperand(
                Operand: (isDistance
                    ? new CompiledWorldOperand(new BodyDistanceOperand(bodyA, bodyB))
                    : new CompiledWorldOperand(new LineOfSightOperand(bodyA, bodyB))
                ),
                ValueKind: spatialValueKind,
                Describe: describe
            );
        }

        // $upright: — the same single-body-reference grammar $parked: spends, widened through BodyRefTokenWidth
        // (not hardcoded to two tokens) so a cell:<row>:<key> reference composes here too.
        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.UprightPrefix
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var suffix = name[WorldRuleFacts.UprightPrefix.Length..];
            var tokens = suffix.Split(separator: ':');
            var width = BodyRefTokenWidth(start: 0, tokens: tokens);

            if (tokens.Length != width) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.SpatialChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' does not spell '{WorldRuleFacts.UprightPrefix}<bodyRef>' ({s_bodyRefVocabulary})"
                );
            }

            var uprightBody = ResolveBodyRefToken(
                channel: name,
                definition: definition,
                ruleName: ruleName,
                start: 0,
                tokens: tokens
            );

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(new UprightOperand(uprightBody)),
                ValueKind: CellKind.Fixed,
                Describe: describe
            );
        }

        // $parked: — the same single-body-reference grammar $distance:/$los: spend one half of theirs on, so it
        // composes with argmax/argmin directly ($parked:argmax:threat).
        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.ParkedPrefix
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var suffix = name[WorldRuleFacts.ParkedPrefix.Length..];
            var tokens = suffix.Split(separator: ':');

            if (tokens.Length != 2) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.ParkedChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' does not spell '{WorldRuleFacts.ParkedPrefix}<bodyRef>' (a 'body:<n>' or 'argmax:<row>'/'argmin:<row>' pair)"
                );
            }

            var parkedBody = ResolveBodyRefToken(
                channel: name,
                definition: definition,
                ruleName: ruleName,
                start: 0,
                tokens: tokens
            );

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(new ParkedOperand(parkedBody)),
                ValueKind: CellKind.Int,
                Describe: describe
            );
        }

        // $channel: — the 1-based local seat's own folded channel value, resolved to (seat, ordinal) once here so
        // evaluation is a plain array read (Server.WorldServer.ReadChannelValue). The seat/channel-name pair is
        // proven against population.localSeats and the declared channels[] rows, exactly as $machine:'s
        // screen/address pair is proven against the declared screens.
        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.ChannelPrefix
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var suffix = name[WorldRuleFacts.ChannelPrefix.Length..];
            var separator = suffix.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: ':'
            );

            if (
                (separator < 0) ||
                !int.TryParse(
                s: suffix[..separator],
                style: System.Globalization.NumberStyles.Integer,
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var seat
            ) ||
                (seat < 1) ||
                (seat > definition.Population.LocalSeats) ||
                string.IsNullOrEmpty(value: suffix[(separator + 1)..])
            ) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.ChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' does not spell '{WorldRuleFacts.ChannelPrefix}<seat>:<channelName>' with seat in 1..{definition.Population.LocalSeats}"
                );
            }

            var channelName = suffix[(separator + 1)..];
            var channelOrdinal = ResolveChannelOrdinal(
                definition: definition,
                name: channelName
            );

            if (channelOrdinal < 0) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.ChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' names channel '{channelName}', which the document does not declare in 'channels[]'"
                );
            }

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(new ChannelOperand(seat: (seat - 1), channelOrdinal: channelOrdinal)),
                ValueKind: CellKind.Fixed,
                Describe: describe
            );
        }

        // $link: — one adjacency row name, proven against the document's own adjacencies section at compile time so
        // a typo'd seam refuses instead of reading 0 (fresh) forever.
        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.LinkPrefix
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var adjacencyName = name[WorldRuleFacts.LinkPrefix.Length..];

            if (
                string.IsNullOrEmpty(value: adjacencyName) ||
                adjacencyName.Contains(value: ':') ||
                (WorldDefinitionRows.FindAdjacency(
                adjacencies: definition.Adjacencies,
                name: adjacencyName
            ) is null)
            ) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.LinkChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' does not spell '{WorldRuleFacts.LinkPrefix}<adjacencyName>' naming a declared 'adjacencies' row"
                );
            }

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(new LinkStalenessOperand(adjacencyName)),
                ValueKind: CellKind.Int,
                Describe: describe
            );
        }

        if (name.StartsWith(comparisonType: StringComparison.Ordinal, value: WorldRuleFacts.NavigationPrefix)) {
            RefuseKeyOnReservedChannel(key: key, keyFieldLabel: keyFieldLabel, name: name, ruleName: ruleName);
            var tokens = name[WorldRuleFacts.NavigationPrefix.Length..].Split(separator: ':');
            var width = BodyRefTokenWidth(start: 0, tokens: tokens);
            if (tokens.Length != width + 1 || tokens[width] is not ("hasPath" or "active" or "arrived" or "unreachable" or "remaining" or "pending" or "capacity")) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.SpatialChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' does not spell '{WorldRuleFacts.NavigationPrefix}<bodyRef>:<hasPath|active|arrived|unreachable|remaining|pending|capacity>' ({s_bodyRefVocabulary})"
                );
            }
            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(new NavigationOperand(
                    bodyA: ResolveBodyRefToken(channel: name, definition: definition, ruleName: ruleName, start: 0, tokens: tokens),
                    row: tokens[width]
                )),
                ValueKind: CellKind.Int,
                Describe: describe
            );
        }

        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.NearestPrefix
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var tokens = name[WorldRuleFacts.NearestPrefix.Length..].Split(separator: ':');
            var width = BodyRefTokenWidth(
                start: 0,
                tokens: tokens
            );

            if (tokens.Length != (width + 1)) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.SpatialChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' does not spell '{WorldRuleFacts.NearestPrefix}<bodyRef>:<row>' ({s_bodyRefVocabulary}, then the keyed tag row)"
                );
            }

            var from = ResolveBodyRefToken(
                channel: name,
                definition: definition,
                ruleName: ruleName,
                start: 0,
                tokens: tokens
            );

            _ = ResolveNumericRow(
                channel: name,
                definition: definition,
                malformed: WorldRuleRefusal.SpatialChannelMalformed,
                name: tokens[width],
                requireKeyed: true,
                ruleName: ruleName
            );

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(new NearestOperand(
                    bodyA: from,
                    row: tokens[width],
                    stateHandle: ResolveWorldStateHandle(definition: definition, name: tokens[width])
                )),
                ValueKind: CellKind.Int,
                Describe: describe
            );
        }

        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: RuleFacts.SymmetryPrefix
        )) {
            return ResolveSymmetryOperand(
                definition: definition,
                describe: describe,
                fieldLabel: fieldLabel,
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName,
                verb: verb
            );
        }

        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldStateRow.ReservedNamePrefix
        )) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateRowUnknown,
                ruleName: ruleName,
                detail: $"'{name}' carries the reserved '{WorldStateRow.ReservedNamePrefix}' prefix but names none of the reserved channels ('{RuleFacts.Tick}', '{WorldRuleFacts.Population}', '{WorldRuleFacts.RegionPrefix}<placementId>', '{WorldRuleFacts.MachinePrefix}<screen>:<address>', '{RuleFacts.ReducePrefix}<op>:<row>', '{WorldRuleFacts.ArgMaxPrefix}<row>', '{WorldRuleFacts.ArgMinPrefix}<row>', '{WorldRuleFacts.DistancePrefix}<a>:<b>', '{WorldRuleFacts.LineOfSightPrefix}<a>:<b>', '{WorldRuleFacts.UprightPrefix}<bodyRef>', '{WorldRuleFacts.NavigationPrefix}<bodyRef>:<facet>', '{WorldRuleFacts.ParkedPrefix}<bodyRef>', '{WorldRuleFacts.LinkPrefix}<adjacencyName>', '{WorldRuleFacts.ChannelPrefix}<seat>:<channelName>')"
            );
        }

        // A declared row name is dot-free by construction (CellName refuses a dot) — this only ever fires for an
        // author reaching for a "row.key" spelling in one string. Named explicitly rather than falling through to a
        // generic "unknown row", which would leave the actual mistake (use the separate key field) unsaid.
        if (name.Contains(value: '.')) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"'{fieldLabel}' value '{name}' carries a '.' — a state row name is never dotted; address the cell with '{keyFieldLabel}' instead of dotting it into '{fieldLabel}'"
            );
        }

        var row = (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: name
        )
            ?? throw new WorldRuleException(
            refusal: WorldRuleRefusal.StateRowUnknown,
            ruleName: ruleName,
            detail: $"'{name}' names no state row, and is not a reserved channel ('{RuleFacts.Tick}', '{WorldRuleFacts.Population}', '{WorldRuleFacts.RegionPrefix}<placementId>', '{WorldRuleFacts.MachinePrefix}<screen>:<address>', '{RuleFacts.ReducePrefix}<op>:<row>', '{WorldRuleFacts.ArgMaxPrefix}<row>', '{WorldRuleFacts.ArgMinPrefix}<row>', '{WorldRuleFacts.DistancePrefix}<a>:<b>', '{WorldRuleFacts.LineOfSightPrefix}<a>:<b>', '{WorldRuleFacts.UprightPrefix}<bodyRef>', '{WorldRuleFacts.NavigationPrefix}<bodyRef>:<facet>', '{WorldRuleFacts.ParkedPrefix}<bodyRef>', '{WorldRuleFacts.LinkPrefix}<adjacencyName>', '{WorldRuleFacts.ChannelPrefix}<seat>:<channelName>')"
        ));

        if (
            (row.Kind == CellKind.Text) &&
            !allowText
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"state row '{name}' is kind=text — a rule compares numbers, never text"
            );
        }

        if (TryResolveDynamicKey(
            cell: out var dynamicKey,
            definition: definition,
            key: key,
            keyFieldLabel: keyFieldLabel,
            ruleName: ruleName,
            verb: verb
        )) {
            if (!row.IsKeyed) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName,
                    detail: $"'{verb}' {keyFieldLabel} '{key}' addresses a cell by indirection, but row '{name}' is not keyed"
                );
            }

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(new StateCellOperand(
                    row: name,
                    key: null,
                    keyFrom: dynamicKey,
                    stateHandle: ResolveWorldStateHandle(definition: definition, name: name),
                    valueKind: row.Kind
                )),
                ValueKind: row.Kind,
                Describe: describe
            );
        }

        var resolvedKey = ResolveKey(
            key: key,
            keyFieldLabel: keyFieldLabel,
            row: row,
            ruleName: ruleName,
            verb: verb
        );

        // A READ operand must address a cell the row declares TODAY: an undeclared cell reads 0 forever with no
        // refusal anywhere (silently broken gating), so it refuses at compile instead. Write destinations mint their
        // cells and are deliberately not funneled through here.
        //
        // A DRAW SITE's slot cell is DECLARED BY ITS FACET, even before it holds one: the boot resolver fills every
        // first-fill site at load, so a running document's draw site always carries its cell. Validation runs on the
        // document BEFORE that resolution, so without this arm a rule gated on a drawn value would refuse at boot for
        // a document that is correct — the refusal would report a state the engine passes through, never one it runs
        // in.
        if (
            !row.HasCell(key: resolvedKey) &&
            !(row.IsDraw && (resolvedKey == WorldStateRow.SlotKey.Value))
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUndeclared,
                ruleName: ruleName,
                detail: $"'{verb}' {fieldLabel} '{name}' reads cell '{resolvedKey}', which the row does not declare — an undeclared cell reads 0 forever; declare the cell first (an authored 0 is fine)"
            );
        }

        return new ResolvedOperand(
            Operand: new CompiledWorldOperand(new StateCellOperand(
                row: name,
                key: resolvedKey,
                keyFrom: null,
                stateHandle: ResolveWorldStateHandle(definition: definition, name: name),
                valueKind: row.Kind
            )),
            ValueKind: row.Kind,
            Describe: describe
        );
    }
    private static CompiledWorldPredicate ResolvePredicate(ActionPredicate.CompareState compare, string ruleName, WorldDefinition definition) {
        var name = (compare.State ?? string.Empty);
        var comparison = compare.Comparison;
        var hasValue = (compare.Value is not null);
        var hasComparand = (compare.ComparandState is not null);

        // 'comparandKey' is an appendage of 'comparandState'; on its own it is a parsed-and-discarded field, refused
        // by name rather than silently ignored under the constant spelling.
        if (
            (compare.ComparandKey is not null) &&
            (compare.ComparandState is null)
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.ComparandAmbiguous,
                ruleName: ruleName,
                detail: "names 'comparandKey' without 'comparandState' — a comparand key addresses a cell inside a comparand row, which must be named"
            );
        }

        // Exactly one comparand spelling: an authored constant, or another row/channel read live. Both, or neither,
        // is an authoring mistake refused by name rather than one spelling silently winning.
        if (hasValue == hasComparand) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.ComparandAmbiguous,
                ruleName: ruleName,
                detail: (hasValue
                ? "names both 'value' and 'comparandState' — a compareState spells exactly one comparand, never both"
                : "names neither 'value' nor 'comparandState' — a compareState must spell exactly one comparand")
            );
        }

        var lhs = ResolveOperand(
            name: name,
            key: compare.Key,
            ruleName: ruleName,
            definition: definition,
            verb: "compareState",
            fieldLabel: "state",
            keyFieldLabel: "key"
        );

        if (hasValue) {
            var describe = $"{lhs.Describe} {DescribeComparison(comparison: comparison)} {compare.Value!.Value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}";
            var (value, lowered) = LowerConstantComparison(
                comparison: comparison,
                kind: lhs.ValueKind,
                literal: compare.Value.Value,
                ruleName: ruleName
            );

            return new CompiledWorldPredicate(
                Left: lhs.Operand,
                Comparison: lowered,
                Value: value,
                ValueKind: lhs.ValueKind,
                Comparand: null,
                Describe: describe
            );
        }

        var rhs = ResolveOperand(
            name: compare.ComparandState!,
            key: compare.ComparandKey,
            ruleName: ruleName,
            definition: definition,
            verb: "compareState",
            fieldLabel: "comparandState",
            keyFieldLabel: "comparandKey"
        );

        // Mixed kinds refuse by name: an int tick count against a fixed-point row (or vice versa) mixes scales
        // silently, which is worse than naming the mismatch — the constant spelling keeps its existing, more
        // permissive behavior (every shipped world's compareState already leans on it), so this check applies ONLY
        // to the new comparand-row spelling.
        if (lhs.ValueKind != rhs.ValueKind) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.ComparandKindMismatch,
                ruleName: ruleName,
                detail: $"'{name}' is kind={DescribeCellKind(kind: lhs.ValueKind)} but comparand '{compare.ComparandState}' is kind={DescribeCellKind(kind: rhs.ValueKind)} — mixed-kind comparisons are refused; author both sides the same kind"
            );
        }

        var mixedDescribe = $"{lhs.Describe} {DescribeComparison(comparison: comparison)} {rhs.Describe}";

        return new CompiledWorldPredicate(
            Left: lhs.Operand,
            Comparison: comparison,
            Value: default,
            ValueKind: lhs.ValueKind,
            Comparand: rhs.Operand,
            Describe: mixedDescribe
        );
    }
    private static CompiledWorldEffect ResolveRemoveHudPanel(ActionEffect.RemoveHudPanel effect, string ruleName) {
        if (string.IsNullOrWhiteSpace(value: effect.Id)) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.HudPanelInvalid,
                ruleName: ruleName,
                detail: "'removeHudPanel' names no panel 'id'"
            );
        }

        return new CompiledWorldEffect(new RemoveHudPanelEffect(id: effect.Id, describe: $"removeHudPanel {effect.Id}"));
    }
    private static CompiledWorldEffect ResolveRemovePlacement(ActionEffect.RemovePlacement effect, string ruleName) {
        if (string.IsNullOrWhiteSpace(value: effect.Id)) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.PlacementInvalid,
                ruleName: ruleName,
                detail: "'removePlacement' names no placement 'id'"
            );
        }

        return new CompiledWorldEffect(new RemovePlacementEffect(id: effect.Id, describe: $"removePlacement {effect.Id}"));
    }
    // upsertHudPanel/upsertPlacement are whole-row upserts, exactly like WorldMutation.UpsertHudPanel/UpsertPlacement
    // submitted from the console or an addon — the row's own content (capacity, unknown binding, unresolved
    // prototypeId) is validated by the ORDINARY whole-document revalidation when the effect actually fires, never
    // duplicated here. Compile time checks only what a whole-row upsert can check in isolation: that it names itself.
    private static CompiledWorldEffect ResolveUpsertHudPanel(ActionEffect.UpsertHudPanel effect, string ruleName) {
        if (
            (effect.Panel is null) ||
            string.IsNullOrWhiteSpace(value: effect.Panel.Id)
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.HudPanelInvalid,
                ruleName: ruleName,
                detail: "'upsertHudPanel' names no panel 'id'"
            );
        }

        return new CompiledWorldEffect(new UpsertHudPanelEffect(panel: effect.Panel, describe: $"upsertHudPanel {effect.Panel.Id}"));
    }
    private static CompiledWorldEffect ResolveUpsertPlacement(ActionEffect.UpsertPlacement effect, string ruleName) {
        if (
            (effect.Placement is null) ||
            string.IsNullOrWhiteSpace(value: effect.Placement.Id)
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.PlacementInvalid,
                ruleName: ruleName,
                detail: "'upsertPlacement' names no placement 'id'"
            );
        }

        return new CompiledWorldEffect(new UpsertPlacementEffect(placement: effect.Placement, describe: $"upsertPlacement {effect.Placement.Id}"));
    }
    // value XOR valueSeconds XOR (fromState, fromKey): the same duality ResolvePredicate enforces for compareState's
    // comparand, applied to the write side and widened by one spelling. 'fromKey' is an appendage of 'fromState' on
    // the same terms 'comparandKey' is.
    // A constant comparand against an int or bool operand is lowered EXACTLY: an integral literal is the raw it
    // names, and a fractional one becomes the equivalent integer comparison (x > 1.5 is x >= 2, x <= 1.5 is x <= 1,
    // x == 1.5 never holds, x != 1.5 always holds) rather than a rounded literal that would move the gate. A fixed
    // operand keeps its exact fixed-point literal.
    private static (long Value, ActionStateComparison Comparison) LowerConstantComparison(CellKind kind, decimal literal, ActionStateComparison comparison, string ruleName) {
        if ((kind == CellKind.Fixed) || (decimal.Truncate(d: literal) == literal)) {
            return (LiteralToRaw(kind: kind, literal: literal, ruleName: ruleName, verb: "compareState"), comparison);
        }

        var floor = LiteralToRaw(kind: kind, literal: decimal.Floor(d: literal), ruleName: ruleName, verb: "compareState");
        var ceiling = LiteralToRaw(kind: kind, literal: decimal.Ceiling(d: literal), ruleName: ruleName, verb: "compareState");

        return comparison switch {
            ActionStateComparison.Greater or ActionStateComparison.GreaterOrEqual => (ceiling, ActionStateComparison.GreaterOrEqual),
            ActionStateComparison.Less or ActionStateComparison.LessOrEqual => (floor, ActionStateComparison.LessOrEqual),
            ActionStateComparison.Equal => (long.MaxValue, ActionStateComparison.Greater),
            _ => (long.MinValue, ActionStateComparison.GreaterOrEqual),
        };
    }
    private static long LiteralToRaw(CellKind kind, decimal literal, string ruleName, string verb) {
        try {
            var raw = kind switch {
                CellKind.Int => checked((long)decimal.Round(d: literal, decimals: 0, mode: MidpointRounding.ToEven)),
                CellKind.Fixed => NumericLiteral.ToFixed(value: literal).Value,
                _ => ((literal != decimal.Zero) ? 1L : 0L), // Bool — Text is refused before numeric lowering.
            };

            return raw;
        } catch (OverflowException) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"'{verb}' literal {literal.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} is outside the representable {DescribeCellKind(kind: kind)} state range"
            );
        }
    }
    private static WorldStateHandle ResolveWorldStateHandle(WorldDefinition definition, string name) {
        if (definition.StateCatalog.TryResolve(
            lane: WorldStateOwnershipLane.World,
            name: name,
            handle: out var handle
        )) {
            return handle;
        }

        throw new InvalidOperationException(message: $"Validated world state row '{name}' is absent from its compiled catalog.");
    }
    private static bool TryParseReduceOp(string text, out WorldStateReduceOp op) {
        op = text switch {
            "max" => WorldStateReduceOp.Max,
            "min" => WorldStateReduceOp.Min,
            "sum" => WorldStateReduceOp.Sum,
            "count" => WorldStateReduceOp.Count,
            _ => WorldStateReduceOp.None,
        };

        return (op != WorldStateReduceOp.None);
    }

    /// <summary>Compiles one rule against a candidate document. Does not check name presence or uniqueness — that is
    /// <see cref="CompileAll"/>'s job, the one caller with a sibling list to check against.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <param name="definition">The candidate document.</param>
    /// <returns>The compiled rule.</returns>
    /// <exception cref="WorldRuleException">The rule names something the document does not declare, or uses a
    /// predicate/effect kind world scope has no meaning for.</exception>
    public static CompiledWorldRule Compile(WorldRule rule, WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: rule);
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (rule.ForEach is { } forEach) {
            _ = ResolveNumericRow(
                channel: "forEach",
                definition: definition,
                malformed: WorldRuleRefusal.StateRowUnknown,
                name: forEach,
                requireKeyed: true,
                ruleName: rule.Name
            );
        }

        s_bindingScope = ((rule.ForEach is null)
            ? []
            : [RuleBinding.Each]
        );

        try {
            var bindings = CompileBindings(rule: rule, definition: definition);
            var gate = new List<CompiledWorldPredicate>();

            FlattenPredicate(
                predicate: rule.Gate,
                gate: gate,
                ruleName: rule.Name,
                definition: definition
            );

            if (gate.Count > WorldRuleCapacity.MaxPredicateTokens) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.PredicateKindInadmissible,
                    ruleName: rule.Name,
                    detail: $"gate compiles to {gate.Count} tokens, exceeding the {WorldRuleCapacity.MaxPredicateTokens}-token ceiling"
                );
            }

            return new CompiledWorldRule(
                Name: rule.Name,
                Mode: rule.Mode,
                Gate: gate.ToArray(),
                Effects: rule.Decision is not null ? CompileDecisionEffects(rule.Effects, rule.Name, definition) : CompileEffects(
                    effects: rule.Effects,
                    ruleName: rule.Name,
                    definition: definition,
                    subject: "rule"
                ),
                ForEach: rule.ForEach,
                Decision: CompileDecision(rule, definition),
                Bindings: bindings
            );
        } finally {
            s_bindingScope = null;
            s_ruleBindings = null;
        }
    }
    /// <summary>Compiles every rule in the definition's <c>rules</c> section, in document order.</summary>
    /// <param name="definition">The candidate document — its <c>state</c> and <c>placements</c> sections resolve
    /// every name a rule can spell.</param>
    /// <returns>The compiled rules, in authored order.</returns>
    /// <exception cref="WorldRuleException">A rule's name is missing, reserved, or duplicated, or it fails to
    /// compile.</exception>
    public static CompiledWorldRule[] CompileAll(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var rules = (definition.Rules ?? []);

        if (rules.Count == 0) {
            return [];
        }

        var seen = new HashSet<string>(
            capacity: rules.Count,
            comparer: StringComparer.Ordinal
        );
        var compiled = new CompiledWorldRule[rules.Count];

        for (var index = 0; (index < rules.Count); index++) {
            var rule = rules[index];
            // CellName already proved the shape (non-empty, dot-free, free of the reserved character set) at the
            // JSON converter or at the console verb, naming the offending character — a default-valued struct from a
            // programmatically built definition is the one way an empty name still reaches here.
            var name = (rule?.Name.Value ?? string.Empty);

            if (string.IsNullOrWhiteSpace(value: name)) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.NameMissing,
                    ruleName: "<unnamed>",
                    detail: "a rule declares a name"
                );
            }
            // The same reserved-prefix rule a state ROW name carries (see WorldStateRow.ReservedNamePrefix): '$' marks
            // what the engine mints, and nothing mints a rule — so a '$'-prefixed name is refused rather than
            // accepted, evaluated, and persisted as an authored name that reads like engine bookkeeping.
            if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldStateRow.ReservedNamePrefix
            )) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.NameReserved,
                    ruleName: name,
                    detail: $"carries the reserved character '{WorldStateRow.ReservedNamePrefix}' as its first character — that prefix marks what the ENGINE mints, and nothing mints a rule"
                );
            }
            if (!seen.Add(item: name)) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.NameDuplicated,
                    ruleName: name,
                    detail: "duplicates an earlier rule's name"
                );
            }

            compiled[index] = Compile(
                definition: definition,
                rule: rule!
            );
        }

        return compiled;
    }
    /// <summary>Compiles every interaction in the definition's <c>interactions</c> section, in document order — the
    /// generalized property-interaction table's one compile path. Each row desugars into a synthesized
    /// <see cref="WorldRule"/> (its co-occurrence spelled as an ordinary <see cref="ActionPredicate.CompareState"/>/
    /// <see cref="ActionPredicate.All"/> gate over the same <see cref="WorldRuleFacts.ArgMaxPrefix"/>/
    /// <see cref="WorldRuleFacts.DistancePrefix"/>/<see cref="WorldRuleFacts.RegionPrefix"/> reserved channels a
    /// hand-authored rule already reads) and rides <see cref="Compile"/> unchanged — there is no second evaluation
    /// engine, only a second authoring surface compiling to the one rule substrate. Interactions occupy their own
    /// name namespace, separate from <see cref="WorldRule.Name"/> (see <see cref="WorldInteraction"/>'s remarks).
    /// </summary>
    /// <param name="definition">The candidate document — its <c>properties</c>, <c>state</c>, and <c>placements</c>
    /// sections resolve every name an interaction can spell.</param>
    /// <returns>The compiled interactions, in authored order.</returns>
    /// <exception cref="WorldRuleException">An interaction's name is missing, reserved, or duplicated, its
    /// <c>left</c>/<c>right</c> property reference is not in the declared registry, or it otherwise fails to compile.
    /// </exception>
    public static CompiledWorldRule[] CompileAllInteractions(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var interactions = (definition.Interactions?.Interactions ?? []);

        if (interactions.Count == 0) {
            return [];
        }
        if (interactions.Count > WorldInteractionCapacity.MaxInteractions) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: "<interactions>", detail: $"declares {interactions.Count} rows, exceeding the {WorldInteractionCapacity.MaxInteractions}-interaction ceiling", subject: "interaction");
        }

        var registry = new HashSet<string>(
            collection: (definition.Properties?.Names ?? []),
            comparer: StringComparer.Ordinal
        );
        var seen = new HashSet<string>(
            capacity: interactions.Count,
            comparer: StringComparer.Ordinal
        );
        var compiled = new CompiledWorldRule[interactions.Count];

        for (var index = 0; (index < interactions.Count); index++) {
            var interaction = interactions[index];
            // CellName already proved the shape at the JSON converter or console verb — a default-valued struct
            // from a programmatically built definition is the one way an empty name still reaches here, the same
            // caveat CompileAll's own name walk carries.
            var name = (interaction?.Name.Value ?? string.Empty);

            if (string.IsNullOrWhiteSpace(value: name)) {
                throw new WorldRuleException(
                    detail: "an interaction declares a name",
                    refusal: WorldRuleRefusal.NameMissing,
                    ruleName: "<unnamed>",
                    subject: "interaction"
                );
            }
            // The same reserved-prefix rule a rule name (and a state ROW name) carries: '$' marks what the engine
            // mints, and nothing mints an interaction.
            if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldStateRow.ReservedNamePrefix
            )) {
                throw new WorldRuleException(
                    detail: $"carries the reserved character '{WorldStateRow.ReservedNamePrefix}' as its first character — that prefix marks what the ENGINE mints, and nothing mints an interaction",
                    refusal: WorldRuleRefusal.NameReserved,
                    ruleName: name,
                    subject: "interaction"
                );
            }
            if (!seen.Add(item: name)) {
                throw new WorldRuleException(
                    detail: "duplicates an earlier interaction's name",
                    refusal: WorldRuleRefusal.NameDuplicated,
                    ruleName: name,
                    subject: "interaction"
                );
            }

            var row = interaction!;

            // The validated-vocabulary check: 'left' is ALWAYS a property reference; 'right' is one too under
            // Distance, but names a REGION PLACEMENT under Region instead (checked structurally, not against the
            // registry — see the Region arm below).
            if (!registry.Contains(item: row.Left)) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.PropertyUnknown,
                    ruleName: name,
                    detail: $"'left' names '{row.Left}', which is not a registered property (see the 'properties' section)",
                    subject: "interaction"
                );
            }

            _ = ResolveNumericRow(
                channel: "left",
                definition: definition,
                malformed: WorldRuleRefusal.PropertyUnknown,
                name: row.Left,
                requireKeyed: true,
                ruleName: name
            );

            switch (row.CoOccurrence) {
                case WorldInteractionCoOccurrence.Distance:
                    if (!registry.Contains(item: row.Right)) {
                        throw new WorldRuleException(
                            refusal: WorldRuleRefusal.PropertyUnknown,
                            ruleName: name,
                            detail: $"'right' names '{row.Right}', which is not a registered property (see the 'properties' section)",
                            subject: "interaction"
                        );
                    }

                    _ = ResolveNumericRow(
                        channel: "right",
                        definition: definition,
                        malformed: WorldRuleRefusal.PropertyUnknown,
                        name: row.Right,
                        requireKeyed: true,
                        ruleName: name
                    );

                    if (
                        (row.Range < decimal.Zero)
                    ) {
                        throw new WorldRuleException(
                            refusal: WorldRuleRefusal.SpatialChannelMalformed,
                            ruleName: name,
                            detail: $"'range' {row.Range} is not a non-negative distance",
                            subject: "interaction"
                        );
                    }

                    s_bindingScope = [RuleBinding.Left, RuleBinding.Right];
                    break;
                case WorldInteractionCoOccurrence.Region:
                    if (!HasRegion(
                        definition: definition,
                        placementId: row.Right
                    )) {
                        throw new WorldRuleException(
                            refusal: WorldRuleRefusal.RegionUnknown,
                            ruleName: name,
                            detail: $"'right' names placement '{row.Right}', which declares no region facet",
                            subject: "interaction"
                        );
                    }

                    s_bindingScope = [RuleBinding.Left];
                    break;
                default:
                    throw new WorldRuleException(
                        refusal: WorldRuleRefusal.PredicateKindInadmissible,
                        ruleName: name,
                        detail: $"'coOccurrence' value '{row.CoOccurrence}' is not a defined WorldInteractionCoOccurrence",
                        subject: "interaction"
                    );
            }

            try {
                compiled[index] = new CompiledWorldRule(
                    Name: name,
                    Mode: row.Mode,
                    Gate: [],
                    Effects: CompileEffects(
                        effects: row.Effects,
                        ruleName: name,
                        definition: definition,
                        subject: "interaction"
                    ),
                    Interaction: new CompiledInteraction(
                        CoOccurrence: row.CoOccurrence,
                        Left: row.Left,
                        Range: NumericLiteral.ToFixed(value: row.Range),
                        Right: row.Right
                    )
                );
            } finally {
                s_bindingScope = null;
            }
        }

        return compiled;
    }

    // One resolved operand (address + value kind + read-back spelling) plus the cell kind the mixed-kind guard reads.
    // The operand's own ValueKind is baked in by its case constructor at every call site; this check is the one
    // place that invariant is checked rather than silently re-stamped (a record struct's own "with" could once fix a
    // drifted ValueKind for free — a case-type union has no such generic clone, so the two must already agree).
    private readonly record struct ResolvedOperand {
        public ResolvedOperand(CompiledWorldOperand Operand, CellKind ValueKind, string Describe) {
            if (Operand.ValueKind != ValueKind) {
                throw new InvalidOperationException(
                    message: $"a resolved '{Describe}' operand's own ValueKind ({Operand.ValueKind}) does not match the case constructor's ValueKind ({ValueKind})"
                );
            }
            this.Operand = Operand;
            this.ValueKind = ValueKind;
            this.Describe = Describe;
        }

        public string Describe { get; }
        public CompiledWorldOperand Operand { get; }
        public CellKind ValueKind { get; }
    }
}
