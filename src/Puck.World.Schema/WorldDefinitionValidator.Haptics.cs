using Puck.World.Protocol;

namespace Puck.World;

/// <summary>The <c>haptics</c> section's validation: unique ids, bounded motor intensities and trigger scalars, finite
/// positive pulse duration, <see cref="WorldHapticMode"/>/<see cref="WorldHapticRow.Signal"/> agreement, and valid
/// optional seat selection.</summary>
public static partial class WorldDefinitionValidator {
    private static void ValidateHaptics(WorldDefinition definition, int localSeats, List<string> errors) {
        var haptics = definition.Haptics;

        if (haptics.Count > WorldHapticCapacity.MaxRows) {
            errors.Add(item: $"haptics declares {haptics.Count} rows, exceeding the {WorldHapticCapacity.MaxRows}-row ceiling.");
        }

        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < haptics.Count); index++) {
            var row = haptics[index];
            var path = $"haptics[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            RequireUniqueName(
                value: row.Id,
                seen: ids,
                path: path,
                field: "id",
                errors: errors
            );

            JudgeScalar(
                definition: definition,
                errors: errors,
                field: WorldValueFields.HapticLow,
                path: $"{path}.low",
                scalar: row.Low
            );
            JudgeScalar(
                definition: definition,
                errors: errors,
                field: WorldValueFields.HapticHigh,
                path: $"{path}.high",
                scalar: row.High
            );

            if (
                !float.IsFinite(f: row.DurationSeconds) ||
                (row.DurationSeconds <= 0f) ||
                (row.DurationSeconds > WorldHapticCapacity.MaxDurationSeconds)
            ) {
                errors.Add(item: $"{path}.durationSeconds {row.DurationSeconds} must be finite and within (0, {WorldHapticCapacity.MaxDurationSeconds}].");
            }

            if (row.Mode == WorldHapticMode.Level) {
                if (row.Signal is not null) {
                    errors.Add(item: $"{path}.signal cannot be authored when mode is '{WorldHapticMode.Level}'; use '{WorldHapticMode.Rise}' or '{WorldHapticMode.Change}'.");
                }
            } else if (row.Signal is not { } signal) {
                errors.Add(item: $"{path}.signal is required when mode is '{row.Mode}'.");
            } else {
                JudgeScalar(
                    definition: definition,
                    errors: errors,
                    field: WorldValueFields.HapticSignal,
                    path: $"{path}.signal",
                    scalar: signal
                );
            }

            if (row.Seat is { } seatToken) {
                if (
                    !PrincipalTokens.TryParseSeat(
                        number: out var seat,
                        token: seatToken
                    ) ||
                    (seat > localSeats)
                ) {
                    errors.Add(item: $"{path}.seat '{seatToken}' names no seat (expected seat1..seat{localSeats}).");
                }
            }
        }
    }
}
