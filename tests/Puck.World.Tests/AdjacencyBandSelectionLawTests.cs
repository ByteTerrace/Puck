using Puck.Assets.Documents;
using System.Numerics;
using Puck.World.Authoring;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the solid subset a band delivers across a seam (<see cref="WorldAdjacencyGeometry.Select"/>):
/// the budget is spent on what meets the seam plane before what merely falls inside the band, so a dense neighbour
/// still delivers the ground a crossing body stands on.</summary>
public sealed class AdjacencyBandSelectionLawTests {
    private const float FarBoxHalfExtent = 0.5f;
    private const float SeamZ = 24f;

    private static WorldPrototype Box(string id, float halfExtent) {
        var shape = new ShapeDocument(
            Id: 0,
            Name: id,
            Type: SdfSolidPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(x: halfExtent, y: 0.1f, z: halfExtent),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0);
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: id,
                Palette: null,
                Shapes: [shape],
                Frames: null
            ),
            source: id
        );

        return new WorldPrototype(Id: id, Document: canonical.Document, HashRaw: canonical.Hash);
    }
    private static WorldPlacement Solid(string id, string prototype, Vector3 position) =>
        new(Id: id, PrototypeId: prototype, Position: position, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f));
    // A south-facing seam at z = SeamZ whose band reaches deep enough to admit every row below.
    private static WorldFaceFrame Seam() => new WorldAdjacencyBoundary(
        Center: new DocumentVector3(x: 0f, y: 0f, z: SeamZ),
        OutwardYawDegrees: 0f,
        OutwardPitchDegrees: 0f,
        Width: 48f,
        Height: 16f
    ).CompileFrame();

    [Fact]
    public void TheGroundMeetingTheSeamOutranksNearerRowsThatStopShortOfIt() {
        var source = Fixtures.BuildDocument();
        var far = Box(id: "post", halfExtent: FarBoxHalfExtent);
        var ground = Box(id: "ground", halfExtent: SeamZ);
        var rows = new List<WorldPlacement>();

        // More posts than the band budget, every one authored ahead of the ground and inside the band, none touching
        // the seam plane.
        for (var index = 0; (index < (WorldAdjacencyGeometry.MaximumPlacementsPerBand + 4)); index++) {
            rows.Add(item: Solid(id: $"post{index}", prototype: far.Id, position: new Vector3(x: (index * 2f), y: 0f, z: (SeamZ - 10f))));
        }

        rows.Add(item: Solid(id: "ground", prototype: ground.Id, position: Vector3.Zero));

        var definition = source with {
            CreationsRaw = [far, ground],
            PlacementRowsRaw = rows,
        };
        var selection = WorldAdjacencyGeometry.Select(
            definition: definition,
            frame: Seam(),
            overlapDepth: FixedQ4816.FromDouble(value: 40.0)
        );

        Assert.True(condition: selection.Truncated);
        Assert.Equal(actual: selection.Placements.Count, expected: WorldAdjacencyGeometry.MaximumPlacementsPerBand);
        Assert.Equal(actual: selection.Placements[0].Id, expected: "ground");
        // The remaining budget goes to the posts in document order.
        Assert.Equal(actual: selection.Placements[1].Id, expected: "post0");
        Assert.Equal(actual: selection.Placements[^1].Id, expected: $"post{(WorldAdjacencyGeometry.MaximumPlacementsPerBand - 2)}");
    }
    [Fact]
    public void AnUnboundedShapeIsDeliveredBeforeEveryOtherRow() {
        var source = Fixtures.BuildDocument();
        var ground = Box(id: "ground", halfExtent: SeamZ);
        var plane = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: "net",
                Palette: null,
                Shapes: [new ShapeDocument(Id: 0, Name: "net", Type: SdfSolidPrimitive.Plane, Position: Vector3.Zero, Rotation: Quaternion.Identity, Scale: Vector3.One, Material: 0, Blend: SdfBlendOp.Union, Smooth: 0f, Group: 0)],
                Frames: null
            ),
            source: "net"
        );
        var net = new WorldPrototype(Id: "net", Document: plane.Document, HashRaw: plane.Hash);
        var definition = source with {
            CreationsRaw = [ground, net],
            PlacementRowsRaw = [
                Solid(id: "ground", prototype: ground.Id, position: Vector3.Zero),
                Solid(id: "net", prototype: net.Id, position: new Vector3(x: 0f, y: -16f, z: 0f)),
            ],
        };
        var selection = WorldAdjacencyGeometry.Select(
            definition: definition,
            frame: Seam(),
            overlapDepth: FixedQ4816.FromDouble(value: 4.0)
        );

        Assert.False(condition: selection.Truncated);
        Assert.Equal(actual: selection.Placements.Select(selector: static placement => placement.Id), expected: ["net", "ground"]);
    }
    [Fact]
    public void ARowOutsideTheBandIsNeverDelivered() {
        var source = Fixtures.BuildDocument();
        var far = Box(id: "post", halfExtent: FarBoxHalfExtent);
        var definition = source with {
            CreationsRaw = [far],
            PlacementRowsRaw = [
                Solid(id: "inside", prototype: far.Id, position: new Vector3(x: 0f, y: 0f, z: (SeamZ - 2f))),
                Solid(id: "beyond-the-depth", prototype: far.Id, position: new Vector3(x: 0f, y: 0f, z: (SeamZ - 30f))),
                Solid(id: "beside-the-aperture", prototype: far.Id, position: new Vector3(x: 60f, y: 0f, z: SeamZ)),
            ],
        };
        var selection = WorldAdjacencyGeometry.Select(
            definition: definition,
            frame: Seam(),
            overlapDepth: FixedQ4816.FromDouble(value: 4.0)
        );

        Assert.False(condition: selection.Truncated);
        Assert.Equal(actual: selection.Placements.Select(selector: static placement => placement.Id), expected: ["inside"]);
    }
}
