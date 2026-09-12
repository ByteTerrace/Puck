namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Compares documented BIOS outputs against an optional, hash-verified local retail BIOS used as a black box.</summary>
internal sealed class FirmwareOracleStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "firmware-oracle";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.B;
    /// <inheritdoc/>
    public bool IsConcurrent => true;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (AgbBiosProfile.Identify(image: context.BiosImage.Span).Kind != AgbBiosKind.RealVerified) {
            return PostStageOutcome.Skip(detail: "functional retail oracle requires a verified retail image supplied with --bios; the image is never bundled or dumped");
        }
        using var comparison = new FirmwareOracleComparison(retail: context.BiosImage.ToArray());
        foreach (var thumb in new[] { false, true }) {
            CheckMath(comparison: comparison, thumb: thumb);
            FirmwareOracleVectors.CheckAffine(comparison: comparison, thumb: thumb);
            FirmwareOracleVectors.CheckMemory(comparison: comparison, thumb: thumb);
            FirmwareOracleVectors.CheckSoundBias(comparison: comparison, thumb: thumb);
        }
        return comparison.Outcome();
    }

    private static void CheckMath(FirmwareOracleComparison comparison, bool thumb) {
        // Zero divisors are bounded compatibility probes, not a claim that division by zero has a mathematical value.
        // The separately licensed mGBA suite's authored BIOS-math tests also exercise 0/0 and INT_MIN/-1.
        int[] numerators = [0, 1, -1, int.MinValue, int.MinValue + 1, int.MaxValue, -65_537, -32_768, -257, -2, 2, 255, 32_767, 65_535, 0x40000000];
        int[] denominators = [0, 1, -1, 2, -2, 3, -7, 255, -256, 32_767, -32_768, int.MinValue, int.MaxValue];
        foreach (var numerator in numerators) {
            foreach (var denominator in denominators) {
                foreach (var number in new byte[] { 6, 7 }) {
                    if (denominator == 0 && numerator is < -1 or > 1) {
                        comparison.NonreturningDivision(number: number, thumb: thumb,
                            r0: (uint)(number == 6 ? numerator : denominator), r1: (uint)(number == 6 ? denominator : numerator));
                        continue;
                    }
                    comparison.Registers(number: number, thumb: thumb,
                        r0: (uint)(number == 6 ? numerator : denominator), r1: (uint)(number == 6 ? denominator : numerator),
                        registers: [0, 1, 3]);
                }
            }
        }
        var squareInputs = new SortedSet<uint> { 0, 1, 2, 3, uint.MaxValue };
        foreach (var root in new uint[] { 2, 3, 7, 15, 16, 31, 32, 255, 256, 1023, 1024, 32_767, 32_768, 65_534, 65_535 }) {
            var square = root * root;
            squareInputs.Add(item: square - 1);
            squareInputs.Add(item: square);
            squareInputs.Add(item: square + 1);
        }
        for (var bit = 1; bit < 32; ++bit) {
            var value = 1u << bit;
            squareInputs.Add(item: value - 1);
            squareInputs.Add(item: value);
            squareInputs.Add(item: value + 1);
        }
        foreach (var input in squareInputs) {
            comparison.Registers(number: 8, thumb: thumb, r0: input, registers: [0]);
        }
        for (var tangent = (int)short.MinValue; tangent <= short.MaxValue; tangent += 257) {
            comparison.Registers(number: 9, thumb: thumb, r0: (uint)tangent, registers: [0], mask: 0xFFFF);
        }
        int[] coordinates = [-32_768, -32_767, -16_385, -16_384, -16_383, -8193, -1, 0, 1, 8191, 16_383, 16_384, 16_385, 32_767];
        foreach (var x in coordinates) {
            comparison.Registers(number: 9, thumb: thumb, r0: (uint)x, registers: [0], mask: 0xFFFF);
            foreach (var y in coordinates) {
                comparison.Registers(number: 10, thumb: thumb, r0: (uint)x, r1: (uint)y, registers: [0], mask: 0xFFFF);
            }
        }
        // Out-of-nominal-width arguments are compared only through the documented return register.
        foreach (var input in new uint[] { 0x8000, 0x8001, 0xBFFF, 0xC000, 0xC001, 0xFFFF, 0xFFFF0000, 0xFFFF0001, 0xFFFF3FFF, 0xFFFF4000, 0xFFFF4001, 0xFFFF7FFF }) {
            comparison.Registers(number: 9, thumb: thumb, r0: input, registers: [0], mask: 0xFFFF);
            comparison.Registers(number: 10, thumb: thumb, r0: input, r1: 0x4000, registers: [0], mask: 0xFFFF);
        }
    }
}
