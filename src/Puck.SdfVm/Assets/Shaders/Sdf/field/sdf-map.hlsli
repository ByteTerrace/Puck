// mapCore, the field interpreter the mesh bake samples the program through (passes/sdf-mesh-bake.comp.hlsl).
#ifndef FIELD_SDF_MAP_HLSLI
#define FIELD_SDF_MAP_HLSLI
// Only the mesh bake sets this, sampling one instance group's field without the segments no instance owns.
static bool sdfMapOmitWorldSegments = false;
// A folded rigid leaf's point: the pose before its fold run, then the run as written with the generic interpreter's
// exact formulas (symmetry plane, repeat, limited repeat, and the translate/rotate/identity scale between them).
float3 sdfRigidFoldPoint(float3 p, uint extensionOffset, uint dataOffset) {
    uint4 prefix = sdfProgramWord(extensionOffset);
    p -= asfloat(prefix.xyz);
    if ((prefix.w & SDF_RIGID_LEAF_IDENTITY_ROTATION) == 0u) {
        p = rotatePointByInverseQuaternion(p, asfloat(sdfProgramWord(extensionOffset + 1u)));
    }
    uint first = (prefix.w & SDF_RIGID_LEAF_SHAPE_MASK);
    uint count = min(sdfProgramWord(extensionOffset + 2u).x, SDF_RIGID_LEAF_MAX_FOLD_RUN);
    if ((first > sdfProgramLayout.instructionCount) || (count > (sdfProgramLayout.instructionCount - first))) {
        return asfloat(0x7fc00000u).xxx;
    }
    [loop]
    for (uint step = 0u; (step < count); step++) {
        uint index = (first + step);
        uint op = SDF_INSTRUCTION_OP(sdfFieldHeader(index));
        float4 data0 = sdfFieldData(dataOffset, index, 0u);
        float4 data1 = sdfFieldData(dataOffset, index, 1u);
        switch (op) {
#include "../isa/sdf-field-rigid.hlsli"
            default: return asfloat(0x7fc00000u).xxx;
        }
    }
    return p;
}
#ifdef SDF_FIELD_DUAL
// The same fold run over a dual point: each arm resolves to its formula's dual overload.
SdfDual3 sdfRigidFoldPoint(SdfDual3 p, uint extensionOffset, uint dataOffset) {
    uint4 prefix = sdfProgramWord(extensionOffset);
    p = sdfdSubtract(p, asfloat(prefix.xyz));
    if ((prefix.w & SDF_RIGID_LEAF_IDENTITY_ROTATION) == 0u) {
        p = sdfdRotateInverse(p, asfloat(sdfProgramWord(extensionOffset + 1u)));
    }
    uint first = (prefix.w & SDF_RIGID_LEAF_SHAPE_MASK);
    uint count = min(sdfProgramWord(extensionOffset + 2u).x, SDF_RIGID_LEAF_MAX_FOLD_RUN);
    if ((first > sdfProgramLayout.instructionCount) || (count > (sdfProgramLayout.instructionCount - first))) {
        return sdfdLift(asfloat(0x7fc00000u).xxx);
    }
    [loop]
    for (uint step = 0u; (step < count); step++) {
        uint index = (first + step);
        uint op = SDF_INSTRUCTION_OP(sdfFieldHeader(index));
        float4 data0 = sdfFieldData(dataOffset, index, 0u);
        float4 data1 = sdfFieldData(dataOffset, index, 1u);
        switch (op) {
#include "../isa/sdf-field-rigid.hlsli"
            default: return sdfdLift(asfloat(0x7fc00000u).xxx);
        }
    }
    return p;
}
#endif
// Region admission and the primitive action both come from the shape catalogue.
precise float sdfReliefRegionDistance(uint shapeType, float3 p, float4 data0, float4 data1) {
    float result;
    switch (shapeType) {
#include "../isa/sdf-field-relief-shapes.hlsli"
        default: return asfloat(0x7fc00000u);
    }
    return result;
}
// The product of a masked relief's weights at its point q, from the rowCount rows from firstRow (the fixed-point
// reader's walk is SdfFieldEvaluator.MaskProduct; a band's weight is the shared sdfReliefBandWeight): each band row opens a mask, each pose row poses the next primitive row's region, and
// a mask's distance is the least of its regions' at turn_back(q - origin). A mask whose distance is at or below its
// start skips its remaining regions, whose weight is its inside whatever they read, and an exactly zero product returns
// at once. The caller bounds rowCount by SDF_MAX_RELIEF_ROWS and by the rows before the relief in its segment.
float sdfMaskProduct(uint firstRow, uint rowCount, float3 q, uint dataOffset) {
    float weight = 1.0;
    float4 band = float4(0.0, 1.0, 1.0, 1.0);
    float distance = SDF_FAR_DISTANCE;
    bool open = false;
    float3 origin = float3(0.0, 0.0, 0.0);
    float4 turn = float4(0.0, 0.0, 0.0, 1.0);
    [loop]
    for (uint step = 0u; step < SDF_MAX_RELIEF_ROWS; step++) {
        if (step >= rowCount) { break; }
        uint row = (firstRow + step);
        uint4 header = sdfFieldHeader(row);
        float4 rowData0 = sdfFieldData(dataOffset, row, 0u);
        float4 rowData1 = sdfFieldData(dataOffset, row, 1u);
        uint kind = SDF_INSTRUCTION_BLEND(header);
        switch (kind) {
#include "../isa/sdf-field-relief-rows.hlsli"
            default: return asfloat(0x7fc00000u);
        }
    }
    if (open) {
        weight *= sdfReliefBandWeight(distance, band.x, (band.x + band.y), band.z, band.w);
    }
    return weight;
}
float sdfFieldLane(int slot, uint lane) {
#ifdef SDF_DYNAMIC_TRANSFORMS
    if (slot >= 0) { return sdfComponent(sdfDynamicTransformRow(3u * (uint)slot + 2u), lane); }
#endif
    return 0.0;
}

SdfHit mapCore(float3 worldPosition, uint instanceMaskBase, bool trackMaterial) {
    // Layout offsets/counts — decoded ONCE per invocation by sdfLoadProgramLayout (see the struct above), not
    // re-derived here. The host-baked bounding-sphere tables sit right after the instruction data (2 uint4 per data pair):
    // the per-shape table (2 uint4 per instruction), then the segment directory (a count vector, then 2 uint4 per
    // segment), then the instance directory, then the world-segment list. KEEP IN SYNC with SdfProgram.PackBounds /
    // PackInstances / PackWorldSegments.
    uint dataOffset = sdfProgramLayout.dataOffset;
    uint boundsOffset = sdfProgramLayout.boundsOffset;
    uint segmentOffset = sdfProgramLayout.segmentOffset;
    uint segmentCount = sdfProgramLayout.segmentCount;
    uint rigidPlanOffset = sdfProgramLayout.rigidPlanOffset;
    uint instanceOffset = sdfProgramLayout.instanceOffset;
    uint instanceCount = sdfProgramLayout.instanceCount;
    uint worldSegmentOffset = sdfProgramLayout.worldSegmentOffset;

    if (!sdfProgramLayout.valid || instanceCount > SDF_MAX_INSTANCES) {
        return sdfIsaErrorHit();
    }

    // The merge cursors: the next always-evaluated WORLD segment, and the visible-instance enumeration (current mask
    // word + remaining bits, the in-progress instance's [instanceSegment, instanceSegmentEnd) range).
    // SDF_SEGMENT_NONE = exhausted on either side. The mask cursor starts "before word 0" (0xFFFFFFFFu, 0u), so
    // sdfNextVisibleInstanceRange's own word-fetch loop wraps onto word 0 on its first call. A program with no instance
    // lists every segment as a world segment (SdfProgram.PackWorldSegments), so the merge is the one walk. Keep it one:
    // NVIDIA's Direct3D 12 driver on Turing takes a linear all-segment walk beside it, chosen by a program-uniform flag,
    // for instanced programs too, evaluating every instance whatever the lattice's mask.
    uint worldCursor = 0u;
    uint worldCount = 0u;
    uint worldNext = SDF_SEGMENT_NONE;
    uint maskWordIndex = 0xFFFFFFFFu;
    uint maskWordBits = 0u;
    uint instanceSegment = SDF_SEGMENT_NONE;
    uint instanceSegmentEnd = SDF_SEGMENT_NONE;
    uint pendingInstance = SDF_SEGMENT_NONE;
    // The instance whose whole-instance skip was last decided (sdfInstanceCannotWin), and whether the root is a hard
    // union of independent operands, which admits that skip for an instance the part table does not compile.
    uint testedInstance = SDF_SEGMENT_NONE;
    uint testedWord = SDF_SEGMENT_NONE;
    bool rootUnion = sdfCanTracePartsIndependently();

    worldCount = (sdfMapOmitWorldSegments ? 0u : SDF_WORLD_SEGMENT_COUNT(sdfProgramWord(worldSegmentOffset)));
    worldNext = ((0u < worldCount) ? sdfProgramWord(worldSegmentOffset + SDF_DIRECTORY_HEADER_VECTORS).x : SDF_SEGMENT_NONE);
    sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex, maskWordBits, instanceSegment, instanceSegmentEnd, pendingInstance);

    float3 localPosition = worldPosition;
    // distanceScale is the PER-SAMPLE, PER-CANDIDATE domain correction: SDF_OP_SCALE folds its min-axis factor in here
    // and SDF_OP_LOG_SPHERE its r/density shell factor, composing multiplicatively. It multiplies EACH shape candidate
    // before blending. A separate candidate clamp applies in hard fields with no magnitude readers.
    float distanceScale = 1.0;
    int currentSlot = SDF_TRANSFORM_SLOT_NONE;
    uint frameKey = 0u;
    uint framePending = SDF_SEGMENT_NONE;
    float3 framePosition = worldPosition;
    float frameScale = 1.0;
#ifdef SDF_FIELD_DUAL
    // The local point's Jacobian over the world point, and the frame cache's copy of it.
    float3x3 localJacobian = SDF_DUAL_IDENTITY;
    float3x3 frameJacobian = SDF_DUAL_IDENTITY;
#endif
    int frameSlot = SDF_TRANSFORM_SLOT_NONE;
    SdfDynamicRoot dynamicRoot;
    dynamicRoot.slot = SDF_TRANSFORM_SLOT_NONE;
    dynamicRoot.localPosition = worldPosition;
#ifdef SDF_DYNAMIC_TRANSFORMS
    uint rootDynamicIndex = 0u;
#endif
    // SDF_OP_LANE_ERODE's pending effect on the NEXT SDF_OP_SHAPE_BLEND: a cheap early-out skip (no field cost) or,
    // otherwise, a world-unit additive erosion. Consumed and cleared there; reset with the chain.
    bool laneErodeSkipShape = false;
    float laneErodeAmount = 0.0;
    // The texturing half of an active wallpaper fold: the cell key times the fold's material stride, added to the
    // material id of later shape wins in the chain (never to the screen sentinel). Reset with the chain.
    int parityMaterialDelta = 0;
    SdfHit result;

    result.distance = SDF_FAR_DISTANCE;
    result.material = 0;
    result.instanceIndex = -1;
    result.frameSlot = SDF_TRANSFORM_SLOT_NONE;
#ifdef SDF_FIELD_DUAL
    result.gradient = float3(0.0, 0.0, 0.0);
#endif

    // Each validated scope saves its immediate parent's complete field.
#if SDF_MAX_FIELD_SCOPE_DEPTH != 4
#error Update the switched parent slots for the new scope depth.
#endif
    SdfHit parent0 = result, parent1 = result, parent2 = result, parent3 = result;
    uint fieldDepth = 0u;
    // Walk ResetPoint segments in directory order, merging world and visible-instance ranges in ascending order.
    // A Union chain can be skipped when its sphere's lower bound cannot beat the running minimum; the next ResetPoint
    // makes its discarded transform state dead. This preserves the field and material winner even if backends choose
    // different skips. A dynamic sphere's center is offset + entity position; rotation is folded into its baked radius.
    uint previousSegment = SDF_SEGMENT_NONE;
    [loop]
    for (uint merge = 0u; merge < sdfProgramLayout.vectorCount; merge++) {
        // Select the next segment by the two-pointer merge: world segments never fall inside an instance's range, so
        // comparing the next world segment against the next owned segment yields the globally ascending order the
        // blend ops require.
        uint segment;
        int segmentInstance = -1;

        if (worldNext < instanceSegment) {
            segment = worldNext;
            worldCursor++;
            worldNext = ((worldCursor < worldCount) ? sdfProgramWord(worldSegmentOffset + SDF_DIRECTORY_HEADER_VECTORS + worldCursor).x : SDF_SEGMENT_NONE);
        } else if (instanceSegment < instanceSegmentEnd) {
            if (instanceSegment == instanceSegmentEnd) {
                sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex,
                    maskWordBits, instanceSegment, instanceSegmentEnd, pendingInstance);
                continue;
            }
            if (sdfProgramLayout.partProgramOffset != 0u
            ) {
                uint4 part = sdfProgramWord(sdfProgramLayout.partProgramOffset + 1u + pendingInstance);
                bool partReady = ((part.z & SDF_PART_LEAF_COUNT_MASK) != 0u);
#ifndef SDF_DYNAMIC_TRANSFORMS
                partReady = partReady && ((part.z & 0x80000000u) == 0u);
#endif
                // Each visible instance is tested once, at its first segment, against the running minimum: a compiled
                // part always joins as a hard union, and under the root-union certificate so does every generic one.
                if (pendingInstance != testedInstance) {
                    testedInstance = pendingInstance;
                    float gain = sdfPartDistanceGain(part);
                    if ((partReady || rootUnion) && (gain > 0.0)) {
                        float gap = sdfInstanceGap(instanceOffset, pendingInstance, worldPosition);
                        if (sdfInstanceCannotWin(gap, gain, result.distance)) {
                            sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex,
                                maskWordBits, instanceSegment, instanceSegmentEnd, pendingInstance);
                            continue;
                        }
                    }
                }
                if (partReady) {
                    sdfComposePartProgram(result, worldPosition, part, dataOffset, (int)pendingInstance, trackMaterial, dynamicRoot);
                    sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex,
                        maskWordBits, instanceSegment, instanceSegmentEnd, pendingInstance);
                    continue;
                }
            }
            segmentInstance = (int)pendingInstance;
            segment = instanceSegment++;

            if (instanceSegment == instanceSegmentEnd) {
                sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex, maskWordBits, instanceSegment, instanceSegmentEnd, pendingInstance);
            }
        } else {
            break;
        }

        if (segment >= segmentCount || (previousSegment != SDF_SEGMENT_NONE && segment <= previousSegment)) { return sdfIsaErrorHit(); }
        previousSegment = segment;
        uint4 segmentMeta = sdfProgramWord(segmentOffset + SDF_DIRECTORY_HEADER_VECTORS + (SDF_BOUND_RECORD_VECTORS * segment) + 1u);
        if (segmentMeta.z > segmentMeta.w || segmentMeta.w > sdfProgramLayout.instructionCount) { return sdfIsaErrorHit(); }
        uint segmentBoundMode = (segmentMeta.x & SDF_SEGMENT_BOUND_MASK);

        [branch]
        if (segmentBoundMode != SDF_BOUND_NONE
        ) {
            float4 segmentBound = asfloat(sdfProgramWord(segmentOffset + SDF_DIRECTORY_HEADER_VECTORS + (SDF_BOUND_RECORD_VECTORS * segment)));
            float3 boundCenter = segmentBound.xyz;
            bool boundReady = (segmentBoundMode == SDF_BOUND_STATIC);

#ifdef SDF_DYNAMIC_TRANSFORMS
            if (segmentBoundMode == SDF_BOUND_DYNAMIC) {
                boundCenter = sdfDynamicBoundCenter(boundCenter, segmentMeta.y);
                boundReady = true;
            }
#endif

            // A dynamic sphere without the dynamic-transform buffer (a probe kernel) stays unready: evaluate fully.
            // Squared-distance form of length(p - c) - radius >= runningMin (the max keeps a negative running
            // minimum from flipping the sign). The far guard preserves capped Sweep candidates; see TryGetSweepBound.
            if (boundReady && (result.distance <= SDF_FAR_DISTANCE)) {
                float3 toCenter = (worldPosition - boundCenter);
                float clearance = max((result.distance + segmentBound.w), 0.0);

                if (dot(toCenter, toCenter) >= (clearance * clearance)) {
                    continue;
                }
            }
        }

        // Host-compiled rigid leaves: arbitrary Reset-delimited Translate/Rotate/TransformDynamic/Shape chains have
        // already been reduced to direct local poses. The authored instruction stream remains intact for every
        // non-rigid segment and the CPU SdfFieldEvaluator, but the walk pays no opcode loop/switch/PHI lattice for
        // common articulated geometry. One dynamic slot is shared by the run, so a multi-primitive bone loads once.
        [branch]
        if ((segmentMeta.x & SDF_SEGMENT_RIGID_PLAN) != 0u) {
            if (!sdfProgramRange(rigidPlanOffset, segmentCount, 1u)) { return sdfIsaErrorHit(); }
            uint4 plan = sdfProgramWord(rigidPlanOffset + segment);
            if (!sdfProgramRange(plan.x, plan.y, 3u)) { return sdfIsaErrorHit(); }
            bool planReady = true;

#ifndef SDF_DYNAMIC_TRANSFORMS
            planReady = (plan.z == SDF_TRANSFORM_SLOT_STATIC_WORD);
#endif

            if (planReady) {
                float3 rigidBasePosition = worldPosition;
                int rigidSlot = SDF_TRANSFORM_SLOT_NONE;
#ifdef SDF_FIELD_DUAL
                // A rigid chain's Jacobian is a rotation: the slot's, then each leaf's own.
                float3x3 rigidBaseJacobian = SDF_DUAL_IDENTITY;
#endif

#ifdef SDF_DYNAMIC_TRANSFORMS
                if (plan.z != SDF_TRANSFORM_SLOT_STATIC_WORD) {
                    uint dynamicSlot = (uint)SDF_TRANSFORM_SLOT_UNPACK(plan.z);
                    rigidSlot = (int)dynamicSlot;
                    rigidBasePosition = sdfWorldPointInSlot(worldPosition, dynamicSlot, dynamicRoot);
#ifdef SDF_FIELD_DUAL
                    rigidBaseJacobian = sdfdRotateJacobian(SDF_DUAL_IDENTITY, sdfDynamicTransformRow(3u * dynamicSlot + 1u));
#endif
                }
#endif

                [loop]
                for (uint leaf = 0u; (leaf < plan.y); leaf++) {
                    uint leafOffset = (plan.x + (3u * leaf));
                    uint4 packedPose = sdfProgramWord(leafOffset);
                    uint packedShape = packedPose.w;
                    uint shapeIndex = (packedShape & SDF_RIGID_LEAF_SHAPE_MASK);
                    bool folded = ((packedShape & SDF_RIGID_LEAF_FOLDED) != 0u);

                    if (folded) {
                        leaf++; // the extension slot
                    }

                    // The outer dynamic sphere avoids a forward quaternion by inflating around the entity root. This
                    // tight primitive sphere is baked in the SAME chain frame rigidBasePosition occupies, so it rejects
                    // distant bones before pose/shape payload loads. Negative radius marks a non-Union/unbounded leaf.
                    float4 leafBound = asfloat(sdfProgramWord(leafOffset + 2u));

                    if ((leafBound.w >= 0.0) && (result.distance <= SDF_FAR_DISTANCE)
                    ) {
                        float3 toLeafCenter = (rigidBasePosition - leafBound.xyz);
                        float leafClearance = max((result.distance + leafBound.w), 0.0);

                        if (dot(toLeafCenter, toLeafCenter) >= (leafClearance * leafClearance)) {
                            continue;
                        }
                    }

                    uint4 shapeHeader = sdfProgramWord(SDF_PROGRAM_HEADER_VECTORS + shapeIndex);


#ifdef SDF_FIELD_DUAL
                    SdfDual3 rigidPoint = sdfdMake(rigidBasePosition, rigidBaseJacobian);

                    if (folded) {
                        rigidPoint = sdfRigidFoldPoint(rigidPoint, (leafOffset + 3u), dataOffset);
                    }

                    rigidPoint = sdfdSubtract(rigidPoint, asfloat(packedPose.xyz));

                    if ((packedShape & SDF_RIGID_LEAF_IDENTITY_ROTATION) == 0u) {
                        rigidPoint = sdfdRotateInverse(rigidPoint, asfloat(sdfProgramWord(leafOffset + 1u)));
                    }

                    float4 shapeData0 = sdfFieldData(dataOffset, shapeIndex, 0u);
                    float4 shapeData1 = sdfFieldData(dataOffset, shapeIndex, 1u);
                    SdfDual candidate = evaluateShape(SDF_INSTRUCTION_SHAPE(shapeHeader), rigidPoint, shapeData0, shapeData1);
#else
                    float3 leafBasePosition = rigidBasePosition;

                    if (folded) {
                        leafBasePosition = sdfRigidFoldPoint(rigidBasePosition, (leafOffset + 3u), dataOffset);
                    }

                    float3 rigidPosition = (leafBasePosition - asfloat(packedPose.xyz));

                    if ((packedShape & SDF_RIGID_LEAF_IDENTITY_ROTATION) == 0u) {
                        rigidPosition = rotatePointByInverseQuaternion(rigidPosition, asfloat(sdfProgramWord(leafOffset + 1u)));
                    }

                    float4 shapeData0 = sdfFieldData(dataOffset, shapeIndex, 0u);
                    float4 shapeData1 = sdfFieldData(dataOffset, shapeIndex, 1u);
                    float candidate = evaluateShape(SDF_INSTRUCTION_SHAPE(shapeHeader), rigidPosition, shapeData0, shapeData1);
#endif
                    int material = (trackMaterial ? (int)SDF_INSTRUCTION_MATERIAL(shapeHeader) : 0);

                    sdfComposeCandidate(result, candidate, SDF_INSTRUCTION_BLEND(shapeHeader), material, segmentInstance, rigidSlot, shapeData1.x, 0.0, trackMaterial);
                }

                continue;
            }
        }

        [loop]
        for (uint index = segmentMeta.z; index < min(segmentMeta.w, sdfProgramLayout.instructionCount); index++) {
            if (index == framePending) {
                framePosition = localPosition;
                frameScale = distanceScale;
#ifdef SDF_FIELD_DUAL
                frameJacobian = localJacobian;
#endif
                frameSlot = currentSlot;
                framePending = SDF_SEGMENT_NONE;
            }

            // The validated layout spans and this loop's index bound admit direct header and payload reads.
            uint4 instructionHeader = sdfFieldHeader(index);
            uint op = SDF_INSTRUCTION_OP(instructionHeader);

            // The SHARED BLEND TAIL's inputs. SHAPE and POP_FIELD both feed it a candidate (already in world units) plus
            // a blend/material/smooth, then it runs the ONE material-winner + blendShape below — so a POP reuses SHAPE's
            // tail instead of a second copy of the ten-way blend switch (the whole cost saving; accumulator plan). Every
            // other op leaves composePending false, so a scope-free program's SHAPE path computes the identical floats it
            // did before (byte-identical render) — the tail simply moved a few lines down.
            bool composePending = false;
            float composeCandidate = SDF_FAR_DISTANCE;
            uint composeBlend = SDF_BLEND_UNION;
            int composeMaterial = 0;
            int composeInstance = -1;
            int composeSlot = SDF_TRANSFORM_SLOT_NONE;
            float composeSmooth = 0.0;
            float composeOperand = 0.0;
#ifdef SDF_FIELD_DUAL
            float3 composeGradient = float3(0.0, 0.0, 0.0);
#endif

            switch (op) {
#include "../isa/sdf-field-map.hlsli"
                default: {
                    return sdfIsaErrorHit();
                }
            }

            // The SHARED BLEND TAIL (SHAPE + POP_FIELD). The material winner uses the SAME strict compares a shape does —
            // union-like wins when nearer, intersection-like when farther (the surviving surface is the farther field's),
            // subtraction-like when the CARVED surface shows — so a bowl carved from a box wears the carving shape's
            // interior material, and the incumbent keeps its material on a TIE (a scoped shape resting on the ground plane
            // is a contact locus of ties). Then blend the candidate into result.distance. composePending is false for
            // every point/field op AND for a bound-skipped SHAPE, so those paths are byte-for-byte the pre-scope walk.
            if (composePending) {
#ifdef SDF_FIELD_DUAL
                sdfComposeCandidate(result, sdfdMake(composeCandidate, composeGradient), composeBlend, composeMaterial, composeInstance, composeSlot, composeSmooth, composeOperand, trackMaterial);
#else
                sdfComposeCandidate(result, composeCandidate, composeBlend, composeMaterial, composeInstance, composeSlot, composeSmooth, composeOperand, trackMaterial);
#endif
            }
        }
    }

    return result;
}

#undef SDF_VM_LOAD_DATA0
#undef SDF_VM_LOAD_DATA1

#endif
