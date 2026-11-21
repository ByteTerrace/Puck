using System.Numerics;
using Puck.Maths;
using Puck.World.Authoring;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <content>
/// The inverse-kinematics half of the stamp pool: finding each of a rig's effectors' world target through the rig's
/// reads, and folding <see cref="WorldEffectorSolver"/>'s corrections into the bones' own deltas so the ordinary parent
/// chaining carries them.
/// </content>
public sealed partial class WorldCreationInstances {
    // Per-solve scratch, sized to the chain ceiling and reused by every effector of every registration — the solve is
    // single-threaded on the window-pump thread, and these are what keep PackTransforms allocation-free.
    private readonly Vector3[] m_solveJoints = new Vector3[CreationEffectorDocument.MaxChainBones];
    private readonly Quaternion[] m_solveParents = new Quaternion[CreationEffectorDocument.MaxChainBones];
    private readonly Quaternion[] m_solveCorrections = new Quaternion[CreationEffectorDocument.MaxChainBones];

    // Solves every effector of the rig, in declaration order, and folds each bone's correction into that bone's own
    // delta. Returns whether any own delta moved, so the caller re-chains only when a solve actually changed something.
    //
    // Each effector first resolves who its target is (TargetIdentity) and releases its latch through ReleaseLatch when
    // that differs from the target it last acted on, or there is none; only then is a latch consulted. Its applied
    // weight is its eased level times its authored weight times its layer share (CreationRig.EffectorShare), read fresh
    // and never eased, and a share of zero releases the latch too. A frame that resolves no target solves toward the
    // last goal it drew, held in the creation's own frame, while the weight eases out, so a lost target is never read
    // again and the limb eases back from where it was.
    private bool ApplyEffectors(Registration live, IReadOnlyList<ShapeDocument> shapes, Dictionary<int, FrameTransformDocument>? poses, float deltaSeconds, Vector3 rootPosition, Quaternion rootRotation, float placementScale, int shapeCount, WorldDefinition? definition) {
        var effectors = live.Rig.Effectors;

        if (effectors.Count == 0) {
            return false;
        }

        // A row-rooted registration carries no body, so it publishes no facts: an `always` gate still holds and a
        // surface probe still answers, which is what lets a placed machine arm reach for a surface.
        var view = live.View;
        var facts = view.Facts;
        var moving = (live.DriverSpeed > WorldGaitDrivers.MovingSpeed);
        var inverseRoot = Quaternion.Conjugate(value: rootRotation);
        var inverseScale = ((placementScale > 0f)
            ? (1f / placementScale)
            : 0f
        );
        var corrected = false;
        var solves = 0;

        for (var index = 0; (index < effectors.Count); index++) {
            var compiled = effectors[index];
            var effector = compiled.Document;
            var bones = compiled.Bones.Count;

            if (bones < CreationEffectorDocument.MinChainBones) {
                live.EffectorWeight[index] = 0f;
                live.EffectorApplied[index] = 0f;
                ReleaseLatch(index: index, live: live);
                live.EffectorHasTarget[index] = false;

                continue;
            }

            var tipSlot = compiled.Tip;

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
            var goal = new EffectorGoal(
                effector: compiled,
                index: index,
                live: live,
                poses: poses,
                reads: view,
                rootPosition: rootPosition,
                rootRotation: rootRotation,
                scale: placementScale,
                shapes: shapes,
                tipWorld: tipWorld
            );
            var identity = TargetIdentity(effector: compiled, goal: ref goal, reads: view);

            if (!identity.Present || (identity != live.EffectorIdentity[index])) {
                ReleaseLatch(index: index, live: live);
            }
            live.EffectorIdentity[index] = identity;

            var gateHolds = WorldGaitDrivers.HoldsGate(
                facts: facts,
                gate: compiled.Gate,
                moving: moving,
                reads: view
            );
            var contactWeight = 1f;
            bool resolved;
            Vector3 targetWorld;
            Vector3 normalWorld;

            if (!identity.Present) {
                // Nothing about a lost target is read again.
                (resolved, targetWorld, normalWorld) = (false, tipWorld, Vector3.UnitY);
            } else if (gateHolds) {
                resolved = ApplyPlant(
                    contactWeight: out contactWeight,
                    deltaSeconds: deltaSeconds,
                    effector: compiled,
                    goal: ref goal,
                    index: index,
                    live: live,
                    normal: out normalWorld,
                    rootRotation: rootRotation,
                    scale: placementScale,
                    target: out targetWorld
                );
            } else {
                // A closed gate eases the tip off its target; once its weight has reached zero no solve reads a target,
                // so none is asked.
                (targetWorld, normalWorld) = (tipWorld, Vector3.UnitY);
                resolved = ((live.EffectorWeight[index] > 0f) && goal.Resolve(normal: out normalWorld, target: out targetWorld));
                // A contact released in flight cannot be reused on a later landing at another world point.
                ReleaseLatch(index: index, live: live);
            }

            var share = live.Rig.EffectorShare(effector: compiled, weights: live.DriverWeight);

            // A layer that takes the effector away lets go of its contact, so it plants afresh where the body stands when
            // the layer leaves.
            if (share <= 0f) {
                ReleaseLatch(index: index, live: live);
            }

            var holds = (resolved && gateHolds);
            var level = (holds
                ? (contactWeight * WorldGaitDrivers.CurveLevel(curve: compiled.WeightCurve, definition: definition, driver: compiled.Owner, phases: live.DriverPhase))
                : 0f
            );
            var weight = WorldGaitDrivers.EaseWeight(
                blend: out _,
                blendInSeconds: effector.BlendInSeconds,
                blendOutSeconds: effector.BlendOutSeconds,
                deltaSeconds: deltaSeconds,
                level: level,
                weight: live.EffectorWeight[index]
            );

            // Snapped onto its level, at either end and between, unlike a driver's weight, which only needs the release
            // end: a correction held at 1 − ε keeps the tip that fraction short of its goal, and since the goal is a
            // WORLD point while the shortfall is measured from the body, a latched contact would creep as the body
            // travelled. Snapping makes a plant hold still.
            live.EffectorWeight[index] = ((MathF.Abs(x: (weight - level)) < WorldGaitDrivers.RestWeight) ? level : weight);

            live.EffectorHasTarget[index] = resolved;
            live.EffectorTarget[index] = targetWorld;
            live.EffectorNormal[index] = normalWorld;
            live.EffectorAlign[index] = 0f;

            var applied = (live.EffectorWeight[index] * (effector.Weight?.Value ?? 1f) * share);

            live.EffectorApplied[index] = applied;

            Vector3 goalCreation;
            Vector3 normalCreation;

            if (resolved) {
                goalCreation = (Vector3.Transform(rotation: inverseRoot, value: (targetWorld - rootPosition)) * inverseScale);
                normalCreation = Vector3.Transform(rotation: inverseRoot, value: normalWorld);
                live.EffectorGoal[index] = goalCreation;
                live.EffectorGoalNormal[index] = normalCreation;
                live.EffectorHasGoal[index] = true;
            } else if (live.EffectorHasGoal[index]) {
                // A fade toward the last goal drawn, held in the creation's frame: the limb eases back to its driven pose
                // from where it was.
                (goalCreation, normalCreation) = (live.EffectorGoal[index], live.EffectorGoalNormal[index]);
            } else {
                continue;
            }

            if (applied <= 0f) {
                continue;
            }

            // The weight blends the GOAL, not the solved pose: at weight w the tip is asked for a point w of the way to
            // the target, so a released effector eases back onto the driver-posed limb through poses the chain can
            // actually hold.
            var targetCreation = Vector3.Lerp(
                amount: Math.Clamp(
                    max: 1f,
                    min: 0f,
                    value: applied
                ),
                value1: posedTip,
                value2: goalCreation
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

            solves++;
            for (var bone = 0; (bone < bones); bone++) {
                var slot = compiled.Bones[bone];
                var parent = live.PartParent[slot];

                // A bone's joint is carried by its PARENT's motion, never by its own: a swing about that joint fixes
                // it, so the parent frame is the one that decides where it currently is.
                parents[bone] = ((parent >= 0)
                    ? live.PartDeltaRotation[parent]
                    : Quaternion.Identity
                );
                joints[bone] = ((parent >= 0)
                    ? Compose(
                        point: live.Rig.Joint(shape: slot),
                        rotation: live.PartDeltaRotation[parent],
                        translation: live.PartDeltaTranslation[parent]
                    )
                    : live.Rig.Joint(shape: slot)
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

                var slot = compiled.Bones[bone];
                var joint = live.Rig.Joint(shape: slot);
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
                normal: normalCreation,
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

        WorldCreationWork.ProcessWork.Add(amount: solves, kind: WorldCreationWork.EffectorSolves);

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
        var turn = VectorFunctions.ScaleTurn(
            fraction: amount,
            turn: WorldEffectorSolver.FromTo(
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

    // The effector's goal for one frame: resolved (TryResolveTarget) at most once, and only when a branch reads it, or
    // asked only whether it drifted from a latch (Drifted).
    private ref struct EffectorGoal(CreationRigEffector effector, Registration live, int index, WorldRigReads reads, IReadOnlyList<ShapeDocument> shapes, Dictionary<int, FrameTransformDocument>? poses, Vector3 tipWorld, Vector3 rootPosition, Quaternion rootRotation, float scale) {
        private bool m_asked;
        private bool m_resolved;
        private Vector3 m_target;
        private Vector3 m_normal;

        public bool Resolve(out Vector3 target, out Vector3 normal) {
            if (!m_asked) {
                m_asked = true;
                m_resolved = TryResolveTarget(
                    answer: ref live.EffectorProbe[index],
                    effector: effector,
                    live: live,
                    normal: out m_normal,
                    poses: poses,
                    reads: reads,
                    rootPosition: rootPosition,
                    rootRotation: rootRotation,
                    scale: scale,
                    shapes: shapes,
                    target: out m_target,
                    tipWorld: tipWorld
                );
            }

            target = m_target;
            normal = m_normal;

            return m_resolved;
        }
        // Whether the target has drifted farther than the distance from a latched one. A surface probe compares the
        // surface points under the latch and under the tip (the latch less its standoff along its normal), which a held
        // march answers without the field gradient (WorldEffectorSolver.StanceDrifted); every other target compares the
        // resolved target itself.
        public bool Drifted(Vector3 latch, Vector3 latchNormal, float distance) {
            var goal = effector.Document.Target;

            if ((effector.TargetShape >= 0) || (effector.Target is not { Kind: CreationRigReadKind.Probe } read)) {
                return (Resolve(normal: out _, target: out var target) && (Vector3.Distance(value1: latch, value2: target) > distance));
            }

            return reads.StanceDrifted(
                answer: ref live.EffectorProbe[index],
                distance: distance,
                latch: (latch - (latchNormal * MathF.Max(x: 0f, y: ((goal.Standoff?.Value ?? 0f) * scale)))),
                origin: tipWorld,
                reach: ((goal.Reach?.Value ?? 0f) * scale),
                read: read,
                rootRotation: rootRotation,
                towards: (goal.Direction?.Value ?? Vector3.Zero)
            );
        }
    }
    // Who an effector's target is this frame: the shape, for a target on the creation's own shape; the body's address
    // (slot and occupant generation), for a body target; the cell holding a point, for a state target; and the field
    // the probe reads, for a surface, which a probe that misses does not change.
    private static EffectorTargetIdentity TargetIdentity(CreationRigEffector effector, WorldRigReads reads, ref EffectorGoal goal) {
        if (effector.TargetShape >= 0) {
            return new(Kind: EffectorTargetKind.Shape, Shape: effector.TargetShape);
        }

        return effector.Target switch {
            { Kind: CreationRigReadKind.Body } body => (reads.TryBodyAddress(address: out var address, read: body) ? new(Kind: EffectorTargetKind.Body, Address: address) : default),
            { Kind: CreationRigReadKind.State } => (goal.Resolve(normal: out _, target: out _) ? new(Kind: EffectorTargetKind.State) : default),
            { Kind: CreationRigReadKind.Probe } probe => ((reads.ProbeField(read: probe) is { } field) ? new(Field: field, Kind: EffectorTargetKind.Surface) : default),
            _ => default,
        };
    }
    private enum EffectorTargetKind : byte { None, Shape, Body, State, Surface }
    // Who an effector's target is (TargetIdentity); default names none. Equal identities name the same target.
    private readonly record struct EffectorTargetIdentity(EffectorTargetKind Kind, WorldEntityAddress Address = default, int Shape = -1, object? Field = null) {
        public bool Present => (Kind != EffectorTargetKind.None);
    }
    // The one door an effector's contact is released through: its plant latch and its resting step. A lost or changed
    // target, a closed gate, a layer share of zero and a continuity cut all release here.
    private static void ReleaseLatch(Registration live, int index) {
        live.EffectorPlanted[index] = false;
        live.EffectorStepping[index] = false;
    }
    // Lands the error each resolved effector reports (body.rig): the distance from its tip's drawn origin to its target on
    // the final pose, after every effector and part follower has run; NaN where no target resolved.
    private static void MeasureEffectorErrors(Registration live) {
        var effectors = live.Rig.Effectors;

        for (var index = 0; (index < effectors.Count); index++) {
            live.EffectorError[index] = (((effectors[index].Tip >= 0) && live.EffectorHasTarget[index])
                ? Vector3.Distance(value1: PackedShapePose(live: live, shapeIndex: effectors[index].Tip).Position, value2: live.EffectorTarget[index])
                : float.NaN);
        }
    }

    // Holds the effector's target, and the contact normal there, where it was when the plant window opened, and
    // reports whether a usable target exists: a latched contact survives a probe that misses this frame, which is the
    // whole point of latching.
    private static bool ApplyPlant(Registration live, CreationRigEffector effector, int index, ref EffectorGoal goal, float deltaSeconds, Quaternion rootRotation, float scale, out Vector3 target, out Vector3 normal, out float contactWeight) {
        contactWeight = 1f;
        if (effector.Document.Plant is not { } plant) {
            ReleaseLatch(index: index, live: live);

            return goal.Resolve(normal: out normal, target: out target);
        }
        if (!WorldGaitDrivers.TryDriver(
            driver: effector.PlantDriver,
            phase: out var phase,
            phases: live.DriverPhase,
            weight: out var driverWeight,
            weights: live.DriverWeight
        )) {
            ReleaseLatch(index: index, live: live);

            return goal.Resolve(normal: out normal, target: out target);
        }
        if (plant.Replant is { } replant) {
            if (driverWeight <= WorldGaitDrivers.RestWeight) {
                return ApplyRestingPlant(
                    deltaSeconds: deltaSeconds,
                    driver: effector.PlantDriver,
                    goal: ref goal,
                    index: index,
                    live: live,
                    normal: out normal,
                    replant: replant,
                    scale: scale,
                    target: out target,
                    up: StepUp(
                        effector: effector.Document,
                        rootRotation: rootRotation
                    )
                );
            }
            if (live.EffectorStepping[index]) {
                // The gait took the limb back mid-step: the cycle latches afresh rather than snapping the tip back to
                // the point the step left.
                ReleaseLatch(index: index, live: live);
            }
        }

        var window = plant.Window.Value;

        if (
            (plant.SwingWeight is not null) &&
            (driverWeight <= WorldGaitDrivers.RestWeight)
        ) {
            live.EffectorPlanted[index] = false;
            return goal.Resolve(normal: out normal, target: out target);
        }

        if (!WorldEffectorSolver.InWindow(
            from: window.X,
            phase: phase,
            to: window.Y
        )) {
            live.EffectorPlanted[index] = false;
            contactWeight = (1f - (driverWeight * (1f - (plant.SwingWeight ?? 1f))));

            return goal.Resolve(normal: out normal, target: out target);
        }
        // A contact latched in its window holds its point whatever a probe would answer, so it asks none.
        if (live.EffectorPlanted[index]) {
            target = live.EffectorPlantTarget[index];
            normal = live.EffectorPlantNormal[index];

            return true;
        }
        if (!goal.Resolve(normal: out normal, target: out target)) {
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
    // turned from the left point's normal to the landing point's over the step. The replant's lengths are the body's own,
    // at its scale. A latched stance that is not stepping asks only whether it drifted (EffectorGoal.Drifted), and
    // resolves its target afresh only when it did.
    private static bool ApplyRestingPlant(Registration live, int driver, int index, ref EffectorGoal goal, CreationReplantDocument replant, float scale, float deltaSeconds, Vector3 up, out Vector3 target, out Vector3 normal) {
        if (!live.EffectorPlanted[index]) {
            live.EffectorStepping[index] = false;

            if (!goal.Resolve(normal: out normal, target: out target)) {
                return false;
            }

            live.EffectorPlanted[index] = true;
            live.EffectorPlantTarget[index] = target;
            live.EffectorPlantNormal[index] = normal;

            return true;
        }

        var latch = live.EffectorPlantTarget[index];

        if (live.EffectorStepping[index]) {
            if (goal.Resolve(normal: out var freshNormal, target: out var fresh)) {
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
                height: (replant.Height * scale),
                progress: progress,
                to: live.EffectorStepTo[index],
                up: up
            );
            normal = Vector3.Transform(
                rotation: VectorFunctions.ScaleTurn(
                    fraction: progress,
                    turn: WorldEffectorSolver.FromTo(
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
            !SiblingStepping(
                driver: driver,
                index: index,
                live: live
            ) &&
            goal.Drifted(distance: (replant.Distance * scale), latch: latch, latchNormal: normal) &&
            goal.Resolve(normal: out var landingNormal, target: out var landing)
        ) {
            live.EffectorStepping[index] = true;
            live.EffectorStepFrom[index] = latch;
            live.EffectorStepTo[index] = landing;
            live.EffectorStepToNormal[index] = landingNormal;
            live.EffectorStepProgress[index] = 0f;
        }

        return true;
    }
    // Whether another effector whose plant names the same driver is mid-step: a pair of feet steps one at a time.
    private static bool SiblingStepping(Registration live, int driver, int index) {
        var effectors = live.Rig.Effectors;

        for (var other = 0; (other < effectors.Count); other++) {
            if ((other != index) && live.EffectorStepping[other] && (effectors[other].PlantDriver == driver)) {
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
    // The effector's world-space goal this frame, or false when nothing answers — a probe that finds no surface, a
    // body target that is not live, a state cell that holds no point. The normal is the probed surface's, or for a
    // target on one of the creation's shapes the reverse of its approach direction as the shape carries it; a body or
    // state target, or a shape target with no direction, names no surface and reports world up. A surface probe's reach
    // and standoff are the body's own lengths, at its scale.
    private static bool TryResolveTarget(ref WorldFieldAnswer<WorldSurfaceQuery, WorldSurfaceAnswer> answer, CreationRigEffector effector, Registration live, WorldRigReads reads, IReadOnlyList<ShapeDocument> shapes, Dictionary<int, FrameTransformDocument>? poses, Vector3 tipWorld, Vector3 rootPosition, Quaternion rootRotation, float scale, out Vector3 target, out Vector3 normal) {
        target = tipWorld;
        normal = Vector3.UnitY;

        var goal = effector.Document.Target;

        if (effector.TargetShape is var shape and >= 0) {
            // The shape's pose as this frame drew it so far: after every driver and every earlier effector, which the
            // canonicalizer guarantees is its final pose. The point rides the shape from its rest pose.
            var rest = shapes[shape];
            var (basePosition, baseRotation) = BasePose(poses: poses, shape: rest);
            var point = ((goal.Offset is { } offset)
                ? (basePosition + Vector3.Transform(rotation: (baseRotation * Quaternion.Conjugate(value: rest.Rotation.Value)), value: (offset.Value - rest.Position.Value)))
                : basePosition);
            var posed = Compose(point: point, rotation: live.PartDeltaRotation[shape], translation: live.PartDeltaTranslation[shape]);
            var direction = (goal.Direction?.Value ?? Vector3.Zero);

            target = (rootPosition + Vector3.Transform(rotation: rootRotation, value: (posed * scale)));
            if (direction.LengthSquared() > 0f) {
                normal = -Vector3.Normalize(value: Vector3.Transform(rotation: (rootRotation * live.PartDeltaRotation[shape]), value: direction));
            }

            return true;
        }
        if (effector.Target is not { } read) {
            return false;
        }

        switch (read.Kind) {
            case CreationRigReadKind.Body: {
                    if (!reads.TryBody(orientation: out var orientation, position: out var position, read: read)) {
                        return false;
                    }

                    // The offset rides the TARGET body's attitude, so a handle authored on a carried crate stays on the
                    // crate's corner as it turns rather than sliding around it.
                    target = (position + Vector3.Transform(
                        rotation: orientation,
                        value: (goal.Offset?.Value ?? Vector3.Zero)
                    ));

                    return true;
                }
            case CreationRigReadKind.State: {
                    return reads.TryPoint(read: read, value: out target);
                }
            default: {
                    return reads.TryProbe(
                        answer: ref answer,
                        read: read,
                        origin: tipWorld,
                        reach: ((goal.Reach?.Value ?? 0f) * scale),
                        rootRotation: rootRotation,
                        normal: out normal,
                        standoff: ((goal.Standoff?.Value ?? 0f) * scale),
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

        state = RigState(live: live, shapeName: shapeName);
        return true;
    }
    /// <summary>Reads a live registration's last packed rig state, including placements that have no body.</summary>
    /// <param name="registration">The registration index reported by creation inspection.</param>
    /// <param name="state">The last packed rig state, or null when no registration occupies the index.</param>
    /// <param name="shapeName">An optional shape whose chained delta to read.</param>
    /// <returns>Whether a live registration answered.</returns>
    public bool TryRegistrationRig(int registration, out WorldRigState? state, string? shapeName = null) {
        state = null;
        if ((((uint)registration) >= ((uint)m_poolCount)) || (m_pool[registration] is not { } live)) {
            return false;
        }

        state = RigState(live: live, shapeName: shapeName);
        return true;
    }

    private static WorldRigState RigState(Registration live, string? shapeName) {
        var document = live.Creation.EngineDocument;
        var drivers = new List<WorldRigDriver>();
        var effectors = new List<WorldRigEffector>();
        var driverRows = live.Rig.Drivers;

        for (var index = 0; (index < driverRows.Count); index++) {
            drivers.Add(item: new WorldRigDriver(
                Name: driverRows[index].Document.Name,
                Phase: live.DriverPhase[index],
                Weight: live.DriverWeight[index],
                Source: ((driverRows[index].Lead is null) ? null : (live.DriverLed[index] ? WorldRigDriver.SourceLead : WorldRigDriver.SourceCell))
            ));
        }

        var effectorRows = live.Rig.Effectors;

        for (var index = 0; (index < effectorRows.Count); index++) {
            effectors.Add(item: new WorldRigEffector(
                Name: effectorRows[index].Document.Name,
                Weight: live.EffectorWeight[index],
                Planted: live.EffectorPlanted[index],
                Target: (live.EffectorHasTarget[index]
                ? live.EffectorTarget[index]
                : null),
                Bones: effectorRows[index].Bones.Count,
                Stepping: live.EffectorStepping[index],
                Align: ((effectorRows[index].Document.Align is null)
                ? null
                : live.EffectorAlign[index]),
                Normal: live.EffectorNormal[index],
                Applied: live.EffectorApplied[index],
                Error: (float.IsNaN(f: live.EffectorError[index]) ? null : live.EffectorError[index])
            ));
        }

        return new WorldRigState(
            Creation: live.Creation.Id,
            Drivers: drivers,
            Effectors: effectors,
            Speed: live.DriverSpeed
        ) {
            Shape = ((shapeName is null) ? null : RigShape(live: live, shapes: (document.Shapes ?? []), shapeName: shapeName)),
            Activity = live.DriverActivity,
        };
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
                TurnRadians: VectorFunctions.TurnAngle(turn: turn)
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
/// <param name="Source">For a driver with a lead, what its phase read at the last advance: <see cref="SourceLead"/> or
/// <see cref="SourceCell"/>; <see langword="null"/> for a driver with none.</param>
public readonly record struct WorldRigDriver(string Name, float Phase, float Weight, string? Source = null) {
    /// <summary>The phase read the presenting seat's guess on the driver's lead.</summary>
    public const string SourceLead = "lead";
    /// <summary>The phase read the driver's state cell.</summary>
    public const string SourceCell = "cell";
}
/// <summary>One declared effector's live value in a <see cref="WorldRigState"/>.</summary>
/// <param name="Name">The effector's authored name.</param>
/// <param name="Weight">Its eased weight in [0, 1]: the level it eases toward is its gate times its contact weight times
/// its <c>weightBy</c> curve.</param>
/// <param name="Planted">Whether its contact latch is holding a target this frame.</param>
/// <param name="Target">The world point its tip is being asked for, or <see langword="null"/> when nothing
/// resolved (a probe that found no surface, an inactive body target, a cell holding no point) or nothing was asked (a
/// closed gate whose weight has eased to zero).</param>
/// <param name="Bones">The bones its chain resolved to — fewer than
/// <see cref="CreationEffectorDocument.MinChainBones"/> means the effector is inert.</param>
/// <param name="Stepping">Whether its resting plant is mid-step toward a new point
/// (<see cref="CreationReplantDocument"/>).</param>
/// <param name="Align">The share of the tip's tilt its align took out this frame, in [0, 1], or
/// <see langword="null"/> when the effector authors no <see cref="CreationEffectorDocument.Align"/>.</param>
/// <param name="Normal">The unit world contact normal the tip was aligned against, meaningful while
/// <paramref name="Target"/> is.</param>
/// <param name="Applied">The weight the solve and the align used: the eased weight times the authored weight times the
/// layer share, in [0, 1].</param>
/// <param name="Error">The distance, world units, from the tip's drawn origin to <paramref name="Target"/> on the final
/// pose, after every effector and part follower has run, or <see langword="null"/> when no target resolved.</param>
public readonly record struct WorldRigEffector(string Name, float Weight, bool Planted, Vector3? Target, int Bones, bool Stepping, float? Align = null, Vector3 Normal = default, float Applied = 0f, float? Error = null) {
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
    /// <summary>Gets the last driver advance's counted work and retained continuation.</summary>
    public WorldGaitDrivers.Activity Activity { get; init; }
}
