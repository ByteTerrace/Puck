using System.Numerics;
using Puck.SdfVm;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>THE LAW: a body's stamp follows the look its snapshot entry carries. When the server moves a body's look (a
/// look assignment's selector following the body's state cell) the snapshot carries the new look index with a moved
/// revision, and the next frame re-latches that body's registration onto the new look's creation. Held on a session
/// view, which reads its destination through the same <see cref="WorldBodyStampCensus"/> the local scene does.</summary>
[Collection(SceneProbeCollection.Name)]
public sealed class BodyStampLookLawTests {
    // Two creation looks whose one shape sits one and two units above the root, so the packed height names the look.
    private static WorldDefinition Document() {
        var low = CreationFixtures.Prototype(document: CreationFixtures.Document(
            name: "low",
            palette: CreationFixtures.GreyAndBlue,
            shapes: [(CreationFixtures.UnitSphereShape with { Position = new Vector3(x: 0f, y: 1f, z: 0f) })]
        ));
        var high = CreationFixtures.Prototype(document: CreationFixtures.Document(
            name: "high",
            palette: CreationFixtures.GreyAndBlue,
            shapes: [(CreationFixtures.UnitSphereShape with { Position = new Vector3(x: 0f, y: 2f, z: 0f) })]
        ));

        return (CreationDomainParentLawTests.Definition(creation: low) with {
            CreationsRaw = [low, high],
            LookRowsRaw = [
                new WorldLook(
                    Name: "suited",
                    Source: new WorldLookSource.Creation(PrototypeId: low.Id),
                    Scale: 1f,
                    Motion: WorldLookMotion.Default
                ),
                new WorldLook(
                    Name: "pilot",
                    Source: new WorldLookSource.Creation(PrototypeId: high.Id),
                    Scale: 1f,
                    Motion: WorldLookMotion.Default
                ),
            ],
        });
    }
    private static WorldSnapshot Snapshot(byte look, int revision, ulong tick) => new(
        Entries: new EntitySnapshot[] {
            new(
                Index: 0,
                Position: Vector3.Zero,
                Orientation: Quaternion.Identity,
                BodyColor: Vector3.One,
                Active: true,
                Kit: 0,
                Look: look,
                CatalogRig: 0,
                Continuity: default
            ),
        },
        Revision: revision,
        StepTicks: 1680UL,
        Tick: tick
    );
    private static float ShapeHeight(SdfFrame frame) {
        var copy = Assert.Single(frame.MeshInstances);
        var target = Assert.IsType<WorldPickTarget>(copy.Identity);
        var shape = Document().Creations.Single(creation => creation.Id == target.Prototype).EngineDocument.Shapes![0];
        return Vector3.Transform(shape.Position.Value, copy.Matrix).Y;
    }

    [Fact]
    public void ASnapshotsMovedLookReLatchesTheBodysStamp() {
        var definition = Document();
        var session = new WorldSessionMirror(placeholder: definition);

        session.DeliverDefinition(definition: definition, version: default);
        session.DeliverSnapshot(snapshot: Snapshot(look: 0, revision: 1, tick: 1UL));

        var emitter = new WorldSessionSceneEmitter(domains: new WorldValueDomainGuard(),
            effectiveCameraName: null,
            mirror: session
        );
        var source = new SdfCompositionFrameSource(
            dresser: emitter,
            emitters: [emitter]
        );

        SdfFrame Frame() => source.CaptureFrame(
            deltaSeconds: 0f,
            height: WorldViewInstances.DefaultSessionHeight,
            interpolationAlpha: 0f,
            width: WorldViewInstances.DefaultSessionWidth
        );

        Assert.Equal(
            actual: ShapeHeight(frame: Frame()),
            expected: 1f,
            tolerance: 1e-4f
        );
        Assert.Equal(1, source.ProgramBuilds);

        session.DeliverSnapshot(snapshot: Snapshot(look: 1, revision: 2, tick: 2UL));

        Assert.Equal(
            actual: ShapeHeight(frame: Frame()),
            expected: 2f,
            tolerance: 1e-4f
        );

        // And back, as the cell flips again.
        session.DeliverSnapshot(snapshot: Snapshot(look: 0, revision: 3, tick: 3UL));

        Assert.Equal(
            actual: ShapeHeight(frame: Frame()),
            expected: 1f,
            tolerance: 1e-4f
        );
    }
}
