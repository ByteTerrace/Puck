using System.Numerics;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>THE LAW: a creation animates only by what its drivers, effectors or frames move. A timeline frame animates
/// where one of its transforms differs from its shape's authored pose, and an effector animates the creation whatever its
/// frames hold. A creation whose frames carry no transforms, or only its shapes' authored poses, moves nothing, so it
/// is not animated and a placement of it stamps statically. The creation is the <c>uploaded-sources</c> canary's
/// session ball, whose empty <c>idle</c> frame once sent it through the stamp pool.</summary>
public sealed class StaticFrameStampLawTests {
    private const string SessionWorld = "tests/Puck.World.Canaries/uploaded-sources/session.world.json";

    private static (WorldPrototype Creation, WorldPlacement Placement) Ball(IReadOnlyList<FrameDocument> frames) {
        var definition = AuthoredGameFixtures.Load(relativePath: SessionWorld);
        var creation = Assert.Single(collection: definition.Creations);

        return ((creation with { Document = (creation.Document with { Frames = frames }) }), Assert.Single(collection: definition.Placements));
    }
    private static FrameTransformDocument Rest(ShapeDocument shape) => new(
        Id: shape.Id,
        Position: shape.Position,
        Rotation: shape.Rotation,
        Scale: shape.Scale
    );

    [Fact]
    public void ACreationWhoseFramesCarryNoTransformsStampsStatically() {
        var (creation, placement) = Ball(frames: [new FrameDocument(
            Name: "idle",
            Transforms: []
        )]);

        Assert.False(condition: WorldPlacementStamper.IsAnimated(creation: creation));
        Assert.True(condition: WorldPlacementStamper.IsStaticStamp(
            creation: creation,
            placement: placement
        ));
    }
    [Fact]
    public void AFrameRestatingTheAuthoredPoseMovesNothing() {
        var (template, _) = Ball(frames: []);
        var shape = Assert.Single(collection: (template.Document.Shapes ?? []));

        var (creation, placement) = Ball(frames: [new FrameDocument(
            Name: "rest",
            Transforms: [Rest(shape: shape)]
        )]);

        Assert.False(condition: WorldPlacementStamper.IsAnimated(creation: creation));
        Assert.True(condition: WorldPlacementStamper.IsStaticStamp(
            creation: creation,
            placement: placement
        ));
    }
    [Fact]
    public void AnEffectorAnimatesACreationWhoseFramesMoveNothing() {
        var (template, _) = Ball(frames: [new FrameDocument(
            Name: "idle",
            Transforms: []
        )]);
        var creation = (template with {
            Document = (template.Document with {
                Effectors = [new CreationEffectorDocument(
                    Chain: ["ball"],
                    Name: "reach",
                    Target: new CreationEffectorTargetDocument(Kind: "ground"),
                    Tip: "ball"
                )],
            }),
        });

        // An effector moves its chain wherever its target lies, so the stamp stays in the pool where it is solved.
        Assert.True(condition: WorldPlacementStamper.IsAnimated(creation: creation));
    }
    [Fact]
    public void AFrameMovingAShapeAnimates() {
        var (template, _) = Ball(frames: []);
        var shape = Assert.Single(collection: (template.Document.Shapes ?? []));

        var (creation, placement) = Ball(frames: [new FrameDocument(
            Name: "lift",
            Transforms: [(Rest(shape: shape) with { Position = (shape.Position.Value + Vector3.UnitY) })]
        )]);

        Assert.True(condition: WorldPlacementStamper.IsAnimated(creation: creation));
        Assert.False(condition: WorldPlacementStamper.IsStaticStamp(
            creation: creation,
            placement: placement
        ));
    }
}
