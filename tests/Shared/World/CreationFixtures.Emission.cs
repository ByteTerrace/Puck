using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;

namespace Puck.World.Testing;

internal static partial class CreationFixtures {
    /// <summary>Gives a stationary creation part motion, so its body is pooled rather than rigid
    /// (<see cref="WorldRigidAssets.IsRigid(WorldPrototype, WorldLook?)"/>): a driver writes nothing on its own, so a frame
    /// poses a shape a hair off its rest position. A creation that already moves a part is returned as it is.</summary>
    /// <param name="creation">The canonical prototype.</param>
    /// <returns>The prototype, or one whose first shape without domain operators (a fold's frame pose is never read) a
    /// frame moves; a creation of folds alone gains a tiny shape to move.</returns>
    public static WorldPrototype Articulated(WorldPrototype creation) {
        if (!WorldRigidAssets.IsRigid(creation)) {
            return creation;
        }

        var shapes = (creation.Document.Shapes ?? []);
        var mover = shapes.FirstOrDefault(predicate: static shape => (shape.Domain is not { Count: > 0 }));

        if (mover is null) {
            mover = new ShapeDocument(
                Id: (shapes.Select(selector: static shape => shape.Id).DefaultIfEmpty(defaultValue: -1).Max() + 1),
                Name: null,
                Type: SdfSolidPrimitive.Sphere,
                Position: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Scale: new Vector3(value: 1e-3f),
                Material: 0,
                Blend: SdfBlendOp.Union,
                Smooth: 0f,
                Group: 0
            );
            shapes = [.. shapes, mover];
        }

        return Prototype(id: creation.Id, document: creation.Document with {
            Drivers = (creation.Document.Drivers is { Count: > 0 } drivers ? drivers : [new CreationDriverDocument(Name: "fixtureClock", Signal: CreationDriverDocument.SignalTime, Cadence: 1f)]),
            Frames = [.. (creation.Document.Frames ?? []), new FrameDocument(Name: "fixtureStep", Transforms: [new FrameTransformDocument(Id: mover.Id, Position: (mover.Position.Value + new Vector3(x: 1e-3f, y: 0f, z: 0f)), Rotation: mover.Rotation)])],
            Shapes = shapes,
        });
    }
    /// <summary>Emits <paramref name="creation"/> through the animated pool on body 0 at <paramref name="bodyScale"/>,
    /// over the flat code-built document carrying it and one look naming it.</summary>
    /// <param name="creation">The canonical prototype the body wears.</param>
    /// <param name="bodyScale">The body's look scale.</param>

    /// <param name="articulated">Whether to exercise articulated emission even for a stationary fixture.</param>
    /// <returns>The built program, without an instance grid.</returns>
    public static SdfProgram EmitPool(WorldPrototype creation, float bodyScale, bool articulated = true) {
        // Exercise articulated field emission even when this shape fixture has no moving parts.
        // Rigid template sharing and copy scaling have their own end-to-end tests.
        if (articulated) {
            creation = Articulated(creation: creation);
        }
        var definition = (Fixtures.BuildGradientUpDocument(gradientUp: false) with {
            CreationsRaw = [creation],
            LookRowsRaw = [new WorldLook(
                Name: "rig",
                Source: new WorldLookSource.Creation(PrototypeId: creation.Id),
                Scale: bodyScale,
                Motion: WorldLookMotion.Default
            )],
        });
        var pool = new WorldCreationInstances();

        pool.Reconcile(placementFrames: WorldPlacementFrameCompilation.Compile(placements: [], worldSeed: 0UL),
            placements: [],
            creations: [creation],
            dynamics: [],
            bodyStamps: [new WorldCreationInstances.BodyStamp(
                BodyIndex: 0,
                Creation: creation,
                Scale: bodyScale,
                Look: WorldLook.Implicit
            )]
        );

        var builder = new SdfProgramBuilder();

        pool.Emit(
            builder: builder,
            colors: WorldBakedColors.Of(definition: definition),
            slotBase: 0
        );

        return builder.Build();
    }
    /// <summary>Emits the shapes through the animated pool as a prototype named <paramref name="name"/>.</summary>
    /// <param name="name">The prototype identifier.</param>
    /// <param name="shapes">The shapes under test, in authored order.</param>
    /// <param name="bodyScale">The body's look scale.</param>
    /// <param name="palette">The palette, or <see langword="null"/> for <see cref="Grey"/>.</param>
    /// <param name="articulated">Whether to exercise articulated emission even for a stationary fixture.</param>
    /// <returns>The built program, without an instance grid.</returns>
    public static SdfProgram EmitPool(string name, IReadOnlyList<ShapeDocument> shapes, float bodyScale = 1f, IReadOnlyList<PaletteEntryDocument>? palette = null, bool articulated = true) => EmitPool(
        articulated: articulated,
        bodyScale: bodyScale,
        creation: Prototype(document: Document(
            name: name,
            palette: (palette ?? Grey),
            shapes: shapes
        ))
    );
}
