using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>A rigid body's live scale (<c>bodies.scale</c>) changes its root matrix without rebuilding the shared
/// program. A frame with nothing delivered moves nothing. Held on a session view, which reads its
/// destination through the same <see cref="WorldBodyStampCensus"/> the local scene does.</summary>
[Collection(SceneProbeCollection.Name)]
public sealed class BodyStampScaleLawTests {
    // The body wears a creation look whose one shape sits one unit above the root, so its packed height is its scale.
    private static WorldDefinition Document() {
        var creation = CreationFixtures.Prototype(document: CreationFixtures.Document(
            name: "orb",
            palette: CreationFixtures.GreyAndBlue,
            shapes: [(CreationFixtures.UnitSphereShape with { Position = new Vector3(x: 0f, y: 1f, z: 0f) })]
        ));

        return Fixtures.WithBodyScale(
            initial: FixedQ4816.One,
            source: CreationDomainParentLawTests.Definition(creation: creation)
        );
    }
    private static WorldSnapshot Snapshot(double scale, ulong tick) => new(
        Entries: new EntitySnapshot[] {
            new(
                Index: 0,
                Position: Vector3.Zero,
                Orientation: Quaternion.Identity,
                BodyColor: Vector3.One,
                Active: true,
                Kit: 0,
                Look: 0,
                CatalogRig: 0,
                Continuity: default,
                State: new long[] { FixedQ4816.FromDouble(value: scale).Value }
            ),
        },
        Revision: 1,
        StepTicks: 1680UL,
        Tick: tick
    );
    private static float ShapeHeight(SdfFrame frame) => Vector3.Transform(Vector3.UnitY, Assert.Single(frame.MeshInstances).Matrix).Y;
    private static int[] Revision(ISdfSceneEmitter emitter) {
        var revision = new int[emitter.RevisionComponentCount];

        emitter.WriteRevision(destination: revision);

        return revision;
    }

    [Fact]
    public void AScaleRegisterWriteChangesTheCopyMatrixWithoutRebuildingTheProgram() {
        var session = new WorldSessionMirror(placeholder: Document());

        session.DeliverDefinition(definition: Document(), version: default);
        session.DeliverSnapshot(snapshot: Snapshot(
            scale: 1d,
            tick: 1UL
        ));

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

        // An idle frame: nothing delivered, so the revision stays put.
        var settled = Revision(emitter: emitter);

        Assert.Equal(
            actual: Revision(emitter: emitter),
            expected: settled
        );

        // A new scale changes the root matrix while leaving the geometry revision unchanged.
        session.DeliverSnapshot(snapshot: Snapshot(
            scale: 0.5d,
            tick: 2UL
        ));

        var moved = Revision(emitter: emitter);

        Assert.Equal(
            actual: moved,
            expected: settled
        );
        Assert.Equal(
            actual: ShapeHeight(frame: Frame()),
            expected: 0.5f,
            tolerance: 1e-4f
        );
        Assert.Equal(1, source.ProgramBuilds);
    }
}
