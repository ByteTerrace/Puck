using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Boot, quality selection and save retain one authored indirect tier and its independent source controls.</summary>
[Collection(SceneProbeCollection.Name)]
public sealed class WorldIndirectQualityLawTests {
    [InlineData(-1)]
    [InlineData(3)]
    [Theory]
    public void UnknownBootAndPresetTiersRefuseBeforePresentation(int ordinal) {
        var invalid = ((SdfIndirectTier)ordinal);
        WorldRenderDefaults[] rows = [
            new(Indirect: new(Tier: invalid)),
            new(LowRaw: new(ShadowTier.Off, false, 1f, Indirect: invalid)),
        ];

        foreach (var row in rows) {
            Assert.False(condition: WorldDefinitionValidator.TryValidate(Fixtures.BuildDocument() with { RenderRaw = row },
                neighbours: null, reason: out var reason));
            Assert.Contains(actualString: reason, expectedSubstring: "indirect");
        }
    }
}
