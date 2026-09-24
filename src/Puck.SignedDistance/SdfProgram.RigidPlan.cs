using System.Numerics;

namespace Puck.SignedDistance;

// The rigid-leaf execution plan (see the packed layout in SdfProgram.cs): segments whose chains reduce to direct poses,
// optionally around one fold run, compiled for mapCore's and mapGradCore's rigid walks.
public sealed partial class SdfProgram {
    // Lowers the overwhelmingly common authored shape of animated geometry into a GPU execution plan without changing
    // the public ISA. A compiled segment may contain any number of Reset-delimited rigid leaves and any primitive/blend/
    // material combination. Static Translate/Rotate chains collapse into one local pose; an optional TransformDynamic
    // must precede that local pose and be shared by the segment, which lets the shader load the animated bone once.
    // A static chain may also carry one contiguous run of point folds (SymmetryPlane/Repeat/RepeatLimited), applied
    // between the pose before it and the pose after it. Anything else that mutates distance scale, folds space, edits
    // the field, or crosses a scope stays on the general VM.
    private RigidPlan CompileRigidPlan(List<BoundRecord> segments) {
        var segmentPlans = new RigidSegmentPlan[segments.Count];
        var leaves = new List<RigidLeafPlan>();

        for (var segmentIndex = 0; (segmentIndex < segments.Count); segmentIndex++) {
            var firstLeaf = leaves.Count;

            if (TryCompileRigidSegment(
                segment: segments[segmentIndex],
                leaves: leaves,
                dynamicSlot: out var dynamicSlot
            )) {
                segmentPlans[segmentIndex] = new RigidSegmentPlan(
                    FirstLeaf: firstLeaf,
                    LeafCount: (leaves.Count - firstLeaf),
                    DynamicSlot: dynamicSlot
                );
            } else if (leaves.Count != firstLeaf) {
                leaves.RemoveRange(
                    index: firstLeaf,
                    count: (leaves.Count - firstLeaf)
                );
            }
        }

        return new RigidPlan(
            Leaves: leaves,
            Segments: segmentPlans
        );
    }
    // Packs one fixed directory entry per segment followed by three vectors per compiled leaf:
    //   dir  = (absolute first-leaf vector, leaf count, dynamic slot + 1 [0 = static], 0)
    //   leaf = (local position.xyz, shapeInstruction | identityRotationBit), local quaternion, tight sphere
    // Original instruction ranges remain available to both generic evaluators.
    private void PackRigidPlan(int rigidPlanOffsetVectors, in RigidPlan plan) {
        var leafTableOffsetVectors = (rigidPlanOffsetVectors + plan.Segments.Length);

        for (var segment = 0; (segment < plan.Segments.Length); segment++) {
            var segmentPlan = plan.Segments[segment];
            var entryBase = ((rigidPlanOffsetVectors + segment) * WordsPerVector);

            m_words[entryBase] = ((uint)(leafTableOffsetVectors + (3 * segmentPlan.FirstLeaf)));
            m_words[(entryBase + 1)] = ((uint)segmentPlan.LeafCount);
            m_words[(entryBase + 2)] = ((uint)(segmentPlan.DynamicSlot + 1));
        }

        for (var index = 0; (index < plan.Leaves.Count); index++) {
            var leaf = plan.Leaves[index];
            var entryBase = ((leafTableOffsetVectors + (3 * index)) * WordsPerVector);

            if (leaf.IsFoldExtension) {
                WriteVector4(
                    words: m_words,
                    baseIndex: entryBase,
                    x: leaf.PrefixPosition.X,
                    y: leaf.PrefixPosition.Y,
                    z: leaf.PrefixPosition.Z,
                    w: 0f
                );
                m_words[(entryBase + 3)] = ((uint)leaf.FoldFirst) | (leaf.PrefixRotation.IsIdentity
                    ? RigidLeafIdentityRotationFlag
                    : 0u
                );
                WriteVector4(
                    words: m_words,
                    baseIndex: (entryBase + WordsPerVector),
                    x: leaf.PrefixRotation.X,
                    y: leaf.PrefixRotation.Y,
                    z: leaf.PrefixRotation.Z,
                    w: leaf.PrefixRotation.W
                );
                m_words[(entryBase + (2 * WordsPerVector))] = ((uint)leaf.FoldCount);
                continue;
            }

            WriteVector4(
                words: m_words,
                baseIndex: entryBase,
                x: leaf.Position.X,
                y: leaf.Position.Y,
                z: leaf.Position.Z,
                w: 0f
            );
            m_words[(entryBase + 3)] = ((uint)leaf.ShapeInstruction) | (leaf.Rotation.IsIdentity
                ? RigidLeafIdentityRotationFlag
                : 0u
            ) | ((leaf.FoldCount > 0)
                ? RigidLeafFoldedFlag
                : 0u
            );
            WriteVector4(
                words: m_words,
                baseIndex: (entryBase + WordsPerVector),
                x: leaf.Rotation.X,
                y: leaf.Rotation.Y,
                z: leaf.Rotation.Z,
                w: leaf.Rotation.W
            );
            WriteVector4(
                words: m_words,
                baseIndex: (entryBase + (2 * WordsPerVector)),
                x: leaf.BoundCenter.X,
                y: leaf.BoundCenter.Y,
                z: leaf.BoundCenter.Z,
                w: leaf.BoundRadius
            );
        }
    }
    private bool TryCompileRigidSegment(in BoundRecord segment, List<RigidLeafPlan> leaves, out int dynamicSlot) {
        // AnalyzeBounds may split at an instance boundary even without a ResetPoint. Such a boundary deliberately
        // preserves point-state carry into the following segment, while the direct plan is state-free; keep it generic.
        if (
            (segment.End < m_instructions.Length) &&
            (m_instructions[segment.End].Op != SdfOp.ResetPoint)
        ) {
            dynamicSlot = -1;

            return false;
        }

        var firstLeaf = leaves.Count;
        var commonDynamicSlot = int.MinValue;
        var chainDynamicSlot = -1;
        var position = Vector3.Zero;
        var rotation = Quaternion.Identity;
        // The chain's fold run: the pose before it, and the instructions from its first fold through its last, which may
        // interleave the Translate/Rotate/identity-Scale ops a domain emits around each fold (the shader executes the
        // run as written). The pose after the last fold restarts at identity and accumulates into position/rotation.
        var foldFirst = -1;
        var foldCount = 0;
        var shapeSinceFold = false;
        var prefixPosition = Vector3.Zero;
        var prefixRotation = Quaternion.Identity;

        for (var index = segment.Instruction; (index < segment.End); index++) {
            var instruction = m_instructions[index];

            switch (instruction.Op) {
                case SdfOp.ResetPoint: {
                        chainDynamicSlot = -1;
                        position = Vector3.Zero;
                        rotation = Quaternion.Identity;
                        foldFirst = -1;
                        foldCount = 0;
                        shapeSinceFold = false;
                        break;
                    }
                case SdfOp.SymmetryPlane:
                case SdfOp.Repeat:
                case SdfOp.RepeatLimited: {
                        // A fold is a piecewise isometry: the plan applies the run between the two poses. One run per
                        // chain, on a static chain, fits the leaf extension slot; a shape inside it stays generic.
                        var runLength = ((foldFirst < 0)
                            ? 1
                            : ((index - foldFirst) + 1)
                        );

                        if (
                            (chainDynamicSlot >= 0) ||
                            shapeSinceFold ||
                            (runLength > RigidLeafMaxFoldRun)
                        ) {
                            dynamicSlot = -1;

                            return false;
                        }

                        if (foldFirst < 0) {
                            prefixPosition = position;
                            prefixRotation = rotation;
                            foldFirst = index;
                        }

                        // Ops since the previous fold now execute inside the run, so the pose after it starts here.
                        position = Vector3.Zero;
                        rotation = Quaternion.Identity;
                        foldCount = runLength;
                        break;
                    }
                case SdfOp.Translate: {
                        var offset = new Vector3(
                            x: instruction.Data0.X,
                            y: instruction.Data0.Y,
                            z: instruction.Data0.Z
                        );

                        position += Vector3.Transform(
                            rotation: rotation,
                            value: offset
                        );
                        break;
                    }
                case SdfOp.Rotate: {
                        var authored = new Quaternion(
                            w: instruction.Data0.W,
                            x: instruction.Data0.X,
                            y: instruction.Data0.Y,
                            z: instruction.Data0.Z
                        );

                        rotation = Quaternion.Concatenate(
                            value1: authored,
                            value2: rotation
                        );
                        break;
                    }
                case SdfOp.TransformDynamic: {
                        // A prefix transform around a dynamic bone would require two static poses. It is legal VM input,
                        // just not this compact two-vector leaf format, so leave that uncommon chain on the fallback.
                        if (
                            (foldCount > 0) ||
                            (chainDynamicSlot >= 0) ||
                            (position != Vector3.Zero) ||
                            !rotation.IsIdentity
                        ) {
                            dynamicSlot = -1;

                            return false;
                        }

                        chainDynamicSlot = ((int)instruction.Data0.X);
                        break;
                    }
                case SdfOp.Scale when (instruction.Data0 == Vector4.One):
                    // Placement emission can retain an identity scale between rigid poses. It changes neither the
                    // query point nor distance scale, so it does not require a generic segment.
                    break;
                case SdfOp.ShapeBlend: {
                        // Leaves retain their original shape header: both GPU rigid walks apply its Detail and
                        // Secondary mode gates before evaluating the primitive, just as the generic walks do.
                        if (int.MinValue == commonDynamicSlot) {
                            commonDynamicSlot = chainDynamicSlot;
                        } else if (commonDynamicSlot != chainDynamicSlot) {
                            dynamicSlot = -1;

                            return false;
                        }

                        var boundCenter = Vector3.Zero;
                        var boundRadius = -1f;

                        // A folded leaf's copies leave any single sphere; it evaluates whenever its segment does.
                        if (
                            (foldCount == 0) &&
                            (((uint)SdfBlendOp.Union) == instruction.Blend) &&
                            TryGetLocalBound(
                            center: out var localBoundCenter,
                            convexPolygonProfiles: m_convexPolygonProfiles,
                            instruction: instruction,
                            instructionIndex: index,
                            radius: out var localBoundRadius,
                            sweepCurves: m_sweepCurves
                        )
                        ) {
                            // Pre-transform the primitive's local sphere into the chain frame. mapCore already has the
                            // query in that frame, so its tight test needs no forward dynamic quaternion.
                            boundCenter = (position + Vector3.Transform(
                                rotation: rotation,
                                value: localBoundCenter
                            ));
                            boundRadius = ((localBoundRadius * BoundRadiusScale) + BoundRadiusPadding);
                        }

                        shapeSinceFold = (foldCount > 0);
                        leaves.Add(item: new RigidLeafPlan(
                            BoundCenter: boundCenter,
                            BoundRadius: boundRadius,
                            FoldCount: foldCount,
                            FoldFirst: foldFirst,
                            Position: position,
                            PrefixPosition: prefixPosition,
                            PrefixRotation: prefixRotation,
                            Rotation: rotation,
                            ShapeInstruction: index
                        ));

                        if (foldCount > 0) {
                            leaves.Add(item: new RigidLeafPlan(
                                BoundCenter: Vector3.Zero,
                                BoundRadius: -1f,
                                FoldCount: foldCount,
                                FoldFirst: foldFirst,
                                IsFoldExtension: true,
                                Position: Vector3.Zero,
                                PrefixPosition: prefixPosition,
                                PrefixRotation: prefixRotation,
                                Rotation: Quaternion.Identity,
                                ShapeInstruction: index
                            ));
                        }

                        break;
                    }
                default: {
                        dynamicSlot = -1;

                        return false;
                    }
            }
        }

        if (leaves.Count == firstLeaf) {
            dynamicSlot = -1;

            return false;
        }

        dynamicSlot = commonDynamicSlot;

        return true;
    }

    // A segment either maps to a contiguous rigid-leaf run or has LeafCount == 0 and stays on the generic interpreter.
    // DynamicSlot is shared by the run (-1 = static); requiring one slot lets mapCore hoist the per-frame bone transform
    // once for every coalesced primitive in that segment.
    private readonly record struct RigidSegmentPlan(int FirstLeaf, int LeafCount, int DynamicSlot);
    // A leaf slot. A folded leaf (FoldCount > 0) is followed by an extension slot carrying the pose before its folds;
    // the extension repeats the fold fields and sets IsFoldExtension.
    private readonly record struct RigidLeafPlan(int ShapeInstruction, Vector3 Position, Quaternion Rotation, Vector3 BoundCenter, float BoundRadius,
        int FoldFirst = -1, int FoldCount = 0, Vector3 PrefixPosition = default, Quaternion PrefixRotation = default, bool IsFoldExtension = false);
    private readonly record struct RigidPlan(RigidSegmentPlan[] Segments, List<RigidLeafPlan> Leaves);
}
