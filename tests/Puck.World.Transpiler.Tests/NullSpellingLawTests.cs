using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>null</c> has one meaning where a member holds a name or a key. Written bare it is the
/// document's null, as it is in every other member, and a row or key actually named null is written as the computed
/// name <c>$"null"</c>. The decompiler prints each as the spelling that reads back as itself.
/// </summary>
public class NullSpellingLawTests {
    private const string Header = "schema: \"puck.world.definition.v1\"\n\n";

    private static JsonObject Lower(string body) => WorldSources.LowerSourceClean(source: (Header + body));

    [Fact]
    public void ABareNullInANameMemberIsTheDocumentsNull() {
        var bodies = Assert.IsType<JsonObject>(@object: Lower(body: "bodies {\n    capacityRow: null\n}\n")["bodies"]);

        Assert.True(condition: bodies.ContainsKey(propertyName: "capacityRow"));
        Assert.Null(@object: bodies["capacityRow"]);
    }
    [Fact]
    public void AComputedNullIsTheNameNull() {
        var bodies = Assert.IsType<JsonObject>(@object: Lower(body: "bodies {\n    capacityRow: $\"null\"\n}\n")["bodies"]);

        Assert.Equal(
            actual: bodies["capacityRow"]?.GetValue<string>(),
            expected: "null"
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ANameMemberHoldingNullOrTheNameNullPrintsAsItself(bool named) {
        var document = new JsonObject {
            ["bodies"] = new JsonObject { ["capacityRow"] = (named ? "null" : null) },
            ["schema"] = "puck.world.definition.v1",
        };
        var printed = WorldDecompiler.Decompile(root: document);

        Assert.Contains(
            actualString: printed,
            expectedSubstring: (named ? "capacityRow: $\"null\"" : "capacityRow: null")
        );

        _ = WorldSources.AssertRoundTrips(original: document);
    }
}
