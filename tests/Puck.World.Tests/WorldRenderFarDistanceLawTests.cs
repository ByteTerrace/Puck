using Puck.SdfVm;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for <c>render.farDistance</c>: the validator admits exactly the representable band
/// [<see cref="WorldRenderDefaults.MinFarDistance"/>, <see cref="WorldRenderDefaults.MaxFarDistance"/>] and refuses
/// everything else BY NAME; absence resolves to <see cref="SdfFrame.DefaultFarDistance"/> bit-exactly (an unauthored
/// world marches to the same 40 it always did); an authored value threads through untouched.</summary>
public sealed class WorldRenderFarDistanceLawTests {
    private static WorldRenderDefaults Render(float? farDistance) => (WorldRenderDefaults.Absent with { FarDistance = farDistance });
    private static bool TryValidateLocal(WorldDefinition definition) => WorldDefinitionValidator.TryValidate(
        definition: definition,
        neighbours: null,
        reason: out _
    );
    private static bool ValidatesWith(float? farDistance) => TryValidateLocal(definition: (Fixtures.BuildDocument() with { RenderRaw = Render(farDistance: farDistance) }));

    [Theory]
    [InlineData(0f, "render.far-distance-zero")]
    [InlineData(-1f, "render.far-distance-negative")]
    [InlineData(0.5f, "render.far-distance-below-floor")]
    [InlineData(8193f, "render.far-distance-above-ceiling")]
    [InlineData(float.NaN, "render.far-distance-nan")]
    [InlineData(float.PositiveInfinity, "render.far-distance-infinite")]
    public void FarDistance_OutsideTheBand_RefusesByName_ControlDefaultClean(float farDistance, string lawId) {
        Laws.RefusalWithControl(
            lawId: lawId,
            deniedOutcome: () => ValidatesWith(farDistance: farDistance),
            controlOutcome: static () => ValidatesWith(farDistance: SdfFrame.DefaultFarDistance)
        );
    }
    [Theory]
    [InlineData(WorldRenderDefaults.MinFarDistance)]
    [InlineData(WorldRenderDefaults.MaxFarDistance)]
    [InlineData(200f)]
    public void FarDistance_InsideTheBand_Validates(float farDistance) {
        Assert.True(condition: ValidatesWith(farDistance: farDistance));
    }
    [Fact]
    public void FarDistance_Refusal_NamesTheField() {
        var admitted = WorldDefinitionValidator.TryValidate(
            definition: (Fixtures.BuildDocument() with { RenderRaw = Render(farDistance: 0f) }),
            neighbours: null,
            reason: out var reason
        );

        Assert.False(condition: admitted);
        Assert.Contains(expectedSubstring: "render.farDistance", actualString: reason);
    }

    [Fact]
    public void Absent_ResolvesToTheEngineDefaultBitExact() {
        Assert.Null(@object: WorldRenderDefaults.Absent.FarDistance);
        Assert.Equal(expected: SdfFrame.DefaultFarDistance, actual: WorldRenderFarDistance.Resolve(defaults: WorldRenderDefaults.Absent));
        Assert.Equal(expected: 40f, actual: WorldRenderFarDistance.Resolve(defaults: Fixtures.BuildDocument().Render));
    }
    [Fact]
    public void Authored_ThreadsThroughUntouched() {
        Assert.Equal(expected: 200f, actual: WorldRenderFarDistance.Resolve(defaults: Render(farDistance: 200f)));
        Assert.Equal(expected: WorldRenderDefaults.MaxFarDistance, actual: WorldRenderFarDistance.Resolve(defaults: Render(farDistance: WorldRenderDefaults.MaxFarDistance)));
    }
    [Fact]
    public void Frame_RefusesANonPositiveFarDistance_ControlDefaultClean() {
        // The engine-side twin of the validator's door: a frame whose far distance is not finite-positive is refused
        // before packing (SdfWorldEngine.PrepareFrame), never guarded per kernel. The frame record itself carries the
        // pinned default so a frame that never sets it is the byte-identical pre-field upload.
        var frame = new SdfFrame(
            Program: null!,
            ProgramChanged: false,
            Views: [],
            Time: 0f,
            WarpAmount: 0f
        );

        Assert.Equal(expected: 40f, actual: frame.FarDistance);
        Assert.Equal(expected: 40f, actual: SdfFrame.DefaultFarDistance);
    }
}
