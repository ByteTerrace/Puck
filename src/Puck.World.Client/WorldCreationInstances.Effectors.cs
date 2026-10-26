using System.Numerics;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <content>
/// The inverse-kinematics half of the stamp pool: resolving a creation's declared effectors against its shape slots,
/// finding each one's world target, and folding <see cref="WorldEffectorSolver"/>'s corrections into the bones' own
/// deltas so the ordinary parent chaining carries them.
/// </content>
public sealed partial class WorldCreationInstances {
    // Per-solve scratch, sized to the chain ceiling and reused by every effector of every registration — the solve is
    // single-threaded on the window-pump thread, and these are what keep PackTransforms allocation-free.
    private readonly Vector3[] m_solveJoints = new Vector3[CreationEffectorDocument.MaxChainBones];
    private readonly Quaternion[] m_solveParents = new Quaternion[CreationEffectorDocument.MaxChainBones];
    private readonly Quaternion[] m_solveCorrections = new Quaternion[CreationEffectorDocument.MaxChainBones];

    // Solves every declared effector and folds each bone's correction into that bone's own delta. Returns whether any
    // own delta moved, so the caller re-chains only when a solve actually changed something.
    private bool ApplyEffectors(Registration live, CreationDocument document, IReadOnlyList<ShapeDocument> shapes, Dictionary<int, FrameTransformDocument>? poses, IWorldStampSource client, float deltaSeconds, Vector3 rootPosition, Quaternion rootRotation, float placementScale, int shapeCount) {
        if (document.Effectors is not { Count: > 0 } effectors) {
            return false;
        }
        if (!live.EffectorsResolved) {
            ResolveEffectorSlots(
                effectors: effectors,
                live: live,
                shapeCount: shapeCount,
                shapes: shapes
            );
        }

        // A row-rooted registration carries no body, so it publishes no facts: an `always` gate still holds and a
        // surface probe still answers, which is what lets a placed machine arm reach for a surface.
        var facts = ((live.BodyIndex is { } body)
            ? client.Facts(index: body)
            : BodyFacts.None
        );
        var moving = (live.DriverSpeed > WorldGaitDrivers.MovingSpeed);
        var blend = WorldGaitDrivers.WeightBlend(deltaSeconds: deltaSeconds);
        var inverseRoot = Quaternion.Conjugate(value: rootRotation);
        var inverseScale = ((placementScale > 0f)
            ? (1f / placementScale)
            : 0f
        );
        var corrected = false;
        var count = Math.Min(
            val1: effectors.Count,
            val2: CreationDocument.MaxEffectors
        );

        for (var index = 0; (index < count); index++) {
            var effector = effectors[index];
            var bones = live.EffectorBoneCount[index];

            if (bones < CreationEffectorDocument.MinChainBones) {
                live.EffectorWeight[index] = 0f;
                live.EffectorPlanted[index] = false;
                live.EffectorHasTarget[index] = false;

                continue;
            }

            var tipSlot = live.EffectorTipSlot[index];
            var boneBase = (index * CreationEffectorDocument.MaxChainBones);

            var (tipPosition, _) = BasePose(
                poses: poses,
                shape: shapes[tipSlot]
            );
            var posedTip = Compose(
                point: tipPosition,
                rotation: live.PartDeltaRotation[tipSlot],
                translation: live.PartDeltaTranslation[tipSlot]
            );
            var tipWorld = (rootPosition + Vector3.Transform(
                rotation: rootRotation,
                value: (posedTip * placementScale)
            ));
            var resolved = TryResolveTarget(
                client: client,
                effector: effector,
                normal: out var normalWorld,
                reads: live.Reads,
                rootRotation: rootRotation,
                target: out var targetWorld,
                tipWorld: tipWorld
            );

            var gateHolds = WorldGaitDrivers.GateHolds(
                facts: facts,
                gate: effector.When,
                moving: moving,
                reads: live.Reads
            );
            var contactWeight = 1f;

            if (gateHolds) {
                resolved = ApplyPlant(
                    contactWeight: out contactWeight,
                    deltaSeconds: deltaSeconds,
                    document: document,
                    effector: effector,
                    index: index,
                    live: live,
                    normal: ref normalWorld,
                    resolved: resolved,
                    rootRotation: rootRotation,
                    target: ref targetWorld
                );
            } else {
                // A contact released in flight cannot be reused on a later landing at another world point.
                live.EffectorPlanted[index] = false;
                live.EffectorStepping[index] = false;
            }

            var holds = (resolved && gateHolds);
            var weight = (live.EffectorWeight[index] + (((holds
                ? contactWeight
                : 0f) - live.EffectorWeight[index]) * blend));

            // Snapped at BOTH ends of the exponential approach, unlike a driver's weight, which only needs the
            // release end: a correction held at 1 − ε keeps the tip that fraction short of its goal, and since the
            // goal is a WORLD point while the shortfall is measured from the body, a latched contact would creep as
            // the body travelled. Snapping makes a plant hold still.
            live.EffectorWeight[index] = (holds
                ? ((weight > (1f - WorldGaitDrivers.RestWeight))
                    ? 1f
                    : weight)
                : ((weight < WorldGaitDrivers.RestWeight)
                    ? 0f
                    : weight
            ));

            live.EffectorHasTarget[index] = resolved;
            live.EffectorTarget[index] = targetWorld;
            live.EffectorNormal[index] = normalWorld;
            live.EffectorAlign[index] = 0f;

            var applied = (live.EffectorWeight[index] * (effector.Weight?.Value ?? 1f));

            if (
                (applied <= 0f) ||
                !resolved
            ) {
                continue;
            }

            // The gate weight blends the GOAL, not the solved pose: at weight w the tip is asked for a point w of the
            // way to the target, so a released effector eases back onto the driver-posed limb through poses the chain
            // can actually hold.
            var targetCreation = Vector3.Lerp(
                amount: Math.Clamp(
                    max: 1f,
                    min: 0f,
                    value: applied
                ),
                value1: posedTip,
                value2: (Vector3.Transform(
                    rotation: inverseRoot,
                    value: (targetWorld - rootPosition)
                ) * inverseScale)
            );
            var joints = m_solveJoints.AsSpan(
                length: bones,
                start: 0
            );
            var parents = m_solveParents.AsSpan(
                length: bones,
                start: 0
            );
            var corrections = m_solveCorrections.AsSpan(
                length: bones,
                start: 0
            );

            for (var bone = 0; (bone < bones); bone++) {
                var slot = live.EffectorBoneSlot[(boneBase + bone)];
                var parent = live.PartParent[slot];

                // A bone's joint is carried by its PARENT's motion, never by its own: a swing about that joint fixes
                // it, so the parent frame is the one that decides where it currently is.
                parents[bone] = ((parent >= 0)
                    ? live.PartDeltaRotation[parent]
                    : Quaternion.Identity
                );
                joints[bone] = ((parent >= 0)
                    ? Compose(
                        point: RestJoint(shape: shapes[slot]),
                        rotation: live.PartDeltaRotation[parent],
                        translation: live.PartDeltaTranslation[parent]
                    )
                    : RestJoint(shape: shapes[slot])
                );
                corrections[bone] = Quaternion.Identity;
            }

            WorldEffectorSolver.Solve(
                corrections: corrections,
                iterations: CreationEffectorDocument.Iterations,
                parentRotations: parents,
                posedJoints: joints,
                posedTip: ref posedTip,
                target: targetCreation
            );

            var folded = false;

            for (var bone = 0; (bone < bones); bone++) {
                if (corrections[bone] == Quaternion.Identity) {
                    continue;
                }

                var slot = live.EffectorBoneSlot[(boneBase + bone)];
                var joint = RestJoint(shape: shapes[slot]);
                var rotation = live.PartOwnRotation[slot];
                var translation = live.PartOwnTranslation[slot];

                // Rotating about the REST joint, on the near side of the bone's own delta: exactly the form
                // WorldEffectorSolver returns, and the one the parent chain then carries.
                WorldGaitDrivers.Chain(
                    parentRotation: corrections[bone],
                    parentTranslation: (joint - Vector3.Transform(
                        rotation: corrections[bone],
                        value: joint
                    )),
                    rotation: ref rotation,
                    translation: ref translation
                );

                live.PartOwnRotation[slot] = rotation;
                live.PartOwnTranslation[slot] = translation;
                corrected = true;
                folded = true;
            }

            if (folded) {
                // Re-chained between effectors, not only at the end: two effectors sharing a bone (a spine's own
                // chain and a tip chain hanging off it) must each read the posed skeleton the one before it left.
                ChainPartDeltas(
                    live: live,
                    shapeCount: shapeCount
                );
            }
            if (AlignTip(
                amount: ((effector.Align ?? 0f) * Math.Min(
                    val1: applied,
                    val2: 1f
                )),
                effector: effector,
                index: index,
                live: live,
                normal: Vector3.Transform(
                    rotation: inverseRoot,
                    value: normalWorld
                ),
                poses: poses,
                shapes: shapes,
                tipSlot: tipSlot
            )) {
                ChainPartDeltas(
                    live: live,
                    shapeCount: shapeCount
                );
                corrected = true;
            }
        }

        return corrected;
    }
    // An effector's align (CreationEffectorDocument.Align): turns the solved tip about its own posed origin, the
    // shortest way and `amount` of it, so the axis that pointed against the probe at rest points along the contact
    // normal. The turn folds into the tip's own delta on the near side of its parent's, as a bone's correction does,
    // so the tip's descendants ride it. Returns whether the own delta moved.
    private static bool AlignTip(Registration live, CreationEffectorDocument effector, IReadOnlyList<ShapeDocument> shapes, Dictionary<int, FrameTransformDocument>? poses, int index, int tipSlot, float amount, Vector3 normal) {
        if (amount <= 0f) {
            return false;
        }

        var direction = (effector.Target.Direction?.Value ?? Vector3.Zero);
        var turn = Quaternion.Slerp(
            amount: amount,
            quaternion1: Quaternion.Identity,
            quaternion2: WorldEffectorSolver.FromTo(
                from: Vector3.Transform(
                    rotation: live.PartDeltaRotation[tipSlot],
                    value: ((direction.LengthSquared() > 0f)
                        ? -direction
                        : Vector3.UnitY)
                ),
                to: normal
            )
        );

        live.EffectorAlign[index] = amount;

        if (turn == Quaternion.Identity) {
            return false;
        }

        var (tipPosition, _) = BasePose(
            poses: poses,
            shape: shapes[tipSlot]
        );
        var parent = live.PartParent[tipSlot];
        var parentRotation = ((parent >= 0)
            ? live.PartDeltaRotation[parent]
            : Quaternion.Identity
        );
        var inverseParent = Quaternion.Conjugate(value: parentRotation);
        // The posed origin and the turn, carried back through the parent's delta into the frame the tip's own delta
        // lives in: parent ∘ (turn about pivot′) equals (turn about pivot) ∘ parent.
        var pivot = Vector3.Transform(
            rotation: inverseParent,
            value: (Compose(
                point: tipPosition,
                rotation: live.PartDeltaRotation[tipSlot],
                translation: live.PartDeltaTranslation[tipSlot]
            ) - ((parent >= 0)
                ? live.PartDeltaTranslation[parent]
                : Vector3.Zero))
        );
        var local = ((inverseParent * turn) * parentRotation);
        var rotation = live.PartOwnRotation[tipSlot];
        var translation = live.PartOwnTranslation[tipSlot];

        WorldGaitDrivers.Chain(
            parentRotation: local,
            parentTranslation: (pivot - Vector3.Transform(
                rotation: local,
                value: pivot
            )),
            rotation: ref rotation,
            translation: ref translation
        );

        live.PartOwnRotation[tipSlot] = rotation;
        live.PartOwnTranslation[tipSlot] = translation;

        return true;
    }
    // Holds the effector's target, and the contact normal there, where it was when the plant window opened, and
    // reports whether a usable target exists: a latched contact survives a probe that misses this frame, which is the
    // whole point of latching.
    private static bool ApplyPlant(Registration live, CreationDocument document, CreationEffectorDocument effector, int index, bool resolved, float deltaSeconds, Quaternion rootRotation, ref Vector3 target, ref Vector3 normal, out float contactWeight) {
        contactWeight = 1f;
        if (effector.Plant is not { } plant) {
            live.EffectorPlanted[index] = false;
            live.EffectorStepping[index] = false;

            return resolved;
        }
        if (!WorldGaitDrivers.TryDriver(
            driver: plant.Driver,
            phase: out var phase,
            phases: live.DriverPhase,
            rows: (document.Drivers ?? []),
            weight: out var driverWeight,
            weights: live.DriverWeight
        )) {
            live.EffectorPlanted[index] = false;
            live.EffectorStepping[index] = false;

            return resolved;
        }
        if (plant.Replant is { } replant) {
            if (driverWeight <= WorldGaitDrivers.RestWeight) {
                return ApplyRestingPlant(
                    deltaSeconds: deltaSeconds,
                    document: document,
                    driver: plant.Driver,
                    index: index,
                    live: live,
                    normal: ref normal,
                    replant: replant,
                    resolved: resolved,
                    target: ref target,
                    up: StepUp(
                        effector: effector,
                        rootRotation: rootRotation
                    )
                );
            }
            if (live.EffectorStepping[index]) {
                // The gait took the limb back mid-step: the cycle latches afresh rather than snapping the tip back to
                // the point the step left.
                live.EffectorStepping[index] = false;
                live.EffectorPlanted[index] = false;
            }
        }

        var window = plant.Window.Value;

        if (
            (plant.SwingWeight is not null) &&
            (driverWeight <= WorldGaitDrivers.RestWeight)
        ) {
            live.EffectorPlanted[index] = false;
            return resolved;
        }

        if (!WorldEffectorSolver.InWindow(
            from: window.X,
            phase: phase,
            to: window.Y
        )) {
            live.EffectorPlanted[index] = false;
            contactWeight = (1f - (driverWeight * (1f - (plant.SwingWeight ?? 1f))));

            return resolved;
        }
        if (live.EffectorPlanted[index]) {
            target = live.EffectorPlantTarget[index];
            normal = live.EffectorPlantNormal[index];

            return true;
        }
        if (!resolved) {
            return false;
        }

        live.EffectorPlanted[index] = true;
        live.EffectorPlantTarget[index] = target;
        live.EffectorPlantNormal[index] = normal;

        return true;
    }
    // A resting plant (CreationReplantDocument): the tip holds its latched world point until the point its target
    // resolves to drifts past the replant distance, then steps there along a raised arc, one effector per plant
    // driver at a time. A latch survives a probe that misses this frame; a step heading for a point the probe has
    // since lost keeps the last point it found. The contact normal travels with the point: latched with it, and
    // turned from the left point's normal to the landing point's over the step.
    private static bool ApplyRestingPlant(Registration live, CreationDocument document, string driver, int index, CreationReplantDocument replant, bool resolved, float deltaSeconds, Vector3 up, ref Vector3 target, ref Vector3 normal) {
        if (!live.EffectorPlanted[index]) {
            live.EffectorStepping[index] = false;

            if (!resolved) {
                return false;
            }

            live.EffectorPlanted[index] = true;
            live.EffectorPlantTarget[index] = target;
            live.EffectorPlantNormal[index] = normal;

            return true;
        }

        var fresh = target;
        var freshNormal = normal;
        var latch = live.EffectorPlantTarget[index];

        if (live.EffectorStepping[index]) {
            if (resolved) {
                live.EffectorStepTo[index] = fresh;
                live.EffectorStepToNormal[index] = freshNormal;
            }

            var progress = MathF.Min(
                x: 1f,
                y: (live.EffectorStepProgress[index] + ((deltaSeconds > 0f) ? (deltaSeconds / replant.Seconds) : 0f))
            );

            live.EffectorStepProgress[index] = progress;

            if (progress >= 1f) {
                live.EffectorStepping[index] = false;
                live.EffectorPlantTarget[index] = live.EffectorStepTo[index];
                live.EffectorPlantNormal[index] = live.EffectorStepToNormal[index];
                target = live.EffectorStepTo[index];
                normal = live.EffectorStepToNormal[index];

                return true;
            }

            target = StepPoint(
                from: live.EffectorStepFrom[index],
                height: replant.Height,
                progress: progress,
                to: live.EffectorStepTo[index],
                up: up
            );
            normal = Vector3.Transform(
                rotation: Quaternion.Slerp(
                    amount: progress,
                    quaternion1: Quaternion.Identity,
                    quaternion2: WorldEffectorSolver.FromTo(
                        from: live.EffectorPlantNormal[index],
                        to: live.EffectorStepToNormal[index]
                    )
                ),
                value: live.EffectorPlantNormal[index]
            );

            return true;
        }

        target = latch;
        normal = live.EffectorPlantNormal[index];

        if (
            resolved &&
            (Vector3.Distance(value1: latch, value2: fresh) > replant.Distance) &&
            !SiblingStepping(
                document: document,
                driver: driver,
                index: index,
                live: live
            )
        ) {
            live.EffectorStepping[index] = true;
            live.EffectorStepFrom[index] = latch;
            live.EffectorStepTo[index] = fresh;
            live.EffectorStepToNormal[index] = freshNormal;
            live.EffectorStepProgress[index] = 0f;
        }

        return true;
    }
    // Whether another effector whose plant names the same driver is mid-step: a pair of feet steps one at a time.
    private static bool SiblingStepping(Registration live, CreationDocument document, string driver, int index) {
        var effectors = (document.Effectors ?? []);
        var count = Math.Min(
            val1: effectors.Count,
            val2: CreationDocument.MaxEffectors
        );

        for (var other = 0; (other < count); other++) {
            if (
                (other != index) &&
                live.EffectorStepping[other] &&
                string.Equals(
                a: effectors[other].Plant?.Driver,
                b: driver,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Returns a resting step's world point: an eased travel from <paramref name="from"/> to
    /// <paramref name="to"/>, lifted along <paramref name="up"/> by a half-sine arc peaking at
    /// <paramref name="height"/> mid-step.</summary>
    /// <param name="from">The latched point the step leaves, world units.</param>
    /// <param name="to">The point the step lands on, world units.</param>
    /// <param name="up">The unit direction the arc rises along.</param>
    /// <param name="height">The arc's peak lift, world units.</param>
    /// <param name="progress">The step's progress in [0, 1].</param>
    /// <returns>The step's point.</returns>
    private static Vector3 StepPoint(Vector3 from, Vector3 to, Vector3 up, float height, float progress) {
        var t = Math.Clamp(
            max: 1f,
            min: 0f,
            value: progress
        );
        var eased = VectorFunctions.Smoothstep(t: t);

        return (Vector3.Lerp(
            amount: eased,
            value1: from,
            value2: to
        ) + (up * (height * MathF.Sin(x: (MathF.PI * t)))));
    }
    // The direction a resting step's arc rises along: against a surface target's probe direction, carried by the
    // root; world up for a target that names no direction.
    private static Vector3 StepUp(CreationEffectorDocument effector, Quaternion rootRotation) {
        var direction = (effector.Target.Direction?.Value ?? Vector3.Zero);

        return ((direction.LengthSquared() > 0f)
            ? -Vector3.Normalize(value: Vector3.Transform(
                rotation: rootRotation,
                value: direction
            ))
            : Vector3.UnitY
        );
    }
    // A rigid delta applied to a creation-space point.
    private static Vector3 Compose(Vector3 point, Quaternion rotation, Vector3 translation) => (Vector3.Transform(
        rotation: rotation,
        value: point
    ) + translation);
    // A bone's hinge in rest creation space: the pivot of its first swing, its authored joint when it swings nothing,
    // and its own origin when it declares neither.
    private static Vector3 RestJoint(ShapeDocument shape) => ((shape.Swings is { Count: > 0 } swings)
        ? swings[0].Pivot.Value
        : ((shape.Joint is { } joint)
            ? joint.Value
            : shape.Position.Value
    ));
    // Each effector's bone and tip shape slots, resolved once per registration off the immutable document. A slot the
    // document cannot name leaves the effector with too few bones, which reads as inert.
    private static void ResolveEffectorSlots(Registration live, IReadOnlyList<CreationEffectorDocument> effectors, IReadOnlyList<ShapeDocument> shapes, int shapeCount) {
        Array.Clear(array: live.EffectorBoneCount);
        Array.Fill(
            array: live.EffectorTipSlot,
            value: -1
        );

        var count = Math.Min(
            val1: effectors.Count,
            val2: CreationDocument.MaxEffectors
        );

        for (var index = 0; (index < count); index++) {
            var effector = effectors[index];
            var chain = (effector.Chain ?? []);
            var tip = ShapeSlot(
                name: effector.Tip,
                shapeCount: shapeCount,
                shapes: shapes
            );

            if (
                (tip < 0) ||
                (chain.Count < CreationEffectorDocument.MinChainBones) ||
                (chain.Count > CreationEffectorDocument.MaxChainBones)
            ) {
                continue;
            }

            var boneBase = (index * CreationEffectorDocument.MaxChainBones);
            var bones = 0;

            for (var bone = 0; (bone < chain.Count); bone++) {
                var slot = ShapeSlot(
                    name: chain[bone],
                    shapeCount: shapeCount,
                    shapes: shapes
                );

                if (slot < 0) {
                    bones = 0;

                    break;
                }

                live.EffectorBoneSlot[(boneBase + bone)] = slot;
                bones++;
            }

            live.EffectorBoneCount[index] = bones;
            live.EffectorTipSlot[index] = tip;
        }

        live.EffectorsResolved = true;
    }
    private static int ShapeSlot(IReadOnlyList<ShapeDocument> shapes, int shapeCount, string? name) {
        if (name is null) {
            return -1;
        }
        for (var index = 0; (index < shapeCount); index++) {
            if (string.Equals(
                a: shapes[index].Name?.Value,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return index;
            }
        }

        return -1;
    }
    // The effector's world-space goal this frame, or false when nothing answers — a probe that finds no surface, a
    // body target that is not live, a state cell that holds no point. The normal is the probed surface's; a body or
    // state target names no surface and reports world up.
    private static bool TryResolveTarget(CreationEffectorDocument effector, IWorldStampSource client, WorldStateLease reads, Vector3 tipWorld, Quaternion rootRotation, out Vector3 target, out Vector3 normal) {
        target = tipWorld;
        normal = Vector3.UnitY;

        var goal = effector.Target;

        switch (goal.Kind) {
            case CreationEffectorTargetDocument.KindBody: {
                    var index = (goal.Index ?? -1);

                    if (
                        (((uint)index) >= ((uint)WorldClient.EntityCapacity)) ||
                        !client.IsActive(index: index)
                    ) {
                        return false;
                    }

                    // The offset rides the TARGET body's attitude, so a handle authored on a carried crate stays on the
                    // crate's corner as it turns rather than sliding around it.
                    target = (client.Position(index: index) + Vector3.Transform(
                        rotation: client.Orientation(index: index),
                        value: (goal.Offset?.Value ?? Vector3.Zero)
                    ));

                    return true;
                }
            case CreationEffectorTargetDocument.KindState: {
                    return (
                        (goal.Reference is { } reference) &&
                        WorldGaitDrivers.TryReadStateVector(
                        reads: reads,
                        reference: reference,
                        value: out target
                    )
                    );
                }
            default: {
                    return WorldEffectorSolver.TryProbeSurface(
                        field: client.StaticField,
                        origin: tipWorld,
                        reach: (goal.Reach?.Value ?? 0f),
                        rootRotation: rootRotation,
                        normal: out normal,
                        standoff: (goal.Standoff?.Value ?? 0f),
                        target: out target,
                        towards: (goal.Direction?.Value ?? Vector3.Zero)
                    );
                }
        }
    }

    /// <summary>Reads a body-rooted creation look's live rig state — the decisions <c>body.rig</c> echoes.</summary>
    /// <param name="bodyIndex">The population entity index.</param>
    /// <param name="state">The rig's declared drivers and effectors with their current values, or
    /// <see langword="null"/> when no live creation look is stamped on the body.</param>
    /// <param name="shapeName">The authored name of a shape whose resolved parent and chained delta to report in
    /// <see cref="WorldRigState.Shape"/>, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> when a live creation look answered.</returns>
    /// <remarks>Reports the values the last <see cref="PackTransforms"/> left, never a fresh advance: a read-back
    /// that stepped the drivers would answer at a phase nothing was drawn at.</remarks>
    public bool TryBodyRig(int bodyIndex, out WorldRigState? state, string? shapeName = null) {
        state = null;

        if (!TryFindBody(
            bodyIndex: bodyIndex,
            live: out var live,
            poolIndex: out _
        )) {
            return false;
        }

        var document = live.Creation.EngineDocument;
        var drivers = new List<WorldRigDriver>();
        var effectors = new List<WorldRigEffector>();
        var driverRows = (document.Drivers ?? []);
        var driverCount = Math.Min(
            val1: driverRows.Count,
            val2: CreationDocument.MaxDrivers
        );

        for (var index = 0; (index < driverCount); index++) {
            drivers.Add(item: new WorldRigDriver(
                Name: driverRows[index].Name,
                Phase: live.DriverPhase[index],
                Weight: live.DriverWeight[index]
            ));
        }

        var effectorRows = (document.Effectors ?? []);
        var effectorCount = Math.Min(
            val1: effectorRows.Count,
            val2: CreationDocument.MaxEffectors
        );

        for (var index = 0; (index < effectorCount); index++) {
            effectors.Add(item: new WorldRigEffector(
                Name: effectorRows[index].Name,
                Weight: live.EffectorWeight[index],
                Planted: live.EffectorPlanted[index],
                Target: (live.EffectorHasTarget[index]
                ? live.EffectorTarget[index]
                : null),
                Bones: live.EffectorBoneCount[index],
                Stepping: live.EffectorStepping[index],
                Align: ((effectorRows[index].Align is null)
                ? null
                : live.EffectorAlign[index]),
                Normal: live.EffectorNormal[index]
            ));
        }

        state = new WorldRigState(
            Creation: live.Creation.Id,
            Drivers: drivers,
            Effectors: effectors,
            Speed: live.DriverSpeed
        ) {
            Shape = ((shapeName is null) ? null : RigShape(live: live, shapes: (document.Shapes ?? []), shapeName: shapeName)),
        };

        return true;
    }

    // The named shape's resolved parent and chained delta as the last pack left them; an absent shape answers with
    // no index, and a registration not yet packed with no parent resolved.
    private static WorldRigShape RigShape(Registration live, IReadOnlyList<ShapeDocument> shapes, string shapeName) {
        var count = shapes.Count;

        for (var index = 0; (index < count); index++) {
            if (!string.Equals(a: shapes[index].Name?.Value, b: shapeName, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            var parent = (live.PartParentsResolved ? live.PartParent[index] : -1);
            var turn = live.PartDeltaRotation[index];

            return new WorldRigShape(
                Name: shapeName,
                Index: index,
                Parent: ((parent >= 0) ? shapes[parent].Name?.Value : null),
                Delta: live.PartDeltaTranslation[index],
                TurnRadians: (2f * MathF.Acos(x: Math.Clamp(value: MathF.Abs(x: turn.W), min: 0f, max: 1f)))
            );
        }

        return new WorldRigShape(Name: shapeName, Index: -1, Parent: null, Delta: Vector3.Zero, TurnRadians: 0f);
    }
}
/// <summary>One shape's live rig placement in a <see cref="WorldRigState"/>: the parent its delta chains under and the
/// delta the last pack composed for it.</summary>
/// <param name="Name">The shape's authored name.</param>
/// <param name="Index">Its index in the creation's shapes, or -1 when the creation has no shape by that name.</param>
/// <param name="Parent">The authored name of the shape its delta chains under, or <see langword="null"/> for the
/// creation root.</param>
/// <param name="Delta">The translation of its chained rigid delta, in creation units of the engine frame.</param>
/// <param name="TurnRadians">The angle of its chained rigid delta's rotation, radians in [0, 2π].</param>
public readonly record struct WorldRigShape(string Name, int Index, string? Parent, Vector3 Delta, float TurnRadians);
/// <summary>One declared driver's live value in a <see cref="WorldRigState"/>.</summary>
/// <param name="Name">The driver's authored name.</param>
/// <param name="Phase">Its running phase, radians.</param>
/// <param name="Weight">Its eased gate weight in [0, 1].</param>
public readonly record struct WorldRigDriver(string Name, float Phase, float Weight);
/// <summary>One declared effector's live value in a <see cref="WorldRigState"/>.</summary>
/// <param name="Name">The effector's authored name.</param>
/// <param name="Weight">Its eased gate weight in [0, 1].</param>
/// <param name="Planted">Whether its contact latch is holding a target this frame.</param>
/// <param name="Target">The world point its tip is being asked for, or <see langword="null"/> when nothing
/// resolved (a probe that found no surface, an inactive body target, a cell holding no point).</param>
/// <param name="Bones">The bones its chain resolved to — fewer than
/// <see cref="CreationEffectorDocument.MinChainBones"/> means the effector is inert.</param>
/// <param name="Stepping">Whether its resting plant is mid-step toward a new point
/// (<see cref="CreationReplantDocument"/>).</param>
/// <param name="Align">The share of the tip's tilt its align took out this frame, in [0, 1], or
/// <see langword="null"/> when the effector authors no <see cref="CreationEffectorDocument.Align"/>.</param>
/// <param name="Normal">The unit world contact normal the tip was aligned against, meaningful while
/// <paramref name="Target"/> is.</param>
public readonly record struct WorldRigEffector(string Name, float Weight, bool Planted, Vector3? Target, int Bones, bool Stepping, float? Align = null, Vector3 Normal = default) {
    /// <summary>Gets the contact word <c>body.rig</c> prints: <c>stepping</c> mid-step, else <c>yes</c> while the latch
    /// holds and <c>no</c> while it does not.</summary>
    public string Contact => (Stepping
        ? "stepping"
        : (Planted
            ? "yes"
            : "no"
    ));
}
/// <summary>A body-rooted creation look's live animation state — what <c>body.rig</c> echoes.</summary>
/// <param name="Creation">The stamped creation's row id.</param>
/// <param name="Speed">The body's eased rendered speed, metres per second — the value the <c>moving</c>/<c>still</c>
/// gate tokens test.</param>
/// <param name="Drivers">Each declared driver's live phase and weight.</param>
/// <param name="Effectors">Each declared effector's live weight, latch, and target.</param>
public sealed record WorldRigState(string Creation, float Speed, IReadOnlyList<WorldRigDriver> Drivers, IReadOnlyList<WorldRigEffector> Effectors) {
    /// <summary>Gets the shape the read asked about, or <see langword="null"/> when it named none.</summary>
    public WorldRigShape? Shape { get; init; }
}
