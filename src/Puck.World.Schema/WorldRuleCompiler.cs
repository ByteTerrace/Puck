using System.Globalization;
using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>Compiles the world's rules and interactions: the state library's own compile pieces composed with the
/// world's registered vocabulary (<see cref="WorldRuleVocabulary"/>), the decision policy, and the interaction
/// co-occurrence. Called twice by design: wrapped per rule inside the validator so a malformed rule refuses by
/// name, and unwrapped inside the server's install path for the live array the tick evaluates.</summary>
public static partial class WorldRuleCompiler {
    /// <summary>Creates the compile context for a document.</summary>
    /// <param name="definition">The world.</param>
    public static WorldRuleCompileContext Context(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return new WorldRuleCompileContext(definition: definition);
    }

    /// <summary>Compiles one rule against a fresh context.</summary>
    public static CompiledWorldRule Compile(WorldRule rule, WorldDefinition definition) => Compile(rule: rule, context: Context(definition: definition));

    /// <summary>Compiles one rule. Does not check name presence or uniqueness — that is <see cref="CompileAll"/>'s job.</summary>
    /// <exception cref="RuleException">The rule names something the document does not declare, or uses a
    /// predicate/effect kind rule scope has no meaning for.</exception>
    public static CompiledWorldRule Compile(WorldRule rule, WorldRuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: rule);
        ArgumentNullException.ThrowIfNull(argument: context);

        RuleCompiler.BeginScope(rule: rule, context: context);

        try {
            var bindings = RuleCompiler.CompileBindings(rule: rule, context: context);
            var gate = RuleCompiler.CompileGate(predicate: rule.Gate, ruleName: rule.Name, context: context);
            var effects = ((rule.Decision is not null)
                ? CompileDecisionEffects(effects: rule.Effects, ruleName: rule.Name, context: context)
                : RuleCompiler.CompileEffects(effects: rule.Effects, ruleName: rule.Name, context: context, subject: "rule"));

            return new CompiledWorldRule(
                Name: rule.Name,
                Mode: rule.Mode,
                Gate: gate,
                Effects: effects,
                ForEach: rule.ForEach,
                Decision: CompileDecision(rule: rule, context: context),
                Bindings: bindings
            );
        } finally {
            context.ClearScope();
        }
    }

    /// <summary>Compiles every rule in document order, checking that each carries a unique, unreserved name.</summary>
    public static CompiledWorldRule[] CompileAll(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var rules = (definition.Rules ?? []);

        if (rules.Count == 0) {
            return [];
        }

        var context = Context(definition: definition);
        var seen = new HashSet<string>(capacity: rules.Count, comparer: StringComparer.Ordinal);
        var compiled = new CompiledWorldRule[rules.Count];

        for (var index = 0; (index < rules.Count); index++) {
            var rule = rules[index];

            RuleCompiler.RequireName(name: (rule?.Name.Value ?? string.Empty), seen: seen, subject: "rule");
            compiled[index] = Compile(rule: rule!, context: context);
        }

        return compiled;
    }

    /// <summary>Compiles every interaction in document order.</summary>
    public static CompiledWorldRule[] CompileAllInteractions(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var interactions = (definition.Interactions?.Interactions ?? []);

        if (interactions.Count == 0) {
            return [];
        }
        if (interactions.Count > WorldInteractionCapacity.MaxInteractions) {
            throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: "<interactions>", detail: $"declares {interactions.Count} rows, exceeding the {WorldInteractionCapacity.MaxInteractions}-interaction ceiling", subject: "interaction");
        }

        var context = Context(definition: definition);
        var registry = new HashSet<string>(collection: (definition.Properties?.Names ?? []), comparer: StringComparer.Ordinal);
        var seen = new HashSet<string>(capacity: interactions.Count, comparer: StringComparer.Ordinal);
        var compiled = new CompiledWorldRule[interactions.Count];

        for (var index = 0; (index < interactions.Count); index++) {
            var row = interactions[index];
            var name = (row?.Name.Value ?? string.Empty);

            RuleCompiler.RequireName(name: name, seen: seen, subject: "interaction");

            if (!registry.Contains(item: row!.Left)) {
                throw new RuleException(refusal: WorldRuleRefusal.PropertyUnknown, ruleName: name, detail: $"'left' names '{row.Left}', which is not a registered property (see the 'properties' section)", subject: "interaction");
            }

            _ = RuleCompiler.ResolveNumericRow(name: row.Left, ruleName: name, context: context, requireKeyed: true, malformed: WorldRuleRefusal.PropertyUnknown, channel: "left");
            context.ClearScope();

            switch (row.CoOccurrence) {
                case WorldInteractionCoOccurrence.Distance:
                    if (!registry.Contains(item: row.Right)) {
                        throw new RuleException(refusal: WorldRuleRefusal.PropertyUnknown, ruleName: name, detail: $"'right' names '{row.Right}', which is not a registered property (see the 'properties' section)", subject: "interaction");
                    }

                    _ = RuleCompiler.ResolveNumericRow(name: row.Right, ruleName: name, context: context, requireKeyed: true, malformed: WorldRuleRefusal.PropertyUnknown, channel: "right");

                    if (row.Range < decimal.Zero) {
                        throw new RuleException(refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: name, detail: $"'range' {row.Range} is not a non-negative distance", subject: "interaction");
                    }

                    context.BindingScope = [BoundKey.Left, BoundKey.Right];

                    break;
                case WorldInteractionCoOccurrence.Region:
                    if (!context.HasRegion(placementId: row.Right)) {
                        throw new RuleException(refusal: WorldRuleRefusal.RegionUnknown, ruleName: name, detail: $"'right' names placement '{row.Right}', which declares no region facet", subject: "interaction");
                    }

                    context.BindingScope = [BoundKey.Left];

                    break;
                default:
                    throw new RuleException(refusal: RuleRefusal.PredicateKindInadmissible, ruleName: name, detail: $"'coOccurrence' value '{row.CoOccurrence}' is not a defined WorldInteractionCoOccurrence", subject: "interaction");
            }

            try {
                compiled[index] = new CompiledWorldRule(
                    Name: name,
                    Mode: row.Mode,
                    Gate: [],
                    Effects: RuleCompiler.CompileEffects(effects: row.Effects, ruleName: name, context: context, subject: "interaction"),
                    Interaction: new CompiledInteraction(Left: row.Left, Right: row.Right, CoOccurrence: row.CoOccurrence, Range: NumericLiteral.ToFixed(value: row.Range))
                );
            } finally {
                context.ClearScope();
            }
        }

        return compiled;
    }

    /// <summary>Compiles a flock-affinity expression: a fixed-point program over <c>$left</c>/<c>$right</c> whose
    /// operands are state-backed facts only, so movement-pass observations stay order-independent.</summary>
    public static CompiledExpressionToken[] CompileFlockAffinity(ValueExpression expression, WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: expression);

        var context = Context(definition: definition);

        context.BindingScope = [BoundKey.Left, BoundKey.Right];

        try {
            var tokens = RuleCompiler.CompileExpression(expression: expression, kind: CellKind.Fixed, ruleName: "flock affinity", verb: "affinity", context: context);

            foreach (var token in tokens) {
                if (token.Operand is { } operand && operand is not (StateCellOperand or ReductionOperand or SymmetryOperand)) {
                    throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: "flock affinity", detail: $"{operand.GetType().Name} is not a state-backed fact; movement-pass observations must be order-independent");
                }
            }

            return tokens;
        } finally {
            context.ClearScope();
        }
    }

    /// <summary>Compiles a pattern row's value expression over its token domain.</summary>
    public static bool TryCompilePatternValue(WorldDefinition definition, PatternRow pattern, string tokenDomain, string ruleName, out CompiledExpressionToken[]? tokens, out string reason) =>
        RuleCompiler.TryCompilePatternValue(context: Context(definition: definition), pattern: pattern, tokenDomain: tokenDomain, ruleName: ruleName, tokens: out tokens, reason: out reason);

    internal static EffectFact ResolveCue(WorldEffect.EmitCue effect, string ruleName, WorldRuleCompileContext context) {
        if (!WorldGameplayCue.IsValidName(candidate: effect.Name)) {
            throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'emitCue' name must contain 1..{WorldRuleCapacity.MaxCueNameLength} ASCII letters, digits, dots, hyphens, or underscores, and begin and end with a letter or digit");
        }
        if ((effect.Payload?.Length ?? 0) > WorldRuleCapacity.MaxCuePayloadLength) {
            throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'emitCue' payload exceeds {WorldRuleCapacity.MaxCuePayloadLength} UTF-16 code units");
        }

        var key = string.Empty;
        CompiledCellRef? keyFrom = null;

        if (effect.Key is { } bodyKey) {
            (key, keyFrom) = context.ResolveBodyAddress(key: bodyKey, verb: "emitCue", ruleName: ruleName);
        }

        return new EmitCueEffect(cue: effect.Name, payload: effect.Payload, key: key, keyFrom: keyFrom, describe: $"emitCue {effect.Name}");
    }

    internal static EffectFact ResolveBodyVerticalVelocity(string key, decimal value, BodyMotionOp operation, string verb, string ruleName, WorldRuleCompileContext context) {
        var address = context.ResolveBodyAddress(key: key, verb: verb, ruleName: ruleName);
        var fixedValue = RuleCompiler.ResolveFixedLiteral(value: value, field: "value", verb: verb, ruleName: ruleName);

        return new BodyEffect(
            key: address.Key,
            keyFrom: address.KeyFrom,
            body: new CompiledWorldBodyEffect(Operation: operation, Value: fixedValue, Direction: default, DurationTicks: 0UL),
            describe: $"{verb} body:{key} {value.ToString(provider: CultureInfo.InvariantCulture)}"
        );
    }

    internal static EffectFact ResolveBodyImpulse(WorldEffect.ApplyBodyImpulse effect, string ruleName, WorldRuleCompileContext context) {
        var address = context.ResolveBodyAddress(key: effect.Key, verb: "applyBodyImpulse", ruleName: ruleName);
        var magnitudeSquared = effect.BodyDirection.LengthSquared();

        if (!float.IsFinite(f: magnitudeSquared) || (magnitudeSquared <= 0f) || (MathF.Abs(x: (MathF.Sqrt(x: magnitudeSquared) - 1f)) > 0.0001f)) {
            throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: "'applyBodyImpulse' bodyDirection must be finite, non-zero, and unit length because the runtime does not normalize it");
        }

        var direction = new FixedVector3(
            X: FixedQ4816.FromDouble(value: effect.BodyDirection.X),
            Y: FixedQ4816.FromDouble(value: effect.BodyDirection.Y),
            Z: FixedQ4816.FromDouble(value: effect.BodyDirection.Z)
        );
        var duration = RuleCompiler.DurationTicksExact(seconds: effect.DurationSeconds, ruleName: ruleName, verb: "applyBodyImpulse");

        return new BodyEffect(
            key: address.Key,
            keyFrom: address.KeyFrom,
            body: new CompiledWorldBodyEffect(
                Operation: BodyMotionOp.PlanarImpulse,
                Value: RuleCompiler.ResolveFixedLiteral(value: effect.Speed, field: "speed", verb: "applyBodyImpulse", ruleName: ruleName),
                Direction: direction,
                DurationTicks: duration
            ),
            describe: $"applyBodyImpulse body:{effect.Key}"
        );
    }

    internal static EffectFact ResolveBodyDesignation(WorldEffect.DesignateBody effect, string ruleName, WorldRuleCompileContext context) {
        if (!Enum.IsDefined(value: effect.Kind)) {
            throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'designateBody' kind '{effect.Kind}' is not defined");
        }
        if (!context.Definition.TargetRegisters.Any(predicate: row => string.Equals(a: row.Name, b: effect.Register, comparisonType: StringComparison.Ordinal))) {
            throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'designateBody' names undeclared register '{effect.Register}'");
        }

        var address = context.ResolveBodyAddress(key: effect.Key, verb: "designateBody", ruleName: ruleName);
        string? targetKey = null;
        CompiledCellRef? targetKeyFrom = null;

        if (effect.Kind == WorldBodyDesignationKind.Body) {
            if (effect.TargetKey is null) {
                throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: "'designateBody' kind=body requires targetKey");
            }

            (targetKey, targetKeyFrom) = context.ResolveBodyAddress(key: effect.TargetKey, verb: "designateBody.targetKey", ruleName: ruleName);
        } else if (effect.TargetKey is not null) {
            throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: "'designateBody' kind=clear does not admit targetKey");
        }

        return new BodyEffect(
            key: address.Key,
            keyFrom: address.KeyFrom,
            body: new CompiledWorldBodyEffect(
                Operation: BodyMotionOp.Designate,
                Value: default,
                Direction: default,
                DurationTicks: 0UL,
                Register: effect.Register,
                TargetKey: targetKey,
                TargetKeyFrom: targetKeyFrom,
                Designation: effect.Kind
            ),
            describe: $"designateBody body:{effect.Key} {effect.Register} {effect.Kind}"
        );
    }

    internal static EffectFact ResolveFieldPaint(WorldEffect.PaintField effect, string ruleName, WorldRuleCompileContext context) {
        if (!Enum.IsDefined(value: effect.Operation)) {
            throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'paintField' operation '{effect.Operation}' is not defined");
        }

        var fields = (context.Definition.Fields ?? throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: "'paintField' requires a declared lattice"));

        if (!fields.Fields.Any(predicate: field => string.Equals(a: field.Name, b: effect.Field, comparisonType: StringComparison.Ordinal))) {
            throw new RuleException(refusal: RuleRefusal.StateRowUnknown, ruleName: ruleName, detail: $"'paintField' names no lattice row '{effect.Field}'");
        }
        if (
            (effect.X < 0) || (effect.X >= fields.Lattice.Width) ||
            (effect.Y < 0) || (effect.Y >= fields.Lattice.Layers) ||
            (effect.Z < 0) || (effect.Z >= fields.Lattice.Depth)
        ) {
            throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'paintField' cell ({effect.X},{effect.Y},{effect.Z}) is outside the lattice");
        }
        if ((effect.Radius < 0) || (effect.Radius > WorldRuleCapacity.MaxFieldPaintRadius)) {
            throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'paintField' radius must be in 0..{WorldRuleCapacity.MaxFieldPaintRadius}");
        }

        return new PaintFieldEffect(
            paint: new CompiledWorldFieldPaint(
                Field: effect.Field,
                X: effect.X,
                Y: effect.Y,
                Z: effect.Z,
                Value: RuleCompiler.ResolveFixedLiteral(value: effect.Value, field: "value", verb: "paintField", ruleName: ruleName),
                Operation: effect.Operation,
                Radius: effect.Radius
            ),
            describe: $"paintField {effect.Field} ({effect.X},{effect.Y},{effect.Z}) radius={effect.Radius}"
        );
    }

    internal static EffectFact ResolveUpsertHudPanel(WorldEffect.UpsertHudPanel effect, string ruleName) {
        if (effect.Panel is null || string.IsNullOrWhiteSpace(value: effect.Panel.Id)) {
            throw new RuleException(refusal: WorldRuleRefusal.HudPanelInvalid, ruleName: ruleName, detail: "'upsertHudPanel' requires a panel with a non-empty id");
        }

        return new UpsertHudPanelEffect(panel: effect.Panel, describe: $"upsertHudPanel {effect.Panel.Id}");
    }

    internal static EffectFact ResolveRemoveHudPanel(WorldEffect.RemoveHudPanel effect, string ruleName) {
        if (string.IsNullOrWhiteSpace(value: effect.Id)) {
            throw new RuleException(refusal: WorldRuleRefusal.HudPanelInvalid, ruleName: ruleName, detail: "'removeHudPanel' requires a non-empty id");
        }

        return new RemoveHudPanelEffect(id: effect.Id, describe: $"removeHudPanel {effect.Id}");
    }

    internal static EffectFact ResolveUpsertPlacement(WorldEffect.UpsertPlacement effect, string ruleName) {
        if (effect.Placement is null || string.IsNullOrWhiteSpace(value: effect.Placement.Id)) {
            throw new RuleException(refusal: WorldRuleRefusal.PlacementInvalid, ruleName: ruleName, detail: "'upsertPlacement' requires a placement with a non-empty id");
        }

        return new UpsertPlacementEffect(placement: effect.Placement, describe: $"upsertPlacement {effect.Placement.Id}");
    }

    internal static EffectFact ResolveRemovePlacement(WorldEffect.RemovePlacement effect, string ruleName) {
        if (string.IsNullOrWhiteSpace(value: effect.Id)) {
            throw new RuleException(refusal: WorldRuleRefusal.PlacementInvalid, ruleName: ruleName, detail: "'removePlacement' requires a non-empty id");
        }

        return new RemovePlacementEffect(id: effect.Id, describe: $"removePlacement {effect.Id}");
    }

    internal static EffectFact ResolvePose(WorldEffect.Pose effect, string ruleName, WorldRuleCompileContext context) {
        CompiledCellRef? keyFrom = null;
        var index = -1;

        if (RuleCompiler.TryResolveDynamicKey(key: effect.Key, ruleName: ruleName, context: context, verb: "pose", keyFieldLabel: "key", cell: out var dynamicKey)) {
            keyFrom = dynamicKey;
        } else if (!int.TryParse(s: effect.Key, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out index) || (index < 0)) {
            throw new RuleException(refusal: WorldRuleRefusal.SpatialChannelMalformed, ruleName: ruleName, detail: $"'pose' names body '{effect.Key}', which is not a non-negative integer");
        }
        if (index >= context.Definition.Population.Capacity) {
            throw new RuleException(refusal: WorldRuleRefusal.BodyIndexUnknown, ruleName: ruleName, detail: $"'pose' names body {index}, which is outside the document's declared entity-table capacity ({context.Definition.Population.Capacity})");
        }

        var bodyText = ((keyFrom is null) ? index.ToString(provider: CultureInfo.InvariantCulture) : effect.Key);
        var spawnPoint = (effect.SpawnPoint ?? string.Empty);

        if ((spawnPoint.Length > 0) == (effect.Position is not null)) {
            throw new RuleException(refusal: WorldRuleRefusal.PoseAmbiguous, ruleName: ruleName, detail: "'pose' authors exactly one of 'spawnPoint' and 'position'");
        }
        if ((effect.Position is null) && ((effect.YawDegrees != 0f) || (effect.PitchDegrees != 0f) || (effect.RollDegrees != 0f))) {
            throw new RuleException(refusal: WorldRuleRefusal.PoseAmbiguous, ruleName: ruleName, detail: "'pose' angles are only legal with a literal 'position'; a spawnPoint supplies its own yaw and zero pitch/roll");
        }
        if (effect.Position is { } position) {
            if (
                !float.IsFinite(f: position.X) || !float.IsFinite(f: position.Y) || !float.IsFinite(f: position.Z) ||
                !float.IsFinite(f: effect.YawDegrees) || !float.IsFinite(f: effect.PitchDegrees) || !float.IsFinite(f: effect.RollDegrees)
            ) {
                throw new RuleException(refusal: WorldRuleRefusal.PoseAmbiguous, ruleName: ruleName, detail: "'pose' position and angles must be finite");
            }

            const double DegreesToRadians = (Math.PI / 180.0);

            return new PoseEffect(
                spawnPoint: string.Empty,
                key: effect.Key,
                keyFrom: keyFrom,
                pose: new CompiledWorldPose(
                    Position: new FixedVector3(X: FixedQ4816.FromDouble(value: position.X), Y: FixedQ4816.FromDouble(value: position.Y), Z: FixedQ4816.FromDouble(value: position.Z)),
                    YawRadians: FixedQ4816.FromDouble(value: (effect.YawDegrees * DegreesToRadians)),
                    PitchRadians: FixedQ4816.FromDouble(value: (effect.PitchDegrees * DegreesToRadians)),
                    RollRadians: FixedQ4816.FromDouble(value: (effect.RollDegrees * DegreesToRadians))
                ),
                describe: $"pose body:{bodyText} at ({position.X}, {position.Y}, {position.Z}) yaw={effect.YawDegrees} pitch={effect.PitchDegrees} roll={effect.RollDegrees}"
            );
        }
        if (WorldDefinitionRows.FindSpawnPoint(spawnPoints: context.Definition.SpawnPoints, id: spawnPoint) is null) {
            throw new RuleException(refusal: WorldRuleRefusal.SpawnPointUnknown, ruleName: ruleName, detail: $"'pose' names spawnPoint '{spawnPoint}', which the 'spawnPoints' section does not declare");
        }

        return new PoseEffect(spawnPoint: spawnPoint, key: effect.Key, keyFrom: keyFrom, pose: null, describe: $"pose body:{bodyText} at {spawnPoint}");
    }
}
