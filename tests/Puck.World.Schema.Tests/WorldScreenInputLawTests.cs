using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Laws for a screen row's input destination: a document that declares passthrough refuses by name, a
/// simulation screen's mapping comes from the row alone, the screen glass's bezel maps to no pixel, and a
/// <c>$pointer</c> read maps its ray without allocating whether it hits, lands on the bezel or misses.</summary>
public sealed class WorldScreenInputLawTests {
    // The face coordinate the screen glass shows image coordinate `image` at: its inverse, run backwards.
    private static double FaceOf(double image) {
        var glass = ((SourceWarpInverse.Affine)WorldScreenMappings.Glass.Inverse!);

        return ((image - glass.M13) / glass.M11);
    }
    // A screen facing +z at (1, 2, 3), two units wide and one and a half tall.
    private static WorldScreen Screen(SourceDestination? input) => new(
        HalfDepth: 0.1f,
        HalfHeight: 0.75f,
        HalfWidth: 1f,
        Index: 0,
        Origin: new DocumentVector3(value: new Vector3(
            x: 1f,
            y: 2f,
            z: 3f
        )),
        Right: new DocumentVector3(value: Vector3.UnitX),
        Round: 0f,
        Route: new WorldScreenRoute(
            EngageRadius: 0f,
            Engageable: false,
            Input: input
        ),
        Source: new WorldScreenSource.None(),
        Up: new DocumentVector3(value: Vector3.UnitY)
    );
    // A ray straight down −z onto the face point (u, v).
    private static SourceRay RayAt(double u, double v) => new(
        Direction: FixedVector3.FromVector3(value: -Vector3.UnitZ),
        Origin: FixedVector3.FromVector3(value: new Vector3(
            x: ((float)(1.0 + (((2.0 * u) - 1.0) * 1.0))),
            y: ((float)(2.0 - (((2.0 * v) - 1.0) * 0.75))),
            z: 10f
        ))
    );

    [Fact]
    public void ADocumentThatDeclaresPassthroughRefusesByName() {
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: new WorldDefinition(ScreensRaw: [Screen(input: SourceDestination.Passthrough)]),
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "screens[0].route.input 'Passthrough' is refused"
        );

        foreach (var input in new SourceDestination?[] { null, SourceDestination.Presentation, SourceDestination.Simulation }) {
            Assert.True(
                condition: WorldDefinitionValidator.TryValidateLocally(
                    definition: new WorldDefinition(ScreensRaw: [Screen(input: input)]),
                    reason: out var accepted
                ),
                userMessage: accepted
            );
        }
    }
    // An engageable screen's emulator view takes a crop in source pixels, a pixel aspect inside its bound and a
    // #RRGGBB fill, and only an engageable screen authors one; each refusal names its member.
    [Fact]
    public void AScreensDisplayRefusesByNameWhatNoEmulatorViewShows() {
        WorldScreen Engageable(WorldScreenDisplay display, bool engageable = true) => (Screen(input: null) with {
            Route = new WorldScreenRoute(
                Display: display,
                EngageRadius: 3f,
                Engageable: engageable
            ),
        });

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: new WorldDefinition(ScreensRaw: [Engageable(display: new WorldScreenDisplay(
                    Crop: new WorldScreenDisplayCrop(Bottom: 8, Top: 8),
                    Fill: "#102030",
                    PixelAspect: (8f / 7f)
                ))]),
                reason: out var accepted
            ),
            userMessage: accepted
        );

        foreach (var (display, engageable, refusal) in new (WorldScreenDisplay, bool, string)[] {
            (new WorldScreenDisplay(Crop: new WorldScreenDisplayCrop(Left: -1)), true, "screens[0].route.display.crop.left -1 must be within 0..4096"),
            (new WorldScreenDisplay(Crop: new WorldScreenDisplayCrop(Right: 5000)), true, "screens[0].route.display.crop.right 5000 must be within 0..4096"),
            (new WorldScreenDisplay(PixelAspect: 0f), true, "screens[0].route.display.pixelAspect 0 must be a finite ratio"),
            (new WorldScreenDisplay(PixelAspect: float.NaN), true, "screens[0].route.display.pixelAspect NaN must be a finite ratio"),
            (new WorldScreenDisplay(PixelAspect: 5f), true, "screens[0].route.display.pixelAspect 5 must be a finite ratio"),
            (new WorldScreenDisplay(Fill: "black"), true, "screens[0].route.display.fill 'black' must be #RRGGBB"),
            (new WorldScreenDisplay(), false, "screens[0].route.display is authored but engageable is false"),
        }) {
            Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
                definition: new WorldDefinition(ScreensRaw: [Engageable(display: display, engageable: engageable)]),
                reason: out var reason
            ));
            Assert.Contains(
                actualString: reason,
                expectedSubstring: refusal
            );
        }
    }
    [Fact]
    public void ASimulationScreenMapsARayToItsSourcePixelFromTheRowAlone() {
        var mapping = WorldScreenMappings.Of(
            screen: Screen(input: SourceDestination.Simulation),
            source: SourceHandle.Producer(name: "cabinet"),
            sourceHeight: 144,
            sourceWidth: 160
        );

        Assert.True(
            condition: mapping.TryValidate(refusal: out var refusal),
            userMessage: refusal
        );
        Assert.Equal(
            expected: SourceDestination.Simulation,
            actual: mapping.Destination
        );

        // The glass insets the image inside the bezel on every side, so image point (x, y) sits at the face point its
        // inverse takes to it.
        var hit = mapping.MapRay(ray: RayAt(
            u: FaceOf(image: (40.5 / 160)),
            v: FaceOf(image: (100.5 / 144))
        ));

        Assert.True(condition: hit.IsOnSource);
        Assert.Equal(
            expected: (40L, 100L),
            actual: (hit.PixelX, hit.PixelY)
        );
        Assert.Equal(
            expected: hit,
            actual: WorldScreenMappings.Of(
                screen: Screen(input: SourceDestination.Simulation),
                source: SourceHandle.Producer(name: "cabinet"),
                sourceHeight: 144,
                sourceWidth: 160
            ).MapRay(ray: RayAt(
                u: FaceOf(image: (40.5 / 160)),
                v: FaceOf(image: (100.5 / 144))
            ))
        );
        Assert.Equal(
            expected: SourceHitOutcome.OutsideWarp,
            actual: mapping.MapRay(ray: RayAt(
                u: (FaceOf(image: 0.0) / 2),
                v: 0.5
            )).Outcome
        );
    }
    [Fact]
    public void APointerReadMapsItsRayWithoutAllocating() {
        // The operands a $pointer rule compiles: one per facet, over the row's source-normalized mapping.
        var mapping = WorldScreenMappings.Normalized(screen: Screen(input: SourceDestination.Simulation));
        var operands = new[] { PointerFacet.X, PointerFacet.Y, PointerFacet.On }.Select(selector: facet => new PointerOperand(
            facet: facet,
            mapping: mapping,
            screenIndex: 0,
            seat: 0
        )).ToArray();
        var hit = RayAt(
            u: 0.5,
            v: 0.25
        );
        var bezel = RayAt(
            u: (FaceOf(image: 0.0) / 2),
            v: 0.5
        );
        var miss = RayAt(
            u: 2.0,
            v: 0.5
        );

        Assert.Equal(expected: SourceHitOutcome.OnSource, actual: mapping.MapRay(ray: hit).Outcome);
        Assert.Equal(expected: SourceHitOutcome.OutsideWarp, actual: mapping.MapRay(ray: bezel).Outcome);
        Assert.Equal(expected: SourceHitOutcome.OutsidePlacement, actual: mapping.MapRay(ray: miss).Outcome);
        Assert.Equal(expected: 1L, actual: operands[2].Read(ray: hit));
        Assert.All(
            action: ray => Assert.Equal(expected: 0L, actual: operands[2].Read(ray: ray)),
            collection: new SourceRay?[] { bezel, miss, null }
        );

        var sink = 0L;

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => {
            foreach (var operand in operands) {
                sink ^= operand.Read(ray: hit);
                sink ^= operand.Read(ray: bezel);
                sink ^= operand.Read(ray: miss);
            }
        }));
        GC.KeepAlive(obj: sink);
    }
}
