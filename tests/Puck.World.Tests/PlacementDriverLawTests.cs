using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Scenery drivers animate without borrowing an inhabitant's facts or duplicating static geometry.</summary>
public sealed class PlacementDriverLawTests {
    [Fact]
    public void DriverOnlyPlacementCannotBypassAnimatedDistributionRestrictions() {
        var document = CreationDomainParentLawTests.FollowRig() with {
            Drivers = [new CreationDriverDocument(
                Name: "sway",
                Signal: "time",
                Cadence: 1f
            )],
        };
        var creation = CreationDomainParentLawTests.Prototype(document: document);
        var placement = new WorldPlacement(
            "grass",
            creation.Id,
            Vector3.Zero,
            0f,
            1f,
            Distribution: new WorldDistribution(
                Region: new WorldDistributionRegion.Scatter(
                    CellSize: 1f,
                    Depth: 10,
                    Radius: 1,
                    Seed: 3u,
                    Spacing: 3,
                    Width: 10
                ),
                Fill: new WorldSequence(
                    Name: WorldSequence.None,
                    Offset: 0,
                    Step: 0f
                )
            )
        );
        var definition = Fixtures.BuildDocument() with {
            CreationsRaw = [creation],
            PlacementRowsRaw = [placement],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "distribution/mirror facets are static-stamp-only"
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition with { PlacementRowsRaw = [placement with { Distribution = null }] },
                reason: out reason
            ),
            userMessage: reason
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void UninhabitedTimeDriverMovesOnlyWhenItsOwnGateHolds(bool requireGrounded) {
        var shape = new ShapeDocument(
            Id: 0,
            Name: "blade",
            Type: SdfSolidPrimitive.Box,
            Position: Vector3.UnitY,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(
                x: .05f,
                y: 1f,
                z: .01f
            ),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: null,
            Swings: [new ShapeSwingDocument(
                    Driver: "wind",
                    Pivot: Vector3.Zero,
                    Axis: Vector3.UnitZ,
                    Amplitude: .5f
                )]
        );
        var creation = CreationDomainParentLawTests.Prototype(document: new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "wind",
            Palette: [new(
                    "#779944",
                    null,
                    null,
                    null
                )],
            Shapes: [shape],
            Frames: null,
            Drivers: [new CreationDriverDocument(
                    Name: "wind",
                    Signal: "time",
                    Cadence: 1f,
                    When: (requireGrounded
            ? ["Grounded"]
            : null),
                    BlendInSeconds: 0f
                )]
        ));
        var placement = new WorldPlacement(
            "grass",
            creation.Id,
            new Vector3(
                x: 3f,
                y: 0f,
                z: 4f
            ),
            0f,
            1f
        );

        Assert.True(condition: WorldPlacementStamper.IsAnimated(creation: creation));
        Assert.False(condition: WorldPlacementStamper.IsStaticStamp(
            creation: creation,
            placement: placement
        ));
        Assert.Equal(
            0,
            WorldPlacementStamper.StaticStampInstances(
                [creation],
                [placement]
            )
        );
        var pool = new WorldStampPool();

        pool.Reconcile(
            bodyStamps: [],
            creations: [creation],
            dynamics: [],
            placements: [placement]
        );
        var definition = CreationDomainParentLawTests.Definition(creation);
        var client = CreationDomainParentLawTests.Client(definition: definition);
        var transforms = new DynamicTransform[WorldStampPool.DynamicSlotCount];

        CreationDomainParentLawTests.Advance(
            client: client,
            pool: pool,
            tick: 1,
            transforms: transforms
        );
        var root = transforms[0];
        var initial = transforms[1];

        for (ulong tick = 2; (tick <= 31); tick++) {
            CreationDomainParentLawTests.Advance(
                client: client,
                pool: pool,
                tick: tick,
                transforms: transforms
            );
        }
        Assert.Equal(
            root.Position,
            transforms[0].Position
        );
        Assert.Equal(
            root.Orientation,
            transforms[0].Orientation
        );
        var movement = Vector3.Distance(
            value1: initial.Position,
            value2: transforms[1].Position
        );

        if (requireGrounded) {
            // The fixture has a grounded body, but this placement does not ride it.
            Assert.InRange(
                actual: movement,
                high: 1e-6f,
                low: 0f
            );
        } else {
            Assert.InRange(
                actual: movement,
                high: .5f,
                low: .1f
            );
        }
    }
}
