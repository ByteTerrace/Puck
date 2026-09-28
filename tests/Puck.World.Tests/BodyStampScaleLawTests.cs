using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>THE LAW: a body stamp bakes its body's live scale (<c>bodies.scaleRow</c>) at each rebuild, and a
/// state-only write of that cell moves the scene's revision, so the next frame rebuilds and packs the stamp at the new
/// scale; a frame with nothing delivered moves nothing. Held on a session view, which reads its destination through
/// the same <see cref="WorldBodyStampCensus"/> the local scene does.</summary>
public sealed class BodyStampScaleLawTests {
    // The body wears a creation look whose one shape sits one unit above the root, so its packed height is its scale.
    private static WorldDefinition Document(double scale) {
        var creation = CreationFixtures.Prototype(document: CreationFixtures.Document(
            name: "orb",
            palette: CreationFixtures.GreyAndBlue,
            shapes: [(CreationFixtures.UnitSphereShape with { Position = new Vector3(x: 0f, y: 1f, z: 0f) })]
        ));
        var definition = CreationDomainParentLawTests.Definition(creation: creation);
        var row = new WorldStateRow(
            Name: CellName.Parse(candidate: "scale"),
            Kind: CellKind.Fixed,
            Min: FixedQ4816.FromDouble(value: 0.05).Value,
            Max: FixedQ4816.One.Value,
            Capacity: 8,
            Cells: [new StateCell(
                Key: CellName.Parse(candidate: "0"),
                Value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: scale).Value)
            )]
        );

        return (definition with {
            PopulationRaw = (definition.Population with { ScaleRow = "scale" }),
            StateRaw = ((definition.StateRaw ?? new WorldStateSection()) with {
                World = [.. (definition.StateRaw?.World ?? []), row],
            }),
        });
    }
    // The packed height of the stamp's shape: the pool's first registration sits past the avatar catalog's slots, and
    // its first shape one slot past its root.
    private static float ShapeHeight(SdfFrame frame) => frame.DynamicTransforms[(WorldRigCatalog.DynamicTransformCapacity + 1)].Position.Y;
    private static int[] Revision(ISdfSceneEmitter emitter) {
        var revision = new int[emitter.RevisionComponentCount];

        emitter.WriteRevision(destination: revision);

        return revision;
    }

    [Fact]
    public void AScaleRowWriteMovesTheRevisionAndTheNextPackUsesTheNewScale() {
        var session = new WorldSessionMirror(placeholder: Document(scale: 1d));

        session.DeliverDefinition(definition: Document(scale: 1d), version: default);
        session.DeliverSnapshot(snapshot: new WorldSnapshot(
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
                    Continuity: default
                ),
            },
            Revision: 1,
            StepTicks: 1680UL,
            Tick: 1UL
        ));

        var emitter = new WorldSessionSceneEmitter(
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

        // A state-only write of the body's scale cell moves the census component, and the next frame packs at it.
        session.DeliverState(
            definition: Document(scale: 0.5d),
            stamp: new WorldStateStamp(EngineTick: 3360UL, Everything: true, MovedRows: ReadOnlyMemory<int>.Empty, Tick: 2UL),
            version: default
        );

        var moved = Revision(emitter: emitter);

        Assert.NotEqual(
            actual: moved,
            expected: settled
        );
        Assert.Equal(
            actual: moved[..3],
            expected: settled[..3]
        );
        Assert.Equal(
            actual: ShapeHeight(frame: Frame()),
            expected: 0.5f,
            tolerance: 1e-4f
        );
    }
}
