using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a per-shape dynamic instance the stamp pool emits carries an INFLUENCE bound — a sphere no point of the
/// shape's field influence lies outside of. The tile cull applies that contract per tile cone and the interpreter's
/// per-sample influence skip applies it per sample, so a bound short of the primitive's reach clips geometry. The
/// pool once bounded a shape at 0.9 x max(scale), which covers neither a unit sphere nor a box's corners; the
/// packer's smooth halo hid it at tile granularity. Each shape here is checked against the same reach measure the
/// static stamper's <see cref="CreationStampEmitter.ShapeStampBound"/> takes.
/// </summary>
public sealed class WorldStampPoolBoundLawTests {
    private const string PrototypeId = "bounded";

    private static ShapeDocument Shape(int id, SdfSolidPrimitive type, Vector3 scale, float? dilate = null, float? onion = null) =>
        new(
            Id: id,
            Name: null,
            Type: type,
            Position: new Vector3(
                x: (1.5f * id),
                y: 0f,
                z: 0f
            ),
            Rotation: Quaternion.Identity,
            Scale: scale,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0,
            Dilate: dilate,
            Onion: onion
        );
    private static readonly ShapeDocument[] Shapes = [
        Shape(
            id: 0,
            scale: new Vector3(
                x: 0.2f,
                y: 0.3f,
                z: 0.1f
            ),
            type: SdfSolidPrimitive.Box
        ),
        Shape(
            id: 1,
            scale: new Vector3(value: 0.19f),
            type: SdfSolidPrimitive.Sphere
        ),
        Shape(
            id: 2,
            scale: new Vector3(
                x: 0.052f,
                y: 0.4755f,
                z: 0.052f
            ),
            type: SdfSolidPrimitive.Capsule
        ),
        Shape(
            id: 3,
            scale: new Vector3(
                x: 0.27f,
                y: 0.085f,
                z: 0.27f
            ),
            type: SdfSolidPrimitive.Cylinder
        ),
        Shape(
            dilate: 0.1f,
            id: 4,
            onion: 0.05f,
            scale: new Vector3(value: 0.25f),
            type: SdfSolidPrimitive.Sphere
        ),
    ];

    private static SdfProgram EmitPool() {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: PrototypeId,
                Palette: null,
                Shapes: Shapes,
                Frames: null,
                Noise: null
            ),
            source: PrototypeId
        );
        var creation = new WorldPrototype(
            Id: PrototypeId,
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
        var definition = (Fixtures.BuildGradientUpDocument(gradientUp: false) with {
            CreationsRaw = [creation],
            LookRowsRaw = [
                new WorldLook(
                    Name: "rig",
                    Source: new WorldLookSource.Creation(PrototypeId: PrototypeId),
                    Scale: 1f,
                    Motion: WorldLookMotion.Default
                ),
            ],
        });
        var pool = new WorldStampPool();

        pool.Reconcile(
            placements: [],
            creations: [creation],
            dynamics: [],
            bodyStamps: [
                new WorldStampPool.BodyStamp(
                    BodyIndex: 0,
                    Creation: creation,
                    Scale: 1f,
                    Motion: WorldLookMotion.Default
                ),
            ]
        );

        var builder = new SdfProgramBuilder();

        pool.Emit(
            builder: builder,
            definition: definition,
            probeWorstCase: false,
            maxPlacementScale: 1f,
            slotBase: 0
        );

        return builder.Build(buildInstanceGrid: false);
    }

    [Fact]
    public void EveryPerShapeInstanceCoversItsPrimitiveReach() {
        var program = EmitPool();
        var active = program.Instances.Where(predicate: instance => instance.Active).ToArray();

        // One live instance per authored shape (the ungrouped pass), in document order, ahead of the parked pool slots.
        Assert.True(
            condition: (active.Length >= Shapes.Length),
            userMessage: $"expected at least {Shapes.Length} live instances, found {active.Length}"
        );

        for (var index = 0; (index < Shapes.Length); index++) {
            var shape = Shapes[index];
            var required = ((SdfSolidGeometry.Reach(
                type: shape.Type,
                scale: shape.Scale
            ) + (shape.Dilate ?? 0f)) + (shape.Onion ?? 0f));

            Assert.True(
                condition: (active[index].Radius >= required),
                userMessage: $"shape {index} ({shape.Type}) packs radius {active[index].Radius} below its reach {required}"
            );
        }
    }
}
