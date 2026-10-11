namespace Puck.Maths.Tests;

internal static partial class Subjects {
    public static string? PrimeFloorReciprocalDivision() {
        var divisors = new HashSet<ulong> { 1, 2, 3, 23, 983040, 983041, 10_000_000_000_000_000_000, ulong.MaxValue };

        for (var bit = 1; (bit < 64); ++bit) {
            var power = (1UL << bit);

            divisors.Add(item: (power - 1));
            divisors.Add(item: power);
            divisors.Add(item: (power + 1));
        }
        // Segment endpoints across the driver's range, including its short final segment.
        for (var segment = 1UL; (segment < (1UL << 24)); segment *= 3) {
            divisors.Add(item: (segment * 983040));
            divisors.Add(item: ((segment * 983040) - 1));
        }
        ulong[] anchors = [0, 1, 2, uint.MaxValue, (1UL << 43), (1UL << 48), (1UL << 63),
            10_000_000_000_000_000_000, (ulong.MaxValue - 1), ulong.MaxValue];

        foreach (var divisor in divisors) {
            var reciprocal = PrimeDivisionKernels.FloorReciprocal(divisor: divisor);

            foreach (var anchor in anchors) {
                var failure = PrimeFloorReciprocalQuotient(dividend: anchor, divisor: divisor, reciprocal: reciprocal);

                if (failure is not null) { return failure; }
                var multiple = (((UInt128)anchor) * divisor);

                if (multiple > ulong.MaxValue) { continue; }
                // Both sides of each quotient transition; UInt128 keeps the operand generator from wrapping.
                foreach (var offset in new ulong[] { 0, 1, (divisor - 1) }) {
                    var dividend = (multiple + offset);

                    if (dividend > ulong.MaxValue) { continue; }
                    failure = PrimeFloorReciprocalQuotient(dividend: ((ulong)dividend), divisor: divisor, reciprocal: reciprocal);
                    if (failure is not null) { return failure; }
                }
                if (multiple != 0) {
                    failure = PrimeFloorReciprocalQuotient(dividend: ((ulong)(multiple - 1)), divisor: divisor, reciprocal: reciprocal);
                    if (failure is not null) { return failure; }
                }
            }
            var walk = 1UL;

            for (var index = 0; (index < 256); ++index) {
                walk = unchecked(((walk * 6364136223846793005UL) + 1442695040888963407UL));
                var failure = PrimeFloorReciprocalQuotient(dividend: walk, divisor: divisor, reciprocal: reciprocal);

                if (failure is not null) { return failure; }
            }
        }
        return null;
    }

    private static string? PrimeFloorReciprocalQuotient(ulong dividend, ulong divisor, ulong reciprocal) {
        var expected = (dividend / divisor);
        var actual = PrimeDivisionKernels.DivideByFloorReciprocal(dividend: dividend, divisor: divisor, reciprocal: reciprocal);

        return ((actual == expected) ? null : $"floor-reciprocal quotient {dividend}/{divisor}: got {actual}, expected {expected}");
    }
}
