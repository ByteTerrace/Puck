using System.Globalization;
using Puck.Maths;
using Puck.Physics.Motion;
using CompiledCellRef = Puck.State.Rules.CompiledCellRef;
using CompiledRule = Puck.State.Rules.CompiledRule;
using CompiledRuleGroup = Puck.State.Rules.CompiledRuleGroup;
using CompiledValueSource = Puck.State.Rules.CompiledValueSource;
using IRuleEffect = Puck.State.Rules.IRuleEffect;
using OperandSite = Puck.State.Rules.OperandSite;
using RuleCompiler = Puck.State.Rules.RuleCompiler;
using RuleRefusal = Puck.State.Rules.RuleRefusal;

namespace Puck.World;

/// <summary>Compiles the world's rules against the arena-addressed rule compiler: the library's compile pieces
/// composed with the world's registered vocabulary (<see cref="WorldFactsVocabulary"/>).</summary>
public static partial class WorldFactsCompiler {
    /// <summary>Compiles every rule the document declares, in document order.</summary>
    /// <param name="definition">The world.</param>
    /// <returns>The compiled rules.</returns>
    public static CompiledRule[] CompileAll(WorldDefinition definition) => CompileAll(
        context: null,
        definition: definition
    );
    /// <summary>Compiles every rule the document declares, then every rule group over them.</summary>
    /// <param name="definition">The world.</param>
    /// <param name="context">A context already built for <paramref name="definition"/>, or <see langword="null"/>
    /// to build one.</param>
    /// <returns>The compiled rules in document order, and the compiled groups in document order.</returns>
    /// <exception cref="RuleException">A rule or a group is malformed.</exception>
    public static (CompiledRule[] Rules, CompiledRuleGroup[] Groups) CompileDocument(WorldDefinition definition, WorldFactsCompileContext? context = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        context ??= Context(definition: definition);

        var rules = CompileAll(
            context: context,
            definition: definition
        );

        return (rules, RuleCompiler.CompileGroups(
            context: context,
            groups: definition.RuleGroups,
            rules: rules
        ));
    }

    private static CompiledRule[] CompileAll(WorldDefinition definition, WorldFactsCompileContext? context) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var rules = (definition.Rules ?? []);

        if (rules.Count == 0) {
            return [];
        }

        var compiled = new CompiledRule[rules.Count];

        context ??= Context(definition: definition);

        var seen = new HashSet<string>(
            capacity: rules.Count,
            comparer: StringComparer.Ordinal
        );

        for (var index = 0; (index < rules.Count); index++) {
            var rule = rules[index];

            RuleCompiler.RequireName(
                name: (rule?.Name.Value ?? string.Empty),
                seen: seen,
                subject: "rule"
            );
            compiled[index] = Compile(
                context: context,
                rule: rule!
            );
        }

        return compiled;
    }

    /// <summary>Compiles one world rule: the library's rule, plus the choice policy only a world evaluates. A rule
    /// carrying a decision holds its effects in the decision's options, so its own effect list may be empty.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <param name="context">The compile context.</param>
    /// <returns>The compiled rule.</returns>
    /// <exception cref="RuleException">The rule or its policy is malformed.</exception>
    public static CompiledRule Compile(WorldRule rule, WorldFactsCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: rule);

        return RuleCompiler.Compile(
            context: context,
            extend: compiled => {
                var decision = CompileDecision(
                    context: context,
                    rule: rule
                );

                return new CompiledWorldFactsRule(
                    decision: decision,
                    original: (compiled with {
                        Needs = context.Needs.Build(),
                    })
                );
            },
            requireEffects: (rule.Decision is null),
            rule: rule
        );
    }
    /// <summary>Creates the compile context for a document.</summary>
    /// <param name="definition">The world.</param>
    /// <returns>The context.</returns>
    public static WorldFactsCompileContext Context(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return new WorldFactsCompileContext(definition: definition);
    }
    /// <summary>Resolves the catalog ordinal of the document's <see cref="WorldIdentityFactLane"/> row, refusing by
    /// name when the document declares none.</summary>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="where">The site spelled in the refusal.</param>
    /// <returns>The lane row's catalog ordinal.</returns>
    public static int ResolveIdentityLane(string ruleName, WorldFactsCompileContext context, string where) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (context.FindRow(name: WorldIdentityFactLane.RowName) is not { Kind: CellKind.Int, IsKeyed: true }) {
            throw new RuleException(
                detail: $"{where} reads or writes an identity fact, but the document declares no keyed int row named '{WorldIdentityFactLane.RowName}' in state.world — a world carries facts only when it declares that lane",
                refusal: WorldRuleRefusal.IdentityLaneUndeclared,
                ruleName: ruleName
            );
        }

        return RuleCompiler.ResolveRowOrdinal(
            context: context,
            name: WorldIdentityFactLane.RowName
        );
    }

    internal static IRuleEffect ResolveBodyDesignation(WorldEffect.DesignateBody effect, string ruleName, WorldFactsCompileContext context) {
        if (!Enum.IsDefined(value: effect.Kind)) {
            throw new RuleException(
                detail: $"'designateBody' kind '{effect.Kind}' is not defined",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName
            );
        }
        if (!context.Definition.TargetRegisters.Any(predicate: row => string.Equals(
            a: row.Name,
            b: effect.Register,
            comparisonType: StringComparison.Ordinal
        ))) {
            throw new RuleException(
                detail: $"'designateBody' names undeclared register '{effect.Register}'",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName
            );
        }

        var address = context.ResolveBodyAddress(
            key: effect.Key,
            ruleName: ruleName,
            verb: "designateBody"
        );
        var targetKey = default(CellKey);
        CompiledCellRef? targetKeyFrom = null;

        if (effect.Kind == WorldBodyDesignationKind.Body) {
            if (effect.TargetKey is null) {
                throw new RuleException(
                    detail: "'designateBody' kind=body requires targetKey",
                    refusal: RuleRefusal.EffectKindInadmissible,
                    ruleName: ruleName
                );
            }

            var target = context.ResolveBodyAddress(
                key: effect.TargetKey,
                ruleName: ruleName,
                verb: "designateBody.targetKey"
            );

            targetKey = target.Key;
            targetKeyFrom = target.KeyFrom;
        } else if (effect.TargetKey is not null) {
            throw new RuleException(
                detail: "'designateBody' kind=clear does not admit targetKey",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName
            );
        }

        return new WorldBodyMotionEffect(
            action: new WorldBodyAction(
                Designation: effect.Kind,
                Direction: default,
                DurationTicks: 0UL,
                Operation: BodyMotionOp.Designate,
                Register: effect.Register,
                TargetKey: targetKey,
                TargetKeyFrom: targetKeyFrom,
                Value: default
            ),
            describe: $"designateBody body:{effect.Key} {effect.Register} {effect.Kind}",
            key: address.Key,
            keyFrom: address.KeyFrom
        );
    }
    internal static IRuleEffect ResolveBodyImpulse(WorldEffect.ApplyBodyImpulse effect, string ruleName, WorldFactsCompileContext context) {
        var address = context.ResolveBodyAddress(
            key: effect.Key,
            ruleName: ruleName,
            verb: "applyBodyImpulse"
        );
        var magnitudeSquared = effect.BodyDirection.LengthSquared();

        if (
            !float.IsFinite(f: magnitudeSquared) ||
            (magnitudeSquared <= 0f) ||
            (MathF.Abs(x: (MathF.Sqrt(x: magnitudeSquared) - 1f)) > 0.0001f)
        ) {
            throw new RuleException(
                detail: "'applyBodyImpulse' bodyDirection must be finite, non-zero, and unit length because the runtime does not normalize it",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName
            );
        }

        return new WorldBodyMotionEffect(
            action: new WorldBodyAction(
                Direction: new FixedVector3(
                    X: FixedQ4816.FromDouble(value: effect.BodyDirection.X),
                    Y: FixedQ4816.FromDouble(value: effect.BodyDirection.Y),
                    Z: FixedQ4816.FromDouble(value: effect.BodyDirection.Z)
                ),
                DurationTicks: RuleCompiler.DurationTicksExact(
                    ruleName: ruleName,
                    seconds: effect.DurationSeconds,
                    verb: "applyBodyImpulse"
                ),
                Operation: BodyMotionOp.PlanarImpulse,
                Value: RuleCompiler.ResolveFixedLiteral(
                    field: "speed",
                    ruleName: ruleName,
                    value: effect.Speed,
                    verb: "applyBodyImpulse"
                )
            ),
            describe: $"applyBodyImpulse body:{effect.Key}",
            key: address.Key,
            keyFrom: address.KeyFrom
        );
    }
    internal static IRuleEffect ResolveBodyVerticalVelocity(string key, decimal value, BodyMotionOp operation, string verb, string ruleName, WorldFactsCompileContext context) {
        var address = context.ResolveBodyAddress(
            key: key,
            ruleName: ruleName,
            verb: verb
        );

        return new WorldBodyMotionEffect(
            action: new WorldBodyAction(
                Direction: default,
                DurationTicks: 0UL,
                Operation: operation,
                Value: RuleCompiler.ResolveFixedLiteral(
                    field: "value",
                    ruleName: ruleName,
                    value: value,
                    verb: verb
                )
            ),
            describe: $"{verb} body:{key} {value.ToString(provider: CultureInfo.InvariantCulture)}",
            key: address.Key,
            keyFrom: address.KeyFrom
        );
    }
    internal static IRuleEffect ResolveCue(WorldEffect.EmitCue effect, string ruleName, WorldFactsCompileContext context) {
        if (!WorldGameplayCue.IsValidName(candidate: effect.Name)) {
            throw new RuleException(
                detail: $"'emitCue' name must contain 1..{WorldRuleCapacity.MaxCueNameLength} ASCII letters, digits, dots, hyphens, or underscores, and begin and end with a letter or digit",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName
            );
        }
        if ((effect.Payload?.Length ?? 0) > WorldRuleCapacity.MaxCuePayloadLength) {
            throw new RuleException(
                detail: $"'emitCue' payload exceeds {WorldRuleCapacity.MaxCuePayloadLength} UTF-16 code units",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName
            );
        }

        var key = default(CellKey);
        CompiledCellRef? keyFrom = null;

        if (effect.Key is { } bodyKey) {
            var address = context.ResolveBodyAddress(
                key: bodyKey,
                ruleName: ruleName,
                verb: "emitCue"
            );

            key = address.Key;
            keyFrom = address.KeyFrom;
        }

        return new WorldCueEffect(
            cue: effect.Name,
            describe: $"emitCue {effect.Name}",
            key: key,
            keyFrom: keyFrom,
            payload: effect.Payload
        );
    }
    internal static IRuleEffect ResolveFieldPaint(WorldEffect.PaintField effect, string ruleName, WorldFactsCompileContext context) {
        if (!Enum.IsDefined(value: effect.Operation)) {
            throw new RuleException(
                detail: $"'paintField' operation '{effect.Operation}' is not defined",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName
            );
        }

        var fields = (context.Definition.Fields
            ?? throw new RuleException(
            detail: "'paintField' requires a declared lattice",
            refusal: RuleRefusal.EffectKindInadmissible,
            ruleName: ruleName
        ));

        if (!fields.Fields.Any(predicate: field => string.Equals(
            a: field.Name,
            b: effect.Field,
            comparisonType: StringComparison.Ordinal
        ))) {
            throw new RuleException(
                detail: $"'paintField' names no lattice row '{effect.Field}'",
                refusal: RuleRefusal.StateRowUnknown,
                ruleName: ruleName
            );
        }
        if (
            (effect.X < 0) ||
            (effect.X >= fields.Lattice.Width) ||
            (effect.Y < 0) ||
            (effect.Y >= fields.Lattice.Layers) ||
            (effect.Z < 0) ||
            (effect.Z >= fields.Lattice.Depth)
        ) {
            throw new RuleException(
                detail: $"'paintField' cell ({effect.X},{effect.Y},{effect.Z}) is outside the lattice",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName
            );
        }
        if (
            (effect.Radius < 0) ||
            (effect.Radius > WorldRuleCapacity.MaxFieldPaintRadius)
        ) {
            throw new RuleException(
                detail: $"'paintField' radius must be in 0..{WorldRuleCapacity.MaxFieldPaintRadius}",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName
            );
        }

        return new WorldPaintFieldEffect(
            describe: $"paintField {effect.Field} ({effect.X},{effect.Y},{effect.Z}) radius={effect.Radius}",
            paint: new CompiledWorldFieldPaint(
                Field: effect.Field,
                Operation: effect.Operation,
                Radius: effect.Radius,
                Value: RuleCompiler.ResolveFixedLiteral(
                    field: "value",
                    ruleName: ruleName,
                    value: effect.Value,
                    verb: "paintField"
                ),
                X: effect.X,
                Y: effect.Y,
                Z: effect.Z
            )
        );
    }
    internal static IRuleEffect ResolveIdentityFact(WorldEffect.SetIdentityFact effect, string ruleName, WorldFactsCompileContext context) {
        const string Verb = "setIdentityFact";

        var address = context.ResolveBodyAddress(
            key: effect.Key,
            ruleName: ruleName,
            verb: Verb
        );

        if (!CellName.TryParse(
            candidate: effect.Fact,
            name: out var fact,
            reason: out var factReason
        )) {
            throw new RuleException(
                detail: $"'{Verb}' fact '{effect.Fact}' {factReason}",
                refusal: WorldRuleRefusal.IdentityFactMalformed,
                ruleName: ruleName
            );
        }

        var laneOrdinal = ResolveIdentityLane(
            context: context,
            ruleName: ruleName,
            where: $"'{Verb}'"
        );

        if ((effect.Value is not null) == (effect.Expression is not null)) {
            throw new RuleException(
                detail: $"'{Verb}' must name exactly one of 'value' or 'expression'",
                refusal: RuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName
            );
        }
        if (effect.Value is { } literal) {
            return new WorldIdentityFactEffect(
                describe: $"{Verb} body:{effect.Key}.{fact} = {literal.ToString(provider: CultureInfo.InvariantCulture)}",
                fact: fact,
                key: address.Key,
                keyFrom: address.KeyFrom,
                laneOrdinal: laneOrdinal,
                source: CompiledValueSource.Constant(rawValue: RuleCompiler.LiteralToRaw(
                    kind: CellKind.Int,
                    literal: literal,
                    ruleName: ruleName,
                    verb: Verb
                ))
            );
        }

        var program = RuleCompiler.CompileExpression(
            context: context,
            expression: effect.Expression,
            kind: CellKind.Int,
            ruleName: ruleName,
            verb: Verb
        );

        return new WorldIdentityFactEffect(
            describe: $"{Verb} body:{effect.Key}.{fact} := expression[{program.Length}]",
            fact: fact,
            key: address.Key,
            keyFrom: address.KeyFrom,
            laneOrdinal: laneOrdinal,
            source: CompiledValueSource.FromExpression(expression: program)
        );
    }
    internal static IRuleEffect ResolvePose(WorldEffect.Pose effect, string ruleName, WorldFactsCompileContext context) {
        CompiledCellRef? keyFrom = null;
        var index = -1;

        if (RuleCompiler.TryResolveDynamicKey(
            cell: out var dynamicKey,
            context: context,
            key: effect.Key,
            keyFieldLabel: "key",
            ruleName: ruleName,
            verb: "pose"
        )) {
            keyFrom = dynamicKey;
        } else if (
            !int.TryParse(
            s: effect.Key,
            style: NumberStyles.Integer,
            provider: CultureInfo.InvariantCulture,
            result: out index
        ) ||
            (index < 0)
        ) {
            throw new RuleException(
                detail: $"'pose' names body '{effect.Key}', which is not a non-negative integer",
                refusal: WorldRuleRefusal.SpatialChannelMalformed,
                ruleName: ruleName
            );
        }
        if (index >= context.Definition.Population.Capacity) {
            throw new RuleException(
                detail: $"'pose' names body {index}, which is outside the document's declared entity-table capacity ({context.Definition.Population.Capacity})",
                refusal: WorldRuleRefusal.BodyIndexUnknown,
                ruleName: ruleName
            );
        }

        var bodyText = ((keyFrom is null)
            ? index.ToString(provider: CultureInfo.InvariantCulture)
            : effect.Key
        );
        var spawnPoint = (effect.SpawnPoint ?? string.Empty);

        if ((spawnPoint.Length > 0) == (effect.Position is not null)) {
            throw new RuleException(
                detail: "'pose' authors exactly one of 'spawnPoint' and 'position'",
                refusal: WorldRuleRefusal.PoseAmbiguous,
                ruleName: ruleName
            );
        }
        if (
            (effect.Position is null) &&
            ((effect.YawDegrees != 0f) || (effect.PitchDegrees != 0f) || (effect.RollDegrees != 0f))
        ) {
            throw new RuleException(
                detail: "'pose' angles are only legal with a literal 'position'; a spawnPoint supplies its own yaw and zero pitch/roll",
                refusal: WorldRuleRefusal.PoseAmbiguous,
                ruleName: ruleName
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
                throw new RuleException(
                    detail: "'pose' position and angles must be finite",
                    refusal: WorldRuleRefusal.PoseAmbiguous,
                    ruleName: ruleName
                );
            }

            const double DegreesToRadians = (Math.PI / 180.0);

            return new WorldPoseEffect(
                describe: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"pose body:{bodyText} at ({position.X}, {position.Y}, {position.Z}) yaw={effect.YawDegrees} pitch={effect.PitchDegrees} roll={effect.RollDegrees}"
                ),
                index: index,
                keyFrom: keyFrom,
                pose: new CompiledWorldPose(
                    PitchRadians: FixedQ4816.FromDouble(value: (effect.PitchDegrees * DegreesToRadians)),
                    Position: new FixedVector3(
                        X: FixedQ4816.FromDouble(value: position.X),
                        Y: FixedQ4816.FromDouble(value: position.Y),
                        Z: FixedQ4816.FromDouble(value: position.Z)
                    ),
                    RollRadians: FixedQ4816.FromDouble(value: (effect.RollDegrees * DegreesToRadians)),
                    YawRadians: FixedQ4816.FromDouble(value: (effect.YawDegrees * DegreesToRadians))
                ),
                spawnPoint: string.Empty
            );
        }
        if (WorldDefinitionRows.FindSpawnPoint(
            id: spawnPoint,
            spawnPoints: context.Definition.SpawnPoints
        ) is null) {
            throw new RuleException(
                detail: $"'pose' names spawnPoint '{spawnPoint}', which the 'spawnPoints' section does not declare",
                refusal: WorldRuleRefusal.SpawnPointUnknown,
                ruleName: ruleName
            );
        }

        return new WorldPoseEffect(
            describe: $"pose body:{bodyText} at {spawnPoint}",
            index: index,
            keyFrom: keyFrom,
            pose: null,
            spawnPoint: spawnPoint
        );
    }
    internal static IRuleEffect ResolveRemoveHudPanel(WorldEffect.RemoveHudPanel effect, string ruleName) {
        if (string.IsNullOrWhiteSpace(value: effect.Id)) {
            throw new RuleException(
                detail: "'removeHudPanel' requires a non-empty id",
                refusal: WorldRuleRefusal.HudPanelInvalid,
                ruleName: ruleName
            );
        }

        return new WorldDocumentEffect(
            cost: 4_096L,
            describe: $"removeHudPanel {effect.Id}",
            id: effect.Id,
            panel: null,
            placement: null,
            write: WorldDocumentWrite.RemoveHudPanel
        );
    }
    internal static IRuleEffect ResolveRemovePlacement(WorldEffect.RemovePlacement effect, string ruleName, WorldFactsCompileContext context) {
        if (string.IsNullOrWhiteSpace(value: effect.Id)) {
            throw new RuleException(
                detail: "'removePlacement' requires a non-empty id",
                refusal: WorldRuleRefusal.PlacementInvalid,
                ruleName: ruleName
            );
        }

        var definition = context.Definition;

        return new WorldDocumentEffect(
            cost: ((WorldDefinitionRows.FindPlacement(
                id: effect.Id,
                placements: definition.Placements
            ) is { } placement)
                ? WorldPlacementEffectCost.Of(
                    definition: definition,
                    placement: placement
                )
                : WorldPlacementEffectCost.DocumentCost),
            describe: $"removePlacement {effect.Id}",
            id: effect.Id,
            panel: null,
            placement: null,
            write: WorldDocumentWrite.RemovePlacement
        );
    }
    internal static IRuleEffect ResolveRigidImpulse(WorldEffect.ApplyRigidImpulse effect, string ruleName, WorldFactsCompileContext context) {
        var target = context.ResolveBodyRef(
            channel: "applyRigidImpulse.key",
            ruleName: ruleName,
            start: 0,
            tokens: effect.Key.Split(separator: ':')
        );
        var heading = context.ResolveBodyRef(
            channel: "applyRigidImpulse.headingKey",
            ruleName: ruleName,
            start: 0,
            tokens: effect.HeadingKey.Split(separator: ':')
        );
        var magnitude = RuleCompiler.ResolveOperand(
            context: context,
            key: effect.MagnitudeKey,
            name: effect.MagnitudeState,
            site: new OperandSite(
                FieldLabel: "magnitudeState",
                KeyFieldLabel: "magnitudeKey",
                RuleName: ruleName,
                Verb: "applyRigidImpulse"
            )
        );

        if (magnitude.ValueKind != CellKind.Fixed) {
            throw new RuleException(
                detail: $"'applyRigidImpulse' magnitudeState '{effect.MagnitudeState}' is kind={StateSpelling.Kind(kind: magnitude.ValueKind)} — an impulse magnitude requires a kind=Fixed row",
                refusal: RuleRefusal.EffectSourceKindMismatch,
                ruleName: ruleName
            );
        }

        return new WorldRigidImpulseEffect(
            describe: $"applyRigidImpulse {effect.Key} <- heading:{effect.HeadingKey} magnitude:{effect.MagnitudeState}",
            heading: context.Ordinals(body: in heading),
            magnitude: magnitude.Operand,
            target: context.Ordinals(body: in target)
        );
    }
    internal static IRuleEffect ResolveUpsertHudPanel(WorldEffect.UpsertHudPanel effect, string ruleName) {
        if (
            (effect.Panel is null) ||
            string.IsNullOrWhiteSpace(value: effect.Panel.Id)
        ) {
            throw new RuleException(
                detail: "'upsertHudPanel' requires a panel with a non-empty id",
                refusal: WorldRuleRefusal.HudPanelInvalid,
                ruleName: ruleName
            );
        }

        return new WorldDocumentEffect(
            cost: 4_096L,
            describe: $"upsertHudPanel {effect.Panel.Id}",
            id: effect.Panel.Id,
            panel: effect.Panel,
            placement: null,
            write: WorldDocumentWrite.UpsertHudPanel
        );
    }
    internal static IRuleEffect ResolveUpsertPlacement(WorldEffect.UpsertPlacement effect, string ruleName, WorldFactsCompileContext context) {
        if (
            (effect.Placement is null) ||
            string.IsNullOrWhiteSpace(value: effect.Placement.Id)
        ) {
            throw new RuleException(
                detail: "'upsertPlacement' requires a placement with a non-empty id",
                refusal: WorldRuleRefusal.PlacementInvalid,
                ruleName: ruleName
            );
        }

        return new WorldDocumentEffect(
            cost: WorldPlacementEffectCost.Of(
                definition: context.Definition,
                placement: effect.Placement
            ),
            describe: $"upsertPlacement {effect.Placement.Id}",
            id: effect.Placement.Id,
            panel: null,
            placement: effect.Placement,
            write: WorldDocumentWrite.UpsertPlacement
        );
    }
}
