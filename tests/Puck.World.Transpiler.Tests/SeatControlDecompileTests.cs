using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The views section's <c>seatControl</c> decompiles with its row type's model, as graph and post rows do: its
/// yaw reference, a closed word, prints bare and recompiles to the same document.</summary>
public sealed class SeatControlDecompileTests {
    private const string Views = """
        views {
          seatControl {
            yawReference: World
            minPitch: -60
            maxPitch: 60
          }
        }
        """;

    [Fact]
    public void TheYawReferenceLowersFromItsBareWord() {
        var document = WorldSources.LowerClean(body: Views);

        Assert.Equal(expected: "World", actual: document["views"]!["seatControl"]!["yawReference"]!.GetValue<string>());
    }
    [Fact]
    public void TheDecompilerPrintsTheYawReferenceBareAndItRecompilesToTheSameDocument() {
        var document = WorldSources.LowerClean(body: Views);
        var printed = WorldDecompiler.Decompile(root: document);

        Assert.Contains(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: "yawReference: World");
        Assert.DoesNotContain(actualString: printed, comparisonType: StringComparison.Ordinal, expectedSubstring: "yawReference: \"World\"");
        Assert.Equal(expected: document.ToJsonString(), actual: WorldSources.LowerSourceClean(source: printed).ToJsonString());
    }
}
