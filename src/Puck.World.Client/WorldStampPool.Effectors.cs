using System.Numerics;
using Puck.Physics.Motion;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <content>
/// The inverse-kinematics half of the stamp pool: resolving a creation's declared effectors against its shape slots,
/// finding each one's world target, and folding <see cref="WorldEffectorSolver"/>'s corrections into the bones' own
/// deltas so the ordinary parent chaining carries them.
/// </content>
public sealed partial class WorldStampPool {
    // Per-solve scratch, sized to the chain ceiling and reused by every effector of every registration — the solve is
    // single-threaded on the window-pump thread, and these are what keep PackTransforms allocation-free.
    private readonly Vector3[] m_solveJoints = new Vector3[CreationEffectorDocument.MaxChainBones];
    private readonly Quaternion[] m_solveParents = new Quaternion[CreationEffectorDocument.MaxChainBones];
    private readonly Quaternion[] m_solveCorrections = new Quaternion[CreationEffectorDocument.MaxChainBones];

    // Solves every declared effector and folds each bone's correction into that bone's own delta. Returns whether any
    // own delta moved, so the caller re-chains only when a solve actually changed something.
    private bool ApplyEffectors(Registration live, CreationDocument document, IReadOnlyList<ShapeDocument> shapes, Dictionary<int, FrameTransformDocument>? poses, WorldClient client, float deltaSeconds, Vector3 rootPosition, Quaternion rootRotation, float placementScale, int shapeCount) {
        if (document.Effectors is not { Count: > 0 } effectors) {
            return false;
        }
        if (!live.EffectorsResolved) {
            ResolveEffectorSlots(
                effectors: effectors,
                live: live,
                shapes: shapes,
                shapeCount: shapeCount
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
                rootRotation: rootRotation,
                target: out var targetWorld,
                tipWorld: tipWorld
            );

            resolved = ApplyPlant(
                document: document,
                effector: effector,
                index: index,
                live: live,
                resolved: resolved,
                target: ref targetWorld
            );

            var holds = (resolved && WorldGaitDrivers.GateHolds(
                facts: facts,
                gate: effector.When,
                moving: moving
            ));
            var weight = (live.EffectorWeight[index] + (((holds
                ? 1f
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
                    : weight)
            );

            live.EffectorHasTarget[index] = resolved;
            live.EffectorTarget[index] = targetWorld;

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
                var slot = live.EffectorBoneSlot[boneBase + bone];
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

                var slot = live.EffectorBoneSlot[boneBase + bone];
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
        }

        return corrected;
    }
    // Holds the effector's target where it was when the plant window opened, and reports whether a usable target
    // exists: a latched contact survives a probe that misses this frame, which is the whole point of latching.
    private static bool ApplyPlant(Registration live, CreationDocument document, CreationEffectorDocument effector, int index, bool resolved, ref Vector3 target) {
        if (effector.Plant is not { } plant) {
            live.EffectorPlanted[index] = false;

            return resolved;
        }
        if (!WorldGaitDrivers.TryDriver(
            driver: plant.Driver,
            phase: out var phase,
            phases: live.DriverPhase,
            rows: (document.Drivers ?? []),
            weight: out _,
            weights: live.DriverWeight
        )) {
            live.EffectorPlanted[index] = false;

            return resolved;
        }

        var window = plant.Window.Value;

        if (!WorldEffectorSolver.InWindow(
            from: window.X,
            phase: phase,
            to: window.Y
        )) {
            live.EffectorPlanted[index] = false;

            return resolved;
        }
        if (live.EffectorPlanted[index]) {
            target = live.EffectorPlantTarget[index];

            return true;
        }
        if (!resolved) {
            return false;
        }

        live.EffectorPlanted[index] = true;
        live.EffectorPlantTarget[index] = target;

        return true;
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
            : shape.Position.Value)
    );
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

                live.EffectorBoneSlot[boneBase + bone] = slot;
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
    // body target that is not live, a state cell that holds no point.
    private static bool TryResolveTarget(CreationEffectorDocument effector, WorldClient client, Vector3 tipWorld, Quaternion rootRotation, out Vector3 target) {
        target = tipWorld;

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
                return ((goal.Reference is { } reference) && WorldGaitDrivers.TryReadStateVector(
                    definition: client.Definition,
                    reference: reference,
                    tick: client.Tick,
                    value: out target
                ));
            }
            default: {
                return WorldEffectorSolver.TryProbeSurface(
                    field: client.StaticField,
                    origin: tipWorld,
                    reach: (goal.Reach?.Value ?? 0f),
                    rootRotation: rootRotation,
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
    /// <returns><see langword="true"/> when a live creation look answered.</returns>
    /// <remarks>Reports the values the last <see cref="PackTransforms"/> left, never a fresh advance: a read-back
    /// that stepped the drivers would answer at a phase nothing was drawn at.</remarks>
    public bool TryBodyRig(int bodyIndex, out WorldRigState? state) {
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
                Bones: live.EffectorBoneCount[index]
            ));
        }

        state = new WorldRigState(
            Creation: live.Creation.Id,
            Drivers: drivers,
            Effectors: effectors,
            Speed: live.DriverSpeed
        );

        return true;
    }
}
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
public readonly record struct WorldRigEffector(string Name, float Weight, bool Planted, Vector3? Target, int Bones);
/// <summary>A body-rooted creation look's live animation state — what <c>body.rig</c> echoes.</summary>
/// <param name="Creation">The stamped creation's row id.</param>
/// <param name="Speed">The body's eased rendered speed, metres per second — the value the <c>moving</c>/<c>still</c>
/// gate tokens test.</param>
/// <param name="Drivers">Each declared driver's live phase and weight.</param>
/// <param name="Effectors">Each declared effector's live weight, latch, and target.</param>
public sealed record WorldRigState(string Creation, float Speed, IReadOnlyList<WorldRigDriver> Drivers, IReadOnlyList<WorldRigEffector> Effectors);
