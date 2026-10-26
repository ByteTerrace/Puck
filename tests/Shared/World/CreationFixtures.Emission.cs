
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;

namespace Puck.World.Testing;

internal static partial class CreationFixtures {
    /// <summary>Emits <paramref name="creation"/> through the animated pool on body 0 at <paramref name="bodyScale"/>,
    /// over the flat code-built document carrying it and one look naming it.</summary>
    /// <param name="creation">The canonical prototype the body wears.</param>
    /// <param name="bodyScale">The body's look scale.</param>
    /// <param name="probeWorstCase">Whether to emit the pool's worst-case capacity probe instead of live state.</param>
    /// <param name="maxPlacementScale">The placement-scale ceiling the pool sizes its reach by, or
    /// <see langword="null"/> for <paramref name="bodyScale"/>.</param>
    /// <param name="articulated">Whether to exercise articulated emission even for a stationary fixture.</param>
    /// <returns>The built program, without an instance grid.</returns>
    public static SdfProgram EmitPool(WorldPrototype creation, float bodyScale, bool probeWorstCase = false, float? maxPlacementScale = null, bool articulated = true) {
        // Exercise articulated field emission even when this shape fixture has no moving parts.
        // Rigid template sharing and copy scaling have their own end-to-end tests.
        if (articulated && WorldRigidAssets.IsRigid(creation)) {
            creation = Prototype(id: creation.Id, document: creation.Document with {
                Drivers = [new CreationDriverDocument(Name: "fixtureClock", Signal: CreationDriverDocument.SignalTime, Cadence: 1f)]
            });
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

        pool.Reconcile(
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
            probeWorstCase: probeWorstCase,
            maxPlacementScale: (maxPlacementScale ?? bodyScale),
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
