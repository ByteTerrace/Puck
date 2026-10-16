using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Puck.Testing;
using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>The cost-report parity baseline's native half: prices the presentation dimension of
/// <c>Fixtures/browser-parity/presentation.world.json</c> through <see cref="BrowserCostAnalyzer"/>, exactly as the
/// <c>AnalyzeCosts</c> export spells it, and compares it with the committed <c>Fixtures/browser-parity/expected.json</c>.
/// The Node harness (<c>src/Puck.Dashboard/src/portal/tests/engine-wasm.test.cjs</c>) reads the same file against the
/// published engine, which makes the pair a cross-runtime proof: both runtimes price the identical bytes identically.
/// The test writes the freshly computed baseline beside its assembly (<see cref="TestRecords"/>) before comparing, and
/// <c>puck baselines browser-parity</c> promotes that copy over the committed one.</summary>
public sealed class BrowserParityRecordingTests {
    private const string PresentationCostEntry = "presentation-cost";

    private static string FixturePath(string fileName) => RepositoryPaths.Resolve(relativePath: ("tests/Puck.World.Browser.Tests/Fixtures/browser-parity/" + fileName));

    // The presentation dimension the cost analysis prices for the fixture document, in the export's camelCase wire form.
    private static JsonNode PresentationCost() {
        var analysis = BrowserCostAnalyzer.Analyze(utf8Json: File.ReadAllBytes(path: FixturePath(fileName: "presentation.world.json")));

        Assert.True(condition: analysis.Ok, userMessage: string.Join(separator: "; ", values: (analysis.Errors ?? []).Select(selector: static error => error.Message)));

        return JsonSerializer.SerializeToNode(
            options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
            value: analysis.Report!.Presentation
        )!;
    }

    [Fact]
    public void PresentationCostMatchesTheRecordedBaseline() {
        var computed = new JsonObject { [PresentationCostEntry] = PresentationCost() };
        var path = FixturePath(fileName: "expected.json");
        var rendered = (computed.ToJsonString(options: new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings(replacementText: "\n") + "\n");

        _ = TestRecords.Write(
            artifact: "browser-parity",
            bytes: Encoding.UTF8.GetBytes(s: rendered),
            fileName: Path.GetFileName(path: path)
        );
        Assert.True(condition: File.Exists(path: path), userMessage: $"no recorded baseline at {path}; run `puck baselines browser-parity`.");

        var expected = JsonNode.Parse(json: File.ReadAllText(path: path))!.AsObject();

        Assert.True(
            condition: JsonNode.DeepEquals(expected[PresentationCostEntry], computed[PresentationCostEntry]),
            userMessage: $"the presentation cost moved against {path}; re-record it with `puck baselines browser-parity` if the change was meant."
        );
    }
}
