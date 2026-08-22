using Puck.Physics.Motion;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static string PredicateKind(ActionPredicate predicate) => predicate switch {
        ActionPredicate.CompareState => "compareState",
        ActionPredicate.TimerElapsed => "timerElapsed",
        _ => "?",
    };
    private static void ValidateActionSpec(ActionSpec? spec, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, ISet<string> targetRegisterNames, ISet<string> judgeRowNames, IReadOnlyDictionary<string, WorldStateRow> stateRows, string path, List<string> errors) {
        if (spec is null) {
            return;
        }

        ValidateTrigger(
            trigger: spec.OnPress,
            stateSlots: stateSlots,
            targetRegisterNames: targetRegisterNames,
            judgeRowNames: judgeRowNames,
            stateRows: stateRows,
            latchLegitimate: true,
            path: $"{path}.onPress",
            errors: errors
        );
        ValidateTrigger(
            trigger: spec.OnRelease,
            stateSlots: stateSlots,
            targetRegisterNames: targetRegisterNames,
            judgeRowNames: judgeRowNames,
            stateRows: stateRows,
            latchLegitimate: false,
            path: $"{path}.onRelease",
            errors: errors
        );
        if (spec.OnFact is { } onFact) {
            // The edge latch is one bit per fact trigger in a 64-bit lane word (WorldBody.LaneActionRuntime) — the
            // same shape every other mask in this engine uses. Refused by name rather than silently un-edged.
            if (onFact.Count > MaxFactTriggersPerAction) {
                errors.Add(item: $"{path}.onFact declares {onFact.Count} triggers; the maximum is {MaxFactTriggersPerAction} (one edge-latch bit each).");
            }

            for (var index = 0; (index < onFact.Count); index++) {
                var rule = onFact[index];

                if (!Enum.IsDefined(value: rule.Fact)) {
                    errors.Add(item: $"{path}.onFact[{index}].fact '{rule.Fact}' is not a defined ActionFact.");
                }
                if (!Enum.IsDefined(value: rule.Mode)) {
                    errors.Add(item: $"{path}.onFact[{index}].mode '{rule.Mode}' is not a defined ActionTriggerMode.");
                }
                ValidatePredicate(
                    predicate: rule.Gate,
                    stateSlots: stateSlots,
                    path: $"{path}.onFact[{index}].gate",
                    errors: errors
                );
                if (rule.Effects is not { Count: > 0 }) {
                    errors.Add(item: $"{path}.onFact[{index}].effects must be non-empty.");
                    continue;
                }
                for (var effect = 0; (effect < rule.Effects.Count); effect++) {
                    ValidateEffect(
                        effect: rule.Effects[effect],
                        stateSlots: stateSlots,
                        targetRegisterNames: targetRegisterNames,
                        judgeRowNames: judgeRowNames,
                        stateRows: stateRows,
                        path: $"{path}.onFact[{index}].effects[{effect}]",
                        errors: errors
                    );
                }
            }
        }
    }
    // A lane binding: both trigger channels are optional, but a present trigger's latch must be non-negative and its
    // effects non-empty, and its gate structurally sound.
    private static void ValidateActionStateSlot(ActionStateSlot state, ActionStateLifetime lifetime, string path, List<string> errors) {
        if (state is null) {
            errors.Add(item: $"{path} is required.");
            return;
        }
        if (string.IsNullOrWhiteSpace(value: state.Name)) {
            errors.Add(item: $"{path}.name must be non-empty.");
        }
        if (!Enum.IsDefined(value: state.Kind)) {
            errors.Add(item: $"{path}.kind '{state.Kind}' is not a defined ActionStateKind.");
        }
        if (
            (lifetime == ActionStateLifetime.Durable) &&
            (state.ResetFact is not null)
        ) {
            errors.Add(item: $"{path}.resetFact is not admitted for durable state; durable values enter only through the tick input seam.");
        }
        if (
            state.PlayerWritable &&
            (lifetime != ActionStateLifetime.Durable)
        ) {
            errors.Add(item: $"{path}.playerWritable requires durable lifetime.");
        }
        if (
            state.PlayerWritable &&
            (state.Envelope is null)
        ) {
            errors.Add(item: $"{path}.envelope is required when playerWritable is true.");
        }
        if (
            !state.PlayerWritable &&
            (state.Envelope is not null)
        ) {
            errors.Add(item: $"{path}.envelope is admitted only when playerWritable is true.");
        }
        if (
            !float.IsFinite(f: state.Initial) ||
            ((state.Kind == ActionStateKind.Timer) && (state.Initial < 0f))
        ) {
            errors.Add(item: $"{path}.initial must be finite and non-negative for a timer.");
        }
        if (
            (state.ResetFact is { } reset) &&
            !Enum.IsDefined(value: reset)
        ) {
            errors.Add(item: $"{path}.resetFact '{reset}' is not a defined ActionFact.");
        }

        switch (state.Envelope) {
            case null:
                break;
            case ActionStateEnvelope.Range range:
                if (
                    !float.IsFinite(f: range.Minimum) ||
                    !float.IsFinite(f: range.Maximum) ||
                    (range.Minimum > range.Maximum)
                ) {
                    errors.Add(item: $"{path}.envelope range must have finite minimum <= maximum.");
                } else if (
                    (state.Kind == ActionStateKind.Timer) &&
                    (range.Minimum < 0f)
                ) {
                    errors.Add(item: $"{path}.envelope range minimum must be non-negative for a timer.");
                } else if (
                    (state.Initial < range.Minimum) ||
                    (state.Initial > range.Maximum)
                ) {
                    errors.Add(item: $"{path}.initial must lie inside its envelope.");
                }
                break;
            case ActionStateEnvelope.Set set:
                if (set.Values is not { Count: > 0 }) {
                    errors.Add(item: $"{path}.envelope set must be non-empty.");
                    break;
                }
                if (set.Values.Any(predicate: value => (!float.IsFinite(f: value) || ((state.Kind == ActionStateKind.Timer) && (value < 0f))))) {
                    errors.Add(item: $"{path}.envelope set values must be finite and non-negative for a timer.");
                }
                if (set.Values.Distinct().Count() != set.Values.Count) {
                    errors.Add(item: $"{path}.envelope set values must be unique.");
                }
                if (!set.Values.Contains(value: state.Initial)) {
                    errors.Add(item: $"{path}.initial must belong to its envelope set.");
                }
                break;
            default:
                errors.Add(item: $"{path}.envelope is an unknown envelope kind.");
                break;
        }
    }
    private static Dictionary<string, ActionStateSlot> ValidateActionState(WorldDefinition definition, List<string> errors) {
        var stateSlots = new Dictionary<string, ActionStateSlot>(comparer: StringComparer.Ordinal);
        var count = (definition.BodyState.Count + definition.IdentityState.Count);

        if (count > WorldStateCapacity.MaxBodySlots) {
            errors.Add(item: $"state body and identity lanes declare {count} slots; the combined maximum is {WorldStateCapacity.MaxBodySlots}.");
        }

        void Add(IReadOnlyList<ActionStateSlot> rows, ActionStateLifetime lifetime, string lane) {
            for (var index = 0; (index < rows.Count); index++) {
                var state = rows[index];
                var path = $"state.{lane}[{index}]";

                ValidateActionStateSlot(
                    errors: errors,
                    lifetime: lifetime,
                    path: path,
                    state: state
                );

                if (
                    (state is null) ||
                    string.IsNullOrWhiteSpace(value: state.Name)
                ) {
                    continue;
                }
                if (!stateSlots.TryAdd(
                    key: state.Name,
                    value: state
                )) {
                    errors.Add(item: $"{path} duplicates the body-state name '{state.Name}'; names are world-wide across the body and identity lanes.");
                }
            }
        }

        Add(
            rows: definition.BodyState,
            lifetime: ActionStateLifetime.Ephemeral,
            lane: "body"
        );
        Add(
            rows: definition.IdentityState,
            lifetime: ActionStateLifetime.Durable,
            lane: "identity"
        );
        return stateSlots;
    }
    private static void ValidateEffect(ActionEffect effect, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, ISet<string> targetRegisterNames, ISet<string> judgeRowNames, IReadOnlyDictionary<string, WorldStateRow> stateRows, string path, List<string> errors) {
        if (
            (effect is not null) &&
            !Enum.IsDefined(value: TargetOf(value: effect))
        ) {
            errors.Add(item: $"{path}.target '{TargetOf(value: effect)}' is not a defined ActionTarget.");
        }

        switch (effect) {
            case null:
                errors.Add(item: $"{path} is required.");
                break;
            case ActionEffect.SetVerticalVelocity set:
                RequireFinite(
                    value: set.Velocity,
                    name: $"{path}.velocity",
                    errors: errors
                );
                break;
            case ActionEffect.ScaleVerticalVelocity scale:
                RequireFinite(
                    value: scale.Factor,
                    name: $"{path}.factor",
                    errors: errors
                );
                break;
            case ActionEffect.PlanarImpulse impulse:
                RequireFinite(
                    value: impulse.Speed,
                    name: $"{path}.speed",
                    errors: errors
                );
                RequireNonNegative(
                    value: impulse.DurationSeconds,
                    name: $"{path}.durationSeconds",
                    errors: errors
                );

                if (
                    !IsFinite(value: impulse.BodyDirection) ||
                    (impulse.BodyDirection.LengthSquared() <= MinimumBasisLengthSquared)
                ) {
                    errors.Add(item: $"{path}.bodyDirection must be finite and non-zero.");
                } else {
                    // The runtime rides BodyDirection AS AUTHORED — it is never normalized, only rotated and scaled by
                    // Speed (WorldBody's PlanarImpulse op) — so an unnormalized direction silently rescales the impulse:
                    // an author who typo'd (3, 0, 4) meaning +X gets a 5x speed, not a refusal.
                    var magnitude = impulse.BodyDirection.Length();

                    if (MathF.Abs(x: (magnitude - 1f)) > PlanarImpulseUnitDirectionTolerance) {
                        errors.Add(item: $"{path}.bodyDirection {impulse.BodyDirection} has magnitude {magnitude}, not 1 — PlanarImpulse rides BodyDirection as authored (never normalized), so a non-unit direction silently rescales Speed ({impulse.Speed}).");
                    }
                }

                break;
            case ActionEffect.SetState set:
                RefuseKey(
                    key: set.Key,
                    verb: "setState"
                );
                RefuseFromOperand(
                    fromState: set.FromState,
                    fromKey: set.FromKey,
                    verb: "setState"
                );
                RefuseValueSeconds(
                    valueSeconds: set.ValueSeconds,
                    verb: "setState"
                );

                if (set.Text is not null) {
                    errors.Add(item: $"{path}.text is refused at body scope — 'setState' writes a numeric per-body action-state slot; a text write addresses a world state row, in the rules section.");
                }

                ValidateCounterState(
                    name: set.State,
                    value: set.Value
                );
                break;
            case ActionEffect.AddState add:
                RefuseKey(
                    key: add.Key,
                    verb: "addState"
                );
                RefuseFromOperand(
                    fromState: add.FromState,
                    fromKey: add.FromKey,
                    verb: "addState"
                );
                RefuseValueSeconds(
                    valueSeconds: add.ValueSeconds,
                    verb: "addState"
                );
                ValidateCounterState(
                    name: add.State,
                    value: add.Value
                );
                break;
            case ActionEffect.CountdownState:
                errors.Add(item: $"{path} authors a WORLD state-row countdown, which has no body-scope meaning — admissible only inside a world rule's own effects.");
                break;
            case ActionEffect.StartTimer timer:
                if (!stateSlots.TryGetValue(
                    key: timer.State,
                    value: out var timerSlot
                )) {
                    errors.Add(item: $"{path}.state '{timer.State}' names no declared action state.");
                } else if (timerSlot.Kind != ActionStateKind.Timer) {
                    errors.Add(item: $"{path}.state '{timer.State}' is a counter; startTimer requires a timer.");
                }
                RequireNonNegative(
                    value: timer.Seconds,
                    name: $"{path}.seconds",
                    errors: errors
                );
                break;
            case ActionEffect.Designate designate:
                if (string.IsNullOrWhiteSpace(value: designate.Register)) {
                    errors.Add(item: $"{path}.register must be non-empty.");
                } else if (!targetRegisterNames.Contains(item: designate.Register)) {
                    errors.Add(item: $"{path}.register '{designate.Register}' names no target register.");
                }
                if (designate.Target != ActionTarget.AffectingSubject) {
                    errors.Add(item: $"{path}.target must be AffectingSubject.");
                }
                break;
            case ActionEffect.Generate generate:
                // The ONE effect admissible at both scopes: its names address world `state` rows, so they resolve
                // against the SAME row map a world rule's own generate effect resolves against. Refusing here means a
                // kit naming a dead generator refuses at LOAD, not at first fire.
                ValidateGenerateEffect(
                    row: generate.Row,
                    stateRows: stateRows,
                    path: path,
                    errors: errors
                );
                break;
            case ActionEffect.Judge judge:
                if (string.IsNullOrWhiteSpace(value: judge.JudgeRef)) {
                    errors.Add(item: $"{path}.judgeRef must be non-empty.");
                } else if (!judgeRowNames.Contains(item: judge.JudgeRef)) {
                    errors.Add(item: $"{path}.judgeRef '{judge.JudgeRef}' names no declared judge row.");
                }
                break;
            // upsertHudPanel/removeHudPanel/upsertPlacement/removePlacement author WORLD document rows, and save
            // performs WORLD-scope engine I/O — a per-body action has none of either, so all five are refused BY NAME
            // here (this is the check that actually surfaces: it runs before CompiledBodyMotionProgram.Compile's own
            // mirroring refusal in WorldDefinition.cs, which a passing ValidateEffect never lets a candidate reach).
            case ActionEffect.UpsertHudPanel or ActionEffect.RemoveHudPanel or ActionEffect.UpsertPlacement or ActionEffect.RemovePlacement:
                errors.Add(item: $"{path} authors a WORLD document row, which has no body-scope meaning — admissible only inside a world rule's own effects.");
                break;
            case ActionEffect.Save:
                errors.Add(item: $"{path} has no body-scope meaning — a per-body action has no world file of its own to save, and is admissible only inside a world rule's own effects.");
                break;
            case ActionEffect.Pose:
                errors.Add(item: $"{path} has no body-scope meaning — 'pose' teleports a body the world names, and is admissible only inside a world rule's own effects.");
                break;
            default:
                errors.Add(item: $"{path} is an unknown effect kind.");
                break;
        }

        void RefuseKey(string? key, string verb) {
            if (key is not null) {
                errors.Add(item: $"{path}.key '{key}' is refused at body scope — '{verb}' writes a per-body action-state slot, which is not keyed (a 'key' addresses a world state row's cell, in the rules section).");
            }
        }

        // setState/addState's live copy source ('fromState'/'fromKey') addresses a WORLD state row or reserved
        // channel — a per-body action-state slot has neither, so it is refused here on the same terms RefuseKey
        // already refuses a per-body 'key' (legitimate only in a world rule, via WorldRuleCompiler).
        void RefuseFromOperand(string? fromState, string? fromKey, string verb) {
            if (
                (fromState is not null) ||
                (fromKey is not null)
            ) {
                errors.Add(item: $"{path}.fromState/fromKey are refused at body scope — '{verb}' writes a per-body action-state slot, which has no world state row to copy from (a live copy source is legitimate only in a world rule).");
            }
        }

        // 'valueSeconds' authors an engine-tick countdown against a WORLD state row a companion countdownState effect
        // consumes once per tick — a per-body action-state slot has no such row, so it is refused here on the same
        // terms RefuseFromOperand already refuses a per-body live copy source.
        void RefuseValueSeconds(decimal? valueSeconds, string verb) {
            if (valueSeconds is not null) {
                errors.Add(item: $"{path}.valueSeconds is refused at body scope — '{verb}' writes a per-body action-state slot via 'value', or starts a proper timer via 'startTimer'; 'valueSeconds' is legitimate only in a world rule.");
            }
        }

        void ValidateCounterState(string name, float? value) {
            if (!stateSlots.TryGetValue(
                key: name,
                value: out var slot
            )) {
                errors.Add(item: $"{path}.state '{name}' names no declared action state.");
            } else if (slot.Kind != ActionStateKind.Counter) {
                errors.Add(item: $"{path}.state '{name}' is a timer; this effect requires a counter.");
            }

            if (value is not { } constant) {
                errors.Add(item: $"{path}.value is required at body scope — a live copy source ('fromState') is legitimate only in a world rule.");
            } else {
                RequireFinite(
                    errors: errors,
                    name: $"{path}.value",
                    value: constant
                );
            }
        }

        static ActionTarget TargetOf(ActionEffect value) => value switch {
            ActionEffect.SetVerticalVelocity item => item.Target,
            ActionEffect.ScaleVerticalVelocity item => item.Target,
            ActionEffect.PlanarImpulse item => item.Target,
            ActionEffect.SetState item => item.Target,
            ActionEffect.AddState item => item.Target,
            ActionEffect.StartTimer item => item.Target,
            ActionEffect.Designate item => item.Target,
            _ => ActionTarget.Self,
        };
    }
    /// <summary>Returns the one <c>generate</c>-effect name check, shared by both scopes that can author one: a kit action
    /// (here, through <c>ValidateEffect</c>) and a world rule (through <see cref="WorldRuleCompiler"/>, which refuses
    /// by throwing). One rule, two callers — never two readings of the same requirement.</summary>
    private static void ValidateGenerateEffect(string row, IReadOnlyDictionary<string, WorldStateRow> stateRows, string path, List<string> errors) {
        if (!stateRows.TryGetValue(
            key: (row ?? string.Empty),
            value: out var destination
        )) {
            errors.Add(item: $"{path}.row '{row}' names no state row.");

            return;
        }

        if (destination.Draw is not { } draw) {
            errors.Add(item: $"{path}.row '{row}' declares no draw — 'generate' redraws a draw site.");

            return;
        }

        if (draw.Timing == WorldDrawTiming.Boot) {
            errors.Add(item: $"{path}.row '{row}' declares timing=boot — it draws once at first fill and is never redrawn.");
        }

    }
    // A motion-response gate: the body-fact predicate vocabulary ONLY. Now/Recently/All are accepted; the lane-scoped
    // CompareState/TimerElapsed are rejected by name ("action-state predicates apply only to action triggers"); an
    // unknown kind is loud. Mirrors ValidatePredicate's structure but narrows the admissible set.
    private static void ValidateMotionGate(ActionPredicate? predicate, string path, List<string> errors) {
        switch (predicate) {
            case null:
                break;
            case ActionPredicate.Now now when !Enum.IsDefined(value: now.Fact):
                errors.Add(item: $"{path}.fact '{now.Fact}' is not a defined ActionFact.");
                break;
            case ActionPredicate.Now:
                break;
            case ActionPredicate.Recently recently:
                if (!Enum.IsDefined(value: recently.Fact)) {
                    errors.Add(item: $"{path}.fact '{recently.Fact}' is not a defined ActionFact.");
                }

                if (
                    !float.IsFinite(f: recently.WindowSeconds) ||
                    (recently.WindowSeconds <= 0f)
                ) {
                    errors.Add(item: $"{path}.windowSeconds must be finite and greater than 0.");
                }

                break;
            case ActionPredicate.All all:
                if (all.Predicates is not { Count: > 0 } inner) {
                    errors.Add(item: $"{path}.all must contain at least one predicate.");

                    break;
                }

                for (var index = 0; (index < inner.Count); index++) {
                    ValidateMotionGate(
                        predicate: inner[index],
                        path: $"{path}.all[{index}]",
                        errors: errors
                    );
                }

                break;
            case ActionPredicate.CompareState:
            case ActionPredicate.TimerElapsed:
                errors.Add(item: $"{path} is an action-state predicate ('{PredicateKind(predicate: predicate)}') — action-state predicates apply only to action triggers, not a motion response gate.");
                break;
            default:
                errors.Add(item: $"{path} is an unknown predicate kind.");
                break;
        }
    }
    private static void ValidatePredicate(ActionPredicate? predicate, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, string path, List<string> errors) {
        switch (predicate) {
            case null:
                break;
            case ActionPredicate.Now now when !Enum.IsDefined(value: now.Fact):
                errors.Add(item: $"{path}.fact '{now.Fact}' is not a defined ActionFact.");
                break;
            case ActionPredicate.Now:
                break;
            case ActionPredicate.Recently recently:
                if (!Enum.IsDefined(value: recently.Fact)) {
                    errors.Add(item: $"{path}.fact '{recently.Fact}' is not a defined ActionFact.");
                }

                if (
                    !float.IsFinite(f: recently.WindowSeconds) ||
                    (recently.WindowSeconds <= 0f)
                ) {
                    errors.Add(item: $"{path}.windowSeconds must be finite and greater than 0.");
                }

                break;
            case ActionPredicate.CompareState compare:
                // A per-body action-state slot is not keyed; `key` addresses a WORLD state row's cell and is
                // legitimate only in a world rule. Refused rather than parsed and discarded.
                if (compare.Key is not null) {
                    errors.Add(item: $"{path}.key '{compare.Key}' is refused at body scope — a per-body action-state slot is not keyed (a 'key' addresses a world state row's cell, in the rules section).");
                }
                // A comparand ROW reference addresses a world state row or a reserved per-tick channel; a per-body
                // action-state slot has neither, so the second spelling is legitimate only in a world rule.
                if (
                    (compare.ComparandState is not null) ||
                    (compare.ComparandKey is not null)
                ) {
                    errors.Add(item: $"{path}.comparandState/comparandKey is refused at body scope — a comparand row reference addresses a world state row or a reserved channel, legitimate only in a world rule.");
                }
                if (!stateSlots.TryGetValue(
                    key: compare.State,
                    value: out var compareSlot
                )) {
                    errors.Add(item: $"{path}.state '{compare.State}' names no declared action state.");
                } else if (compareSlot.Kind != ActionStateKind.Counter) {
                    errors.Add(item: $"{path}.state '{compare.State}' is a timer; compareState requires a counter.");
                }
                if (!Enum.IsDefined(value: compare.Comparison)) {
                    errors.Add(item: $"{path}.comparison '{compare.Comparison}' is not a defined ActionStateComparison.");
                }
                if (compare.Value is not { } compareValue) {
                    errors.Add(item: $"{path}.value is required at body scope — a per-body predicate names an authored constant (a comparand row reference is legitimate only in a world rule).");
                } else {
                    RequireFinite(
                        errors: errors,
                        name: $"{path}.value",
                        value: compareValue
                    );
                }
                break;
            case ActionPredicate.TimerElapsed elapsed:
                if (!stateSlots.TryGetValue(
                    key: elapsed.State,
                    value: out var timerSlot
                )) {
                    errors.Add(item: $"{path}.state '{elapsed.State}' names no declared action state.");
                } else if (timerSlot.Kind != ActionStateKind.Timer) {
                    errors.Add(item: $"{path}.state '{elapsed.State}' is a counter; timerElapsed requires a timer.");
                }
                break;
            case ActionPredicate.All all:
                if (all.Predicates is not { Count: > 0 } inner) {
                    errors.Add(item: $"{path}.all must contain at least one predicate.");

                    break;
                }

                for (var index = 0; (index < inner.Count); index++) {
                    ValidatePredicate(
                        predicate: inner[index],
                        stateSlots: stateSlots,
                        path: $"{path}.all[{index}]",
                        errors: errors
                    );
                }

                break;
            default:
                errors.Add(item: $"{path} is an unknown predicate kind.");
                break;
        }
    }
    private static void ValidateTrigger(ActionTrigger? trigger, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, ISet<string> targetRegisterNames, ISet<string> judgeRowNames, IReadOnlyDictionary<string, WorldStateRow> stateRows, bool latchLegitimate, string path, List<string> errors) {
        if (trigger is null) {
            return;
        }

        RequireNonNegative(
            value: trigger.LatchSeconds,
            name: $"{path}.latchSeconds",
            errors: errors
        );

        // The release channel latches nothing — the runtime reads LatchSeconds on the press arm alone. An authored
        // value here would be parsed and silently discarded, so it is refused BY NAME instead. (0 stays legal: it is
        // the field's own default and now means what it always documented, "this tick only".)
        if (
            !latchLegitimate &&
            (trigger.LatchSeconds != 0f)
        ) {
            errors.Add(item: $"{path}.latchSeconds {trigger.LatchSeconds} is refused — the release channel latches nothing, so only 0 is legitimate here (a press buffer is authored on onPress).");
        }

        ValidatePredicate(
            predicate: trigger.Gate,
            stateSlots: stateSlots,
            path: $"{path}.gate",
            errors: errors
        );

        if (trigger.Effects is not { Count: > 0 } effects) {
            errors.Add(item: $"{path}.effects must be non-empty on a present trigger.");

            return;
        }

        for (var index = 0; (index < effects.Count); index++) {
            ValidateEffect(
                effect: effects[index],
                stateSlots: stateSlots,
                targetRegisterNames: targetRegisterNames,
                judgeRowNames: judgeRowNames,
                stateRows: stateRows,
                path: $"{path}.effects[{index}]",
                errors: errors
            );
        }
    }
}
