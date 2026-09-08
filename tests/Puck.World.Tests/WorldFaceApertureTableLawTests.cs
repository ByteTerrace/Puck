using Xunit;

using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: which primitives open a walkable aperture is declared ONCE
/// (<see cref="WorldFaceApertures"/>). The derivation resolves a face's recipe from that table, the validator asks
/// only whether one exists, and <see cref="WorldFacePortalPolicy.TryAperture"/> builds the region by calling it — so
/// a primitive can never be admitted by one consumer and silently answered "no aperture" by the other.
/// </summary>
public sealed class WorldFaceApertureTableLawTests {
    private static WorldFaceFrame Frame { get; } = new(
        HalfDepth: FixedQ4816.FromDouble(value: 0.05),
        HalfHeight: FixedQ4816.FromDouble(value: 1.5),
        HalfWidth: FixedQ4816.FromDouble(value: 0.75),
        Normal: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One),
        Origin: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero),
        Right: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
        Up: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero)
    );

    private static WorldFaceRow Row(SdfSolidPrimitive? primitive) => new(
        Aperture: WorldFaceApertures.For(primitive: primitive),
        FaceName: "screen",
        Frame: Frame,
        PlacementId: "door",
        ScreenIndex: -1,
        ShapeId: 0,
        ShapeType: primitive,
        SlotStarved: false,
        Source: new WorldScreenSource.None()
    );

    [Fact]
    public void TheTableIsTheOnlyDecision_AndEveryConsumerAgreesWithItOnEveryPrimitive() {
        var floor = FixedQ4816.FromDouble(value: 0.5);

        foreach (var primitive in Enum.GetValues<SdfSolidPrimitive>()) {
            var row = Row(primitive: primitive);
            var opens = (WorldFaceApertures.For(primitive: primitive) is not null);

            Assert.Equal(
                actual: WorldFacePortalPolicy.TryAperture(
                aperture: out var aperture,
                crossingFloor: floor,
                row: in row
            ),
                expected: opens
            );
            Assert.Equal(actual: (aperture is not null), expected: opens);
        }

        // A face naming no shape at all opens nothing — the same answer through the same door, not a second rule.
        var shapeless = Row(primitive: null);

        Assert.Null(@object: shapeless.Aperture);
        Assert.False(condition: WorldFacePortalPolicy.TryAperture(
            aperture: out _,
            crossingFloor: floor,
            row: in shapeless
        ));
    }
    [Fact]
    public void TheBoxRecipeExtrudesTheFacesOwnFrame_NeverThinnerThanTheCrossingFloor() {
        var row = Row(primitive: SdfSolidPrimitive.Box);
        var recipe = Assert.IsType<WorldFaceApertureRecipe>(@object: row.Aperture);

        Assert.Equal(expected: SdfSolidPrimitive.Box, actual: recipe.Primitive);

        var thickFloor = FixedQ4816.FromDouble(value: 0.5);
        var thinFloor = FixedQ4816.FromDouble(value: 0.001);

        Assert.True(condition: WorldFacePortalPolicy.TryAperture(
            aperture: out var deep,
            crossingFloor: thickFloor,
            row: in row
        ));
        Assert.True(condition: WorldFacePortalPolicy.TryAperture(
            aperture: out var shallow,
            crossingFloor: thinFloor,
            row: in row
        ));

        var deepBox = Assert.IsType<WorldFaceAperture.Box>(@object: deep);
        var shallowBox = Assert.IsType<WorldFaceAperture.Box>(@object: shallow);

        // The floor wins when it is the larger term, the door's own half-depth when it is: max, never one or the other.
        Assert.Equal(expected: thickFloor, actual: deepBox.Depth);
        Assert.Equal(expected: Frame.HalfDepth, actual: shallowBox.Depth);
        Assert.Equal(expected: Frame, actual: deepBox.Frame);
    }
    [Fact]
    public void APrimitiveOutsideTheTableOpensNothing() {
        // The refused half of the one decision, with the Box control beside it so the negative discriminates.
        Assert.Null(@object: WorldFaceApertures.For(primitive: SdfSolidPrimitive.Sphere));
        Assert.NotNull(@object: WorldFaceApertures.For(primitive: SdfSolidPrimitive.Box));
    }
}
