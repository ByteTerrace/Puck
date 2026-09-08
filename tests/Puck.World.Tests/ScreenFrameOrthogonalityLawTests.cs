using System.Numerics;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A screen row's frame is read twice by two subsystems that cannot compare notes: the client derives the
/// slab's orientation and sampled UV from the right/up pair, while <c>Server.WorldColliderSet.ScreenBox</c> projects
/// the slab's half-extents onto the authored axes. Only an orthogonal pair makes those the same solid, so the document
/// validator refuses a skewed one by name rather than letting the two halves disagree at runtime.</summary>
public sealed class ScreenFrameOrthogonalityLawTests {
    private const int LawScreenIndex = 1;

    private static WorldDefinition WithScreenFrame(Vector3 right, Vector3 up) => Fixtures.BuildDocument() with {
        ScreensRaw = [
            new WorldScreen(
                HalfDepth: 0.1f,
                HalfHeight: 1f,
                HalfWidth: 1f,
                Index: LawScreenIndex,
                Origin: new Vector3(
                    x: 0f,
                    y: 1f,
                    z: 0f
                ),
                Right: right,
                Round: 0f,
                Route: WorldScreenRoute.Passive,
                Source: new WorldScreenSource.TestPattern(
                    Height: 240,
                    Width: 320
                ),
                Up: up
            ),
        ],
    };

    public static IEnumerable<object[]> FrameCases() {
        // Orthogonal pairs — the axis-aligned frame, and the same frame turned 45 degrees about world up.
        yield return [Vector3.UnitX, Vector3.UnitY, true];
        yield return [Vector3.UnitZ, Vector3.UnitY, true];
        yield return [
            Vector3.Normalize(value: new Vector3(x: 1f, y: 0f, z: 1f)),
            Vector3.UnitY,
            true,
        ];
        // Linearly independent but skewed: admissible arithmetic, inadmissible geometry.
        yield return [
            Vector3.UnitX,
            Vector3.Normalize(value: new Vector3(x: 1f, y: 1f, z: 0f)),
            false,
        ];
        yield return [
            Vector3.UnitX,
            Vector3.Normalize(value: new Vector3(x: 0.99f, y: 0.141f, z: 0f)),
            false,
        ];
    }
    [MemberData(nameof(FrameCases))]
    [Theory]
    public void ASkewedScreenFrameRefusesByName(Vector3 right, Vector3 up, bool valid) {
        var admitted = WorldDefinitionValidator.TryValidate(
            definition: WithScreenFrame(
                right: right,
                up: up
            ),
            neighbours: null,
            reason: out var reason
        );

        Assert.Equal(
            actual: admitted,
            expected: valid
        );

        if (!valid) {
            Assert.Contains(
                actualString: reason,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "screens[0] right/up vectors must be orthogonal"
            );
        }
    }
    /// <summary>The orthogonality rule must not swallow the degeneracy rule it follows: a parallel pair is still
    /// refused as linearly dependent, naming the property a reader can act on.</summary>
    [Fact]
    public void AParallelScreenFrameStillRefusesAsLinearlyDependent() {
        _ = WorldDefinitionValidator.TryValidate(
            definition: WithScreenFrame(
                right: Vector3.UnitX,
                up: Vector3.UnitX
            ),
            neighbours: null,
            reason: out var reason
        );

        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "linearly independent"
        );
    }
}
