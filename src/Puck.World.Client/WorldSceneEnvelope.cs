using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>One world scene's render envelope, counted on the CPU from a definition alone: what each part of the
/// composed program the scene emitter, the stamp pool, the avatar catalog and the adjacency reservation would draw costs
/// in instances and materials. A boot reads it before any renderer exists, headless or windowed, and the render-capacity
/// oracle (<see cref="WorldRenderEnvelope"/>) admits every render-relevant mutation through it.</summary>
/// <remarks>A candidate costs what it authors, counted rather than emitted: its screens one slab each, its static
/// placements the stamp reservation <see cref="WorldPlacementStamper.StaticStampReservation"/> counts, and its
/// adjacency bands each the band reservation. The stamp pool's and the avatar catalog's worst cases are the same for
/// every definition and are emitted once a process, as are the unit costs of the placement and band reservations, so
/// a count agrees with what the construction probe (<see cref="WorldSceneEmitter"/>) emits by construction.</remarks>
public sealed class WorldSceneEnvelope {
    private static readonly Lazy<WorldRenderCost> PoolCost = new(valueFactory: static () => Count(emit: static builder => WorldStampPool.EmitProbe(
        builder: builder,
        maxPlacementScale: 1f,
        slotBase: WorldRigCatalog.DynamicTransformCapacity
    )));
    private static readonly Lazy<WorldRenderCost> AvatarCost = new(valueFactory: static () => Count(emit: static builder => {
        var bodyMaterials = new int[WorldBodiesLimits.CapacityCeiling];
        var accentMaterials = new int[WorldBodiesLimits.CapacityCeiling];

        for (var index = 0; (index < WorldBodiesLimits.CapacityCeiling); index++) {
            bodyMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
            accentMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        }

        WorldRigCatalog.Emit(
            builder: builder,
            isActive: static _ => true,
            bodyMaterials: bodyMaterials,
            accentMaterials: accentMaterials,
            probeWorstCase: true,
            slotBase: 0
        );
    }));
    private static readonly Lazy<WorldRenderCost> ScopedStampCost = new(valueFactory: static () => Count(emit: static builder => WorldPlacementStamper.EmitProbe(
        builder: builder,
        reservedCount: 1
    )));
    private static readonly Lazy<WorldRenderCost> ShapeInstanceCost = new(valueFactory: static () => Count(emit: static builder => WorldPlacementStamper.EmitProbe(
        builder: builder,
        reservedCount: 0,
        reservedShapeInstances: 1
    )));
    private static readonly Lazy<WorldRenderCost> BandCost = new(valueFactory: static () => Count(emit: static builder => WorldAdjacencySceneEmitter.EmitReservation(
        bandCount: 1,
        builder: builder,
        slotBase: 0
    )));

    /// <summary>Initializes a new instance of the <see cref="WorldSceneEnvelope"/> class over a boot definition, freezing
    /// the headroom its construction probe reserves: <c>placements.policy.authoringHeadroomInstances</c> per-shape
    /// instances and <c>authoringHeadroomScreens</c> slabs, the instances clamped to what the engine's ceiling leaves
    /// above the boot scene.</summary>
    /// <param name="boot">The boot definition.</param>
    /// <exception cref="ArgumentNullException"><paramref name="boot"/> is <see langword="null"/>.</exception>
    public WorldSceneEnvelope(WorldDefinition boot) {
        ArgumentNullException.ThrowIfNull(argument: boot);

        Boot = Of(candidate: boot);
        HeadroomScreens = boot.Authoring.AuthoringHeadroomScreens;
        HeadroomInstances = Math.Clamp(
            max: Math.Max(
                val1: 0,
                val2: ((SdfProgramBuilder.MaxInstances - Boot.Cost.Instances) - HeadroomScreens)
            ),
            min: 0,
            value: boot.Authoring.AuthoringHeadroomInstances
        );
    }

    /// <summary>Gets the boot definition's count.</summary>
    public WorldSceneCount Boot { get; }
    /// <summary>Gets the materials of the <c>puck.sdf.v1</c> document a renderer composes beside the scene, which every
    /// count adds; <see langword="null"/>, the default, for none.</summary>
    public Func<int>? DocumentMaterials { get; set; }
    /// <summary>Gets the per-shape instances the construction probe reserves beyond the boot scene, so live editing
    /// grows into buffers already sized for it; the renderer grows past it on demand.</summary>
    public int HeadroomInstances { get; }
    /// <summary>Gets the screen slabs the construction probe reserves beyond the boot scene's screens.</summary>
    public int HeadroomScreens { get; }
    /// <summary>Gets the instances the construction probe declares: the boot scene's and both headrooms.</summary>
    public int ReservedInstances => checked(((Boot.Cost.Instances + HeadroomInstances) + HeadroomScreens));

    /// <summary>Counts a candidate definition's composed world scene, with the <c>puck.sdf.v1</c> document
    /// <see cref="DocumentMaterials"/> names.</summary>
    /// <param name="candidate">The candidate definition.</param>
    /// <returns>The count.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is <see langword="null"/>.</exception>
    public WorldSceneCount Measure(WorldDefinition candidate) => Of(
        candidate: candidate,
        adjacency: true,
        documentMaterials: (DocumentMaterials?.Invoke() ?? 0)
    );
    /// <summary>Counts a candidate definition's composed scene: its screens, static placements, the stamp pool's and
    /// avatar catalog's worst cases, and optionally its adjacency bands' reservation and a composed document's
    /// materials.</summary>
    /// <param name="candidate">The candidate definition.</param>
    /// <param name="adjacency">Whether the scene reserves the candidate's adjacency bands, as the world's own scene
    /// does and a session view of it does not.</param>
    /// <param name="documentMaterials">The materials of a <c>puck.sdf.v1</c> document composed beside the scene; a
    /// document declares no instances.</param>
    /// <returns>The count.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is <see langword="null"/>.</exception>
    public static WorldSceneCount Of(WorldDefinition candidate, bool adjacency = true, int documentMaterials = 0) {
        ArgumentNullException.ThrowIfNull(argument: candidate);

        var (scoped, shapes) = WorldPlacementStamper.StaticStampReservation(
            creations: candidate.Creations,
            placements: candidate.Placements,
            worldSeed: (candidate.Generation?.WorldSeed ?? 0UL)
        );

        return new WorldSceneCount(
            Adjacency: (adjacency
                ? (BandCost.Value * WorldAdjacencyBands.ProjectionCapacity(definition: candidate))
                : default),
            Avatars: AvatarCost.Value,
            Document: new WorldRenderCost(
                Instances: 0,
                Materials: documentMaterials
            ),
            Placements: ((ScopedStampCost.Value * scoped) + (ShapeInstanceCost.Value * shapes)),
            Pool: PoolCost.Value,
            Screens: new WorldRenderCost(
                Instances: checked((candidate.Screens.Count + candidate.Authoring.DerivedFaceScreens)),
                Materials: 0
            )
        );
    }
    /// <summary>Describes the boot count and the reservation for the boot log.</summary>
    /// <returns>The <c>[world.render] envelope: …</c> line.</returns>
    public string Describe() {
        var cost = Boot.Cost;

        return $"[world.render] envelope: {cost.Instances} of {SdfProgramBuilder.MaxInstances} instances (screens {Boot.Screens.Instances}, placements {Boot.Placements.Instances}, stamp pool {Boot.Pool.Instances}, avatars {Boot.Avatars.Instances}, adjacency {Boot.Adjacency.Instances}), {cost.Materials} of {SdfProgramBuilder.ScreenMaterialId} materials; reserves {ReservedInstances} instances ({HeadroomInstances} headroom, {HeadroomScreens} headroom screens)";
    }

    private static WorldRenderCost Count(Action<SdfProgramBuilder> emit) {
        var (instances, materials) = SdfProgramMeasure.Count(emit: emit);

        return new WorldRenderCost(
            Instances: instances,
            Materials: materials
        );
    }
}

/// <summary>A world scene's count by part (<see cref="WorldSceneEnvelope.Of"/>).</summary>
/// <param name="Screens">The authored screens' and derived faces' slabs.</param>
/// <param name="Placements">The static placements' stamp reservation.</param>
/// <param name="Pool">The stamp pool's worst case.</param>
/// <param name="Avatars">The avatar catalog's worst case and its palette.</param>
/// <param name="Adjacency">The adjacency bands' reservation.</param>
/// <param name="Document">A composed <c>puck.sdf.v1</c> document's materials.</param>
public readonly record struct WorldSceneCount(WorldRenderCost Screens, WorldRenderCost Placements, WorldRenderCost Pool, WorldRenderCost Avatars, WorldRenderCost Adjacency, WorldRenderCost Document) {
    /// <summary>Gets the whole scene's cost.</summary>
    public WorldRenderCost Cost => (((((Screens + Placements) + Pool) + Avatars) + Adjacency) + Document);
}
