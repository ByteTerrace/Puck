using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World;

public static partial class WorldRuleCompiler {
    private static (string Key, CompiledCellRef? KeyFrom) ResolveBodyAddress(string key, string verb, string ruleName, WorldDefinition definition) {
        if (TryResolveDynamicKey(
            definition: definition,
            key: key,
            ruleName: ruleName,
            verb: verb,
            keyFieldLabel: "key",
            cell: out var dynamicKey
        )) {
            return (key, dynamicKey);
        }

        if (
            !int.TryParse(s: key, style: System.Globalization.NumberStyles.Integer, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var index) ||
            (index < 0) ||
            (index >= definition.Population.Capacity)
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.BodyIndexUnknown,
                ruleName: ruleName,
                detail: $"'{verb}' key '{key}' does not name a body inside capacity {definition.Population.Capacity}"
            );
        }

        return (key, null);
    }
    private static CompiledWorldEffect ResolveRemoveStateCell(ActionEffect.RemoveStateCell effect, string ruleName, WorldDefinition definition) {
        var row = (WorldDefinitionRows.FindStateRow(rows: definition.State, name: effect.State)
            ?? throw new WorldRuleException(refusal: WorldRuleRefusal.StateRowUnknown, ruleName: ruleName, detail: $"'removeStateCell' names no state row '{effect.State}'"));
        var keyFrom = default(CompiledCellRef?);
        string key;

        if (TryResolveDynamicKey(definition: definition, key: effect.Key, ruleName: ruleName, verb: "removeStateCell", keyFieldLabel: "key", cell: out var dynamicKey)) {
            if (!row.IsKeyed) {
                throw new WorldRuleException(refusal: WorldRuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'removeStateCell' uses a dynamic key against non-keyed row '{effect.State}'");
            }
            key = effect.Key!;
            keyFrom = dynamicKey;
        } else {
            key = ResolveKey(key: effect.Key, keyFieldLabel: "key", row: row, ruleName: ruleName, verb: "removeStateCell");
        }

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.RemoveStateCell,
            Row: effect.State,
            Key: key,
            Write: default,
            RawValue: 0L,
            Generator: null,
            Describe: $"removeStateCell {effect.State}.{key}",
            KeyFrom: keyFrom
        );
    }
    private static CompiledWorldEffect ResolveScheduleState(ActionEffect.ScheduleState effect, string ruleName, WorldDefinition definition) {
        var row = (WorldDefinitionRows.FindStateRow(rows: definition.State, name: effect.State)
            ?? throw new WorldRuleException(refusal: WorldRuleRefusal.StateRowUnknown, ruleName: ruleName, detail: $"'scheduleState' names no state row '{effect.State}'"));

        if (row.Kind != CellKind.Int) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'scheduleState' requires a kind=int row; '{effect.State}' is {DescribeCellKind(kind: row.Kind)}");
        }

        var ticks = DurationSimulationTicks(
            seconds: effect.DelaySeconds,
            ratePerSecond: definition.SimulationRateHz,
            ruleName: ruleName,
            verb: "scheduleState"
        );
        var keyFrom = default(CompiledCellRef?);
        string key;

        if (TryResolveDynamicKey(definition: definition, key: effect.Key, ruleName: ruleName, verb: "scheduleState", keyFieldLabel: "key", cell: out var dynamicKey)) {
            if (!row.IsKeyed) {
                throw new WorldRuleException(refusal: WorldRuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'scheduleState' uses a dynamic key against non-keyed row '{effect.State}'");
            }
            key = effect.Key!;
            keyFrom = dynamicKey;
        } else {
            key = ResolveKey(key: effect.Key, keyFieldLabel: "key", row: row, ruleName: ruleName, verb: "scheduleState");
        }

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.ScheduleState,
            Row: effect.State,
            Key: key,
            Write: WorldDocumentWriteKind.Set,
            RawValue: checked((long)ticks),
            Generator: null,
            Describe: $"scheduleState {effect.State}.{key} after {effect.DelaySeconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}s",
            KeyFrom: keyFrom
        );
    }
    private static long DurationSimulationTicks(decimal seconds, int ratePerSecond, string ruleName, string verb) {
        if ((seconds < decimal.Zero) || (ratePerSecond <= 0)) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.EffectKindInadmissible,
                ruleName: ruleName,
                detail: $"'{verb}' requires a non-negative delay and a positive simulation rate"
            );
        }

        var maximumSeconds = (((decimal)long.MaxValue) / ratePerSecond);
        if (seconds > maximumSeconds) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.DurationEngineTicksOutOfRange,
                ruleName: ruleName,
                detail: $"'{verb}' delay exceeds the signed 64-bit simulation-tick carrier"
            );
        }

        var ticks = decimal.Ceiling(d: (seconds * ratePerSecond));
        return decimal.ToInt64(d: ticks);
    }
    private static ulong DurationTicksExact(decimal seconds, string ruleName, string verb) {
        if (
            (seconds < decimal.Zero) ||
            !FixedTickConversion.TryDurationEngineTicksExact(seconds: seconds, ticks: out var ticks)
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.DurationNotExactEngineTicks,
                ruleName: ruleName,
                detail: $"'{verb}' delay {seconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} seconds is not a non-negative exact whole-engine-tick duration"
            );
        }

        return ticks;
    }
    private static CompiledWorldEffect ResolveTransaction(ActionEffect.Transaction effect, string ruleName, WorldDefinition definition) {
        if (effect.Effects is not { Count: > 0 } || effect.Effects.Count > WorldRuleCapacity.MaxTransactionEffects) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"a transaction must carry 1..{WorldRuleCapacity.MaxTransactionEffects} effects");
        }
        if ((effect.OnFailure?.Count ?? 0) > WorldRuleCapacity.MaxTransactionEffects) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"a transaction failure branch exceeds {WorldRuleCapacity.MaxTransactionEffects} effects");
        }

        var effects = CompileTransactionSteps(steps: effect.Effects, ruleName: ruleName, definition: definition);
        var failure = ((effect.OnFailure is { Count: > 0 })
            ? CompileTransactionSteps(steps: effect.OnFailure, ruleName: ruleName, definition: definition)
            : []
        );

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.Transaction,
            Row: string.Empty,
            Key: string.Empty,
            Write: default,
            RawValue: 0L,
            Generator: null,
            Describe: $"transaction {effects.Length} effect(s), failure {failure.Length}",
            Effects: effects,
            OnFailure: failure
        );
    }
    private static CompiledWorldEffect[] CompileTransactionSteps(IReadOnlyList<WorldTransactionStep> steps, string ruleName, WorldDefinition definition) {
        var compiled = new CompiledWorldEffect[steps.Count];
        var placementSuffix = false;

        for (var index = 0; index < steps.Count; index++) {
            compiled[index] = CompileTransactionStep(step: steps[index], ruleName: ruleName, definition: definition);
            var placement = compiled[index].Kind is WorldRuleEffectKind.UpsertPlacement or WorldRuleEffectKind.RemovePlacement;
            if (placementSuffix && !placement) {
                throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: "placement steps must form the transaction's final suffix because they can rebuild the active population");
            }
            placementSuffix |= placement;
        }

        return compiled;
    }
    private static CompiledWorldEffect CompileTransactionStep(WorldTransactionStep? step, string ruleName, WorldDefinition definition) {
        ActionEffect effect = step switch {
            WorldTransactionStep.TransformStateStep transform => new ActionEffect.TransformState(transform.Transform),
            WorldTransactionStep.SetCell set => new ActionEffect.SetState(State: set.State, Key: set.Key, Value: set.Value, FromState: set.FromState, FromKey: set.FromKey, ValueSeconds: set.ValueSeconds, Expression: set.Expression),
            WorldTransactionStep.AddCell add => new ActionEffect.AddState(State: add.State, Key: add.Key, Value: add.Value, FromState: add.FromState, FromKey: add.FromKey, ValueSeconds: add.ValueSeconds, Expression: add.Expression),
            WorldTransactionStep.CountdownCell countdown => new ActionEffect.CountdownState(State: countdown.State, Key: countdown.Key),
            WorldTransactionStep.RemoveCell remove => new ActionEffect.RemoveStateCell(State: remove.State, Key: remove.Key),
            WorldTransactionStep.ScheduleCell schedule => new ActionEffect.ScheduleState(State: schedule.State, DelaySeconds: schedule.DelaySeconds, Key: schedule.Key),
            WorldTransactionStep.GenerateStep generate => new ActionEffect.Generate(Row: generate.Row),
            WorldTransactionStep.UpsertHudPanelStep upsertHud => new ActionEffect.UpsertHudPanel(Panel: upsertHud.Panel),
            WorldTransactionStep.RemoveHudPanelStep removeHud => new ActionEffect.RemoveHudPanel(Id: removeHud.Id),
            WorldTransactionStep.UpsertPlacementStep upsertPlacement => new ActionEffect.UpsertPlacement(Placement: upsertPlacement.Placement),
            WorldTransactionStep.RemovePlacementStep removePlacement => new ActionEffect.RemovePlacement(Id: removePlacement.Id),
            WorldTransactionStep.PoseStep pose => new ActionEffect.Pose(Key: pose.Key, SpawnPoint: pose.SpawnPoint, Position: pose.Position, YawDegrees: pose.YawDegrees, PitchDegrees: pose.PitchDegrees, RollDegrees: pose.RollDegrees),
            WorldTransactionStep.EmitCueStep cue => new ActionEffect.EmitCue(Name: cue.Name, Payload: cue.Payload, Key: cue.Key),
            WorldTransactionStep.SetBodyVerticalVelocityStep body => new ActionEffect.SetBodyVerticalVelocity(Key: body.Key, Velocity: body.Velocity),
            WorldTransactionStep.ScaleBodyVerticalVelocityStep body => new ActionEffect.ScaleBodyVerticalVelocity(Key: body.Key, Factor: body.Factor),
            WorldTransactionStep.ApplyBodyImpulseStep impulse => new ActionEffect.ApplyBodyImpulse(Key: impulse.Key, BodyDirection: impulse.BodyDirection, Speed: impulse.Speed, DurationSeconds: impulse.DurationSeconds),
            WorldTransactionStep.DesignateBodyStep designate => new ActionEffect.DesignateBody(Key: designate.Key, Register: designate.Register, Kind: designate.Kind, TargetKey: designate.TargetKey),
            WorldTransactionStep.PaintFieldStep paint => new ActionEffect.PaintField(Field: paint.Field, X: paint.X, Y: paint.Y, Z: paint.Z, Value: paint.Value, Operation: paint.Operation, Radius: paint.Radius),
            null => throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: "transaction contains a null step"),
            _ => throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"transaction step kind '{step.GetType().Name}' is not supported"),
        };

        return CompileEffect(effect: effect, ruleName: ruleName, definition: definition);
    }
    private static CompiledWorldEffect ResolveCue(ActionEffect.EmitCue effect, string ruleName, WorldDefinition definition) {
        if (!WorldGameplayCue.IsValidName(candidate: effect.Name)) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'emitCue' name must contain 1..{WorldRuleCapacity.MaxCueNameLength} ASCII letters, digits, dots, hyphens, or underscores, and begin and end with a letter or digit");
        }
        if ((effect.Payload?.Length ?? 0) > WorldRuleCapacity.MaxCuePayloadLength) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'emitCue' payload exceeds {WorldRuleCapacity.MaxCuePayloadLength} UTF-16 code units");
        }

        var key = string.Empty;
        CompiledCellRef? keyFrom = null;
        if (effect.Key is { } bodyKey) {
            (key, keyFrom) = ResolveBodyAddress(key: bodyKey, verb: "emitCue", ruleName: ruleName, definition: definition);
        }

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.EmitCue,
            Row: string.Empty,
            Key: key,
            Write: default,
            RawValue: 0L,
            Generator: null,
            Describe: $"emitCue {effect.Name}",
            KeyFrom: keyFrom,
            Cue: effect.Name,
            Payload: effect.Payload
        );
    }
    private static CompiledWorldEffect ResolveBodyVerticalVelocity(string key, decimal value, BodyMotionOp operation, string verb, string ruleName, WorldDefinition definition) {
        var address = ResolveBodyAddress(key: key, verb: verb, ruleName: ruleName, definition: definition);
        var fixedValue = ResolveFixedLiteral(value: value, field: "value", verb: verb, ruleName: ruleName);

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.Body,
            Row: string.Empty,
            Key: address.Key,
            Write: default,
            RawValue: 0L,
            Generator: null,
            Describe: $"{verb} body:{key} {value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}",
            KeyFrom: address.KeyFrom,
            Body: new CompiledWorldBodyEffect(Operation: operation, Value: fixedValue, Direction: default, DurationTicks: 0UL)
        );
    }
    private static CompiledWorldEffect ResolveBodyImpulse(ActionEffect.ApplyBodyImpulse effect, string ruleName, WorldDefinition definition) {
        var address = ResolveBodyAddress(key: effect.Key, verb: "applyBodyImpulse", ruleName: ruleName, definition: definition);
        var magnitudeSquared = effect.BodyDirection.LengthSquared();

        if (
            !float.IsFinite(magnitudeSquared) ||
            (magnitudeSquared <= 0f) ||
            (MathF.Abs(x: (MathF.Sqrt(x: magnitudeSquared) - 1f)) > 0.0001f)
        ) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: "'applyBodyImpulse' bodyDirection must be finite, non-zero, and unit length because the runtime does not normalize it");
        }

        var direction = new FixedVector3(
            X: FixedQ4816.FromDouble(value: effect.BodyDirection.X),
            Y: FixedQ4816.FromDouble(value: effect.BodyDirection.Y),
            Z: FixedQ4816.FromDouble(value: effect.BodyDirection.Z)
        );
        var duration = DurationTicksExact(seconds: effect.DurationSeconds, ruleName: ruleName, verb: "applyBodyImpulse");

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.Body,
            Row: string.Empty,
            Key: address.Key,
            Write: default,
            RawValue: 0L,
            Generator: null,
            Describe: $"applyBodyImpulse body:{effect.Key}",
            KeyFrom: address.KeyFrom,
            Body: new CompiledWorldBodyEffect(
                Operation: BodyMotionOp.PlanarImpulse,
                Value: ResolveFixedLiteral(value: effect.Speed, field: "speed", verb: "applyBodyImpulse", ruleName: ruleName),
                Direction: direction,
                DurationTicks: duration
            )
        );
    }
    private static CompiledWorldEffect ResolveBodyDesignation(ActionEffect.DesignateBody effect, string ruleName, WorldDefinition definition) {
        if (!Enum.IsDefined(value: effect.Kind)) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'designateBody' kind '{effect.Kind}' is not defined");
        }
        if (!definition.TargetRegisters.Any(predicate: row => string.Equals(a: row.Name, b: effect.Register, comparisonType: StringComparison.Ordinal))) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'designateBody' names undeclared register '{effect.Register}'");
        }
        var address = ResolveBodyAddress(key: effect.Key, verb: "designateBody", ruleName: ruleName, definition: definition);
        string? targetKey = null;
        CompiledCellRef? targetKeyFrom = null;

        if (effect.Kind == WorldBodyDesignationKind.Body) {
            if (effect.TargetKey is null) {
                throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: "'designateBody' kind=body requires targetKey");
            }
            (targetKey, targetKeyFrom) = ResolveBodyAddress(key: effect.TargetKey, verb: "designateBody.targetKey", ruleName: ruleName, definition: definition);
        } else if (effect.TargetKey is not null) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: "'designateBody' kind=clear does not admit targetKey");
        }

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.Body,
            Row: string.Empty,
            Key: address.Key,
            Write: default,
            RawValue: 0L,
            Generator: null,
            Describe: $"designateBody body:{effect.Key} {effect.Register} {effect.Kind}",
            KeyFrom: address.KeyFrom,
            Body: new CompiledWorldBodyEffect(
                Operation: BodyMotionOp.Designate,
                Value: default,
                Direction: default,
                DurationTicks: 0UL,
                Register: effect.Register,
                TargetKey: targetKey,
                TargetKeyFrom: targetKeyFrom,
                Designation: effect.Kind
            )
        );
    }
    private static CompiledWorldEffect ResolveFieldPaint(ActionEffect.PaintField effect, string ruleName, WorldDefinition definition) {
        if (!Enum.IsDefined(value: effect.Operation)) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'paintField' operation '{effect.Operation}' is not defined");
        }
        var fields = (definition.Fields ?? throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: "'paintField' requires a declared lattice"));
        if (!fields.Fields.Any(predicate: field => string.Equals(a: field.Name, b: effect.Field, comparisonType: StringComparison.Ordinal))) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.StateRowUnknown, ruleName: ruleName, detail: $"'paintField' names no lattice row '{effect.Field}'");
        }
        if (
            (effect.X < 0) || (effect.X >= fields.Lattice.Width) ||
            (effect.Y < 0) || (effect.Y >= fields.Lattice.Layers) ||
            (effect.Z < 0) || (effect.Z >= fields.Lattice.Depth)
        ) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'paintField' cell ({effect.X},{effect.Y},{effect.Z}) is outside the lattice");
        }
        if ((effect.Radius < 0) || (effect.Radius > WorldRuleCapacity.MaxFieldPaintRadius)) {
            throw new WorldRuleException(refusal: WorldRuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'paintField' radius must be in 0..{WorldRuleCapacity.MaxFieldPaintRadius}");
        }

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.PaintField,
            Row: effect.Field,
            Key: string.Empty,
            Write: default,
            RawValue: 0L,
            Generator: null,
            Describe: $"paintField {effect.Field} ({effect.X},{effect.Y},{effect.Z}) radius={effect.Radius}",
            Paint: new CompiledWorldFieldPaint(
                Field: effect.Field,
                X: effect.X,
                Y: effect.Y,
                Z: effect.Z,
                Value: ResolveFixedLiteral(value: effect.Value, field: "value", verb: "paintField", ruleName: ruleName),
                Operation: effect.Operation,
                Radius: effect.Radius
            )
        );
    }
    private static FixedQ4816 ResolveFixedLiteral(decimal value, string field, string verb, string ruleName) {
        if (WorldStateNumericLiteral.TryToFixed(value: value, result: out var result)) {
            return result;
        }

        throw new WorldRuleException(
            refusal: WorldRuleRefusal.EffectKindInadmissible,
            ruleName: ruleName,
            detail: $"'{verb}' {field} '{value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}' is outside the Q48.16 range"
        );
    }
    private static CompiledWorldEffect ResolveWrite(string rowName, string? key, ActionTarget target, WorldDocumentWriteKind write, decimal? value, string? fromState, string? fromKey, decimal? valueSeconds, string? text, WorldValueExpression? expression, string ruleName, WorldDefinition definition, string verb) {
        if (target != ActionTarget.Self) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.TargetInadmissible,
                ruleName: ruleName,
                detail: $"'{verb}' carries target '{target}' — a world rule has no entity to address, so a target is refused rather than parsed and discarded"
            );
        }

        var row = (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: rowName
        )
            ?? throw new WorldRuleException(
            refusal: WorldRuleRefusal.StateRowUnknown,
            ruleName: ruleName,
            detail: $"'{verb}' names no state row '{rowName}' — declare it with world.row.set state <json> first"
        ));

        var hasText = (text is not null);
        var isTextRow = (row.Kind == CellKind.Text);

        if (
            (hasText && !isTextRow) ||
            (isTextRow && !hasText && (fromState is null)) ||
            (isTextRow && (expression is not null)) ||
            (isTextRow && (write == WorldDocumentWriteKind.Add))
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: (hasText
                    ? $"state row '{rowName}' is kind={DescribeCellKind(kind: row.Kind)} — '{verb}' 'text' writes a kind=text row"
                    : $"state row '{rowName}' is kind=text — '{verb}' writes it through 'text' or a text 'fromState' copy, never arithmetic"
                )
            );
        }

        CompiledCellRef? destinationKeyFrom = null;
        string resolvedKey;

        if (TryResolveDynamicKey(
            cell: out var dynamicDestination,
            definition: definition,
            key: key,
            keyFieldLabel: "key",
            ruleName: ruleName,
            verb: verb
        )) {
            if (!row.IsKeyed) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName,
                    detail: $"'{verb}' key '{key}' addresses a cell by indirection, but row '{rowName}' is not keyed"
                );
            }

            destinationKeyFrom = dynamicDestination;
            resolvedKey = key!;
        } else {
            resolvedKey = ResolveKey(
                key: key,
                keyFieldLabel: "key",
                row: row,
                ruleName: ruleName,
                verb: verb
            );
        }

        var hasValue = (value is not null);
        var hasFrom = (fromState is not null);
        var hasValueSeconds = (valueSeconds is not null);
        var hasExpression = (expression is not null);

        if (hasText) {
            if (
                hasValue ||
                hasFrom ||
                hasValueSeconds ||
                hasExpression ||
                (fromKey is not null)
            ) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.EffectSourceAmbiguous,
                    ruleName: ruleName,
                    detail: $"'{verb}' names 'text' beside 'value'/'valueSeconds'/'fromState' — a text write has exactly one spelling"
                );
            }

            return new CompiledWorldEffect(
                Kind: WorldRuleEffectKind.Write,
                Row: rowName,
                Key: resolvedKey,
                Write: write,
                RawValue: 0L,
                Generator: null,
                Describe: $"{verb} {rowName}.{resolvedKey} = \"{text}\"",
                Text: text,
                KeyFrom: destinationKeyFrom
            );
        }

        if (
            (fromKey is not null) &&
            (fromState is null)
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName,
                detail: $"'{verb}' names 'fromKey' without 'fromState' — a copy source key addresses a cell inside a source row, which must be named"
            );
        }

        var spellingCount = ((((hasValue
            ? 1
            : 0) + (hasFrom
            ? 1
            : 0)) + (hasValueSeconds
            ? 1
            : 0)) + (hasExpression
            ? 1
            : 0));

        if (spellingCount != 1) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName,
                detail: $"'{verb}' must name EXACTLY ONE of 'value', 'valueSeconds', 'fromState', or 'expression' — named {spellingCount}"
            );
        }

        if (hasValueSeconds) {
            if (row.Kind != CellKind.Int) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName,
                    detail: $"state row '{rowName}' is kind={DescribeCellKind(kind: row.Kind)} — '{verb}' 'valueSeconds' authors a whole engine-tick countdown, meaningful only against a kind=int row"
                );
            }

            var literalSeconds = valueSeconds!.Value;
            var maximumSeconds = (((decimal)long.MaxValue) / FixedTickConversion.TicksPerSecond);

            if (literalSeconds > maximumSeconds) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.DurationEngineTicksOutOfRange,
                    ruleName: ruleName,
                    detail: $"'{verb}' authors {rowName} 'valueSeconds' {literalSeconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} — the duration exceeds the signed 64-bit state carrier's maximum of {long.MaxValue} engine ticks (approximately {maximumSeconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} seconds)"
                );
            }

            if (!FixedTickConversion.TryDurationEngineTicksExact(
                seconds: literalSeconds,
                ticks: out var ticks
            )) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.DurationNotExactEngineTicks,
                    ruleName: ruleName,
                    detail: DescribeInexactDuration(
                        literalSeconds: literalSeconds,
                        rowName: rowName,
                        verb: verb
                    )
                );
            }

            return new CompiledWorldEffect(
                Kind: WorldRuleEffectKind.Write,
                Row: rowName,
                Key: resolvedKey,
                Write: write,
                RawValue: checked((long)ticks),
                Generator: null,
                Describe: $"{verb} {rowName}.{resolvedKey} = {literalSeconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}s ({ticks} engine ticks)",
                KeyFrom: destinationKeyFrom
            );
        }

        if (hasValue) {
            var literal = value!.Value;
            var raw = LiteralToRaw(
                kind: row.Kind,
                literal: literal,
                ruleName: ruleName,
                verb: verb
            );

            return new CompiledWorldEffect(
                Kind: WorldRuleEffectKind.Write,
                Row: rowName,
                Key: resolvedKey,
                Write: write,
                RawValue: raw,
                Generator: null,
                Describe: $"{verb} {rowName}.{resolvedKey} = {literal.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}",
                KeyFrom: destinationKeyFrom
            );
        }

        if (hasExpression) {
            if (row.Kind is CellKind.Bool or CellKind.Text) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName,
                    detail: $"state row '{rowName}' is kind={DescribeCellKind(kind: row.Kind)} — numeric expressions require kind=int or kind=fixed"
                );
            }

            var program = CompileExpression(
                definition: definition,
                expression: expression,
                kind: row.Kind,
                ruleName: ruleName,
                verb: verb
            );

            return new CompiledWorldEffect(
                Kind: WorldRuleEffectKind.Write,
                Row: rowName,
                Key: resolvedKey,
                Write: write,
                RawValue: 0L,
                Generator: null,
                Describe: $"{verb} {rowName}.{resolvedKey} := expression[{program.Length}]",
                KeyFrom: destinationKeyFrom,
                Expression: program
            );
        }

        var source = ResolveOperand(
            allowText: isTextRow,
            definition: definition,
            fieldLabel: "fromState",
            key: fromKey,
            keyFieldLabel: "fromKey",
            name: fromState!,
            ruleName: ruleName,
            verb: verb
        );

        if (source.ValueKind != row.Kind) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.EffectSourceKindMismatch,
                ruleName: ruleName,
                detail: $"state row '{rowName}' is kind={DescribeCellKind(kind: row.Kind)} but 'fromState' '{fromState}' is kind={DescribeCellKind(kind: source.ValueKind)} — mixed-kind copies are refused; author both sides the same kind"
            );
        }

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.Write,
            Row: rowName,
            Key: resolvedKey,
            Write: write,
            RawValue: 0L,
            Generator: null,
            Describe: $"{verb} {rowName}.{resolvedKey} := {source.Describe}",
            From: source.Operand,
            KeyFrom: destinationKeyFrom
        );
    }
    private static CompiledWorldExpressionToken[] CompileExpression(WorldValueExpression? expression, CellKind kind, string ruleName, string verb, WorldDefinition definition) {
        if (expression is null) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName,
                detail: $"'{verb}' carries a null expression"
            );
        }

        if (expression.Tokens is not { Count: > 0 } authored || authored.Count > WorldRuleCapacity.MaxExpressionTokens) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName,
                detail: $"'{verb}' expression must carry 1..{WorldRuleCapacity.MaxExpressionTokens} postfix tokens"
            );
        }

        var tokens = new CompiledWorldExpressionToken[authored.Count];
        var depth = 0;

        for (var index = 0; index < authored.Count; index++) {
            tokens[index] = authored[index] switch {
                WorldValueToken.Constant constant => Push(new CompiledWorldExpressionToken(
                        Operation: WorldExpressionOp.Constant,
                        Constant: LiteralToRaw(kind: kind, literal: constant.Value, ruleName: ruleName, verb: verb)
                    )),
                WorldValueToken.State state => ResolveState(state),
                WorldValueToken.Social social => ResolveSocialToken(social.Query),
                WorldValueToken.SocialClock => ResolveSocialMetadata(WorldRuleFactKind.SocialClock),
                WorldValueToken.SocialResult => ResolveSocialMetadata(WorldRuleFactKind.SocialResult),
                WorldValueToken.Add => Binary(WorldExpressionOp.Add),
                WorldValueToken.Subtract => Binary(WorldExpressionOp.Subtract),
                WorldValueToken.Multiply => Binary(WorldExpressionOp.Multiply),
                WorldValueToken.Divide => Binary(WorldExpressionOp.Divide),
                WorldValueToken.Min => Binary(WorldExpressionOp.Minimum),
                WorldValueToken.Max => Binary(WorldExpressionOp.Maximum),
                WorldValueToken.Clamp => Ternary(WorldExpressionOp.Clamp),
                null => throw Malformed("contains a null token"),
                _ => throw Malformed($"contains unsupported token '{authored[index].GetType().Name}'"),
            };
        }

        if (depth != 1) {
            throw Malformed($"leaves {depth} values on the postfix stack instead of exactly one");
        }

        return tokens;

        CompiledWorldExpressionToken ResolveSocialMetadata(WorldRuleFactKind fact) {
            _ = RequireSocialPolicy(ruleName, definition);
            if (kind != CellKind.Int) { throw Malformed($"{fact} requires kind Int"); }
            return Push(new(WorldExpressionOp.Operand, Operand: new(fact, null, null, ValueKind: CellKind.Int)));
        }
        CompiledWorldExpressionToken ResolveSocialToken(WorldSocialQuery query) {
            var compiled = ResolveSocialQuery(query, ruleName, definition);
            if (compiled.Kind != kind) { throw Malformed($"social facet {query.Facet} requires kind {compiled.Kind}"); }
            return Push(new(WorldExpressionOp.Operand, Operand: new(WorldRuleFactKind.Social, null, null, ValueKind: kind, Social: compiled)));
        }
        CompiledWorldExpressionToken ResolveState(WorldValueToken.State state) {
            var resolved = ResolveOperand(
                allowText: false,
                definition: definition,
                fieldLabel: "expression.state.name",
                key: state.Key,
                keyFieldLabel: "expression.state.key",
                name: state.Name,
                ruleName: ruleName,
                verb: verb
            );

            if (resolved.ValueKind != kind) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.EffectSourceKindMismatch,
                    ruleName: ruleName,
                    detail: $"'{verb}' expression reads '{state.Name}' as kind={DescribeCellKind(kind: resolved.ValueKind)} into a kind={DescribeCellKind(kind: kind)} destination"
                );
            }

            depth++;
            return new CompiledWorldExpressionToken(
                Operation: WorldExpressionOp.Operand,
                Operand: resolved.Operand
            );
        }

        CompiledWorldExpressionToken Push(CompiledWorldExpressionToken token) {
            depth++;
            return token;
        }
        CompiledWorldExpressionToken Binary(WorldExpressionOp operation) {
            if (depth < 2) { throw Malformed($"token '{operation}' underflows the postfix stack"); }
            depth--;
            return new CompiledWorldExpressionToken(Operation: operation);
        }
        CompiledWorldExpressionToken Ternary(WorldExpressionOp operation) {
            if (depth < 3) { throw Malformed($"token '{operation}' underflows the postfix stack"); }
            depth -= 2;
            return new CompiledWorldExpressionToken(Operation: operation);
        }
        WorldRuleException Malformed(string detail) => new(refusal: WorldRuleRefusal.EffectSourceAmbiguous, ruleName: ruleName, detail: $"'{verb}' expression {detail}");
    }
}
