using Puck.World.Authoring;
using Puck.Maths;
using Puck.Physics;
using Puck.SignedDistance;

namespace Puck.World.Server;

/// <summary>
/// The analytic <see cref="IContactField"/> — the document-derived, fixed-point, allocation-free-at-steady-state
/// contact provider. It compiles convex colliders from solid screens and every primitive copy in a solid creation
/// placement, so grounding has one owner and the walkable slope means one
/// thing across the whole world.
/// </summary>
/// <remarks>
/// <para>A body contributes up to <see cref="WorldCollider.MaxVolumes"/> sphere, capsule, or oriented-box volumes.
/// Resolution depenetrates each from every collider, killing the velocity component driving into each resolved
/// surface. A support push whose normal's <c>+Y</c> alignment clears the compiled
/// <see cref="FixedWorldCollision.GroundedThreshold"/> grounds the body. Lateral edge approach instead reports an
/// obstruction and the full clearance a step-up needs; vertical landing retains the rounded surface normal.</para>
/// <para>Single-pass-per-iteration relaxation (up to <c>MaxIterations</c>): two adjacent solid boxes can push a body
/// back and forth within one tick. Accepted at authoring scale; the fix (push-order by penetration depth) is cheap and
/// additive. Boxes retain the drawn fillet through <see cref="FixedStaticCollider.RoundedBox"/>; chamfered Boxes
/// and Superellipsoids use their exact convex support maps. Spheres and plain circular cylinders
/// use their closest-point geometry; rounded/chamfered circular rims use contained cores with computed error bounds.
/// Boolean carving, smoothing, and other finite primitive
/// surfaces are not modelled: those primitives use their oriented bounding box (<see cref="FixedStaticCollider.Box"/>). A
/// solid screen still uses its world-axis bounding box, exact only for an axis-aligned slab. A sweep
/// visits only the colliders a 4 m grid over world X and Z places near its move, and rejects any whose padded world
/// bounds miss the body's before its exact query; neither cull changes the result.</para>
/// <para>A placement carrying both a solid and an attach facet compiles no static collider here — its origin/yaw are
/// not the row's authored transform, they are a live body's resolved pose. <see cref="RefreshAttached"/> recomputes
/// those colliders once per tick, before any body advances, so every body's <see cref="Resolve"/> call this tick
/// depenetrates against the same snapshot rather than repeating the recompute per body.</para>
/// </remarks>
public sealed class WorldColliderSet : IContactField {
    private List<FixedStaticCollider> m_attachedColliders = [];
    // The attached row each collider in m_attachedColliders came from, parallel to it.
    private List<int> m_attachedColliderRows = [];
    // The refresh before the latest one: the colliders a body that was grounded last tick stood on, and their rows.
    private List<FixedStaticCollider> m_previousAttachedColliders = [];
    private List<int> m_previousAttachedColliderRows = [];
    // Each attached row's resolved origin at the latest refresh and at the one before it, and whether the row resolved
    // at each: the difference is the displacement a body standing on the row rides (TryRideDisplacement).
    private FixedVector3[] m_attachedOrigins;
    private FixedVector3[] m_previousAttachedOrigins;
    private FixedQuaternion[] m_attachedRotations;
    private FixedQuaternion[] m_previousAttachedRotations;
    private bool[] m_attachedResolved;
    private bool[] m_previousAttachedResolved;

    // Every ATTACHED solid row (Attach + Solid both set — refused only under the FIELD provider, see the validator):
    // its geometry cannot join m_colliders above because its origin/yaw are not the row's static authored transform,
    // they are a live body's pose. RefreshAttached recomputes m_attachedColliders from these once per tick. Attached
    // colliders never carry a grip decision — hold/tether queries search m_colliders alone (see
    // TryHoldableSurfaceAlongDirection/TryNearestSurfaceAlongDirection), so a moving carrier is never itself
    // holdable or tetherable today.
    private readonly IReadOnlyList<(WorldPlacement Placement, WorldPrototype Creation)> m_attachedRows;
    private readonly FixedStaticCollider[] m_colliders;
    // Each m_colliders entry's world bounds, in the same order, so a sweep skips the colliders a body cannot reach.
    private readonly FixedVector3?[] m_colliderExtents;

    // A uniform grid over world X and Z of those bounds, so a sweep visits the colliders near its move rather than
    // all of them. A collider without bounds, or one spanning more than GridCellLimit cells, joins every query.
    private const int GridCellShift = 18;
    private const int GridCellLimit = 64;

    private static readonly FixedQ4816 RegionMargin = FixedQ4816.One;

    private readonly Dictionary<(int X, int Z), int[]> m_colliderGrid;
    private readonly int[] m_unboundedColliders;
    private readonly List<int> m_candidates = [];

    public IReadOnlyList<WorldDiagnosticCollider> DiagnosticColliders { get; private set; } = [];

    // The holdable subset of m_colliders, filtered at build time rather than the full array gated after the fact:
    // filtering first is the difference between "the nearest holdable surface along the probe" and "the nearest
    // surface along the probe, if it is holdable", and the latter lets an ordinary floor shadow every holdable wall
    // beside it. Parallel to m_holdableGrantedByOverride.
    private readonly FixedStaticCollider[] m_holdableColliders;
    // Whether a per-placement WorldPlacementGrip override (rather than the world's own DefaultGrip) is what made
    // the collider at the same index in m_holdableColliders holdable — the read-back's "which policy layer granted
    // it" fact.
    private readonly bool[] m_holdableGrantedByOverride;
    private readonly FixedStaticContactSolver m_solver;

    // A running fold of the last-refreshed m_attachedColliders, so RefreshAttached can tell whether the carrier
    // actually moved without keeping a second snapshot array. AttachedRevision only advances when the fold changes —
    // never on every call — so a world with no moving attached row (the common case) never wakes a sleeping body.
    private ulong m_attachedHash;

    private WorldColliderSet(FixedStaticCollider[] colliders, FixedStaticCollider[] holdableColliders, bool[] holdableGrantedByOverride, FixedWorldCollision tuning, IReadOnlyList<(WorldPlacement Placement, WorldPrototype Creation)> attachedRows) {
        m_colliders = colliders;
        m_colliderExtents = Array.ConvertAll(array: colliders, converter: static collider => collider.WorldHalfExtent());
        (m_colliderGrid, m_unboundedColliders) = BuildGrid(colliders: colliders, extents: m_colliderExtents);
        m_holdableColliders = holdableColliders;
        m_holdableGrantedByOverride = holdableGrantedByOverride;
        m_attachedRows = attachedRows;
        m_attachedVisitor = RecordAttachedCopy;
        m_attachedOrigins = new FixedVector3[attachedRows.Count];
        m_previousAttachedOrigins = new FixedVector3[attachedRows.Count];
        m_attachedRotations = new FixedQuaternion[attachedRows.Count];
        m_previousAttachedRotations = new FixedQuaternion[attachedRows.Count];
        m_attachedResolved = new bool[attachedRows.Count];
        m_previousAttachedResolved = new bool[attachedRows.Count];
        m_solver = new FixedStaticContactSolver(
            ContactSkin: tuning.ContactSkin,
            GroundedThreshold: tuning.GroundedThreshold,
            MaxIterations: tuning.MaxIterations
        );
    }

    /// <summary>Gets a counter that advances whenever <see cref="RefreshAttached"/> finds an attached solid row at a
    /// different resolved pose than the snapshot it replaces — never on a call that leaves every collider unchanged.
    /// A sleeping body compares this against the value it last observed to decide whether a moving carrier woke it.</summary>
    public ulong AttachedRevision { get; private set; }
    /// <summary>Gets the number of solid boxes in the set.</summary>
    public int BoxCount { get; private init; }
    /// <summary>Gets the number of solid circular cylinders in the set.</summary>
    public int CylinderCount { get; private init; }
    /// <summary>Gets the number of placement-derived boxes in the set.</summary>
    public int PlacementBoxCount { get; private init; }
    /// <summary>Gets the number of placement-derived circular cylinders in the set.</summary>
    public int PlacementCylinderCount { get; private init; }
    /// <summary>Gets the number of placement-derived half-spaces in the set.</summary>
    public int PlacementPlaneCount { get; private init; }
    /// <summary>Gets the number of placement-derived spheres in the set.</summary>
    public int PlacementSphereCount { get; private init; }
    /// <summary>Gets the number of solid half-spaces in the set.</summary>
    public int PlaneCount { get; private init; }
    /// <summary>Gets the total collider count.</summary>
    public int SolidCount => (((SphereCount + BoxCount) + CylinderCount) + PlaneCount);
    /// <summary>Gets the number of solid spheres in the set.</summary>
    public int SphereCount { get; private init; }

    /// <summary>Measures the analytic collider vocabulary without materializing the collider array.</summary>
    /// <param name="definition">The live world definition.</param>
    /// <returns>The screen and placement contribution.</returns>
    internal static WorldContactCensus Measure(WorldDefinition definition) {
        var worldSeed = (definition.Generation?.WorldSeed ?? 0UL);
        var spheres = 0L;
        var boxes = 0L;
        var planes = 0L;
        var placementSpheres = 0L;
        var placementBoxes = 0L;
        var placementCylinders = 0L;
        var placementPlanes = 0L;
        var unsupported = 0L;

        foreach (var screen in definition.Screens) {
            if (screen.Solid is not null) {
                boxes++;
            }
        }

        foreach (var placement in definition.Placements) {
            if (
                (placement.Solid is null) ||
                (WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: placement.ShownPrototypeId
            ) is not { } creation)
            ) {
                continue;
            }

            var copies = CreationStampLattice.MaterializedCopyCount(
                pattern: WorldPlacementStamp.PatternFor(placement: placement),
                sampledCount: WorldPlacementStamp.SampledFixedOffsetsFor(
                    placement: placement,
                    worldSeed: worldSeed
                )?.Count,
                mirror: WorldPlacementStamp.MirrorFor(placement: placement)
            );

            foreach (var shape in (creation.Document.Shapes ?? [])) {
                // A shape carrying domain ops compiles one collider PER EXPANDED COPY, exactly as Build emits them —
                // counting the authored shape once would under-report the census the placement budget is read against.
                var shapeCopies = (ShapeDomainOps.TryExpand(
                    domain: shape.Domain,
                    subject: shape.Position,
                    frames: out var frames,
                    refusal: out _
                )
                    ? CreationStampLattice.MultiplySaturated(
                        ceiling: long.MaxValue,
                        left: copies,
                        right: frames.Length
                    )
                    : copies
                );

                if (shape.Type == SdfSolidPrimitive.Plane) {
                    placementPlanes = AddSaturated(
                        left: placementPlanes,
                        right: shapeCopies
                    );
                } else if (
                    (shape.Type == SdfSolidPrimitive.Sphere) &&
                    CreationStampEmitter.IsIsotropicallyScaled(shape: shape)
                ) {
                    placementSpheres = AddSaturated(
                        left: placementSpheres,
                        right: shapeCopies
                    );
                } else if ((shape.Type == SdfSolidPrimitive.Cylinder) &&
                    (MathF.Abs(x: shape.Scale.X) == MathF.Abs(x: shape.Scale.Z))) {
                    placementCylinders = AddSaturated(left: placementCylinders, right: shapeCopies);
                } else {
                    placementBoxes = AddSaturated(
                        left: placementBoxes,
                        right: shapeCopies
                    );
                }
            }
        }

        spheres = AddSaturated(
            left: spheres,
            right: placementSpheres
        );
        boxes = AddSaturated(
            left: boxes,
            right: placementBoxes
        );
        planes = AddSaturated(
            left: planes,
            right: placementPlanes
        );

        return new WorldContactCensus(
            BoxCount: boxes,
            CylinderCount: placementCylinders,
            PlacementBoxCount: placementBoxes,
            PlacementCylinderCount: placementCylinders,
            PlacementPlaneCount: placementPlanes,
            PlacementSphereCount: placementSpheres,
            PlaneCount: planes,
            SphereCount: spheres,
            UnsupportedPlacementCount: unsupported
        );
    }

    private static long AddSaturated(long left, long right) =>
        left.AddSaturating(right: right);
    private static void FoldCollider(in FixedStaticCollider collider, ref Fnv1aHash hash) {
        hash.Add(value: ((byte)collider.Kind));
        hash.Add(value: collider.Center.X.Value);
        hash.Add(value: collider.Center.Y.Value);
        hash.Add(value: collider.Center.Z.Value);
        hash.Add(value: collider.Extent.X.Value);
        hash.Add(value: collider.Extent.Y.Value);
        hash.Add(value: collider.Extent.Z.Value);
        hash.Add(value: collider.Radius.Value);
        hash.Add(value: collider.Chamfer.Value);
        hash.Add(value: collider.Exponent.Value);
        hash.Add(value: collider.Rotation.X.Value);
        hash.Add(value: collider.Rotation.Y.Value);
        hash.Add(value: collider.Rotation.Z.Value);
        hash.Add(value: collider.Rotation.W.Value);
    }

    // Boxes retain their fillet and circular cylinders their surface; other finite primitives retain a bound.
    private static readonly FixedQ4816 SqrtTwo = FixedQ4816.Sqrt(value: FixedQ4816.FromInteger(value: 2));
    private static readonly FixedQ4816 BevelFactor = (FixedQ4816.FromInteger(value: 2) - SqrtTwo);

    private static FixedStaticCollider PlacementFinite(in FixedCreationStampPrimitiveCopy copy, FixedQ4816 margin) {
        if (copy.Shape.Type == SdfSolidPrimitive.Box) {
            var shapeScaleX = FixedQ4816.Max(x: FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: copy.Shape.Scale.X)),
                y: FixedQ4816.FromDouble(value: SdfSolidGeometry.MinimumScale));
            var stampScale = (copy.LocalHalfExtents.X / shapeScaleX);
            var radius = (FixedQ4816.FromDouble(value: SdfSolidGeometry.BoxCornerRadius(scale: copy.Shape.Scale)) * stampScale);
            var chamfer = (FixedQ4816.FromDouble(value: (copy.Shape.Chamfer ?? 0f)) * stampScale);
            var inner = FixedVector3.Max(left: FixedVector3.Zero, right: (copy.LocalHalfExtents - new FixedVector3(X: radius, Y: radius, Z: radius)));

            if (chamfer > FixedQ4816.Zero) {
                // Match SdfProgramBuilder.ChamferedRectangle's erosion, including the cap/profile corner.
                var insetChamfer = FixedQ4816.Max(x: FixedQ4816.Zero, y: (chamfer - (radius * BevelFactor)));

                return FixedStaticCollider.ChamferedBox(copy.Center, copy.Rotation, inner, insetChamfer, (radius + margin));
            }
            return FixedStaticCollider.RoundedBox(copy.Center, copy.Rotation, inner, (radius + margin));
        }
        if (copy.Shape.Type == SdfSolidPrimitive.Superellipsoid) {
            return FixedStaticCollider.Superellipsoid(copy.Center, copy.Rotation, copy.LocalHalfExtents,
                FixedQ4816.FromDouble(value: (copy.Shape.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent))) with { Radius = margin };
        }
        if ((copy.Shape.Type == SdfSolidPrimitive.Cylinder) && (copy.LocalHalfExtents.X == copy.LocalHalfExtents.Z)) {
            var inset = CylinderInset(copy: copy);

            return FixedStaticCollider.Cylinder(center: copy.Center, radius: ((copy.LocalHalfExtents.X - inset.X) + margin),
                halfHeight: ((copy.LocalHalfExtents.Y - inset.Y) + margin), rotation: copy.Rotation);
        }
        return FixedStaticCollider.Box(
            center: copy.Center,
            halfExtents: (copy.LocalHalfExtents + new FixedVector3(
                X: margin,
                Y: margin,
                Z: margin
            )),
            rotation: copy.Rotation
        );
    }
    // A rim without an exact cylinder contact uses its contained core. The maximum missing distance follows
    // from the two radial/height contractions and the authored enclosing cylinder, not a shape-name allowance.
    private static FixedVector2 CylinderInset(in FixedCreationStampPrimitiveCopy copy) {
        var scale = (copy.LocalHalfExtents.X / FixedQ4816.Max(x: FixedQ4816.FromDouble(value: MathF.Abs(x: copy.Shape.Scale.X)),
            y: FixedQ4816.FromDouble(value: SdfSolidGeometry.MinimumScale)));
        var chamfer = (FixedQ4816.FromDouble(value: (copy.Shape.Chamfer ?? 0f)) * scale);
        var rounding = (FixedQ4816.FromDouble(value: (copy.Shape.Rounding ?? 0f)) * scale);

        return ((chamfer > FixedQ4816.Zero) ? new FixedVector2(X: chamfer, Y: FixedQ4816.Zero) : new FixedVector2(X: rounding, Y: rounding));
    }
    // The axis-aligned bounding box of a screen slab's oriented frame: the geometry center sits one HalfDepth behind the
    // front-face Origin along the face normal, and each world-axis half-extent is the |projection| of the three oriented
    // axes. Exact for the axis-aligned screens the built-in world ships; conservative (bounding) for a rotated slab.
    // The authored Right/Up are used as the box axes directly, which is sound only because they are orthogonal: the
    // validator refuses a skewed frame (WorldDefinitionValidator's screen basis rule), so this triple and the
    // orthonormal rotation the client stamps the slab with are the same frame. Relaxing that rule would make this
    // collider and the rendered slab different solids.
    private static (FixedVector3 Center, FixedVector3 HalfExtents) ScreenBox(WorldScreen screen) {
        var normal = FixedVector3.Cross(
            left: FixedVector3.FromVector3(value: screen.Right),
            right: FixedVector3.FromVector3(value: screen.Up)
        ).Normalize();
        var right = FixedVector3.FromVector3(value: screen.Right).Normalize();
        var up = FixedVector3.FromVector3(value: screen.Up).Normalize();
        var halfWidth = FixedQ4816.FromDouble(value: screen.HalfWidth);
        var halfHeight = FixedQ4816.FromDouble(value: screen.HalfHeight);
        var halfDepth = FixedQ4816.FromDouble(value: screen.HalfDepth);
        var center = (FixedVector3.FromVector3(value: screen.Origin) - (normal * halfDepth));
        var half = new FixedVector3(
            X: (((FixedQ4816.Abs(value: right.X) * halfWidth) + (FixedQ4816.Abs(value: up.X) * halfHeight)) + (FixedQ4816.Abs(value: normal.X) * halfDepth)),
            Y: (((FixedQ4816.Abs(value: right.Y) * halfWidth) + (FixedQ4816.Abs(value: up.Y) * halfHeight)) + (FixedQ4816.Abs(value: normal.Y) * halfDepth)),
            Z: (((FixedQ4816.Abs(value: right.Z) * halfWidth) + (FixedQ4816.Abs(value: up.Z) * halfHeight)) + (FixedQ4816.Abs(value: normal.Z) * halfDepth))
        );

        return (Center: center, HalfExtents: half);
    }

    /// <summary>Builds the analytic contact field from a definition.</summary>
    /// <param name="definition">The world definition supplying the collision tuning and solid rows.</param>
    /// <param name="previous">A set built from an earlier definition, whose colliders for each row that stands as it
    /// was (the same row, showing the same creation at the same resolved frame) are taken rather than compiled again, or
    /// <see langword="null"/> to compile every row.</param>
    /// <returns>The analytic field.</returns>
    public static WorldColliderSet Build(WorldDefinition definition, WorldColliderSet? previous = null) {
        var collision = definition.Collision;
        var worldSeed = (definition.Generation?.WorldSeed ?? 0UL);

        var tuning = FixedWorldCollision.Compile(collision: collision);
        var colliders = new List<FixedStaticCollider>();
        var diagnosticColliders = new List<WorldDiagnosticCollider>();
        var holdableColliders = new List<FixedStaticCollider>();
        var holdableGrantedByOverride = new List<bool>();
        var worldDefaultGrip = definition.Collision.DefaultHold;
        var attachedRows = new List<(WorldPlacement Placement, WorldPrototype Creation)>();
        var placementsCompiled = new Dictionary<WorldPlacement, PlacementColliders>(comparer: ReferenceEqualityComparer.Instance);
        var boxes = 0;
        var placementSpheres = 0;
        var placementBoxes = 0;
        var placementCylinders = 0;
        var placementPlanes = 0;

        foreach (var screen in definition.Screens) {
            if (screen.Solid is not { } solid) {
                continue;
            }

            var rowMargin = FixedQ4816.FromDouble(value: solid.Margin);

            var (center, halfExtents) = ScreenBox(screen: screen);
            var screenCollider = FixedStaticCollider.AxisAlignedBox(
                center: center,
                halfExtents: (halfExtents + new FixedVector3(
                    X: rowMargin,
                    Y: rowMargin,
                    Z: rowMargin
                ))
            );

            colliders.Add(item: screenCollider);
            diagnosticColliders.Add(item: new WorldDiagnosticCollider(screenCollider, $"screen:{screen.Index}", "screen", "slab", "box", solid.Margin));
            // A screen carries no grip facet — its collider always follows the world default, never an override.
            if (worldDefaultGrip) {
                holdableColliders.Add(item: screenCollider);
                holdableGrantedByOverride.Add(item: false);
            }
            boxes++;
        }

        foreach (var placement in definition.Placements) {
            // A dealt template collides with nothing itself; its solid facet is what its dealt children carry.
            if (
                (placement.Solid is null) ||
                (placement.Deal is not null) ||
                (WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: placement.ShownPrototypeId
            ) is not { } creation)
            ) {
                continue;
            }

            // An ATTACHED solid row's authored Position/YawDegrees are inert (see WorldPlacement's own doc) — its
            // geometry rides a live body instead, recomputed every tick by RefreshAttached rather than compiled here
            // once. Collected separately; never folded into the static m_colliders array.
            if (placement.Attach is not null) {
                attachedRows.Add(item: (Placement: placement, Creation: creation));

                continue;
            }

            var resolvedFrame = WorldDefinitionRows.ResolvedFrame(
                definition: definition,
                placement: placement
            );

            if (
                (previous is null) ||
                !previous.m_placements.TryGetValue(key: placement, value: out var compiled) ||
                !ReferenceEquals(objA: compiled.Creation, objB: creation) ||
                (compiled.Frame != resolvedFrame) ||
                (compiled.WorldSeed != worldSeed) ||
                (compiled.DefaultGrip != worldDefaultGrip)
            ) {
                compiled = CompilePlacement(
                    creation: creation,
                    defaultGrip: worldDefaultGrip,
                    frame: resolvedFrame,
                    placement: placement,
                    worldSeed: worldSeed
                );
            }

            placementsCompiled[placement] = compiled;
            colliders.AddRange(collection: compiled.Colliders);
            diagnosticColliders.AddRange(collection: compiled.Diagnostics);
            if (compiled.Holdable) {
                holdableColliders.AddRange(collection: compiled.Colliders);
                for (var index = 0; (index < compiled.Colliders.Length); index++) {
                    holdableGrantedByOverride.Add(item: compiled.HoldableIsOverride);
                }
            }
            boxes += compiled.Boxes;
            placementSpheres += compiled.Spheres;
            placementBoxes += compiled.Boxes;
            placementCylinders += compiled.Cylinders;
            placementPlanes += compiled.Planes;
        }

        return new WorldColliderSet(
            holdableColliders: holdableColliders.ToArray(),
            holdableGrantedByOverride: holdableGrantedByOverride.ToArray(),
            colliders: colliders.ToArray(),
            tuning: tuning,
            attachedRows: attachedRows
        ) {
            DiagnosticColliders = diagnosticColliders.ToArray(),
            BoxCount = boxes,
            CylinderCount = placementCylinders,
            PlacementBoxCount = placementBoxes,
            PlacementCylinderCount = placementCylinders,
            PlacementPlaneCount = placementPlanes,
            PlacementSphereCount = placementSpheres,
            PlaneCount = placementPlanes,
            SphereCount = placementSpheres,
            m_placements = placementsCompiled,
        };
    }
    // One static solid row's colliders, compiled from the creation it shows at its resolved frame, with the inputs they
    // were compiled from.
    private sealed record PlacementColliders(WorldPrototype Creation, CompiledPlacementFrame Frame, ulong WorldSeed, bool DefaultGrip,
        FixedStaticCollider[] Colliders, WorldDiagnosticCollider[] Diagnostics, bool Holdable, bool HoldableIsOverride, int Spheres,
        int Boxes, int Cylinders, int Planes);

    // The static solid rows this set compiled, by row, so a set built from a later definition takes the rows that stand.
    private Dictionary<WorldPlacement, PlacementColliders> m_placements = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>Takes a response change into this set in place when it moves no collider: each changed row now shows
    /// another creation, and when that creation compiles, at the row's frame, to exactly the colliders and grip the
    /// row had, the set keeps every collider and only names the new creation in its diagnostics.</summary>
    /// <param name="previous">The definition the set was last built or carried over.</param>
    /// <param name="definition">The definition the response change installed: the same rows, with the changed ones
    /// showing other creations.</param>
    /// <param name="rows">The indices of the rows whose shown creation the change moved.</param>
    /// <returns><see langword="true"/> when the set now stands for <paramref name="definition"/>; <see langword="false"/>
    /// when a changed row's colliders differ (or it is attached), so the set must be built again, and nothing was
    /// changed.</returns>
    internal bool TryCarryAcrossHolding(WorldDefinition previous, WorldDefinition definition, IReadOnlyList<int> rows) {
        List<(WorldPlacement Before, WorldPlacement After, PlacementColliders Compiled)>? carried = null;

        foreach (var row in rows) {
            var before = previous.Placements[row];
            var after = definition.Placements[row];

            // A dealt template and a row with no solid facet compile nothing here.
            if ((after.Solid is null) || (after.Deal is not null)) {
                continue;
            }
            if (
                (after.Attach is not null) ||
                !m_placements.TryGetValue(key: before, value: out var compiled) ||
                (WorldDefinitionRows.FindCreation(creations: definition.Creations, id: after.ShownPrototypeId) is not { } creation)
            ) {
                return false;
            }

            var next = (ReferenceEquals(objA: creation, objB: compiled.Creation)
                ? compiled
                : CompilePlacement(
                    creation: creation,
                    defaultGrip: compiled.DefaultGrip,
                    frame: compiled.Frame,
                    placement: after,
                    worldSeed: compiled.WorldSeed
                )
            );

            if (
                (next.Holdable != compiled.Holdable) ||
                (next.HoldableIsOverride != compiled.HoldableIsOverride) ||
                !next.Colliders.AsSpan().SequenceEqual(other: compiled.Colliders)
            ) {
                return false;
            }

            (carried ??= []).Add(item: (before, after, next));
        }

        if (carried is null) {
            return true;
        }

        var diagnostics = DiagnosticColliders.ToArray();

        foreach (var (before, after, next) in carried) {
            var compiled = m_placements[before];

            _ = m_placements.Remove(key: before);
            m_placements[after] = next;

            if (compiled.Diagnostics.Length == 0) {
                continue;
            }

            var start = Array.FindIndex(array: diagnostics, match: diagnostic => ReferenceEquals(objA: diagnostic, objB: compiled.Diagnostics[0]));

            if (start >= 0) {
                next.Diagnostics.CopyTo(array: diagnostics, index: start);
            }
        }

        DiagnosticColliders = diagnostics;

        return true;
    }

    private static PlacementColliders CompilePlacement(WorldPlacement placement, WorldPrototype creation, CompiledPlacementFrame frame, ulong worldSeed, bool defaultGrip) {
        var solid = placement.Solid!;
        var margin = FixedQ4816.FromDouble(value: solid.Margin);
        var colliders = new List<FixedStaticCollider>();
        var diagnostics = new List<WorldDiagnosticCollider>();
        var spheres = 0;
        var boxes = 0;
        var cylinders = 0;
        var planes = 0;
        // The resolved yaw enters the contract through the SAME degrees-to-fixed-radians idiom every other
        // fixed-point placement path already uses (WorldPopulation, WorldPlacementAttachment), and
        // FixedQuaternion.FromAxisAngle is integer arithmetic — so no platform libm sine reaches a collider.
        // The compiled colliders are re-derived from the document by every process, never baked once and shared,
        // which is why a non-portable transcendental here would be a cross-machine divergence.
        var rotation = FixedQuaternion.FromAxisAngle(
            axis: FixedVector3.UnitY,
            angle: FixedQ4816.FromDegrees(degrees: frame.YawDegrees)
        );

        CreationStampLattice.ForEachFixedInstance(
            origin: FixedVector3.FromVector3(value: frame.Position),
            rotation: rotation,
            pattern: WorldPlacementStamp.PatternFor(placement: placement),
            sampledOffsets: WorldPlacementStamp.SampledFixedOffsetsFor(
                placement: placement,
                worldSeed: worldSeed
            ),
            mirror: WorldPlacementStamp.MirrorFor(placement: placement),
            visitor: instance => CreationStampEmitter.VisitFixedPrimitiveCopies(
                document: creation.EngineDocument,
                transform: new FixedCreationStampTransform(
                    Origin: instance.Origin,
                    Rotation: rotation,
                    Scale: FixedQ4816.FromDouble(value: placement.Scale),
                    ReflectionNormal: instance.ReflectionNormal
                ),
                visitor: copy => {
                    FixedStaticCollider placementCollider;

                    if (copy.Shape.Type == SdfSolidPrimitive.Plane) {
                        var normal = copy.PlaneNormal;

                        placementCollider = FixedStaticCollider.HalfSpace(
                            point: (copy.Center + (normal * margin)),
                            normal: normal
                        );
                        planes++;
                    } else if (
                        (copy.Shape.Type == SdfSolidPrimitive.Sphere) &&
                        (copy.UniformScale > FixedQ4816.Zero)
                    ) {
                        var sphereBounds = SdfSolidGeometry.GetLocalBounds(type: SdfSolidPrimitive.Sphere);

                        placementCollider = FixedStaticCollider.Sphere(
                            center: copy.Center,
                            radius: ((FixedQ4816.FromDouble(value: sphereBounds.HalfExtents.X) * copy.UniformScale) + margin)
                        );
                        spheres++;
                    } else {
                        placementCollider = PlacementFinite(
                            copy: copy,
                            margin: margin
                        );
                        if (placementCollider.Kind == FixedStaticColliderKind.Cylinder) {
                            cylinders++;
                        } else {
                            boxes++;
                        }
                    }

                    colliders.Add(item: placementCollider);
                    diagnostics.Add(item: new WorldDiagnosticCollider(placementCollider, placement.Id.ToString(),
                        creation.Id.ToString(), (copy.Shape.Name?.ToString() ?? copy.Shape.Id.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)),
                        $"{copy.Shape.Type}/{copy.Shape.Blend}", solid.Margin, !creation.EngineDocument.IsGlassShape(shape: copy.Shape)) {
                        ConservativeInset = ((placementCollider.Kind == FixedStaticColliderKind.Cylinder) ? (double)CylinderInset(copy: copy).Length : 0),
                    });
                }
            )
        );

        // Every collider a distribution/mirror expands this ONE row into shares its ONE grip decision — see
        // WorldPlacementGrip's own remarks.
        return new PlacementColliders(
            Creation: creation,
            Frame: frame,
            WorldSeed: worldSeed,
            DefaultGrip: defaultGrip,
            Colliders: colliders.ToArray(),
            Diagnostics: diagnostics.ToArray(),
            Holdable: (placement.Grip?.Holdable ?? defaultGrip),
            HoldableIsOverride: (placement.Grip is not null),
            Spheres: spheres,
            Boxes: boxes,
            Cylinders: cylinders,
            Planes: planes
        );
    }
    /// <summary>Recomputes every ATTACHED solid row's colliders from the live population's CURRENT body poses — the
    /// dynamic counterpart to the static loop <see cref="Build"/> already compiled once for every non-attached solid
    /// row. Called exactly once per tick (<see cref="WorldPopulation.AdvanceSimulated"/>, before any body advances),
    /// so every body's <see cref="Resolve"/> this tick sees the SAME snapshot — an attached solid's carrier is read
    /// as of the END of the PREVIOUS tick, the same one-tick relationship a pushed body already has to every other
    /// body's pose. Reuses <see cref="m_attachedColliders"/>'s backing storage (<see cref="List{T}.Clear"/> keeps the
    /// capacity), so a steady-state document pays no allocation here either.</summary>
    /// <param name="population">The live population supplying each attached row's target body pose — the SAME
    /// resolve <see cref="WorldPlacementAttachment.TryResolve"/> answers for <c>world.attachments</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="population"/> is <see langword="null"/>.</exception>
    public void RefreshAttached(WorldPopulation population) {
        ArgumentNullException.ThrowIfNull(argument: population);

        if (m_attachedRows.Count == 0) {
            return;
        }

        (m_previousAttachedColliders, m_attachedColliders) = (m_attachedColliders, m_previousAttachedColliders);
        (m_previousAttachedColliderRows, m_attachedColliderRows) = (m_attachedColliderRows, m_previousAttachedColliderRows);
        (m_previousAttachedOrigins, m_attachedOrigins) = (m_attachedOrigins, m_previousAttachedOrigins);
        (m_previousAttachedRotations, m_attachedRotations) = (m_attachedRotations, m_previousAttachedRotations);
        (m_previousAttachedResolved, m_attachedResolved) = (m_attachedResolved, m_previousAttachedResolved);
        m_attachedColliders.Clear();
        m_attachedColliderRows.Clear();
        Array.Clear(array: m_attachedResolved);
        RefreshAttachedRows(population: population);
    }

    // This single-threaded refresh reuses its visitor and working fold for every attached row.
    private readonly Action<FixedCreationStampPrimitiveCopy> m_attachedVisitor;

    private Fnv1aHash m_refreshHash;
    private FixedQ4816 m_refreshMargin;
    private int m_refreshRow;

    private void RecordAttachedCopy(FixedCreationStampPrimitiveCopy copy) {

        FixedStaticCollider attached;

        if (copy.Shape.Type == SdfSolidPrimitive.Plane) {
            var normal = copy.PlaneNormal;

            attached = FixedStaticCollider.HalfSpace(
                point: (copy.Center + (normal * m_refreshMargin)),
                normal: normal
            );
            m_attachedColliders.Add(item: attached);
            m_attachedColliderRows.Add(item: m_refreshRow);
            FoldCollider(
                collider: in attached,
                hash: ref m_refreshHash
            );
        } else if (
            (copy.Shape.Type == SdfSolidPrimitive.Sphere) &&
            (copy.UniformScale > FixedQ4816.Zero)
        ) {
            var sphereBounds = SdfSolidGeometry.GetLocalBounds(type: SdfSolidPrimitive.Sphere);

            attached = FixedStaticCollider.Sphere(
                center: copy.Center,
                radius: ((FixedQ4816.FromDouble(value: sphereBounds.HalfExtents.X) * copy.UniformScale) + m_refreshMargin)
            );
            m_attachedColliders.Add(item: attached);
            m_attachedColliderRows.Add(item: m_refreshRow);
            FoldCollider(
                collider: in attached,
                hash: ref m_refreshHash
            );
        } else {
            attached = PlacementFinite(
                copy: copy,
                margin: m_refreshMargin
            );
            m_attachedColliders.Add(item: attached);
            m_attachedColliderRows.Add(item: m_refreshRow);
            FoldCollider(
                collider: in attached,
                hash: ref m_refreshHash
            );
        }

    }
    private void RefreshAttachedRows(WorldPopulation population) {
        m_refreshHash = Fnv1aHash.Create();

        for (var row = 0; (row < m_attachedRows.Count); row++) {
            var (placement, creation) = m_attachedRows[row];

            // An inactive target body resolves nothing — the row's established "contributes nothing" verdict
            // (WorldPlacementAttach's own remarks), never a stale collider parked at the last-known pose.
            if (!WorldPlacementAttachment.TryResolve(
                attach: placement.Attach!,
                population: population,
                position: out var fixedPosition,
                yawRadians: out var fixedYaw,
                reason: out _
            )) {
                continue;
            }

            m_attachedOrigins[row] = fixedPosition;
            m_attachedResolved[row] = true;

            m_refreshMargin = FixedQ4816.FromDouble(value: placement.Solid!.Margin);
            m_refreshRow = row;
            // The SAME creation-shape transform chain the static loop above (and the renderer) already runs, fed the
            // resolved dynamic origin/yaw instead of the row's static authored Position/YawDegrees — and taken through
            // the FIXED-POINT emitter, because this runs every tick on a live body's pose. The single-precision
            // sibling would reach the platform libm's sin/cos here (Quaternion.CreateFromAxisAngle), which no
            // determinism rule permits on a value that decides where a body stops.
            var stampRotation = FixedQuaternion.FromAxisAngle(
                angle: fixedYaw,
                axis: FixedVector3.UnitY
            );

            m_attachedRotations[row] = stampRotation;

            CreationStampEmitter.VisitFixedPrimitiveCopies(
                document: creation.EngineDocument,
                transform: new FixedCreationStampTransform(
                    Origin: fixedPosition,
                    Rotation: stampRotation,
                    Scale: FixedQ4816.FromDouble(value: placement.Scale),
                    ReflectionNormal: null
                ),
                visitor: m_attachedVisitor
            );
        }

        if (m_refreshHash.Value != m_attachedHash) {
            m_attachedHash = m_refreshHash.Value;
            AttachedRevision++;
        }
    }

    /// <summary>Finds a walkable contact against the previous attached geometry and returns its carrier's
    /// transform displacement. The same narrowphase and slope limit as ordinary contact decide support; a yawed box is
    /// therefore just as rideable as an axis-aligned one. Ties retain placement and volume order.</summary>
    /// <param name="position">The body's previous foot point.</param>
    /// <param name="orientation">The body's orientation.</param>
    /// <param name="up">The body's unit up axis.</param>
    /// <param name="volumes">The body's scaled collider volumes.</param>
    /// <param name="displacement">The foot point's displacement under the carrier's translation and yaw.</param>
    /// <returns>Whether a moving support was found.</returns>
    public bool TryRideDisplacement(FixedVector3 position, in FixedQuaternion orientation, in FixedVector3 up,
        ReadOnlySpan<FixedBodyColliderVolume> volumes, out FixedVector3 displacement) {
        displacement = FixedVector3.Zero;
        var found = false;
        var bestDepth = FixedQ4816.Zero;
        var skin = ((m_solver.ContactSkin + m_solver.ContactSkin) + FixedQ4816.Epsilon);

        for (var index = 0; (index < m_previousAttachedColliders.Count); index++) {
            var collider = m_previousAttachedColliders[index];
            var row = m_previousAttachedColliderRows[index];

            if (!m_attachedResolved[row] || !m_previousAttachedResolved[row]) {
                continue;
            }
            foreach (ref readonly var volume in volumes) {
                if (!collider.TryGetPush(orientation: in orientation, position: position, push: out var push,
                    skin: skin, volume: in volume) ||
                    (FixedVector3.Dot(left: push.Normal, right: up) < m_solver.GroundedThreshold) ||
                    (found && (push.Penetration <= bestDepth))) {
                    continue;
                }
                found = true;
                bestDepth = push.Penetration;
                if (m_attachedRotations[row] == m_previousAttachedRotations[row]) {
                    displacement = (m_attachedOrigins[row] - m_previousAttachedOrigins[row]);
                } else {
                    var local = m_previousAttachedRotations[row].RotateInverse(vector: (position - m_previousAttachedOrigins[row]));

                    displacement = ((m_attachedOrigins[row] + m_attachedRotations[row].Rotate(vector: local)) - position);
                }
            }
        }
        return (found && (displacement != FixedVector3.Zero));
    }

    // The cells a world-axis box covers, inclusive: GridCellShift makes each cell 4 m of Q48.16 raw units.
    private static (int X0, int X1, int Z0, int Z1) CellRange(in FixedVector3 center, in FixedVector3 extent) =>
        (((int)((center.X - extent.X).Value >> GridCellShift)), ((int)((center.X + extent.X).Value >> GridCellShift)),
         ((int)((center.Z - extent.Z).Value >> GridCellShift)), ((int)((center.Z + extent.Z).Value >> GridCellShift)));
    private static long CellCount((int X0, int X1, int Z0, int Z1) range) =>
        (((((long)range.X1) - range.X0) + 1L) * ((((long)range.Z1) - range.Z0) + 1L));
    private static (Dictionary<(int X, int Z), int[]> Grid, int[] Unbounded) BuildGrid(FixedStaticCollider[] colliders, FixedVector3?[] extents) {
        var cells = new Dictionary<(int X, int Z), List<int>>();
        var unbounded = new List<int>();

        for (var index = 0; (index < colliders.Length); index++) {
            if (extents[index] is not { } extent) {
                unbounded.Add(item: index);
                continue;
            }
            var range = CellRange(center: colliders[index].Center, extent: extent);

            if (CellCount(range: range) > GridCellLimit) {
                unbounded.Add(item: index);
                continue;
            }
            for (var x = range.X0; (x <= range.X1); x++) {
                for (var z = range.Z0; (z <= range.Z1); z++) {
                    if (!cells.TryGetValue(key: (x, z), value: out var list)) {
                        list = [];
                        cells.Add(key: (x, z), value: list);
                    }
                    list.Add(item: index);
                }
            }
        }
        var grid = new Dictionary<(int X, int Z), int[]>(capacity: cells.Count);

        foreach (var (cell, list) in cells) {
            grid.Add(key: cell, value: [.. list]);
        }
        return (grid, [.. unbounded]);
    }
    // The colliders a move can reach: those near the body's bounds at both ends of the move, grown by a margin for the
    // pushes it meets on the way. The solver checks every collider once the body's bounds leave that region, so the
    // margin trades only speed, never the result.
    private FixedColliderCandidates Candidates(in FixedVector3 previousPosition, in FixedVector3 position, in FixedQuaternion orientation,
        ReadOnlySpan<FixedBodyColliderVolume> volumes) {
        if (volumes.IsEmpty) { return default; }
        var (firstCenter, firstExtent) = FixedColliderBounds.WorldBounds(orientation: in orientation, position: position, volume: in volumes[0]);
        // The least corner is carried negated, so one component-wise maximum grows both corners.
        var negatedMinimum = -(firstCenter - firstExtent);
        var maximum = (firstCenter + firstExtent);

        foreach (ref readonly var volume in volumes) {
            foreach (var at in ((ReadOnlySpan<FixedVector3>)[previousPosition, position])) {
                var (center, extent) = FixedColliderBounds.WorldBounds(orientation: in orientation, position: at, volume: in volume);

                negatedMinimum = FixedVector3.Max(left: negatedMinimum, right: -(center - extent));
                maximum = FixedVector3.Max(left: maximum, right: (center + extent));
            }
        }
        var minimum = -negatedMinimum;
        var margin = new FixedVector3(X: RegionMargin, Y: RegionMargin, Z: RegionMargin);
        var two = FixedQ4816.FromInteger(value: 2);
        var regionCenter = ((minimum + maximum) / two);
        var regionExtent = (((maximum - minimum) / two) + margin);
        var skin = new FixedVector3(X: m_solver.ContactSkin, Y: m_solver.ContactSkin, Z: m_solver.ContactSkin);
        var range = CellRange(center: regionCenter, extent: (regionExtent + skin));

        if (CellCount(range: range) > GridCellLimit) { return default; }
        m_candidates.Clear();
        foreach (var index in m_unboundedColliders) {
            if ((m_colliderExtents[index] is not { } extent) || !m_solver.Misses(bodyCenter: regionCenter, bodyExtent: regionExtent,
                colliderCenter: m_colliders[index].Center, colliderExtent: extent)) {
                m_candidates.Add(item: index);
            }
        }
        for (var x = range.X0; (x <= range.X1); x++) {
            for (var z = range.Z0; (z <= range.Z1); z++) {
                if (!m_colliderGrid.TryGetValue(key: (x, z), value: out var cell)) { continue; }
                foreach (var index in cell) {
                    if (!m_solver.Misses(bodyCenter: regionCenter, bodyExtent: regionExtent,
                        colliderCenter: m_colliders[index].Center, colliderExtent: m_colliderExtents[index]!.Value)) {
                        m_candidates.Add(item: index);
                    }
                }
            }
        }
        m_candidates.Sort();
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_candidates);
        var count = 0;

        for (var read = 0; (read < span.Length); read++) {
            if ((count == 0) || (span[(count - 1)] != span[read])) { span[count++] = span[read]; }
        }
        return new FixedColliderCandidates(indices: span[..count], regionCenter: regionCenter, regionExtent: regionExtent);
    }

    /// <inheritdoc/>
    public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        // ATTACHED solid rows ride the second span: RefreshAttached recomputes them once per tick, never here (Resolve
        // runs once PER BODY, so refreshing here would repeat that recompute for every other body in the world).
        m_solver.Resolve(
            colliders: m_colliders,
            dynamicColliders: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_attachedColliders),
            orientation: in orientation,
            position: ref position,
            up: in up,
            velocity: ref velocity,
            volumes: volumes,
            colliderExtents: m_colliderExtents,
            candidates: Candidates(orientation: in orientation, position: position, previousPosition: position, volumes: volumes)
        );
    /// <inheritdoc/>
    public ContactResolution ResolveSweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity,
        in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        m_solver.ResolveSweep(colliders: m_colliders,
            dynamicColliders: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_attachedColliders),
            previousPosition: in previousPosition, position: ref position, velocity: ref velocity,
            orientation: in orientation, volumes: volumes, up: in up, colliderExtents: m_colliderExtents,
            candidates: Candidates(orientation: in orientation, position: position, previousPosition: in previousPosition, volumes: volumes));
    /// <inheritdoc/>
    // Searches m_holdableColliders alone (never m_attachedColliders — see the field's own remarks), with a zero
    // assist cone: a hold aims at the surface it is already touching, not at a target it is trying to acquire.
    public bool TryHoldableSurfaceAlongDirection(in FixedVector3 origin, in FixedVector3 direction, FixedQ4816 maxDistance, out FixedSurfaceAttachCandidate candidate, out bool grantedByOverride) {
        if (!FixedSurfaceQuery.TryNearestDirected(
            assistHalfAngle: FixedQ4816.Zero,
            candidate: out candidate,
            colliders: m_holdableColliders,
            direction: in direction,
            dynamicColliders: [],
            maxDistance: maxDistance,
            origin: in origin
        )) {
            candidate = default;
            grantedByOverride = false;

            return false;
        }

        grantedByOverride = m_holdableGrantedByOverride[candidate.ColliderIndex];

        return true;
    }
    /// <inheritdoc/>
    public bool TryNearestSurfaceAlongDirection(in FixedVector3 origin, in FixedVector3 direction, FixedQ4816 maxDistance, FixedQ4816 assistHalfAngle, out FixedSurfaceAttachCandidate candidate) => FixedSurfaceQuery.TryNearestDirected(
        assistHalfAngle: assistHalfAngle,
        candidate: out candidate,
        colliders: m_colliders,
        direction: in direction,
        dynamicColliders: [],
        maxDistance: maxDistance,
        origin: in origin
    );
    /// <inheritdoc/>
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) {
        _ = position;
        up = FixedVector3.UnitY;

        return true;
    }
}
