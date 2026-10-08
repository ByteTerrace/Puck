
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;

namespace Puck.World.Testing;

internal static partial class CreationFixtures {
    // The worst-case probe reads no live registration (WorldStampPool.EmitOne emits every reserved slot with the full
    // modifier envelope from the placement-scale ceiling alone), so one probe at scale one serves every law that
    // compares against it. Building it walks every reserved slot; it is built once per run.
    private static readonly Lazy<SdfProgram> PoolProbeAtUnitScale = new(valueFactory: static () => EmitPool(
        bodyScale: 1f,
        creation: UnitSphere(id: "probe"),
        probeWorstCase: true
    ));

    /// <summary>Gets the animated pool's worst-case capacity probe at a placement-scale ceiling of one, shared and
    /// read-only.</summary>
    public static SdfProgram PoolProbe => PoolProbeAtUnitScale.Value;

    /// <summary>Emits <paramref name="creation"/> through the animated pool on body 0 at <paramref name="bodyScale"/>,
    /// over the flat code-built document carrying it and one look naming it.</summary>
    /// <param name="creation">The canonical prototype the body wears.</param>
    /// <param name="bodyScale">The body's look scale.</param>
    /// <param name="probeWorstCase">Whether to emit the pool's worst-case capacity probe instead of live state.</param>
    /// <param name="maxPlacementScale">The placement-scale ceiling the pool sizes its reach by, or
    /// <see langword="null"/> for <paramref name="bodyScale"/>.</param>
    /// <returns>The built program, without an instance grid.</returns>
    public static SdfProgram EmitPool(WorldPrototype creation, float bodyScale, bool probeWorstCase = false, float? maxPlacementScale = null) {
        var definition = (Fixtures.BuildGradientUpDocument(gradientUp: false) with {
            CreationsRaw = [creation],
            LookRowsRaw = [new WorldLook(
                Name: "rig",
                Source: new WorldLookSource.Creation(PrototypeId: creation.Id),
                Scale: bodyScale,
                Motion: WorldLookMotion.Default
            )],
        });
        var pool = new WorldStampPool();

        pool.Reconcile(
            placements: [],
            creations: [creation],
            dynamics: [],
            bodyStamps: [new WorldStampPool.BodyStamp(
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

        return builder.Build(buildInstanceGrid: false);
    }
    /// <summary>Emits the shapes through the animated pool as a prototype named <paramref name="name"/>.</summary>
    /// <param name="name">The prototype identifier.</param>
    /// <param name="shapes">The shapes under test, in authored order.</param>
    /// <param name="bodyScale">The body's look scale.</param>
    /// <param name="palette">The palette, or <see langword="null"/> for <see cref="Grey"/>.</param>
    /// <returns>The built program, without an instance grid.</returns>
    public static SdfProgram EmitPool(string name, IReadOnlyList<ShapeDocument> shapes, float bodyScale = 1f, IReadOnlyList<PaletteEntryDocument>? palette = null) => EmitPool(
        bodyScale: bodyScale,
        creation: Prototype(document: Document(
            name: name,
            palette: (palette ?? Grey),
            shapes: shapes
        ))
    );
}
