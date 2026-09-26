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

        // The glass insets the image by the bezel on every side, so image point (x, y) sits at face point
        // bezel + (1 − 2·bezel)·(x, y).
        const double Inner = (1.0 - (2.0 * WorldScreenMappings.Bezel));
        var hit = mapping.MapRay(ray: RayAt(
            u: (WorldScreenMappings.Bezel + (Inner * (40.5 / 160))),
            v: (WorldScreenMappings.Bezel + (Inner * (100.5 / 144)))
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
                u: (WorldScreenMappings.Bezel + (Inner * (40.5 / 160))),
                v: (WorldScreenMappings.Bezel + (Inner * (100.5 / 144)))
            ))
        );
        Assert.Equal(
            expected: SourceHitOutcome.OutsideWarp,
            actual: mapping.MapRay(ray: RayAt(
                u: (WorldScreenMappings.Bezel / 2),
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
            seat: 0
        )).ToArray();
        var hit = RayAt(
            u: 0.5,
            v: 0.25
        );
        var bezel = RayAt(
            u: (WorldScreenMappings.Bezel / 2),
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
