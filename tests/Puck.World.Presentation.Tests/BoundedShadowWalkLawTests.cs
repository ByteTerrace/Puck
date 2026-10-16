using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>
/// THE LAW: every shape of the debug world's played avatar, the Moth, is rejectable whole at a unit gain in the shadow and
/// ambient-occlusion walks. Its pool emits one dynamic instance per shape, and most of them certify only a distance gain
/// below one (gauges, non-uniform scales, scopes) or none (warps, approximate primitives), which the gain-scaled
/// whole-instance rejection must walk until the gap reaches the incumbent over the gain, or always. Bounded
/// (<see cref="Puck.SignedDistance.SdfProgram.InstanceBoundsSecondaryField"/>), each instance's field reads at least the
/// gap to its sphere there, so the same rejection skips it as soon as the gap reaches the incumbent.
/// </summary>
public sealed class BoundedShadowWalkLawTests {
    [Fact]
    public void EveryShapeOfThePlayedAvatarIsRejectableWholeAtAUnitGainInTheShadowAndAmbientWalks() {
        var definition = AuthoredGameFixtures.Load(relativePath: "src/Puck.World/Assets/worlds/debug.puck");
        var program = CreationFixtures.EmitPool(bodyScale: 1f, creation: definition.Creations.Single(predicate: creation => (creation.Id.Value == "moth")));
        var uncertified = 0;
        var slowed = 0;

        Assert.True(condition: program.InstancesComposable);
        for (var index = 0; (index < program.Instances.Count); index++) {
            var shapes = program.InspectInstance(index: index).Shapes;
            var gain = program.InstanceDistanceGain(index: index);

            uncertified += ((gain > 0f) ? 0 : shapes);
            slowed += (((gain > 0f) && (gain < 1f)) ? shapes : 0);
            Assert.True(condition: program.InstanceBoundsSecondaryField(index: index),
                userMessage: $"Instance {index} of the Moth, at distance gain {gain}, is not bounded.");
        }
        // The rule earns its place on this body: shapes the gain alone left unrejectable or slowed.
        Assert.True(condition: ((uncertified > 0) && (slowed > 0)), userMessage: $"{uncertified} uncertified, {slowed} slowed.");
    }
}
