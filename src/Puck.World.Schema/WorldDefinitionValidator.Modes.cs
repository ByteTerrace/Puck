namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The built-in context-family names (Client.WorldContextFamilies' own constants) an authored seatModes family
    // must not collide with — Schema cannot reference Client (layering), so this is the deliberate mirror; keep it in
    // sync with WorldContextFamilies.Families whenever a built-in family is added, renamed, or removed.
    private static readonly string[] s_reservedContextFamilyNames = ["roster", "engagement", "layout"];

    private static void ValidateSeatModes(WorldDefinition definition, List<string> errors) {
        var families = definition.SeatModes;

        if (families.Count == 0) {
            return;
        }

        var familyNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var needsFlyRig = false;

        for (var index = 0; (index < families.Count); index++) {
            var family = families[index];
            var path = $"seatModes[{index}]";

            if (family is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: family.Name)) {
                errors.Add(item: $"{path}.name is required.");
            } else if (
                Array.IndexOf(
                array: s_reservedContextFamilyNames,
                value: family.Name
            ) >= 0
            ) {
                errors.Add(item: $"{path}.name '{family.Name}' collides with a built-in context family — {string.Join(", ", s_reservedContextFamilyNames)}.");
            } else if (family.Name.StartsWith(
                value: WorldStateBindingContext.FamilyPrefix,
                comparisonType: StringComparison.Ordinal
            )) {
                errors.Add(item: $"{path}.name '{family.Name}' starts with the reserved '{WorldStateBindingContext.FamilyPrefix}' prefix.");
            } else if (!familyNames.Add(item: family.Name)) {
                errors.Add(item: $"{path}.name '{family.Name}' is duplicated.");
            }

            if (family.States.Count == 0) {
                errors.Add(item: $"{path}.states must declare at least one state.");

                continue;
            }

            var stateNames = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var stateIndex = 0; (stateIndex < family.States.Count); stateIndex++) {
                var state = family.States[stateIndex];
                var statePath = $"{path}.states[{stateIndex}]";

                if (state is null) {
                    errors.Add(item: $"{statePath} is required.");

                    continue;
                }

                if (string.IsNullOrWhiteSpace(value: state.Name)) {
                    errors.Add(item: $"{statePath}.name is required.");
                } else if (!stateNames.Add(item: state.Name)) {
                    errors.Add(item: $"{statePath}.name '{state.Name}' is duplicated within {path}.");
                }

                if (state.Target is null) {
                    continue;
                }

                if (!string.Equals(
                    a: state.Target,
                    b: WorldSeatModeState.CameraTarget,
                    comparisonType: StringComparison.Ordinal
                )) {
                    errors.Add(item: $"{statePath}.target '{state.Target}' is not admitted — {WorldSeatModeState.CameraTarget}.");
                } else {
                    needsFlyRig = true;
                }
            }

            if (
                !string.IsNullOrWhiteSpace(value: family.DefaultState) &&
                !stateNames.Contains(item: family.DefaultState)
            ) {
                errors.Add(item: $"{path}.defaultState '{family.DefaultState}' names no state in {path}.states.");
            } else if (string.IsNullOrWhiteSpace(value: family.DefaultState)) {
                errors.Add(item: $"{path}.defaultState is required.");
            }
        }

        if (
            needsFlyRig &&
            (definition.Views.FlyRig is null)
        ) {
            errors.Add(item: "seatModes declares a state targeting 'camera' but views.flyRig is not authored.");
        }
    }
}
