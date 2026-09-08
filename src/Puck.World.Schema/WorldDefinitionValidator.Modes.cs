namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateSeatModes(WorldDefinition definition, List<string> errors) {
        var families = definition.SeatModes;

        if (families.Count == 0) {
            return;
        }

        var reserved = (ContextFamilyVocabularyHook.ReservedFamilyNames ?? []);
        var familyNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var needsCameraRig = false;

        for (var index = 0; (index < families.Count); index++) {
            var family = families[index];
            var path = $"seatModes[{index}]";

            if (family is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: family.Name)) {
                errors.Add(item: $"{path}.name is required.");
            } else if (reserved.Contains(value: family.Name)) {
                errors.Add(item: $"{path}.name '{family.Name}' collides with a built-in context family — {string.Join(separator: ", ", values: reserved)}.");
            } else if (family.Name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldStateBindingContext.FamilyPrefix
            )) {
                errors.Add(item: $"{path}.name '{family.Name}' starts with the reserved '{WorldStateBindingContext.FamilyPrefix}' prefix.");
            } else {
                RequireUniqueName(
                    value: family.Name,
                    seen: familyNames,
                    path: path,
                    field: "name",
                    errors: errors
                );
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

                RequireUniqueName(
                    value: state.Name,
                    seen: stateNames,
                    path: statePath,
                    field: "name",
                    errors: errors
                );

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
                    needsCameraRig = true;
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

        if (needsCameraRig) {
            if (definition.Views.CameraRig is null) {
                errors.Add(item: "seatModes declares a state targeting 'camera' but views.cameraRig is not authored.");
            }

            // The camera control application possesses an inhabited "camera-seat-<n>" placement (see
            // PlayerCommandModule.Mode.cs) — a camera-targeting state means nothing without at least one authored.
            var hasCameraBody = false;

            foreach (var placement in definition.Placements) {
                if (
                    (placement?.Inhabit is not null) &&
                    (placement.Id is { } id) &&
                    id.StartsWith(
                        comparisonType: StringComparison.Ordinal,
                        value: WorldSeatModeState.CameraPlacementIdPrefix
                    )
                ) {
                    hasCameraBody = true;

                    break;
                }
            }

            if (!hasCameraBody) {
                errors.Add(item: $"seatModes declares a state targeting 'camera' but no inhabited placement id starts with '{WorldSeatModeState.CameraPlacementIdPrefix}' — author one (see PlayerCommandModule.Mode.cs's CameraPlacementId) for the possession to have a body.");
            }
        }
    }
}
