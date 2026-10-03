using System.Text.RegularExpressions;
using Puck.Shaders;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfShadowAmortizationLawTests {
    private static string Source(string path) => Regex.Replace(input: File.ReadAllText(path: RepositoryPaths.Resolve(
        relativePath: $"{SdfKernelInterfaces.KernelDirectory}/{path}")), pattern: @"//[^\n]*", replacement: string.Empty);

    [Fact]
    public void OnlySecondaryStableSlotsInterleaveByTheJitterIndexWithAnOffSwitch() {
        var shadow = Source(path: "surface/sdf-shadow.hlsli");

        Assert.Contains(actualString: shadow, expectedSubstring: "(passGroup.temporal != 0u) && (passGroup.shadowAmortize != 0u)");
        Assert.Contains(actualString: shadow, expectedSubstring: "(shadowSlot > 0u) && (shadowSlot < passGroup.shadowSlotCount)");
        Assert.Contains(actualString: shadow, expectedSubstring: "sdfShadowMotionActive = secondary");
        Assert.Contains(actualString: shadow, expectedSubstring: "else if (secondary && hasLight)");
        Assert.Contains(actualString: Source(path: "surface/sdf-shadow-gather.hlsli"), expectedSubstring: "if (sdfShadowMotionActive)");
        Assert.Contains(actualString: shadow, expectedSubstring: "((p.pixel.x & 1u) | ((p.pixel.y & 1u) << 1u)) != (passGroup.historyFrames & 3u)");
        Assert.Contains(actualString: shadow, expectedSubstring: "lit && (decision != SDF_SHADOW_DECISION_REPROJECTED)");
        Assert.Contains(actualString: shadow, expectedSubstring: "marched = history[shadowSlot]");
        Assert.Single(collection: Regex.Matches(input: shadow, pattern: @"\bsoftShadowVisibility\("));
    }
    [Fact]
    public void EveryRejectionHasItsOwnPixelAndMarchDetailAndOwnershipCannotReproject() {
        var shadow = Source(path: "surface/sdf-shadow.hlsli");
        var names = new[] { "interleaved", "ownership", "light-motion", "occluder-motion", "receiver", "reprojected" };

        Assert.Equal(names, SdfWorldWorkDetails.Of(part: SdfWorldPackage.Parts.Shadow));
        foreach (var name in names) {
            Assert.Contains($"SDF_SHADOW_DECISION_{name.Replace(newChar: '_', oldChar: '-').ToUpperInvariant()}", shadow);
        }
        Assert.Matches(actualString: shadow, expectedRegexPattern: @"(?s)if \(\(passGroup.shadowOwnershipReject & bit\) != 0u\).*else if.*shadowLightReject.*else if.*sdfShadowGatherMoved.*else if \(!receiverValid\).*else if.*historyFrames & 3u");
        Assert.Contains(actualString: shadow, expectedSubstring: "if (lit && secondary) { puckCountShadowDecision(decision, shadowSlot, (sdfWorkSteps - before)); }");
        var counters = ShaderInterfaceHlsl.Generate(shaderInterface: SdfWorldInterfaces.World);

        Assert.Contains(actualString: counters, expectedSubstring: "PuckWorkShadowPixelsWord = 22u");
        Assert.Contains(actualString: counters, expectedSubstring: "puckAddWork(row + PuckWorkShadowPixelsWord, 1u)");
        Assert.Contains(actualString: counters, expectedSubstring: "puckAddWork(row + PuckWorkStepsWord, steps)");
        Assert.Contains(actualString: counters, expectedSubstring: "puckAddWork(row + PuckWorkShadowWord + (slot * 2u), steps)");
    }
    [Fact]
    public void MovingOccludersCompareEveryRowAndBothBoundsIncludingSuppressedDepartures() {
        var gather = Source(path: "surface/sdf-shadow-gather.hlsli");

        for (var row = 0; (row < 3); row++) {
            var index = ((row == 0) ? "row" : $"row + {row}u");

            Assert.Contains(actualString: gather, expectedSubstring: $"asuint(sdfDynamicTransforms[{index}]) != asuint(sdfPreviousDynamicTransforms[{index}])");
        }
        Assert.Contains(actualString: gather, expectedSubstring: "previous.xyz += sdfPreviousDynamicTransforms[3u * meta.y].xyz");
        Assert.Matches(actualString: gather, expectedRegexPattern: @"(?s)sdfInstancePassesTileCone\(bound, origin.*\|\|\s*sdfInstancePassesTileCone\(previous, origin");
        Assert.Contains(actualString: gather, expectedSubstring: "sdfShadowMovedFlat(lane)");
        Assert.Contains(actualString: gather, expectedSubstring: "sdfShadowMovedCandidate(instanceOffset, index, origin, direction, chord, inverseAperture, inflate)");
    }
    [Fact]
    public void ReceiverValidationUsesTheSharedFivePercentRuleAndTheLastWriterStamp() {
        var shadow = Source(path: "surface/sdf-shadow.hlsli");
        var reprojection = Source(path: "frame/sdf-reprojection.hlsli");

        Assert.Contains(actualString: shadow, expectedSubstring: "sdfReprojection(record, currentPoint, previousPixel, previousT)");
        Assert.Contains(actualString: shadow, expectedSubstring: "shadowHistory[word + 3u] != passGroup.historyFrames");
        Assert.Contains(actualString: shadow, expectedSubstring: "sdfHistoryReceiverMatches(visibility.identity, shadowHistory[word + 1u], previousT, asfloat(shadowHistory[word + 2u]))");
        Assert.Contains(actualString: reprojection, expectedSubstring: "identity == previousIdentity");
        Assert.Contains(actualString: reprojection, expectedSubstring: "SdfHistoryDepthTolerance = 0.05");
        Assert.Contains(actualString: reprojection, expectedSubstring: "abs(historyT - previousT) <= (SdfHistoryDepthTolerance * previousT)");
        Assert.Contains("sdfHistoryReceiverMatches(hitIdentity, historyIdentity, previousT, historyT)", Source(path: "passes/sdf-resolve.comp.hlsl"));
        Assert.Contains(actualString: shadow, expectedSubstring: "sdfWorkTexels += SDF_SHADOW_HISTORY_WORDS");
    }
    [Fact]
    public void RejectedShadowsAlsoRejectColorHistorySoTheResolveCannotKeepTheirTrail() {
        Assert.Contains("shadowHistoryRW[word + 4u] = shadowReactive", Source(path: "surface/sdf-shadow.hlsli"));
        Assert.Contains("reactivity = max(reactivity, (float)shadowHistory[word + 4u])", Source(path: "passes/sdf-hit-stages.hlsli"));
        Assert.Contains("!worldSoftShadowsDisabled() && (passGroup.shadowSlotCount > 1u)", Source(path: "passes/sdf-hit-stages.hlsli"));
    }
}
