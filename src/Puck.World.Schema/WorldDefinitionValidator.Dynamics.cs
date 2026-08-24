using Puck.Maths;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static HashSet<string> ValidateDynamics(IReadOnlyList<WorldDynamicsRow> dynamics, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < dynamics.Count); index++) {
            var row = dynamics[index];
            var path = $"dynamics[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: row.Name)) {
                errors.Add(item: $"{path} requires a name.");
            } else if (!names.Add(item: row.Name)) {
                errors.Add(item: $"{path} duplicates the name '{row.Name}'.");
            }

            var admitted = true;

            if (
                !float.IsFinite(f: row.Frequency) ||
                (row.Frequency <= 0f)
            ) {
                errors.Add(item: $"{path}.f must be finite and positive.");
                admitted = false;
            } else if (row.Frequency > WorldDynamics.MaxFrequencyHz) {
                errors.Add(item: $"{path}.f {row.Frequency} exceeds the {WorldDynamics.MaxFrequencyHz} Hz ceiling.");
                admitted = false;
            }

            if (
                !float.IsFinite(f: row.Damping) ||
                (row.Damping < 0f)
            ) {
                errors.Add(item: $"{path}.zeta must be finite and non-negative.");
                admitted = false;
            } else if (row.Damping > WorldDynamics.MaxDamping) {
                errors.Add(item: $"{path}.zeta {row.Damping} exceeds the {WorldDynamics.MaxDamping} ceiling.");
                admitted = false;
            }

            if (
                !float.IsFinite(f: row.Response) ||
                (row.Response < WorldDynamics.MinResponse) ||
                (row.Response > WorldDynamics.MaxResponse)
            ) {
                errors.Add(item: $"{path}.r {row.Response} is outside {WorldDynamics.MinResponse}..{WorldDynamics.MaxResponse}.");
                admitted = false;
            }

            // The float triple above can pass every ceiling and still fail to compile: FixedQ4816.FromDouble rounds
            // f/zeta/r to Q16, and a value that rounds to zero (or an off-critical zeta whose derived oscillation
            // rate rounds below one Q16 unit at the Q32 coefficient scale) is a row SecondOrderDynamics.Create
            // refuses. Running the SAME derivation the simulation compiles catches every such band in one place
            // instead of duplicating Create's rounding rules here.
            if (admitted) {
                try {
                    _ = SecondOrderDynamics.Create(
                        frequencyHz: FixedQ4816.FromDouble(value: row.Frequency),
                        dampingRatio: FixedQ4816.FromDouble(value: row.Damping),
                        initialResponse: FixedQ4816.FromDouble(value: row.Response)
                    );
                } catch (ArgumentOutOfRangeException exception) {
                    errors.Add(item: $"{path} does not compile at Q16 — {exception.Message}");
                }
            }
        }

        return names;
    }
}
