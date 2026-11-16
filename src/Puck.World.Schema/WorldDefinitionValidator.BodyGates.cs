using Puck.Physics.Motion;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // A shaping-row gate: the body-fact predicate vocabulary, plus 'held' (a shaping row's own channel test).
    // Now/Recently/All/Any/Not/Held are accepted; the lane-scoped CompareState/TimerElapsed are rejected by name
    // ("action-state predicates apply only to action triggers"); an unknown kind is loud. Mirrors ValidatePredicate's
    // structure but narrows the admissible set.
    private static void ValidateMotionGate(ActionPredicate? predicate, ISet<string> channelNames, string path, List<string> errors) {
        switch (predicate) {
            case null:
                break;
            case WorldPredicate.Now now when !Enum.IsDefined(value: now.Fact):
                errors.Add(item: $"{path}.fact '{now.Fact}' is not a defined ActionFact.");
                break;
            case WorldPredicate.Now:
                break;
            case WorldPredicate.Recently recently:
                if (!Enum.IsDefined(value: recently.Fact)) {
                    errors.Add(item: $"{path}.fact '{recently.Fact}' is not a defined ActionFact.");
                }

                RequirePositive(
                    value: recently.WindowSeconds,
                    name: $"{path}.windowSeconds",
                    errors: errors
                );

                break;
            case WorldPredicate.Held held:
                if (
                    string.IsNullOrWhiteSpace(value: held.Channel) ||
                    !channelNames.Contains(item: held.Channel)
                ) {
                    errors.Add(item: $"{path}.channel '{held.Channel}' names no declared composition channel.");
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
                        channelNames: channelNames,
                        path: $"{path}.all[{index}]",
                        errors: errors
                    );
                }

                break;
            case ActionPredicate.Any any:
                if (any.Predicates is not { Count: > 0 } alternatives) {
                    errors.Add(item: $"{path}.any must contain at least one predicate.");

                    break;
                }

                for (var index = 0; (index < alternatives.Count); index++) {
                    ValidateMotionGate(
                        predicate: alternatives[index],
                        channelNames: channelNames,
                        path: $"{path}.any[{index}]",
                        errors: errors
                    );
                }

                break;
            case ActionPredicate.Not not:
                if (not.Predicate is null) {
                    errors.Add(item: $"{path}.not must contain one predicate.");
                } else {
                    ValidateMotionGate(
                        predicate: not.Predicate,
                        channelNames: channelNames,
                        path: $"{path}.not",
                        errors: errors
                    );
                }

                break;
            case ActionPredicate.CompareState:
            case WorldPredicate.TimerElapsed:
                errors.Add(item: $"{path} is an action-state predicate ('{PredicateKind(predicate: predicate)}') — action-state predicates apply only to action triggers, not a shaping-row gate.");
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
            case WorldPredicate.Now now when !Enum.IsDefined(value: now.Fact):
                errors.Add(item: $"{path}.fact '{now.Fact}' is not a defined ActionFact.");
                break;
            case WorldPredicate.Now:
                break;
            case WorldPredicate.Recently recently:
                if (!Enum.IsDefined(value: recently.Fact)) {
                    errors.Add(item: $"{path}.fact '{recently.Fact}' is not a defined ActionFact.");
                }

                RequirePositive(
                    value: recently.WindowSeconds,
                    name: $"{path}.windowSeconds",
                    errors: errors
                );

                break;
            case ActionPredicate.CompareState compare:
                if (compare.Text is not null) {
                    errors.Add($"{path}.text is refused at body scope — action-state slots are numeric; text comparisons belong to world rules.");
                }
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
                    key: compare.State.Spelling,
                    value: out var compareSlot
                )) {
                    errors.Add(item: $"{path}.state '{compare.State}' names no declared action state.");
                } else if (compareSlot.Kind != ActionStateKind.Counter) {
                    errors.Add(item: $"{path}.state '{compare.State}' is a timer; compareState requires a counter.");
                }
                if (!compare.Comparison.IsComparison()) {
                    errors.Add(item: $"{path}.comparison '{compare.Comparison}' is not a defined ExpressionOp.");
                }
                if (compare.Value is not { } compareValue) {
                    errors.Add(item: $"{path}.value is required at body scope — a per-body predicate names an authored constant (a comparand row reference is legitimate only in a world rule).");
                } else {
                    if (!NumericLiteral.TryToFixed(
                        result: out _,
                        value: compareValue
                    )) {
                        errors.Add(item: $"{path}.value is outside the Q48.16 action-state range.");
                    }
                }
                break;
            case WorldPredicate.TimerElapsed elapsed:
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
            case ActionPredicate.Any any:
                if (any.Predicates is not { Count: > 0 } alternatives) {
                    errors.Add(item: $"{path}.any must contain at least one predicate.");
                    break;
                }

                for (var index = 0; (index < alternatives.Count); index++) {
                    ValidatePredicate(
                        predicate: alternatives[index],
                        stateSlots: stateSlots,
                        path: $"{path}.any[{index}]",
                        errors: errors
                    );
                }

                break;
            case ActionPredicate.Not not:
                if (not.Predicate is null) {
                    errors.Add(item: $"{path}.not must contain one predicate.");
                    break;
                }

                ValidatePredicate(
                    predicate: not.Predicate,
                    stateSlots: stateSlots,
                    path: $"{path}.not",
                    errors: errors
                );
                break;
            case WorldPredicate.Held:
                errors.Add(item: $"{path} is 'held' — legitimate only inside a kit's shaping-row gate, not an action trigger.");
                break;
            default:
                errors.Add(item: $"{path} is an unknown predicate kind.");
                break;
        }
    }
}
