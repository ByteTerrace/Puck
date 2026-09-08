using System.Numerics;
using Puck.World.Authoring;
using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Fields;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

namespace Puck.World.Server;

/// <summary>
/// The SDF-backed <see cref="IContactField"/> — the second provider behind the same seam the analytic
/// <see cref="WorldColliderSet"/> answers. It compiles solid screens as axis-aligned boxes and solid placements as
/// their emitted creation primitives into one <see cref="SdfProgram"/>, reads it through a fixed-point
/// <see cref="SdfFieldEvaluator"/>, and hands both to <see cref="FixedFieldContactSolver"/>, so the contact surface a
/// body solves against is the rendered geometry — smooth-union blends are solid where they are drawn.
/// </summary>
/// <remarks>
/// <para>This type owns the document half: which rows are solid, how they compile, and the read-backs
/// <c>world.collision.status</c> reports. Contact resolution itself belongs to the solver.</para>
/// <para>A world authoring <c>collision.gridCellSize</c> reads the program through an
/// <see cref="SdfBandedFieldEvaluator"/> over an <see cref="SdfDistanceGrid"/>: exact within the band a body's
/// contact can reach — the largest kit collider extent at the world's largest body scale, plus the contact skin,
/// plus the grid's own slack — and the grid's corner bound beyond it. The grid covers every finite instance bound in
/// the program padded by <see cref="GridPadding"/>; a query outside it reads the exact program. The band is read
/// from the kits and the scale row at build; a live whole-row upsert raising the scale row's ceiling past the band
/// takes effect at the next solid rebuild, and until then a body scaled beyond the old ceiling can be pushed out of
/// a surface by up to the grid's slack more than its exact penetration, never less.</para>
/// <para>A solid screen's contact box is axis-aligned because the renderer only ever <c>Translate</c>s a screen slab —
/// a screen's right/up is a UV frame only, never a geometry rotation (see <see cref="SdfProgramBuilder"/>'s
/// <c>ScreenSlab</c> overload doc). Orienting a screen volume for real is a two-surface arc — render and contact must
/// both rotate together — and neither does today.</para>
/// <para>The field's ambient "up" is world <c>+Y</c> unless the world authors
/// <see cref="WorldContactRequirement.GradientDerivedUp"/>, which derives it from the field gradient instead (a
/// planetoid, an inverted ceiling, or the inside of a sphere are all walkable). Contact resolution receives the
/// body's already-resolved ambient up separately, so authored gravity may still define another walkability axis.</para>
/// <para>Immutable and per-revision: it holds no per-body state, so one instance is shared by reference across every
/// bodies and installing a rebuild is a single reference swap on <see cref="WorldServer"/>. The wrapped
/// <see cref="SdfFieldEvaluator"/> holds only a managed <c>CompiledInstruction[]</c>, so a replaced instance needs no
/// disposal.</para>
/// <para>The "which op can be solid" ceiling is <see cref="SdfFieldEvaluator"/>'s warp-free excluded-op set:
/// <see cref="TryBuild"/> forwards the constructor's <see cref="ArgumentException"/> message verbatim as its reject
/// reason, so <see cref="WorldServer"/> turns an unsupported solid into a loud apply-time rejection instead of a
/// constructor throw at install time.</para>
/// </remarks>
public sealed class WorldSolidField : IContactField {
    // The same float-safety margin the client stamper adds around a placement's render reach, in world units.
    private const float InstanceBoundMargin = 0.4f;
    /// <summary>How far the distance grid extends past the outermost finite instance bound, in world units — the open
    /// air around the solids where a body or a sight line is far from every surface and a corner bound answers.</summary>
    public const float GridPadding = 32f;

    private static readonly FixedVector3 UnitY = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    );

    private readonly IFieldEvaluator m_contactField;
    // The field every query reads: the banded evaluator when a grid is authored, the exact program otherwise.
    private readonly IFieldEvaluator m_field;
    private readonly SdfFieldEvaluator m_evaluator;
    private readonly SdfBandedFieldEvaluator? m_banded;
    private readonly FieldLattice? m_lattice;
    private readonly SdfProgram m_program;
    private readonly IWorldQuery m_query;
    private readonly FixedFieldContactSolver m_solver;
    // The largest extent any kit collider volume reaches from its own sample point, at the world's largest body
    // scale, in world units; the contact skin is added per tuning.
    private readonly FixedQ4816 m_kitReach;
    // The hold policy per compiled material id — TryBuild adds exactly one material per solid screen and per solid
    // placement, so a probe's reported material id IS the row that composed it. A material id outside these (the
    // field lattice's own terrain, which no placement row owns) falls back to the world's collision.defaultHold.
    // How far back along a grip ray the surface gradient is sampled. A hit point sits ON the isosurface, where the
    // sign of the field is exactly what is in question; one contact-skin-scale step back into open space gives the
    // gradient a side to face. Small enough that no authored surface curves meaningfully across it.
    private static readonly FixedQ4816 GradientBackoff = FixedQ4816.FromDouble(value: 0.02);

    private readonly bool[] m_holdableMaterials;
    private readonly bool[] m_holdableGrantedByOverride;
    private readonly bool m_defaultGrip;

    private WorldSolidField(SdfProgram program, SdfFieldEvaluator evaluator, SdfDistanceGrid? grid, FixedQ4816 kitReach, FieldLattice? lattice, long placementShapeCount, WorldContactCensus census, FixedWorldCollision tuning, bool[] holdableMaterials, bool[] holdableGrantedByOverride, bool defaultGrip) {
        m_program = program;
        m_evaluator = evaluator;
        m_kitReach = kitReach;
        m_lattice = lattice;
        m_holdableMaterials = holdableMaterials;
        m_holdableGrantedByOverride = holdableGrantedByOverride;
        m_defaultGrip = defaultGrip;
        InstructionCount = program.Instructions.Count;
        PlacementShapeCount = placementShapeCount;

        if (grid is null) {
            m_field = evaluator;
            m_query = evaluator;
        } else {
            m_banded = new SdfBandedFieldEvaluator(
                contactReach: (kitReach + tuning.ContactSkin),
                exact: evaluator,
                grid: grid
            );
            m_field = m_banded;
            m_query = m_banded;
        }

        Census = (census with {
            SolidBakeHash = BakeHash(
                cellSize: tuning.GridCellSize,
                contactReach: (kitReach + tuning.ContactSkin),
                program: program
            ),
        });
        // A field lattice's height columns union with the authored solids for contact; sweeps and line of sight
        // still march the authored program alone.
        m_contactField = ((lattice is null)
            ? m_field
            : new UnionField(
                a: m_field,
                b: new FieldLatticeSolid(lattice: lattice)
            ));
        m_solver = new FixedFieldContactSolver(
            contactSkin: tuning.ContactSkin,
            field: m_contactField,
            gradientProbe: tuning.GradientProbe,
            gradientUp: tuning.GradientUp,
            groundedThreshold: tuning.GroundedThreshold,
            maxIterations: tuning.MaxIterations,
            query: m_query
        );
    }

    /// <summary>Gets the analytic collider census measured from the same definition, so the read-back is comparable
    /// whichever provider the world selected, carrying this field's <see cref="WorldContactCensus.SolidBakeHash"/>.</summary>
    public WorldContactCensus Census { get; }
    /// <summary>Gets the field value below which every query reads the exact program: the contact band when a grid is
    /// authored, zero otherwise.</summary>
    public FixedQ4816 ContactBand => (m_banded?.Band ?? FixedQ4816.Zero);
    /// <summary>Gets the field evaluator the <c>world.collision.probe</c> verb reads distance/material/gradient from, so the
    /// surface the simulation itself solves against is directly observable — beyond <see cref="ContactBand"/> it
    /// reads the grid's corner bound, as the simulation does.</summary>
    public IFieldEvaluator Evaluator => m_field;
    /// <summary>Gets the baked distance grid, or <see langword="null"/> when the world authors no cell size or the
    /// program has nothing finite to cover.</summary>
    public SdfDistanceGrid? Grid => m_banded?.Grid;
    /// <summary>Gets the deterministic gameplay-query view over the same compiled solid program.</summary>
    public IWorldQuery Query => m_query;
    /// <summary>Gets a value indicating whether this field's collision tuning authors <see cref="WorldContactRequirement.GradientDerivedUp"/>.</summary>
    public bool GradientUp => m_solver.GradientUp;
    /// <summary>Gets the compiled program's instruction count — the <c>world.collision.status</c> read-back (a rough size of
    /// the solid field the solver walks).</summary>
    public int InstructionCount { get; }
    /// <summary>Gets the placement primitive-shape emissions in the compiled field.</summary>
    public long PlacementShapeCount { get; }

    /// <summary>Queries the wrapped deterministic SDF evaluator for an unobstructed segment.</summary>
    /// <param name="from">The segment start.</param>
    /// <param name="to">The segment end.</param>
    /// <returns><see langword="true"/> when nothing solid lies between the two points.</returns>
    public bool LineOfSight(in FixedVector3 from, in FixedVector3 to) =>
        m_solver.LineOfSight(
            from: in from,
            to: in to
        );
    /// <summary>Reads the compiled field at a point — the <c>world.collision.probe</c> verb's observation.</summary>
    /// <param name="position">The world-space point to read.</param>
    /// <param name="distance">The signed nearest-surface distance, when the field answered.</param>
    /// <param name="material">The nearest surface's material id, when the field answered.</param>
    /// <param name="gradient">The unit-length field gradient, or zero where none exists.</param>
    /// <returns><see langword="true"/> when the field answered.</returns>
    public bool Probe(in FixedVector3 position, out FixedQ4816 distance, out int material, out FixedVector3 gradient) =>
        m_solver.Probe(
            distance: out distance,
            gradient: out gradient,
            material: out material,
            position: in position
        );
    /// <inheritdoc/>
    public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        m_solver.Resolve(
            orientation: in orientation,
            position: ref position,
            up: in up,
            velocity: ref velocity,
            volumes: volumes
        );
    /// <inheritdoc/>
    public ContactResolution ResolveSweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity,
        in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        m_solver.ResolveSweep(
            orientation: in orientation,
            position: ref position,
            previousPosition: in previousPosition,
            up: in up,
            velocity: ref velocity,
            volumes: volumes
        );
    /// <summary>Builds the SDF contact field from a definition without installing it, or reports the offending op by name.</summary>
    /// <param name="definition">The world definition supplying the collision tuning and solid rows.</param>
    /// <param name="built">The built field on success; <see langword="null"/> on failure.</param>
    /// <param name="reason">The forwarded <see cref="SdfFieldEvaluator"/> reject reason when a solid names an op the
    /// warp-free evaluator cannot interpret; empty on success.</param>
    /// <param name="lattice">The field lattice whose height columns union with the solids for contact, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the field compiled, <see langword="false"/> with a named reason otherwise.</returns>
    public static bool TryBuild(WorldDefinition definition, out WorldSolidField? built, out string reason, FieldLattice? lattice = null) {
        built = null;
        reason = string.Empty;

        var tuning = FixedWorldCollision.Compile(collision: definition.Collision);
        var worldSeed = (definition.Generation?.WorldSeed ?? 0UL);
        var builder = new SdfProgramBuilder();
        var placementShapeCount = 0L;

        var defaultGrip = definition.Collision.DefaultHold;
        var holdableMaterials = new List<bool>();
        var holdableGrantedByOverride = new List<bool>();

        void RecordGrip(int material, bool holdable, bool grantedByOverride) {
            while (holdableMaterials.Count <= material) {
                holdableMaterials.Add(item: defaultGrip);
                holdableGrantedByOverride.Add(item: false);
            }

            holdableMaterials[material] = holdable;
            holdableGrantedByOverride[material] = grantedByOverride;
        }

        foreach (var screen in definition.Screens) {
            if (screen.Solid is not { } solid) {
                continue;
            }

            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            // A screen carries no grip facet of its own, so only the world-level policy can admit it.
            RecordGrip(
                holdable: defaultGrip,
                grantedByOverride: false,
                material: material
            );
            // The same center derivation the frame source and picker bake: the geometry box sits one HalfDepth behind the
            // lit face along the face normal.
            var normal = Vector3.Normalize(value: Vector3.Cross(
                vector1: screen.Right,
                vector2: screen.Up
            ));
            var center = (screen.Origin - (normal * screen.HalfDepth));

            _ = builder
                .Translate(offset: center)
                .Box(
                halfExtents: new Vector3(
                    x: (screen.HalfWidth + solid.Margin),
                    y: (screen.HalfHeight + solid.Margin),
                    z: (screen.HalfDepth + solid.Margin)
                ),
                round: screen.Round,
                material: material
            )
                .ResetPoint();
        }

        foreach (var placement in definition.Placements) {
            // A dealt template collides with nothing itself; its solid facet is what its dealt children carry.
            if (
                (placement.Solid is not { } solid) ||
                (placement.Deal is not null) ||
                (WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: placement.PrototypeId
            ) is not { } creation)
            ) {
                continue;
            }

            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            RecordGrip(
                holdable: (placement.Grip?.Holdable ?? defaultGrip),
                grantedByOverride: (placement.Grip is not null),
                material: material
            );
            var resolvedFrame = WorldDefinitionRows.ResolvedFrame(definition: definition, placement: placement);
            // The one transform conversion boundary: the program is encoded single-precision, but every placement
            // transform reaching it is derived in fixed point first (yaw via integer SinCos, origins via the fixed
            // lattice, reflected frames via fixed quaternion composition) and rounded exactly to float, so every
            // machine encodes bit-identical constants — the evaluator itself stays fixed point throughout.
            var fixedRotation = FixedQuaternion.FromAxisAngle(
                axis: UnitY,
                angle: FixedQ4816.FromDouble(value: (resolvedFrame.YawDegrees * (Math.PI / 180.0)))
            );

            // A creation whose parts carve each other is one candidate against the solid field, never a carve of it.
            // The field scope is one deep, and a nonzero contact margin already spends it per shape (dilation before
            // the authored blend), so a margined solid emits unscoped.
            var scoped = (
                (solid.Margin == 0f) &&
                (creation.Document.Shapes is { Count: > 0 }) &&
                CreationStampEmitter.ComposesInternally(document: creation.EngineDocument)
            );

            // Each placed copy is one program instance carrying a conservative world-space bound: the creation's
            // render reach at the placement's scale, plus the contact margin the shapes are dilated by. The
            // evaluator's exact cull skips a hard-union instance the bound proves cannot win a query, so the bound
            // decides only which work runs, never what distance results; a plane is never culled whatever its bound.
            var reach = (CreationStampEmitter.RenderReach(document: creation.EngineDocument, scale: placement.Scale, fontFor: null) + (solid.Margin > 0f ? solid.Margin : 0f) + InstanceBoundMargin);

            CreationStampLattice.ForEachFixedInstance(
                origin: FixedVector3.FromVector3(value: resolvedFrame.Position),
                rotation: fixedRotation,
                pattern: WorldPlacementStamp.PatternFor(placement: placement),
                sampledOffsets: WorldPlacementStamp.SampledFixedOffsetsFor(placement: placement, worldSeed: worldSeed),
                mirror: WorldPlacementStamp.MirrorFor(placement: placement),
                visitor: instance => {
                    _ = builder.BeginInstance(boundCenter: instance.Origin.ToVector3(), boundRadius: reach);

                    if (scoped) {
                        _ = builder.PushField(compose: SdfBlendOp.Union);
                    }

                    CreationStampEmitter.EmitFixed(
                        builder: builder,
                        document: creation.EngineDocument,
                        transform: new FixedCreationStampTransform(
                            Origin: instance.Origin,
                            Rotation: fixedRotation,
                            Scale: FixedQ4816.FromDouble(value: placement.Scale),
                            ReflectionNormal: instance.ReflectionNormal
                        ),
                        materialFor: _ => material,
                        contactMargin: solid.Margin
                    );

                    if (scoped) {
                        _ = builder.PopField();
                    }

                    _ = builder.EndInstance();
                    placementShapeCount += (creation.Document.Shapes?.Count ?? 0);
                }
            );
        }

        var program = builder.Build(buildInstanceGrid: false);
        SdfFieldEvaluator evaluator;

        try {
            evaluator = new SdfFieldEvaluator(program: program);
        } catch (ArgumentException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }

        built = new WorldSolidField(
            evaluator: evaluator,
            grid: CoverGrid(
                cellSize: tuning.GridCellSize,
                evaluator: evaluator,
                program: program
            ),
            kitReach: KitReach(definition: definition),
            lattice: lattice,
            placementShapeCount: placementShapeCount,
            program: program,
            census: WorldColliderSet.Measure(definition: definition),
            holdableGrantedByOverride: [.. holdableGrantedByOverride],
            holdableMaterials: [.. holdableMaterials],
            defaultGrip: defaultGrip,
            tuning: tuning
        );

        return true;
    }
    // The bake's inputs, folded so two fields with equal hashes carry equal grids: the packed program, the cell size,
    // and the contact reach the band starts from. Zero when no grid is authored.
    private static ulong BakeHash(SdfProgram program, FixedQ4816 cellSize, FixedQ4816 contactReach) {
        if (cellSize <= FixedQ4816.Zero) {
            return 0UL;
        }

        var hash = Fnv1aHash.Create();

        foreach (var word in program.Words) {
            hash.Add(value: word);
        }

        hash.Add(value: cellSize.Value);
        hash.Add(value: contactReach.Value);

        return hash.Value;
    }
    private static SdfDistanceGrid? CoverGrid(SdfFieldEvaluator evaluator, SdfProgram program, FixedQ4816 cellSize) =>
        ((cellSize > FixedQ4816.Zero)
            ? SdfDistanceGrid.TryCover(
                cellSize: cellSize,
                exact: evaluator,
                padding: FixedQ4816.FromDouble(value: GridPadding),
                program: program
            )
            : null
        );
    // The farthest any kit's contact sample compares the field against, at the largest scale a body can wear: a
    // sphere's or capsule's radius, a box's half-extent length. A body absent from the scale row reads scale 1, so
    // the row's ceiling never shrinks the reach below the unscaled collider.
    private static FixedQ4816 KitReach(WorldDefinition definition) {
        var reach = FixedQ4816.Zero;

        foreach (var kit in definition.Kits) {
            if (FixedWorldCollider.Compile(
                collider: kit.Collider,
                creations: definition.Creations
            ) is not { } collider) {
                continue;
            }

            foreach (var volume in collider.Volumes) {
                reach = FixedQ4816.Max(
                    x: reach,
                    y: ((volume.Kind == FixedBodyColliderKind.Box)
                        ? volume.HalfExtents.Length
                        : volume.Radius
                    )
                );
            }
        }

        var scale = FixedQ4816.One;

        if (
            (definition.Population.ScaleRow is { } scaleRow) &&
            (WorldDefinitionRows.FindStateRow(
                rows: definition.State,
                name: scaleRow
            ) is { Max: { } scaleMax })
        ) {
            scale = FixedQ4816.Max(
                x: scale,
                y: FixedQ4816.FromRawBits(value: scaleMax)
            );
        }

        return (reach * scale);
    }
    /// <inheritdoc/>
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) =>
        m_solver.TryUp(
            position: in position,
            up: out up
        );
    /// <summary>Re-wraps this field's already-compiled program with fresh solver scalars, reusing the wrapped
    /// <see cref="SdfFieldEvaluator"/> (safe to share by reference — it holds only an immutable instruction array) and
    /// the distance grid when the cell size is unchanged. A <c>SetCollision</c> edit touches only the collision tuning
    /// row, never the geometry the program bakes (screens and placements), so a slope/skin/probe/iteration tweak
    /// reuses the program instead of recompiling it; a new cell size bakes a new grid over the same program. The
    /// result is a distinct instance (per-revision immutability) so the install-time reference swap still bumps the
    /// revision.</summary>
    /// <param name="tuning">The recompiled collision tuning to adopt.</param>
    /// <returns>A new field over the same evaluator with the new scalars.</returns>
    public WorldSolidField WithTuning(FixedWorldCollision tuning) =>
        new(
            evaluator: m_evaluator,
            grid: (((m_banded is { } banded) && (banded.Grid.CellSize == tuning.GridCellSize))
                ? banded.Grid
                : CoverGrid(
                    cellSize: tuning.GridCellSize,
                    evaluator: m_evaluator,
                    program: m_program
                )),
            kitReach: m_kitReach,
            lattice: m_lattice,
            placementShapeCount: PlacementShapeCount,
            program: m_program,
            census: Census,
            holdableGrantedByOverride: m_holdableGrantedByOverride,
            holdableMaterials: m_holdableMaterials,
            defaultGrip: m_defaultGrip,
            tuning: tuning
        );
    /// <inheritdoc/>
    /// <remarks>The aim-assist cone is not honoured: a field has no candidate LIST to score bearings over, only the
    /// one surface its own march reaches. A caller wanting assisted aim against a field has to widen its own sweep.</remarks>
    public bool TryNearestSurfaceAlongDirection(in FixedVector3 origin, in FixedVector3 direction, FixedQ4816 maxDistance, FixedQ4816 assistHalfAngle, out FixedSurfaceAttachCandidate candidate) {
        _ = assistHalfAngle;

        return TryCast(
            candidate: out candidate,
            direction: in direction,
            maxDistance: maxDistance,
            origin: in origin
        );
    }
    // The one directed march both grip and anchor queries read: the first surface along the ray, with the surface
    // orientation read from the field gradient one step back into open space (a march reports WHERE, never which
    // way -- RayHit.Normal is documented zero).
    private bool TryCast(in FixedVector3 origin, in FixedVector3 direction, FixedQ4816 maxDistance, out FixedSurfaceAttachCandidate candidate) {
        candidate = default;

        if (!m_query.Raycast(
            dir: direction,
            hit: out var hit,
            maxDist: maxDistance,
            origin: FixedPosition.FromLocal(local: origin)
        )) {
            return false;
        }

        var point = hit.Point.Local;

        if (!m_evaluator.TryFieldGradient(
            gradient: out var gradient,
            position: FixedPosition.FromLocal(local: (point - (direction.Normalize() * GradientBackoff)))
        )) {
            return false;
        }
        if (gradient == FixedVector3.Zero) {
            return false;
        }

        candidate = new FixedSurfaceAttachCandidate(
            Point: point,
            Normal: gradient,
            Distance: hit.Distance,
            Source: FixedSurfaceColliderSource.Static,
            ColliderIndex: hit.Material
        );

        return true;
    }
    /// <inheritdoc/>
    /// <remarks>A ray, not a nearest-point search: the field's own deterministic march reports the FIRST surface
    /// along the direction, which is what makes a grip immune to the nearer geometry beside it (the floor under a
    /// wall's foot, the underside of the ledge above). A hit inside geometry cannot arise from a grip that never
    /// lets itself embed, so no inside-out case is invented here.</remarks>
    public bool TryHoldableSurfaceAlongDirection(in FixedVector3 origin, in FixedVector3 direction, FixedQ4816 maxDistance, out FixedSurfaceAttachCandidate candidate, out bool grantedByOverride) {
        candidate = default;
        grantedByOverride = false;

        if (!TryCast(
            candidate: out candidate,
            direction: in direction,
            maxDistance: maxDistance,
            origin: in origin
        )) {
            return false;
        }
        if (!Holdable(
            grantedByOverride: out grantedByOverride,
            material: candidate.ColliderIndex
        )) {
            candidate = default;
            grantedByOverride = false;

            return false;
        }

        return true;
    }
    // The compiled material id's hold verdict — one material per solid screen and per solid placement, so an id
    // outside the recorded range is the field lattice's own terrain and falls back to the world-level policy.
    private bool Holdable(int material, out bool grantedByOverride) {
        grantedByOverride = ((material >= 0) && (material < m_holdableGrantedByOverride.Length) && m_holdableGrantedByOverride[material]);

        return (((material >= 0) && (material < m_holdableMaterials.Length))
            ? m_holdableMaterials[material]
            : m_defaultGrip
        );
    }
}
