using Puck.Forge.Authoring;
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
/// surface. A push whose normal's <c>+Y</c> alignment clears the compiled
/// <see cref="FixedWorldCollision.GroundedThreshold"/> grounds the body — the same test the ground plane uses.</para>
/// <para>Single-pass-per-iteration relaxation (up to <c>MaxIterations</c>): two adjacent solid boxes can push a body
/// back and forth within one tick. Accepted at authoring scale; the fix (push-order by penetration depth) is cheap and
/// additive. A slab's <c>Round</c> and a creation's boolean carving, smoothing, rounded box corners, and non-box primitive
/// surfaces are deliberately not modelled. An isotropically scaled sphere is exact; every other finite placement
/// primitive uses its world-axis bounding box, exact as a bound for an axis-aligned sharp box and conservative for
/// rotated, rounded, or non-box geometry. No broadphase: O(bodies × colliders); a Y-sorted array with an AABB reject on
/// the swept bounds is the cheap first cull if profiling ever demands one, behind this seam with no signature change.</para>
/// <para>A placement carrying both a solid and an attach facet compiles no static collider here — its origin/yaw are
/// not the row's authored transform, they are a live body's resolved pose. <see cref="RefreshAttached"/> recomputes
/// those colliders once per tick, before any body advances, so every body's <see cref="Resolve"/> call this tick
/// depenetrates against the same snapshot rather than repeating the recompute per body.</para>
/// </remarks>
internal sealed class WorldColliderSet : IContactField {
    private static readonly FixedVector3 UnitY = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    );

    private readonly List<FixedStaticCollider> m_attachedColliders = [];
    // Every ATTACHED solid row (Attach + Solid both set — refused only under the FIELD provider, see the validator):
    // its geometry cannot join m_colliders above because its origin/yaw are not the row's static authored transform,
    // they are a live body's pose. RefreshAttached recomputes m_attachedColliders from these once per tick.
    private readonly IReadOnlyList<(WorldPlacement Placement, WorldPrototype Creation)> m_attachedRows;
    private readonly FixedStaticCollider[] m_colliders;
    private readonly FixedStaticContactSolver m_solver;

    private WorldColliderSet(FixedStaticCollider[] colliders, FixedWorldCollision tuning, IReadOnlyList<(WorldPlacement Placement, WorldPrototype Creation)> attachedRows) {
        m_colliders = colliders;
        m_attachedRows = attachedRows;
        m_solver = new FixedStaticContactSolver(
            ContactSkin: tuning.ContactSkin,
            GroundedThreshold: tuning.GroundedThreshold,
            MaxIterations: tuning.MaxIterations
        );
    }

    /// <summary>Gets the number of solid boxes in the set.</summary>
    public int BoxCount { get; private init; }
    /// <summary>Gets the number of placement-derived boxes in the set.</summary>
    public int PlacementBoxCount { get; private init; }
    /// <summary>Gets the number of placement-derived half-spaces in the set.</summary>
    public int PlacementPlaneCount { get; private init; }
    /// <summary>Gets the number of placement-derived spheres in the set.</summary>
    public int PlacementSphereCount { get; private init; }
    /// <summary>Gets the number of solid half-spaces in the set.</summary>
    public int PlaneCount { get; private init; }
    /// <summary>Gets the total collider count.</summary>
    public int SolidCount => ((SphereCount + BoxCount) + PlaneCount);
    /// <summary>Gets the number of solid spheres in the set.</summary>
    public int SphereCount { get; private init; }

    /// <summary>Measures the analytic collider vocabulary without materializing the collider array.</summary>
    /// <param name="definition">The live world definition.</param>
    /// <returns>The screen and placement contribution.</returns>
    internal static WorldContactCensus Measure(WorldDefinition definition) {
        var spheres = 0L;
        var boxes = 0L;
        var planes = 0L;
        var placementSpheres = 0L;
        var placementBoxes = 0L;
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
                id: placement.PrototypeId
            ) is not { } creation)
            ) {
                continue;
            }

            var copies = CreationStampLattice.MaterializedCopyCount(
                pattern: WorldPlacementStamp.PatternFor(placement: placement),
                mirror: WorldPlacementStamp.MirrorFor(placement: placement)
            );

            foreach (var shape in (creation.Document.Shapes ?? [])) {
                // A shape carrying domain ops compiles one collider PER EXPANDED COPY, exactly as Build emits them —
                // counting the authored shape once would under-report the census the placement budget is read against.
                var shapeCopies = (ShapeDomainOps.TryExpand(
                    domain: shape.Domain,
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
            PlacementBoxCount: placementBoxes,
            PlacementPlaneCount: placementPlanes,
            PlacementSphereCount: placementSpheres,
            PlaneCount: planes,
            SphereCount: spheres,
            UnsupportedPlacementCount: unsupported
        );
    }

    private static long AddSaturated(long left, long right) => ((left > (long.MaxValue - right))
        ? long.MaxValue
        : (left + right)
    );
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
    /// <returns>The analytic field.</returns>
    public static WorldColliderSet Build(WorldDefinition definition) {
        var collision = definition.Collision;

        var tuning = FixedWorldCollision.Compile(collision: collision);
        var colliders = new List<FixedStaticCollider>();
        var attachedRows = new List<(WorldPlacement Placement, WorldPrototype Creation)>();
        var spheres = 0;
        var boxes = 0;
        var planes = 0;
        var placementSpheres = 0;
        var placementBoxes = 0;
        var placementPlanes = 0;

        foreach (var screen in definition.Screens) {
            if (screen.Solid is not { } solid) {
                continue;
            }

            var rowMargin = FixedQ4816.FromDouble(value: solid.Margin);

            var (center, halfExtents) = ScreenBox(screen: screen);

            colliders.Add(item: FixedStaticCollider.AxisAlignedBox(
                center: center,
                halfExtents: (halfExtents + new FixedVector3(
                    X: rowMargin,
                    Y: rowMargin,
                    Z: rowMargin
                ))
            ));
            boxes++;
        }

        foreach (var placement in definition.Placements) {
            if (
                (placement.Solid is not { } solid) ||
                (WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: placement.PrototypeId
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

            var margin = FixedQ4816.FromDouble(value: solid.Margin);
            // The authored yaw enters the contract through the SAME degrees-to-fixed-radians idiom every other
            // fixed-point placement path already uses (WorldPopulation, WorldPlacementAttachment), and
            // FixedQuaternion.FromAxisAngle is integer arithmetic — so no platform libm sine reaches a collider.
            // This runs once per boot, but once per boot ON EVERY MACHINE: the compiled colliders are re-derived
            // from the document by every process, never baked once and shared, which is why a non-portable
            // transcendental here is a cross-machine divergence rather than a one-time authoring rounding.
            var rotation = FixedQuaternion.FromAxisAngle(
                axis: UnitY,
                angle: FixedQ4816.FromDouble(value: (placement.YawDegrees * (Math.PI / 180.0)))
            );

            CreationStampLattice.ForEachFixedInstance(
                origin: FixedVector3.FromVector3(value: placement.Position),
                rotation: rotation,
                pattern: WorldPlacementStamp.PatternFor(placement: placement),
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
                        if (copy.Shape.Type == SdfSolidPrimitive.Plane) {
                            var normal = copy.PlaneNormal;

                            colliders.Add(item: FixedStaticCollider.HalfSpace(
                                point: (copy.Center + (normal * margin)),
                                normal: normal
                            ));
                            planes++;
                            placementPlanes++;
                        } else if (
                            (copy.Shape.Type == SdfSolidPrimitive.Sphere) &&
                            (copy.UniformScale > FixedQ4816.Zero)
                        ) {
                            var sphereBounds = SdfSolidGeometry.GetLocalBounds(type: SdfSolidPrimitive.Sphere);

                            colliders.Add(item: FixedStaticCollider.Sphere(
                                center: copy.Center,
                                radius: ((FixedQ4816.FromDouble(value: sphereBounds.HalfExtents.X) * copy.UniformScale) + margin)
                            ));
                            spheres++;
                            placementSpheres++;
                        } else {
                            colliders.Add(item: FixedStaticCollider.AxisAlignedBox(
                                center: copy.Center,
                                halfExtents: (copy.HalfExtents + new FixedVector3(
                                    X: margin,
                                    Y: margin,
                                    Z: margin
                                ))
                            ));
                            boxes++;
                            placementBoxes++;
                        }
                    }
                )
            );
        }

        return new WorldColliderSet(
            colliders: colliders.ToArray(),
            tuning: tuning,
            attachedRows: attachedRows
        ) {
            BoxCount = boxes,
            PlacementBoxCount = placementBoxes,
            PlacementPlaneCount = placementPlanes,
            PlacementSphereCount = placementSpheres,
            PlaneCount = planes,
            SphereCount = spheres,
        };
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

        m_attachedColliders.Clear();

        if (m_attachedRows.Count == 0) {
            return;
        }

        foreach (var (placement, creation) in m_attachedRows) {
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

            var margin = FixedQ4816.FromDouble(value: placement.Solid!.Margin);
            // The SAME creation-shape transform chain the static loop above (and the renderer) already runs, fed the
            // resolved dynamic origin/yaw instead of the row's static authored Position/YawDegrees — and taken through
            // the FIXED-POINT emitter, because this runs every tick on a live body's pose. The single-precision
            // sibling would reach the platform libm's sin/cos here (Quaternion.CreateFromAxisAngle), which no
            // determinism rule permits on a value that decides where a body stops.
            var stampRotation = FixedQuaternion.FromAxisAngle(
                angle: fixedYaw,
                axis: UnitY
            );

            CreationStampEmitter.VisitFixedPrimitiveCopies(
                document: creation.EngineDocument,
                transform: new FixedCreationStampTransform(
                    Origin: fixedPosition,
                    Rotation: stampRotation,
                    Scale: FixedQ4816.FromDouble(value: placement.Scale),
                    ReflectionNormal: null
                ),
                visitor: copy => {
                    if (copy.Shape.Type == SdfSolidPrimitive.Plane) {
                        var normal = copy.PlaneNormal;

                        m_attachedColliders.Add(item: FixedStaticCollider.HalfSpace(
                            point: (copy.Center + (normal * margin)),
                            normal: normal
                        ));
                    } else if (
                        (copy.Shape.Type == SdfSolidPrimitive.Sphere) &&
                        (copy.UniformScale > FixedQ4816.Zero)
                    ) {
                        var sphereBounds = SdfSolidGeometry.GetLocalBounds(type: SdfSolidPrimitive.Sphere);

                        m_attachedColliders.Add(item: FixedStaticCollider.Sphere(
                            center: copy.Center,
                            radius: ((FixedQ4816.FromDouble(value: sphereBounds.HalfExtents.X) * copy.UniformScale) + margin)
                        ));
                    } else {
                        m_attachedColliders.Add(item: FixedStaticCollider.AxisAlignedBox(
                            center: copy.Center,
                            halfExtents: (copy.HalfExtents + new FixedVector3(
                                X: margin,
                                Y: margin,
                                Z: margin
                            ))
                        ));
                    }
                }
            );
        }
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
            volumes: volumes
        );
    /// <inheritdoc/>
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) {
        _ = position;
        up = UnitY;

        return true;
    }
}
