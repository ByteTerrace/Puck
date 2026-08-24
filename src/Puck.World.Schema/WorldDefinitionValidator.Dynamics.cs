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

            RequireUniqueName(
                value: row.Name,
                seen: names,
                path: path,
                field: "",
                errors: errors
            );

            var admitted = true;

            var frequencyValid = (float.IsFinite(f: row.Frequency) && (row.Frequency > 0f) && (row.Frequency <= WorldDynamics.MaxFrequencyHz));

            RequireRange(
                value: row.Frequency,
                min: 0f,
                max: WorldDynamics.MaxFrequencyHz,
                name: $"{path}.f",
                errors: errors,
                minExclusive: true
            );

            if (!frequencyValid) {
                admitted = false;
            }

            var dampingValid = (float.IsFinite(f: row.Damping) && (row.Damping >= 0f) && (row.Damping <= WorldDynamics.MaxDamping));

            RequireRange(
                value: row.Damping,
                min: 0f,
                max: WorldDynamics.MaxDamping,
                name: $"{path}.zeta",
                errors: errors
            );

            if (!dampingValid) {
                admitted = false;
            }

            var responseValid = (float.IsFinite(f: row.Response) && (row.Response >= WorldDynamics.MinResponse) && (row.Response <= WorldDynamics.MaxResponse));

            RequireRange(
                value: row.Response,
                min: WorldDynamics.MinResponse,
                max: WorldDynamics.MaxResponse,
                name: $"{path}.r",
                errors: errors
            );

            if (!responseValid) {
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
