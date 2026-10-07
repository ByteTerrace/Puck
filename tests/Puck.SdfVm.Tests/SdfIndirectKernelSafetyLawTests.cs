using System.Text.RegularExpressions;

using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfIndirectKernelSafetyLawTests {
    [Fact]
    public void IrradianceSamplingKeepsOneBoundedCornerBody() {
        var source = Source(path: "indirect/sdf-indirect-irradiance.hlsli");
        var declaration = source.IndexOf(comparisonType: StringComparison.Ordinal, value: "bool sdfIndirectIrradianceAt(");
        var sampler = source.IndexOf(comparisonType: StringComparison.Ordinal, startIndex: declaration, value: "sdfIndirectProbeIrradiance(");

        Assert.True(condition: (sampler > declaration));
        Assert.Contains("[loop] for (uint corner = 0u; corner < 8u; corner++)", source[declaration..sampler]);
        Assert.DoesNotContain("[unroll] for (uint corner", source[declaration..sampler]);
    }
    [Fact]
    public void BrickDivisionRetainsAllFourLocalCoordinatesAcrossTheIntegerDomain() {
        int[] starts = [int.MinValue, -16777220, -8, -4, 0, 16777216, (int.MaxValue - 3)];

        foreach (var start in starts) {
            for (var residue = 0; (residue < 4); residue++) {
                var coordinate = (start + residue);
                var brick = (coordinate >> 2);
                var local = coordinate & 3;

                Assert.Equal(expected: Math.Floor(d: (coordinate / 4.0)), actual: brick);
                Assert.Equal(actual: ((4L * brick) + local), expected: coordinate);
                Assert.InRange(actual: local, high: 3, low: 0);
            }
        }
        const int BeyondFloatPrecision = 16777219;
        var roundedBrick = ((int)MathF.Floor(x: (((float)BeyondFloatPrecision) / 4f)));

        Assert.Equal(actual: (BeyondFloatPrecision - (roundedBrick * 4)), expected: -1);
        var source = Source(path: "indirect/sdf-indirect-bricks.hlsli");

        Assert.Contains(actualString: source, expectedSubstring: "int3 brick = lattice >> 2;");
        Assert.Contains(actualString: source, expectedSubstring: "int3 local = lattice & 3;");
        Assert.Contains(actualString: source, expectedSubstring: "indirectBricks.GetDimensions(length, stride);");
        Assert.Contains(actualString: source, expectedSubstring: "if ((uint)brickIndex >= capacity) { return -1; }");
        Assert.Contains(actualString: source, expectedSubstring: "step < 32u && low < high");
    }
    [Fact]
    public void CacheAccessChecksTheBoundAllocationAndReturnsUnknownForAbsentWords() {
        var source = Source(path: "indirect/sdf-indirect-cache.hlsli");

        Assert.Contains(actualString: source, expectedSubstring: "SDF_INDIRECT_WORDS.GetDimensions(length, stride);");
        Assert.Contains(actualString: source, expectedSubstring: "word <= length && count <= length - word");
        Assert.Contains(actualString: source, expectedSubstring: "if (!sdfIndirectRange(word, 1u)) { return 0xffffffffu; }");
        Assert.Contains(actualString: source, expectedSubstring: "if (sdfIndirectRange(word, 1u)) { indirectCacheRW[word] = value; }");
        Assert.Contains(actualString: source, expectedSubstring: "if (!sdfIndirectRange(address, SdfIndirectProbeWords)) { return placement; }");
        Assert.Contains(actualString: source, expectedSubstring: "indirectTraceStates.GetDimensions(length, stride);");
        Assert.Contains(actualString: source, expectedSubstring: "indirectDirections.GetDimensions(length, stride);");
        Assert.Contains(actualString: source, expectedSubstring: "min(passGroup.indirectReceiverProofs, sdfIndirectReceiverProofBudget(passGroup.indirectTier))");
        Assert.Contains(actualString: Source(path: "indirect/sdf-indirect-proof.hlsli"),
            expectedSubstring: "if (!sdfIndirectRange(proof, SdfIndirectProofWords)) { return 0u; }");
    }
    [Fact]
    public void FieldWorkHasAStaticTripCeilingAsWellAsItsRemainingQueryBudget() {
        var march = Source(path: "indirect/sdf-indirect-march.hlsli");
        var cells = Source(path: "indirect/sdf-indirect-cells.hlsli");
        var field = Source(path: "indirect/sdf-indirect-field.hlsli");
        var trace = Source(path: "passes/sdf-indirect-trace.comp.hlsl");

        Assert.Contains(actualString: march, expectedSubstring: "step < SdfIndirectLightMarchSteps && budget > 0u");
        Assert.Contains(actualString: march, expectedSubstring: "step < SdfIndirectSegmentSteps && budget > 0u");
        Assert.Contains(actualString: cells, expectedSubstring: "step < max(SdfIndirectLaunchSteps, SdfIndirectNearSteps) && budget > 0u");
        Assert.Contains(actualString: field, expectedSubstring: "candidate < SDF_MAX_INSTANCES");
        Assert.Contains(actualString: trace, expectedSubstring: "segment < SdfIndirectTraceSteps && budget > 0u");
        Assert.DoesNotContain(actualString: (((march + cells) + field) + trace), expectedSubstring: "while (");
    }
    [Fact]
    public void OneLaunchCallSitePreservesBothPhaseSampleSequences() {
        var maximum = Math.Max(val1: SdfIndirectLayout.LaunchSteps, val2: SdfIndirectNearLayout.Steps);
        var start = ((float)IrradianceCells.LaunchStart);

        foreach (var spacing in new[] { 0.001f, 0.02f, 1.5f }) {
            var target = MathF.Max(x: start, y: (spacing * ((float)IrradianceCells.ReceiverBias)));
            float[] choices = [0f, float.NaN, (start * 0.25f), start, (target + start)];

            for (var pattern = 0; (pattern < 625); pattern++) {
                var digits = pattern;
                var answers = new float[4];

                for (var index = 0; (index < answers.Length); index++) {
                    answers[index] = choices[(digits % choices.Length)];
                    digits /= choices.Length;
                }
                for (var budget = 0; (budget <= maximum); budget++) {
                    var expected = SeparateLaunchPhases(answers: answers, budget: budget, target: target);
                    var actual = SharedLaunchLoop(answers: answers, budget: budget, maximum: maximum, target: target);

                    Assert.Equal(expected: expected.Samples, actual: actual.Samples);
                    Assert.Equal(expected: expected.Budget, actual: actual.Budget);
                    Assert.Equal(expected: expected.Position, actual: actual.Position);
                    Assert.Equal(expected: expected.Clearance, actual: actual.Clearance);
                    Assert.Equal(expected: expected.Success, actual: actual.Success);
                }
            }
        }
        var source = Source(path: "indirect/sdf-indirect-cells.hlsli");
        var declaration = source.IndexOf(comparisonType: StringComparison.Ordinal, value: "bool sdfIndirectLaunch(");

        Assert.True(condition: (declaration >= 0));
        var launch = source[declaration..];

        // A second field call expands another complete interpreter in the compiled Views kernel.
        Assert.Single(collection: Regex.Matches(input: launch, pattern: @"\bsdfIndirectSample\s*\("));
        Assert.Contains(actualString: launch, expectedSubstring: "descended = true; height = SdfIndirectSurfaceEpsilon;");
        Assert.Contains(actualString: launch, expectedSubstring: "sdfIndirectLaunchEvaluations += sdfIndirectEvaluations - start;");
    }
    [InlineData("trace", "Trace")]
    [InlineData("classify", "Classify")]
    [InlineData("shade", "Shade")]
    [Theory]
    public void TransportDispatchesValidateUpdatesBeforeTheirFirstGroupWork(string kernel, string budget) {
        var source = Source(path: $"passes/sdf-indirect-{kernel}.comp.hlsl");

        Assert.Contains(actualString: source, expectedSubstring: $"group.x >= sdfIndirect{budget}Budget(passGroup.indirectTier)");
        if (kernel == "classify") {
            Assert.Contains(actualString: source, expectedSubstring: "indirectUpdates.GetDimensions(length, stride);");
            Assert.Contains(actualString: source, expectedSubstring: "if (row >= length) { return; }");
            Assert.Contains(actualString: source, expectedSubstring: "slot >= sdfIndirectBrickCapacity(passGroup.indirectTier) || slot >= length");
            Assert.Contains(actualString: source, expectedSubstring: "if (any(lattice == 2147483647)) { return; }");
        } else {
            Assert.Contains(actualString: source, expectedSubstring: "if (!sdfIndirectProbeUpdate(");
            var cache = Source(path: "indirect/sdf-indirect-cache.hlsli");

            Assert.Contains(actualString: cache, expectedSubstring: "indirectUpdates.GetDimensions(length, stride);");
            Assert.Contains(actualString: cache, expectedSubstring: "row >= length || !sdfIndirectRange(0u, sdfIndirectWordCount(passGroup.indirectTier))");
            Assert.Contains(actualString: cache, expectedSubstring: "update.x >= sdfIndirectProbeCapacity(passGroup.indirectTier) || update.z >= sdfIndirectLevelCount()");
        }
    }
    [Fact]
    public void NonfiniteAndUnrepresentableCellsAreRefusedBeforeIntegerConversion() {
        var source = Source(path: "indirect/sdf-indirect-cache.hlsli");
        var start = source.IndexOf(comparisonType: StringComparison.Ordinal, value: "bool sdfIndirectCellAt(");

        Assert.True(condition: (start >= 0));
        var end = source.IndexOf(comparisonType: StringComparison.Ordinal, startIndex: start, value: "cell = int3(scaled);");

        Assert.True(condition: (end > start));
        var guards = source[start..end];

        Assert.Contains(actualString: guards, expectedSubstring: "!all(isfinite(position))");
        Assert.Contains(actualString: guards, expectedSubstring: "!isfinite(spacing) || spacing <= 0.0");
        Assert.Contains(actualString: guards, expectedSubstring: "!all(isfinite(scaled))");
        Assert.Contains(actualString: guards, expectedSubstring: "any(scaled < -2147483648.0) || any(scaled >= 2147483648.0)");
        Assert.Contains(actualString: source, expectedSubstring: "if (!isfinite(height)) { clearance = 0.0; return 0u; }");
    }

    private static LaunchResult SeparateLaunchPhases(float[] answers, int budget, float target) {
        var samples = new List<float>();
        var height = ((float)IrradianceCells.LaunchStart);
        var descended = false;

        while (budget > 0) {
            budget--;
            var ball = answers[(samples.Count % answers.Length)];

            samples.Add(item: height);
            if (!(ball > 0f) || !float.IsFinite(f: ball)) { break; }
            height -= ball;
            if (height <= ((float)IrradianceField.Resolution)) { descended = true; break; }
        }
        var position = 0f;
        var clearance = 0f;

        if (descended) {
            height = ((float)IrradianceCells.LaunchStart);
            while (budget > 0) {
                budget--;
                var ball = answers[(samples.Count % answers.Length)];

                samples.Add(item: height);
                if (!(ball > 0f) || !float.IsFinite(f: ball)) { break; }
                position = height;
                clearance = ball;
                var delta = (target - height);

                if (ball >= delta) { position = target; clearance = (ball - delta); break; }
                height += ball;
            }
        }
        return new LaunchResult(Budget: budget, Clearance: clearance, Position: position,
            Samples: samples, Success: (descended && (clearance > 0f)));
    }
    private static LaunchResult SharedLaunchLoop(float[] answers, int budget, int maximum, float target) {
        var samples = new List<float>();
        var height = ((float)IrradianceCells.LaunchStart);
        var descended = false;
        var position = 0f;
        var clearance = 0f;

        for (var step = 0; ((step < maximum) && (budget > 0)); step++) {
            budget--;
            var ball = answers[(samples.Count % answers.Length)];

            samples.Add(item: height);
            if (!(ball > 0f) || !float.IsFinite(f: ball)) { break; }
            if (!descended) {
                height -= ball;
                if (height <= ((float)IrradianceField.Resolution)) {
                    descended = true;
                    height = ((float)IrradianceCells.LaunchStart);
                }
                continue;
            }
            position = height;
            clearance = ball;
            var delta = (target - height);

            if (ball >= delta) { position = target; clearance = (ball - delta); break; }
            height += ball;
        }
        return new LaunchResult(Budget: budget, Clearance: clearance, Position: position,
            Samples: samples, Success: (descended && (clearance > 0f)));
    }

    private sealed record LaunchResult(int Budget, float Clearance, float Position, List<float> Samples, bool Success);

    private static string Source(string path) => File.ReadAllText(path: RepositoryPaths.Resolve(
        relativePath: $"src/Puck.SdfVm/Assets/Shaders/Sdf/{path}"));
}
