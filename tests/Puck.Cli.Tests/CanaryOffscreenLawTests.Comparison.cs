using System.Text.Json.Nodes;
using Puck.Assets;
using Puck.Cli.Canary;
using Xunit;

namespace Puck.Cli.Tests;

public sealed partial class CanaryOffscreenLawTests {
    [Fact]
    public void ImageComparisonMeasuresBoxFilteredRgbInTheSelectedRegion() {
        var reference = new byte[((4 * 4) * 4)];

        for (var y = 0; (y < 4); y++) {
            for (var x = 0; (x < 4); x++) {
                var offset = (((y * 4) + x) * 4);
                // Left boxes average 10.5 and round to 11; right boxes average 110.5.
                reference[offset] = ((byte)(((x < 2) ? 10 : 110) + (y % 2)));
                reference[(offset + 1)] = 20;
                reference[(offset + 2)] = 30;
                reference[(offset + 3)] = 0;
            }
        }
        PngEncoder.Write(path: Path.Combine(path1: m_root, path2: "reference.png"), rgba: reference, width: 4, height: 4);
        byte[] capture = [11, 20, 30, 255, 0, 0, 0, 255, 17, 20, 30, 255, 0, 0, 0, 255];

        PngEncoder.Write(path: Path.Combine(path1: m_root, path2: "capture.png"), rgba: capture, width: 2, height: 2);
        var assertion = new CanaryImageComparisonAssertion(Bottom: 1, Capture: "capture.png", Height: 2, Holds: true,
            Left: 0, MaxErrorCodes: 6, MeanErrorCodes: 1, Name: "comparison", Reference: "reference.png",
            ReferenceScale: 2, Right: 0.5, Top: 0, Width: 2);

        Assert.True(condition: Evaluate(value: assertion).Passed);
        Assert.False(condition: Evaluate(value: assertion with { MaxErrorCodes = 5 }).Passed);
        Assert.False(condition: Evaluate(value: assertion with { MeanErrorCodes = 0.99 }).Passed);
        Assert.True(condition: Evaluate(value: assertion with { MeanErrorCodes = 0.99, Holds = false }).Passed);
        Assert.False(condition: Evaluate(value: assertion with { Right = 1 }).Passed);

        CanaryEvaluation Evaluate(CanaryImageComparisonAssertion value) => CanaryAssertions.Evaluate(
            leg: Leg(value), primaryTranscript: Transcript(runDirectory: m_root));
    }
    [Fact]
    public void ImageComparisonNeverPassesABrokenPairInEitherDirection() {
        Gray(fileName: "capture.png", code: 16, width: 2, height: 2);
        foreach (var holds in new[] { true, false }) {
            var assertion = new CanaryImageComparisonAssertion(Bottom: 1, Capture: "capture.png", Height: 2, Holds: holds,
                Left: 0, MaxErrorCodes: 0, MeanErrorCodes: 0, Name: "comparison", Reference: "absent.png",
                ReferenceScale: 2, Right: 1, Top: 0, Width: 2);

            Assert.False(condition: Evaluate(value: assertion).Passed);
            Assert.False(condition: Evaluate(value: assertion with { Reference = "capture.png" }).Passed);
            File.WriteAllText(path: Path.Combine(path1: m_root, path2: "broken.png"), contents: "not a PNG");
            Assert.False(condition: Evaluate(value: assertion with { Reference = "broken.png" }).Passed);
        }
        CanaryEvaluation Evaluate(CanaryImageComparisonAssertion value) => CanaryAssertions.Evaluate(
            leg: Leg(value), primaryTranscript: Transcript(runDirectory: m_root));
    }
    [InlineData("", true)]
    [InlineData("referenceScale", false)]
    [InlineData("maxErrorCodes", false)]
    [InlineData("meanErrorCodes", false)]
    [InlineData("holds", false)]
    [Theory]
    public void ImageComparisonRequiresEveryPartOfItsClaim(string omitted, bool expected) {
        const string Original = "{ \"type\": \"line\", \"name\": \"clean-wire\", \"stream\": \"stdout\", \"match\": \"contains\", \"text\": \"[wire.errors: 0 rejected]\", \"present\": true }";
        var assertion = """
            { "type": "imageComparison", "name": "reference", "capture": "capture.png", "reference": "reference.png",
              "referenceScale": 2, "extent": [2, 2], "region": [0, 0, 1, 1], "maxErrorCodes": 6, "meanErrorCodes": 1, "holds": true }
            """;

        if (omitted.Length != 0) {
            var row = JsonNode.Parse(assertion)!.AsObject();

            Assert.True(condition: row.Remove(propertyName: omitted));
            assertion = row.ToJsonString();
        }
        WriteManifestTree(id: "comparison", manifestBody: Manifest(backends: "\"backends\": [\"vulkan\", \"directx\"],", bootShape: "offscreen",
            id: "comparison", requirements: "[\"gpu\"]").Replace(comparisonType: StringComparison.Ordinal, newValue: assertion, oldValue: Original));
        var (loaded, error) = Load();
        Assert.Equal(actual: loaded, expected: expected);
        if (expected) { Assert.Empty(collection: error); }
    }
    [Fact]
    public void PairedComparisonDefersWithoutPassingAndUsesOnlyTheOppositeRun() {
        Gray(fileName: "capture.png", code: 16, width: 2, height: 2);
        Gray(fileName: "reference.png", code: 200, width: 2, height: 2);
        var other = Directory.CreateDirectory(path: Path.Combine(path1: m_root, path2: "opposite")).FullName;

        File.Copy(sourceFileName: Path.Combine(path1: m_root, path2: "capture.png"), destFileName: Path.Combine(path1: other, path2: "reference.png"));
        var assertion = new CanaryImageComparisonAssertion(Bottom: 1, Capture: "capture.png", Height: 2, Holds: true,
            Left: 0, MaxErrorCodes: 0, MeanErrorCodes: 0, Name: "paired", Reference: "reference.png",
            ReferenceScale: 1, Right: 1, Top: 0, Width: 2) { ReferenceOtherLeg = true };
        var pending = CanaryAssertions.Evaluate(leg: Leg(assertion), primaryTranscript: Transcript(m_root), deferPairedCaptures: true);

        Assert.Equal(expected: 1, actual: pending.Deferred);
        Assert.Empty(collection: pending.Results);
        Assert.False(condition: pending.Passed);
        Assert.True(condition: Evaluate(assertion, Transcript(other)).Passed);
        Assert.False(condition: Evaluate(assertion with { ReferenceOtherLeg = false }, Transcript(other)).Passed);
        foreach (var holds in new[] { true, false }) {
            Assert.False(condition: Evaluate(assertion with { Holds = holds }, null).Passed);
            Assert.False(condition: Evaluate(assertion with { Holds = holds, Reference = "absent.png" }, Transcript(other)).Passed);
        }
        CanaryEvaluation Evaluate(CanaryImageComparisonAssertion value, CanaryTranscript? counterpart) =>
            CanaryAssertions.Evaluate(leg: Leg(value), primaryTranscript: Transcript(m_root), otherLeg: counterpart);
    }
    [InlineData("referenceLeg", "\"current\"", true)]
    [InlineData("referenceLeg", "\"other\"", true)]
    [InlineData("referenceLeg", "\"positive\"", false)]
    [InlineData("referenceLeg", "null", false)]
    [InlineData("reference", "\"../escaped.png\"", false)]
    [InlineData("capture", "\"/absolute.png\"", false)]
    [InlineData("referenceScale", "0", false)]
    [InlineData("referenceScale", "1.5", false)]
    [InlineData("maxErrorCodes", "256", false)]
    [InlineData("meanErrorCodes", "-1", false)]
    [Theory]
    public void ImageComparisonRejectsInvalidOriginsPathsAndBounds(string member, string json, bool expected) {
        const string Original = "{ \"type\": \"line\", \"name\": \"clean-wire\", \"stream\": \"stdout\", \"match\": \"contains\", \"text\": \"[wire.errors: 0 rejected]\", \"present\": true }";
        var row = JsonNode.Parse("""
            { "type": "imageComparison", "name": "reference", "capture": "capture.png", "reference": "reference.png",
              "referenceScale": 2, "extent": [2, 2], "region": [0, 0, 1, 1], "maxErrorCodes": 6, "meanErrorCodes": 1, "holds": true }
            """)!.AsObject();

        row[member] = JsonNode.Parse(json);
        WriteManifestTree(id: "comparison", manifestBody: Manifest(backends: "\"backends\": [\"vulkan\", \"directx\"],", bootShape: "offscreen",
            id: "comparison", requirements: "[\"gpu\"]").Replace(oldValue: Original, newValue: row.ToJsonString(), comparisonType: StringComparison.Ordinal));
        var (loaded, error) = Load();
        Assert.Equal(actual: loaded, expected: expected);
        if (expected) { Assert.Empty(collection: error); }
    }
}
