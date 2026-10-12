using Puck.Maths;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Games.Tests;

/// <summary>
/// The shipped courtyard's solid field answers everywhere a body can be. It is the field the courtyard's authority would
/// solve contact against, and the one an adjacent world builds from its placements to collide across the seam. The
/// exact field refuses a position outside its frame, the cube over which its bounds prove no step overflows, and every
/// instruction joins that proof: the courtyard's banks and rocks are superellipsoids at exponent 2.05 and beyond, and
/// an instruction without an inclusion rule would empty the frame. So the field must still answer out to a million
/// units on every axis and diagonal.
/// </summary>
public sealed class ShippedSolidFrameLawTests {
    private const string Path = "src/Puck.World/Assets/worlds/moth-courtyard.puck";
    // 2²⁰ units: far past any authored terrain, well inside a superellipsoid program's frame of 2²⁸ or more.
    private const long Reach = (1L << 20);

    [Fact]
    public void TheCourtyardSolidFieldAnswersOutToAMillionUnits() {
        Assert.True(
            condition: WorldSolidField.TryBuild(built: out var field, definition: AuthoredGameFixtures.Load(relativePath: Path), reason: out var reason),
            userMessage: reason
        );

        foreach (var position in Far()) {
            Assert.True(
                condition: field!.Probe(distance: out _, gradient: out _, material: out _, position: in position),
                userMessage: $"the courtyard's solid field refuses {position}"
            );
        }
    }

    // The origin and the 26 points a million units out along every axis and diagonal.
    private static IEnumerable<FixedVector3> Far() {
        for (var x = -1; (x <= 1); x++) {
            for (var y = -1; (y <= 1); y++) {
                for (var z = -1; (z <= 1); z++) {
                    yield return new FixedVector3(
                        X: FixedQ4816.FromInteger(value: (x * Reach)),
                        Y: FixedQ4816.FromInteger(value: (y * Reach)),
                        Z: FixedQ4816.FromInteger(value: (z * Reach))
                    );
                }
            }
        }
    }
}
