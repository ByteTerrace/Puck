// mapCore, the tape interpreter every march calls, and its map wrappers.
#ifndef FIELD_SDF_MAP_HLSLI
#define FIELD_SDF_MAP_HLSLI
// Only the primary whole-part traversal sets this. Other query paths keep the complete field.
static bool sdfPrimaryOmitParts = false;
// A distance-only query's ceiling, in mapCore's units before the program's step scale: the walk starts its running
// minimum here, so it returns min(field, ceiling) and every skip sphere and part bound rejects against it. Only a
// caller that saturates at the ceiling sets it, and only when sdfCanTracePartsIndependently certifies that the root is
// a hard union of independent operands (any other root composition would read the ceiling as an operand): the exact
// ambient rungs and the soft-shadow march. Callers restore SDF_FAR_DISTANCE.
static float sdfQueryDistanceCeiling = SDF_FAR_DISTANCE;
#if defined(SDF_PRIMARY_READ) && defined(SDF_PART_RAY_BOUNDS)
bool sdfPartCannotImprove(uint instance, float3 p, float distance);
#endif
// A folded rigid leaf's point: the pose before its fold run, then the run as written with the generic interpreter's
// exact formulas (symmetry plane, repeat, limited repeat, and the translate/rotate/identity scale between them). Bit k
// of reflected records whether run instruction k mirrored the point, for sdfRigidFoldGradient. Programs carrying folds
// never select the core-ops tier.
float3 sdfRigidFoldPoint(float3 p, uint extensionOffset, uint dataOffset, out uint reflected) {
    reflected = 0u;
#ifndef SDF_STRIP_ALL_EXOTIC
    uint4 prefix = sdfProgramWord(extensionOffset);
    p -= asfloat(prefix.xyz);
    if ((prefix.w & SDF_RIGID_LEAF_IDENTITY_ROTATION) == 0u) {
        p = rotatePointByInverseQuaternion(p, asfloat(sdfProgramWord(extensionOffset + 1u)));
    }
    uint first = (prefix.w & SDF_RIGID_LEAF_SHAPE_MASK);
    uint count = min(sdfProgramWord(extensionOffset + 2u).x, SDF_RIGID_LEAF_MAX_FOLD_RUN);
    [loop]
    for (uint step = 0u; (step < count); step++) {
        uint index = (first + step);
        uint op = SDF_INSTRUCTION_OP(sdfProgramWord(SDF_PROGRAM_HEADER_VECTORS + index));
        float4 data0 = asfloat(sdfProgramWord(dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * index)));
        float4 data1 = asfloat(sdfProgramWord(dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * index) + 1u));
        if (op == SDF_OP_SYMMETRY_PLANE) {
            float spT = (dot(p, data0.xyz) + data0.w);
            reflected |= ((spT < 0.0) ? (1u << step) : 0u);
            p -= ((2.0 * min(spT, 0.0)) * data0.xyz);
        } else if (op == SDF_OP_REPEAT) {
            p -= (data0.xyz * round(p * data1.xyz));
        } else if (op == SDF_OP_REPEAT_LIMITED) {
            p -= (data0.xyz * clamp(round(p / data0.xyz), -data1.xyz, data1.xyz));
        } else if (op == SDF_OP_TRANSLATE) {
            p -= data0.xyz;
        } else if (op == SDF_OP_ROTATE) {
            p = rotatePointByInverseQuaternion(p, data0);
        }
    }
#endif
    return p;
}
// The gradient twin: a gradient in the frame after the run, carried back through its reflections and rotations in
// reverse order (repeats and translates are translations) and the prefix rotation, into the leaf's base frame.
float3 sdfRigidFoldGradient(float3 g, uint extensionOffset, uint dataOffset, uint reflected) {
#ifndef SDF_STRIP_ALL_EXOTIC
    uint4 prefix = sdfProgramWord(extensionOffset);
    uint first = (prefix.w & SDF_RIGID_LEAF_SHAPE_MASK);
    uint count = min(sdfProgramWord(extensionOffset + 2u).x, SDF_RIGID_LEAF_MAX_FOLD_RUN);
    [loop]
    for (uint step = count; (step > 0u); step--) {
        uint index = (first + step - 1u);
        uint op = SDF_INSTRUCTION_OP(sdfProgramWord(SDF_PROGRAM_HEADER_VECTORS + index));
        if ((reflected & (1u << (step - 1u))) != 0u) {
            float3 normal = asfloat(sdfProgramWord(dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * index)).xyz);
            g -= ((2.0 * dot(g, normal)) * normal);
        } else if (op == SDF_OP_ROTATE) {
            g = rotatePointByQuaternion(g, asfloat(sdfProgramWord(dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * index))));
        }
    }
    if ((prefix.w & SDF_RIGID_LEAF_IDENTITY_ROTATION) == 0u) {
        g = rotatePointByQuaternion(g, asfloat(sdfProgramWord(extensionOffset + 1u)));
    }
#endif
    return g;
}
SdfHit mapCore(float3 worldPosition, uint instanceMaskBase, bool trackMaterial) {
    sdfTapeTrackMaterial = trackMaterial;
    sdfTapeSample(worldPosition, instanceMaskBase);
    // The root of an independent-part march omits fields present when the camera tape was built.
    if (sdfPrimaryOmitParts) { sdfTapeSampleActive = false; }
    // Every call publishes a fresh fold-safe step bound (stale bounds from a previous sample would be unsound); the
    // fold cases below tighten walkStepBound and the single return publishes it in clamped units.
    sdfMapStepBound = SDF_STEP_BOUND_NONE;
    sdfMapFoldGap = SDF_STEP_BOUND_NONE;
    sdfMapFoldCenter = float3(0.0, 0.0, 0.0);
    sdfMapFoldInner = 0.0;
    sdfMapFoldOuter = SDF_STEP_BOUND_NONE;
    // The material blend channel starts CLEARED every call (a previous sample's seam must never leak — the same
    // soundness discipline sdfMapStepBound follows); the shared blend tail rebuilds it as smooth composes execute.
    if (trackMaterial) {
        sdfMaterialBlendWeight = 0.0;
        sdfMaterialBlendOther = 0;
    }

    // Layout offsets/counts — decoded ONCE per invocation by sdfLoadProgramLayout (see the struct above), not
    // re-derived here. The host-baked bounding-sphere tables sit right after the materials (2 uint4 per material):
    // the per-shape table (2 uint4 per instruction), then the segment directory (a count vector, then 2 uint4 per
    // segment), then the instance directory, then the world-segment list. KEEP IN SYNC with SdfProgram.PackBounds /
    // PackInstances / PackWorldSegments.
    uint dataOffset = sdfProgramLayout.dataOffset;
    uint boundsOffset = sdfProgramLayout.boundsOffset;
    uint segmentOffset = sdfProgramLayout.segmentOffset;
    uint segmentCount = sdfProgramLayout.segmentCount;
    uint rigidPlanOffset = sdfProgramLayout.rigidPlanOffset;
    // The per-PROGRAM Lipschitz STEP SCALE (1/L; see sdfStepScale). Applied as ONE multiply on the FINAL returned
    // distance below, it clamps sphere-tracing steps to the field's true rate of change so a non-1-Lipschitz warp
    // (twist/bend/chamfer/displace/domain-warp) cannot overstep and hole. DISTINCT from the
    // per-sample distanceScale below (the true DOMAIN corrections: Scale's min-axis factor and LogSphere's r/density
    // factor) — that one is applied per candidate mid-walk; this is the field-preserving step clamp on the final min.
    // Never merge the two.
    float stepScale = sdfProgramLayout.stepScale;
    uint instanceOffset = sdfProgramLayout.instanceOffset;
    uint instanceCount = sdfProgramLayout.instanceCount;
    uint worldSegmentOffset = sdfProgramLayout.worldSegmentOffset;
    bool hasInstances = sdfProgramLayout.hasInstances;

    if (!sdfProgramLayout.valid || instanceCount > SDF_MAX_INSTANCES) {
        return sdfIsaErrorHit();
    }

    // The merge cursors (instanced programs only): the next always-evaluated WORLD segment, and the visible-instance
    // enumeration (current mask word + remaining bits, the in-progress instance's [instanceSegment, instanceSegmentEnd)
    // range). SDF_SEGMENT_NONE = exhausted on either side. The mask cursor starts "before word 0" (0xFFFFFFFFu, 0u),
    // so sdfNextVisibleInstanceRange's own word-fetch loop wraps onto word 0 on its first call.
    uint linearCursor = 0u;
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
#ifndef SDF_VM_DISABLE_PART_PROGRAMS
    bool rootUnion = sdfCanTracePartsIndependently();
#else
    bool rootUnion = false;
#endif

    if (hasInstances) {
        worldCount = SDF_WORLD_SEGMENT_COUNT(sdfProgramWord(worldSegmentOffset));
        worldNext = ((0u < worldCount) ? sdfProgramWord(worldSegmentOffset + SDF_DIRECTORY_HEADER_VECTORS).x : SDF_SEGMENT_NONE);
        sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex, maskWordBits, instanceSegment, instanceSegmentEnd, pendingInstance);
    }

    float3 localPosition = worldPosition;
    // distanceScale is the PER-SAMPLE, PER-CANDIDATE domain correction: SDF_OP_SCALE folds its min-axis factor in here
    // and SDF_OP_LOG_SPHERE its r/density shell factor, composing multiplicatively. It multiplies EACH shape candidate
    // before blending, so it participates in the min, the material winner, and the field ops. This is a SEPARATE channel
    // from the per-program stepScale above — that one clamps only the FINAL returned distance to keep the marcher's
    // steps 1-Lipschitz-safe and never touches a candidate mid-walk. Keep the two distinct.
    float distanceScale = 1.0;
    // The riding dynamic slot's per-instance carrier (DynamicTransform.Lanes: components 0 through 3),
    // read by any op evaluating under that slot — currently SDF_OP_LANE_ERODE. Reset with the chain (RESET), set by
    // TRANSFORM_DYNAMIC, exactly like localPosition; zero under no dynamic slot.
    float4 currentLanes = float4(0.0, 0.0, 0.0, 0.0);
    int currentSlot = SDF_TRANSFORM_SLOT_NONE;
    // SDF_OP_LANE_ERODE's pending effect on the NEXT SDF_OP_SHAPE_BLEND: a cheap early-out skip (no field cost) or,
    // otherwise, a world-unit additive erosion. Consumed and cleared there; reset with the chain.
    bool laneErodeSkipShape = false;
    float laneErodeAmount = 0.0;
    // The ball-wall accumulator (see sdfMapStepBound): the min, across every log-sphere wall executed by any chain this
    // call and not published as the fold shell, of the fold's local boundary gap mapped toward world units by the
    // chain's accumulated distanceScale. Published (times the final stepScale, which conservatively covers any
    // upstream non-conformal warp's expansion) at the single return. Deliberately NOT reset by RESET: another chain's
    // fold boundary still bounds where this sample can safely step — a global min is conservative, never unsound.
    float walkStepBound = SDF_STEP_BOUND_NONE;
    // The fold shell (see sdfMapFoldGap), in world units: the similarity fold whose wall lies nearest. Never reset by
    // RESET, like walkStepBound.
    float foldGap = SDF_STEP_BOUND_NONE;
    float3 foldCenter = float3(0.0, 0.0, 0.0);
    float foldInner = 0.0;
    float foldOuter = SDF_STEP_BOUND_NONE;
    // The texturing half of an active wallpaper fold: the cell key times the fold's material stride, added to the
    // material id of later shape wins in the chain (never to the screen sentinel). Reset with the chain.
    int parityMaterialDelta = 0;
    SdfHit result;

    result.distance = sdfQueryDistanceCeiling;
    result.material = 0;
    result.lanes = float4(0.0, 0.0, 0.0, 0.0);
    result.instanceIndex = -1;
    result.frameSlot = SDF_TRANSFORM_SLOT_NONE;

    // Each validated scope saves its immediate parent's complete field and material seam.
    SdfHit fieldParents[SDF_MAX_FIELD_SCOPE_DEPTH];
    float fieldBlendWeights[SDF_MAX_FIELD_SCOPE_DEPTH];
    int fieldBlendOthers[SDF_MAX_FIELD_SCOPE_DEPTH];
    uint fieldDepth = 0u;
    // Walk ResetPoint segments in directory order, merging world and visible-instance ranges in ascending order.
    // A Union chain can be skipped when its sphere's lower bound cannot beat the running minimum; the next ResetPoint
    // makes its discarded transform state dead. This preserves the field and material winner even if backends choose
    // different skips. A dynamic sphere's center is offset + entity position; rotation is folded into its baked radius.
    uint previousSegment = SDF_SEGMENT_NONE;
    [loop]
    for (uint merge = 0u; merge < sdfProgramLayout.vectorCount; merge++) {
        // Select the next segment. Zero-instance: the plain linear counter (independent of any loaded value, so the
        // per-segment sphere loads pipeline across iterations). Instanced: the two-pointer merge — world segments
        // never fall inside an instance's range, so comparing the next world segment against the next owned segment
        // yields the globally ascending order the blend ops require.
        uint segment;
        int segmentInstance = -1;

        if (!hasInstances) {
            linearCursor = sdfTapeNextSegment(linearCursor, segmentCount);
            if (linearCursor >= segmentCount) {
                break;
            }

            segment = linearCursor++;
        } else if (worldNext < instanceSegment) {
            segment = worldNext;
            worldCursor++;
            worldNext = ((worldCursor < worldCount) ? sdfProgramWord(worldSegmentOffset + SDF_DIRECTORY_HEADER_VECTORS + worldCursor).x : SDF_SEGMENT_NONE);
        } else if (instanceSegment < instanceSegmentEnd) {
            instanceSegment = sdfTapeNextSegment(instanceSegment, instanceSegmentEnd);
            if (instanceSegment == instanceSegmentEnd) {
                sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex,
                    maskWordBits, instanceSegment, instanceSegmentEnd, pendingInstance);
                continue;
            }
#ifndef SDF_VM_DISABLE_PART_PROGRAMS
            if (sdfProgramLayout.partProgramOffset != 0u
#ifdef SDF_TAPE_BUILD
                && !sdfTapeBuilding
#endif
            ) {
                uint4 part = sdfProgramWord(sdfProgramLayout.partProgramOffset + 1u + pendingInstance);
                bool partReady = ((part.z & 0x7FFFFFFFu) != 0u);
#ifndef SDF_DYNAMIC_TRANSFORMS
                partReady = partReady && ((part.z & 0x80000000u) == 0u);
#endif
                // Each visible instance is tested once, at its first segment, against the running minimum: a compiled
                // part always joins as a hard union, and under the root-union certificate so does every generic one.
                if (pendingInstance != testedInstance) {
                    testedInstance = pendingInstance;
#if defined(SDF_SCREEN_SOURCES) && defined(SDF_GROUP_SHADOW_GATHER)
                    // A group mask's word summary rejects every summarized instance still pending in this word at once:
                    // the sphere encloses all of them and its scale is their smallest, so each would fail
                    // sdfInstanceCannotWin. The oversized instances it left out stay pending.
                    if (rootUnion && sdfGroupMaskSummarized && (sdfShadowMaskActive || sdfAmbientMaskActive) &&
                        (maskWordIndex != testedWord) && (maskWordIndex < SDF_GROUP_MASK_SUMMARY_WORDS)) {
                        testedWord = maskWordIndex;
                        float4 sphere = sdfGroupMaskSpheres[maskWordIndex];
                        float gap = (length(worldPosition - sphere.xyz) - sphere.w);
                        if ((sphere.w >= 0.0) && (result.distance <= SDF_FAR_DISTANCE) && (gap > 0.0) &&
                            ((gap * sdfGroupMaskScales[maskWordIndex]) >= result.distance)) {
                            uint kept = sdfGroupMaskKept[maskWordIndex];
                            maskWordBits &= kept;
                            if (((kept >> (pendingInstance & 31u)) & 1u) == 0u) {
                                sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex,
                                    maskWordBits, instanceSegment, instanceSegmentEnd, pendingInstance);
                                continue;
                            }
                        }
                    }
#endif
                    if ((partReady || rootUnion) &&
                        sdfInstanceCannotWin(instanceOffset, pendingInstance, asfloat(part.w), worldPosition, result.distance)) {
                        sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex,
                            maskWordBits, instanceSegment, instanceSegmentEnd, pendingInstance);
                        continue;
                    }
                }
                if (partReady) {
                    if (!sdfPrimaryOmitParts
#if defined(SDF_PRIMARY_READ) && defined(SDF_PART_RAY_BOUNDS)
                        && !sdfPartCannotImprove(pendingInstance, worldPosition, result.distance)
#endif
                    ) {
                        sdfFieldVisits += (part.z & 0x7FFFFFFFu);
                        sdfComposePartProgram(result, worldPosition, part, dataOffset, (int)pendingInstance, trackMaterial);
                    }
                    sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex,
                        maskWordBits, instanceSegment, instanceSegmentEnd, pendingInstance);
                    continue;
                }
            }
#endif
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
        if (sdfTapeNextSegment(segment, segment + 1u) != segment) { continue; }
#ifdef SDF_TAPE_BUILD
        sdfTapeBeginSegment();
#endif
        uint4 segmentMeta = sdfProgramWord(segmentOffset + SDF_DIRECTORY_HEADER_VECTORS + (SDF_BOUND_RECORD_VECTORS * segment) + 1u);
        if (segmentMeta.z > segmentMeta.w || segmentMeta.w > sdfProgramLayout.instructionCount) { return sdfIsaErrorHit(); }
        sdfFieldVisits++;
        uint segmentBoundMode = (segmentMeta.x & SDF_SEGMENT_BOUND_MASK);
        // The static far field omits every segment a moving transform places (sdfIndirectStaticField).
        if (sdfIndirectParticipationActive && sdfIndirectStaticField && (segmentBoundMode == SDF_BOUND_DYNAMIC)) { continue; }

        [branch]
        if (segmentBoundMode != SDF_BOUND_NONE
#ifdef SDF_TAPE_BUILD
            && !sdfTapeBuilding
#endif
        ) {
            float4 segmentBound = asfloat(sdfProgramWord(segmentOffset + SDF_DIRECTORY_HEADER_VECTORS + (SDF_BOUND_RECORD_VECTORS * segment)));
            float3 boundCenter = segmentBound.xyz;
            bool boundReady = (segmentBoundMode == SDF_BOUND_STATIC);

#ifdef SDF_DYNAMIC_TRANSFORMS
            if (segmentBoundMode == SDF_BOUND_DYNAMIC) {
                boundCenter += sdfDynamicTransformRow(3u * segmentMeta.y).xyz;
                boundReady = true;
            }
#endif

            // A dynamic sphere without the dynamic-transform buffer (non-world paths) stays unready: evaluate fully.
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
        // non-rigid segment and the CPU SdfFieldEvaluator, but the scalar marches no longer pay an opcode
        // loop/switch/PHI lattice for common articulated geometry. One dynamic slot is shared by the run, so a
        // multi-primitive bone loads once. KEEP-IN-SYNC PAIR: mapGradCore has the parallel rigid-leaf dual walk (same
        // segment/plan decode, same tight-sphere rejects, same pose math) — the two rigid walks must stay twins.
        sdfFieldVisits += (segmentMeta.w - segmentMeta.z);
#ifndef SDF_VM_DISABLE_RIGID_PLAN
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
                float4 rigidLanes = 0.0;
                int rigidSlot = SDF_TRANSFORM_SLOT_NONE;

#ifdef SDF_DYNAMIC_TRANSFORMS
                if (plan.z != SDF_TRANSFORM_SLOT_STATIC_WORD) {
                    uint dynamicSlot = (uint)SDF_TRANSFORM_SLOT_UNPACK(plan.z);
                    rigidLanes = sdfDynamicTransformRow(3u * dynamicSlot + 2u);
                    rigidSlot = (int)dynamicSlot;
                    float4 dynamicPosition = sdfDynamicTransformRow(3u * dynamicSlot);
                    float4 dynamicOrientation = sdfDynamicTransformRow((3u * dynamicSlot) + 1u);
                    rigidBasePosition = rotatePointByInverseQuaternion((worldPosition - dynamicPosition.xyz), dynamicOrientation);
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
#ifdef SDF_TAPE_BUILD
                        && !sdfTapeBuilding
#endif
                    ) {
                        float3 toLeafCenter = (rigidBasePosition - leafBound.xyz);
                        float leafClearance = max((result.distance + leafBound.w), 0.0);

                        if (dot(toLeafCenter, toLeafCenter) >= (leafClearance * leafClearance)) {
                            continue;
                        }
                    }

                    uint4 shapeHeader = sdfProgramWord(SDF_PROGRAM_HEADER_VECTORS + shapeIndex);

                    if (!sdfShapeEnabled(SDF_INSTRUCTION_SHAPE(shapeHeader))) {
                        continue;
                    }
                    if (!sdfTapeShapeLive(shapeIndex)) { sdfTapeSkippedShape(shapeHeader); continue; }

                    float3 leafBasePosition = rigidBasePosition;

                    if (folded) {
                        uint reflected;
                        leafBasePosition = sdfRigidFoldPoint(rigidBasePosition, (leafOffset + 3u), dataOffset, reflected);
                    }

                    float3 rigidPosition = (leafBasePosition - asfloat(packedPose.xyz));

                    if ((packedShape & SDF_RIGID_LEAF_IDENTITY_ROTATION) == 0u) {
                        rigidPosition = rotatePointByInverseQuaternion(rigidPosition, asfloat(sdfProgramWord(leafOffset + 1u)));
                    }

                    float4 shapeData0 = asfloat(sdfProgramWord(dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * shapeIndex)));
                    float4 shapeData1 = asfloat(sdfProgramWord(dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * shapeIndex) + 1u));
                    float candidate = evaluateShape((SDF_INSTRUCTION_SHAPE(shapeHeader) & SDF_SHAPE_TYPE_MASK), rigidPosition, shapeData0, shapeData1);
#ifdef SDF_TAPE_BUILD
                    sdfTapeCandidate(segment, shapeIndex, candidate, SDF_INSTRUCTION_BLEND(shapeHeader), shapeData1.x, rigidSlot, shapeData0.w);
#endif
                    int material = (trackMaterial ? (int)SDF_INSTRUCTION_MATERIAL(shapeHeader) : 0);

                    if (sdfTapeDecided(shapeIndex)) { result.distance = sdfTapeWinnerSeed(SDF_INSTRUCTION_BLEND(shapeHeader)); }
                    sdfComposeCandidate(result, candidate, SDF_INSTRUCTION_BLEND(shapeHeader), material, rigidLanes, segmentInstance, rigidSlot, shapeData1.x, trackMaterial);
                }

 #ifdef SDF_TAPE_BUILD
                sdfTapeEndSegment(segment);
 #endif
                continue;
            }
        }
#endif

        [loop]
        for (uint index = segmentMeta.z; index < min(segmentMeta.w, sdfProgramLayout.instructionCount); index++) {
            // The validated layout spans and this loop's index bound admit direct header and payload reads.
            uint4 instructionHeader = sdfWords[SDF_PROGRAM_HEADER_VECTORS + index];
            uint op = SDF_INSTRUCTION_OP(instructionHeader);
#ifdef SDF_TAPE_BUILD
            if (op == SDF_OP_DISPLACE || op == SDF_OP_CELL_DISPLACE || op == SDF_OP_NOISE_DISPLACE || op == SDF_OP_PUSH_FIELD) {
                sdfTapeForgetField(index, op == SDF_OP_PUSH_FIELD);
            }
#endif
#ifdef SDF_VM_EAGER_PAYLOADS
            float4 data0 = asfloat(sdfWords[dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * index)]);
            float4 data1 = asfloat(sdfWords[dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * index) + 1u]);
#endif

            // The SHARED BLEND TAIL's inputs. SHAPE and POP_FIELD both feed it a candidate (already in world units) plus
            // a blend/material/smooth, then it runs the ONE material-winner + blendShape below — so a POP reuses SHAPE's
            // tail instead of a second copy of the ten-way blend switch (the whole cost saving; accumulator plan). Every
            // other op leaves composePending false, so a scope-free program's SHAPE path computes the identical floats it
            // did before (byte-identical render) — the tail simply moved a few lines down.
            bool composePending = false;
            float composeCandidate = SDF_FAR_DISTANCE;
            uint composeBlend = SDF_BLEND_UNION;
            int composeMaterial = 0;
            float4 composeLanes = float4(0.0, 0.0, 0.0, 0.0);
            int composeInstance = -1;
            int composeSlot = SDF_TRANSFORM_SLOT_NONE;
            float composeSmooth = 0.0;

            switch (op) {
                case SDF_OP_RESET_POINT: {
                    localPosition = worldPosition;
                    distanceScale = 1.0;
                    currentLanes = float4(0.0, 0.0, 0.0, 0.0);
                    currentSlot = SDF_TRANSFORM_SLOT_NONE;
                    laneErodeSkipShape = false;
                    laneErodeAmount = 0.0;
                    if (trackMaterial) {
                        parityMaterialDelta = 0;
                    }
                    break;
                }
                case SDF_OP_TRANSLATE: {
                    SDF_VM_LOAD_DATA0;
                    localPosition -= data0.xyz;
                    break;
                }
                case SDF_OP_ROTATE: {
                    SDF_VM_LOAD_DATA0;
                    localPosition = rotatePointByInverseQuaternion(localPosition, data0);
                    break;
                }
                // data0.xyz = |scale|, pre-clamped away from 0; data0.w = its min axis. BOTH HOST-BAKED by
                // SdfProgramBuilder.Scale (the abs/max/min were 8 dx.op per evaluation). The min-axis factor is the
                // conservative correction for a NON-uniform scale: f(S^-1 p) * min(s) is 1-Lipschitz because
                // |S^-1| <= 1/min(s), so it can only UNDERESTIMATE true distance — never overstep.
                case SDF_OP_SCALE: {
                    SDF_VM_LOAD_DATA0;
                    localPosition /= data0.xyz;
                    distanceScale *= data0.w;
                    break;
                }
#ifndef SDF_STRIP_HEAVY
                // Log-spherical DOMAIN WARP: data0.x = w (= ln shellRatio, HOST-BAKED), data0.y = twist (radians/shell),
                // data0.z = 1/w (HOST-BAKED). Fold the RADIAL log-coordinate to the NEAREST shell (round, exactly like
                // SDF_OP_REPEAT): a translation along log(radius) becomes a uniform Cartesian SCALING, tiling space into
                // self-similar "Droste" shells. The half-cell bound the nearest-shell round gives is what makes the
                // exp(w/2) Lipschitz factor (SdfProgram.AnalyzeLipschitz) a sound step clamp for the over-relaxed march.
                case SDF_OP_LOG_SPHERE: {
                    SDF_VM_LOAD_DATA0;
                    SDF_VM_LOAD_DATA1;
                    float foldRadius = max(length(localPosition), SDF_LOGSPHERE_MIN_RADIUS);
                    float logRadius = log(foldRadius);
                    float shell = round(logRadius * data0.z);   // nearest shell index k
                    float shellScale = exp(shell * data0.x);    // exp(k*w) = the shell's Cartesian scale = r / rFolded

                    // THE SHELL'S WALLS (see sdfMapFoldGap): spheres about the local origin at the shell's two
                    // boundary radii. Past one the fold snaps to the neighbor shell, so the folded value below is only
                    // trustworthy inside them. The host marks a chain that is a similarity (data1.w, with the local
                    // origin in the chain head's frame in data1.xyz): its walls are world spheres of the radii times
                    // distanceScale, which a march crosses exactly when they are the nearest. Any other wall maps
                    // toward world units by distanceScale and joins the ball walls.
                    float innerRadius = exp((shell - 0.5) * data0.x);
                    float outerRadius = exp((shell + 0.5) * data0.x);
                    float wallGap = (min((foldRadius - innerRadius), (outerRadius - foldRadius)) * distanceScale);

                    if ((data1.w > 0.0) && (wallGap < foldGap)) {
                        float3 center = data1.xyz;
#ifdef SDF_DYNAMIC_TRANSFORMS
                        if (currentSlot != SDF_TRANSFORM_SLOT_NONE) {
                            center = (rotatePointByQuaternion(center, sdfDynamicTransformRow(((3u * (uint)currentSlot) + 1u))) +
                                sdfDynamicTransformRow((3u * (uint)currentSlot)).xyz);
                        }
#endif
                        walkStepBound = min(walkStepBound, foldGap);
                        foldGap = wallGap;
                        foldCenter = center;
                        foldInner = (innerRadius * distanceScale);
                        foldOuter = (outerRadius * distanceScale);
                    } else {
                        walkStepBound = min(walkStepBound, wallGap);
                    }

                    localPosition /= shellScale;                // fold every shell onto the prototype

                    // Optional Droste spiral: rotate each shell about Z by k*twist. UNCONDITIONAL (twist == 0 -> cos 0 = 1,
                    // sin 0 = 0, an exact identity on both backends) so no divergent branch re-rolls codegen. A rotation is
                    // an ISOMETRY: it neither touches distanceScale nor contributes to the Lipschitz factor. No atan2, so
                    // the Z axis is a plain rotation fixed line, NOT a polar-pinch singularity.
                    float spinAngle = (shell * data0.y);
                    float spinCos = cos(spinAngle);
                    float spinSin = sin(spinAngle);

                    localPosition.xy = float2(
                        ((spinCos * localPosition.x) - (spinSin * localPosition.y)),
                        ((spinSin * localPosition.x) + (spinCos * localPosition.y)));

                    // The r/density metric correction: within a shell the fold is a similarity that shrank space by
                    // 1/shellScale, so evaluateShape(localPosition) UNDERESTIMATES true distance by 1/shellScale — multiply
                    // the candidate back by shellScale. This rides the SAME per-candidate distanceScale channel SDF_OP_SCALE
                    // writes (applied per candidate below), composing multiplicatively when a Scale is also on the chain.
                    // NEVER stepScale (that clamps the FINAL distance once, and cannot per-candidate-correct a mixed chain).
                    distanceScale *= shellScale;
                    break;
                }
#endif
#ifdef SDF_DYNAMIC_TRANSFORMS
                case SDF_OP_TRANSFORM_DYNAMIC: {
                    // A rigid transform sourced from a per-frame buffer slot (data0.x): place the shape at the slot's world
                    // position + orientation by moving the sample point into the shape's local frame (translate then
                    // inverse-rotate), exactly like an immediate Translate + Rotate would. Also rides the slot's Lanes
                    // row into currentLanes, so a later SDF_OP_LANE_ERODE on this chain reads it.
                    SDF_VM_LOAD_DATA0;
                    uint dynamicSlot = (uint)data0.x;
                    float4 dynamicPosition = sdfDynamicTransformRow((3u * dynamicSlot));
                    float4 dynamicOrientation = sdfDynamicTransformRow(((3u * dynamicSlot) + 1u));
                    localPosition = rotatePointByInverseQuaternion((localPosition - dynamicPosition.xyz), dynamicOrientation);
                    currentLanes = sdfDynamicTransformRow(((3u * dynamicSlot) + 2u));
                    currentSlot = (int)dynamicSlot;
                    break;
                }
#endif
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_REPEAT: {
                    // data0 arrives pre-clamped and data1 = 1/spacing, both HOST-BAKED by SdfProgramBuilder.Repeat.
                    SDF_VM_LOAD_DATA0;
                    SDF_VM_LOAD_DATA1;
                    localPosition -= (data0.xyz * round(localPosition * data1.xyz));
                    break;
                }
#endif
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_REPEAT_LIMITED: {
                    // data0.xyz = spacing, pre-clamped away from 0 exactly as SDF_OP_REPEAT's is (HOST-BAKED by
                    // SdfProgramBuilder.RepeatLimited); data1.xyz = the per-axis cell limit. Unlike Repeat there is no
                    // free lane for 1/spacing, so the divide stays — a per-instruction-uniform divisor lowers to one
                    // reciprocal anyway.
                    SDF_VM_LOAD_DATA0;
                    SDF_VM_LOAD_DATA1;
                    localPosition -= (data0.xyz * clamp(round(localPosition / data0.xyz), -data1.xyz, data1.xyz));
                    break;
                }
                // Stochastic domain-repeat fold (KEEP IN SYNC with SdfProgramBuilder.CellJitter): tile space like
                // SDF_OP_REPEAT, then per cell displace by a hashed offset, optionally tumble (a hashed rotation), and
                // optionally recolor by a hashed material variant. data0.xyz = spacing (pre-clamped), data0.w = jitter
                // (peak-to-peak displacement), data1.xyz = 1/spacing (HOST-BAKED), data1.w = tumble in [0,1].
                // header.y = seed, header.z = SDF_NOISE_* flavor (how the POSITION offset r0 is distributed; White = 0 is
                // the byte-identical default), header.w = materialVariants (0 = geometric only). The hash is INTEGER-ONLY (sdfPcg3d
                // on the two's-complement cell index xored with the seed), so cell decisions are bit-identical across
                // both DXC targets. Displacement and tumble are BOTH isometries — distanceScale is untouched, and the
                // only Lipschitz contribution is the jitter half-amplitude (SdfProgram.AnalyzeLipschitz).
#endif
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_CELL_JITTER: {
                    SDF_VM_LOAD_DATA0;
                    SDF_VM_LOAD_DATA1;
                    float3 cell = round(localPosition * data1.xyz);   // data1.xyz = 1/spacing (host-baked)
                    // asuint(int3(cell)) is well-defined two's-complement for a negative rounded index on both backends.
                    uint3 seed = uint3(SDF_INSTRUCTION_SHAPE(instructionHeader), (SDF_INSTRUCTION_SHAPE(instructionHeader) * SDF_HASH_STREAM_A), (SDF_INSTRUCTION_SHAPE(instructionHeader) * SDF_HASH_STREAM_B));
                    uint3 key = (asuint(int3(cell)) ^ seed);
                    uint3 h0 = sdfPcg3d(key);   // still used by the material-variant apply below (every flavor)

                    // The POSITION offset r0, shaped by the Blend-lane noise flavor (KEEP IN SYNC with
                    // Puck.SignedDistance.SdfNoiseFlavor). Only r0 changes. Every flavor stays in [0, 1] per axis — CLOSED at 1,
                    // because (float)0xFFFFFFFFu rounds up to 2^32 — so (r0 - 0.5) * jitter holds within +-jitter/2 per
                    // axis (White's bound) and the AnalyzeLipschitz reach term is unchanged for all three.
                    uint noiseFlavor = SDF_INSTRUCTION_BLEND(instructionHeader);
                    float3 r0;
                    if (noiseFlavor == SDF_NOISE_BLUE) {
                        // The R3 low-discrepancy lattice: alpha_i = round(2^32 / phi3^i) for phi3 = 1.2207440846057596
                        // (the real root of x^4 = x + 1), as a fixed-point rank-1 lattice on the integer cell index. The uint
                        // mul-add wraps mod 2^32 = the fractional part, so it is BIT-IDENTICAL across DXC's SPIR-V and DXIL
                        // (a float frac would diverge). The three alphas are ROTATED across the axes so the offset components
                        // decorrelate. "Blue-ish" low-discrepancy (de-clumped), NOT true isotropic blue noise. The seed folds
                        // in ADDITIVELY (a lattice translation preserves low-discrepancy, unlike an xor), so the field varies
                        // with seed AND a non-zero seed breaks the circulant's (1,1,1) main-diagonal degeneracy.
                        uint3 uc = (asuint(int3(cell)) + seed);
                        uint bx = ((uc.x * SDF_R3_ALPHA1) + (uc.y * SDF_R3_ALPHA2) + (uc.z * SDF_R3_ALPHA3));
                        uint by = ((uc.x * SDF_R3_ALPHA2) + (uc.y * SDF_R3_ALPHA3) + (uc.z * SDF_R3_ALPHA1));
                        uint bz = ((uc.x * SDF_R3_ALPHA3) + (uc.y * SDF_R3_ALPHA1) + (uc.z * SDF_R3_ALPHA2));
                        r0 = (float3(bx, by, bz) * SDF_INV_2POW32);
                    } else if (noiseFlavor == SDF_NOISE_GAUSSIAN) {
                        // Central-limit: the mean of 3 decorrelated hashed uniforms per axis — a Bates(3) distribution, i.e.
                        // bell-SHAPED and clustered toward the cell centre, not a true Gaussian (it has compact support, which
                        // is exactly what keeps the +-jitter/2 bound). FLOAT-averaged, not uint-summed, to avoid 32-bit overflow.
                        uint3 g1 = sdfPcg3d(key ^ SDF_HASH_STREAM_A);
                        uint3 g2 = sdfPcg3d(key ^ SDF_HASH_STREAM_B);
                        r0 = (((float3)h0 + (float3)g1 + (float3)g2) * (SDF_INV_2POW32 / 3.0));
                    } else {
                        // SDF_NOISE_WHITE (0, the default): the plain independent PCG3D uniform.
                        r0 = ((float3)h0 * SDF_INV_2POW32);
                    }

                    localPosition -= (data0.xyz * cell);               // the SDF_OP_REPEAT fold
                    localPosition -= ((r0 - 0.5) * data0.w);           // per-cell position jitter (data0.w peak-to-peak)

                    // Tumble (an isometry): guarded by amplitude so a zeroed op stays an EXACT identity (no rotate, no
                    // divergent codegen). The hashed axis is uniform on the sphere; the angle is |r1.z| * tumble * pi.
                    if (data1.w > 0.0) {
                        uint3 h1 = sdfPcg3d(key ^ SDF_HASH_TUMBLE);
                        float3 r1 = ((float3)h1 * SDF_INV_2POW32);
                        float zz = ((2.0 * r1.x) - 1.0);
                        float rr = sqrt(max(0.0, (1.0 - (zz * zz))));
                        float phi = (SDF_TAU * r1.y);
                        float3 axis = float3((rr * cos(phi)), (rr * sin(phi)), zz);   // uniform on the unit sphere
                        float angle = ((r1.z * data1.w) * SDF_PI);
                        float ha = (0.5 * angle);
                        float sa = sin(ha);
                        float4 q = float4((axis * sa), cos(ha));       // (x,y,z,w) matches SDF_OP_ROTATE's layout
                        localPosition = rotatePointByInverseQuaternion(localPosition, q);
                    }

                    // Per-cell material variant (same channel WallpaperFold recolors through): a hashed palette row in
                    // 0..variants-1, added to a later shape's material by the SDF_OP_SHAPE_BLEND parityMaterialDelta apply.
                    if (trackMaterial && (SDF_INSTRUCTION_MATERIAL(instructionHeader) != 0u)) {
                        parityMaterialDelta = (int)(h0.z % SDF_INSTRUCTION_MATERIAL(instructionHeader));
                    }

                    break;
                }
                // Angular domain-repeat fold (KEEP IN SYNC with SdfProgramBuilder.RepeatPolar): fold the plane
                // perpendicular to the axis into `count` equal sectors so the prototype repeats ROTATIONALLY around it.
                // data0.x = sector angle 2*pi/count (HOST-BAKED), data0.y = count/(2*pi) = 1/angle (HOST-BAKED),
                // data0.z = count, data0.w = 1/count (HOST-BAKED). header.y = axis (SDF_AXIS_*), header.z = mirror
                // flag (reflect each sector across its bisector — the kaleidoscope fold), header.w = materialStride
                // (per-sector palette stride; 0 = geometric only). A rotation into the base sector (and, when mirrored, a
                // reflection) about the axis — BOTH isometries, so distances are preserved (factor 1, no step clamp; like
                // SDF_OP_REPEAT the prototype must stay clear of the sector walls). atan2/floor are floats, so a
                // sector-seam pixel carries the usual +-1 LSB warp noise (geometry only; the per-sector material can flip
                // at a seam exactly as SDF_OP_WALLPAPER_FOLD's can). At the axis (r == 0) atan2(0,0) = 0 keeps it a no-op.
#endif
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_REPEAT_POLAR: {
                    SDF_VM_LOAD_DATA0;
                    uint polarAxis = SDF_INSTRUCTION_SHAPE(instructionHeader);
                    // The fold plane (u,v) perpendicular to the axis; the axial coordinate is untouched.
                    float2 pv;
                    if (polarAxis == SDF_AXIS_X) { pv = localPosition.yz; }
                    else if (polarAxis == SDF_AXIS_Z) { pv = localPosition.xy; }
                    else { pv = localPosition.xz; }   // SDF_AXIS_Y (default): the XZ ground plane

                    float sectorAngle = data0.x;
                    float a = (atan2(pv.y, pv.x) + (0.5 * sectorAngle));
                    float r = length(pv);
                    float sector = floor(a * data0.y);                         // data0.y = 1/angle
                    a = ((a - (sectorAngle * sector)) - (0.5 * sectorAngle));  // a in [-angle/2, angle/2)
                    if (SDF_INSTRUCTION_BLEND(instructionHeader) != 0u) { a = abs(a); }             // mirror: reflect across the sector bisector
                    pv = (float2(cos(a), sin(a)) * r);

                    if (polarAxis == SDF_AXIS_X) { localPosition.yz = pv; }
                    else if (polarAxis == SDF_AXIS_Z) { localPosition.xy = pv; }
                    else { localPosition.xz = pv; }

                    // Per-sector material variant (the same channel SDF_OP_WALLPAPER_FOLD / SDF_OP_CELL_JITTER recolor
                    // through): wrap the raw sector index into [0, count) and stride the palette.
                    if (trackMaterial && (SDF_INSTRUCTION_MATERIAL(instructionHeader) != 0u)) {
                        float wrapped = (sector - (data0.z * floor(sector * data0.w)));   // data0.z = count, data0.w = 1/count
                        parityMaterialDelta = ((int)wrapped * (int)SDF_INSTRUCTION_MATERIAL(instructionHeader));
                    }

                    break;
                }
                // POINT op (KEEP IN SYNC with SdfProgramBuilder.DomainWarp): perturb the sample point by a bounded,
                // cross-coupled sinusoidal field before the shapes evaluate — organic bulge/wobble/terrain. data0.xyz =
                // per-axis frequency, data0.w = amplitude; each axis is driven by the NEXT axis's coordinate so the warp
                // is non-separable. Deterministic float trig (±1 LSB). The Jacobian is I plus a perturbation of spectral
                // norm <= amp*max|freq_i| (J - I is a scaled cyclic permutation, so its norm is its largest entry), so the metric
                // stretches by up to (1 + amp*max|freq_i|); SdfProgram.AnalyzeLipschitz bakes
                // that step clamp and folds the point's max travel (amp*sqrt(3)) into a downstream twist/bend's reach.
#endif
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_DOMAIN_WARP: {
                    SDF_VM_LOAD_DATA0;
                    localPosition += (data0.w * float3(
                        sin(data0.x * localPosition.y),
                        sin(data0.y * localPosition.z),
                        sin(data0.z * localPosition.x)));
                    break;
                }
                // Reflection fold across an ARBITRARY plane (KEEP IN SYNC with SdfProgramBuilder.SymmetryPlane): the
                // general-normal superset of SDF_OP_SYMMETRY_X/Y/Z. data0.xyz = unit plane normal, data0.w = offset;
                // points on the negative side (dot(p,n)+offset < 0) mirror onto the positive side. For n = (1,0,0),
                // offset 0 this is abs(localPosition.x) to the bit. A reflection is an isometry, so distanceScale is
                // untouched and the field stays 1-Lipschitz.
#endif
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_SYMMETRY_PLANE: {
                    SDF_VM_LOAD_DATA0;
                    float spT = (dot(localPosition, data0.xyz) + data0.w);
                    localPosition -= ((2.0 * min(spT, 0.0)) * data0.xyz);
                    break;
                }
                // The warps rotate a plane pair by an angle keyed on ONE coordinate (GLSL mat2(c,-s,s,c) * v, written as
                // explicit components: x' = c*x + s*y, y' = -s*x + c*y). NOT isometries — space stretches tangentially —
                // so authored rates stay moderate (the validator bounds them) and SdfProgram.AnalyzeLipschitz folds the
                // exact Jacobian operator norm over the chain's reach into the program's step clamp.
                //
                // The three members are DISTINCT ops, not a symmetric family — each names the plane it rotates:
                //   BEND_X: angle keyed on x, rotates the XY plane
                //   BEND_Y: angle keyed on y, rotates the XY plane
                //   BEND_Z: angle keyed on y, rotates the YZ plane
                // TWIST_Y keys on y and rotates XZ (the axis-orthogonal plane) — the only one that twists about its axis.
#endif
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_ROTATE_PLANE: {
                    SDF_VM_LOAD_DATA0;
                    uint u = (SDF_INSTRUCTION_SHAPE(instructionHeader) == SDF_PLANE_YZ) ? SDF_AXIS_Y : SDF_AXIS_X;
                    uint v = (SDF_INSTRUCTION_SHAPE(instructionHeader) == SDF_PLANE_XY) ? SDF_AXIS_Y : SDF_AXIS_Z;
                    uint driver = SDF_INSTRUCTION_BLEND(instructionHeader);
                    if (driver >= 3u) { return sdfIsaErrorHit(); }
                    float angle = data0.x * (localPosition[driver] - data0.y);
                    float c = cos(angle), sn = sin(angle);
                    float pu = localPosition[u], pv = localPosition[v];
                    localPosition[u] = c * pu + sn * pv;
                    localPosition[v] = -sn * pu + c * pv;
                    break;
                }
#endif
                // Radial flare warp (KEEP IN SYNC with SdfProgramBuilder.AxialProfile): data0 = (amount, bulge, top,
                // 1/span), data1.x = the host-baked 1/max(s) size correction over t in [0, 1]. t folds toward the far
                // end of the span; s(t) is floored at SDF_FLARE_MIN_SCALE so a parameter combination that drives it
                // non-positive still warps finitely. distanceScale takes the size correction here, the same channel
                // SDF_OP_SCALE/SDF_OP_LOG_SPHERE use; the residual shear from the y-varying scale is bounded
                // separately by SdfProgram.AnalyzeLipschitz's chain step clamp, not corrected per candidate.
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_AXIAL_PROFILE: {
                    SDF_VM_LOAD_DATA0;
                    SDF_VM_LOAD_DATA1;
                    uint axis = SDF_INSTRUCTION_SHAPE(instructionHeader);
                    if (axis >= 3u) { return sdfIsaErrorHit(); }
                    float rawT = (data0.z - localPosition[axis]) * data0.w;
                    float t = saturate(rawT);
                    float rawS = data1.y + data0.x * t + data0.y * sin(SDF_PI * t);
                    float scale = max(rawS, SDF_FLARE_MIN_SCALE);
                    float invScale = 1.0 / scale;
                    [unroll] for (uint component = 0u; component < 3u; component++) {
                        if (component != axis) { localPosition[component] *= invScale; }
                    }
                    distanceScale *= data1.x;
                    break;
                }
#endif
                // Polynomial shear (KEEP IN SYNC with SdfProgramBuilder.Shear): only X moves, by an exact
                // (not small-angle) polynomial of Y — data0 = (linear, quadratic).
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_SHEAR: {
                    SDF_VM_LOAD_DATA0;
                    uint target = SDF_INSTRUCTION_SHAPE(instructionHeader), driver = SDF_INSTRUCTION_BLEND(instructionHeader);
                    if (target >= 3u || driver >= 3u) { return sdfIsaErrorHit(); }
                    float t = localPosition[driver];
                    localPosition[target] += ((data0.z * t + data0.y) * t + data0.x) * t;
                    break;
                }
#endif
                // Gaussian domain push (KEEP IN SYNC with SdfProgramBuilder.GaussianPush): always a HEAD+TAIL pair —
                // this case reads its own data0 (Center)/data1 (Radii) plus the TAIL instruction's data0.xyz (Push)
                // by indexing one instruction ahead in the packed word stream (SdfProgram guarantees the tail is
                // adjacent and same-owner). The tail's own case is an inert no-op.
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_GAUSSIAN_PUSH: {
                    SDF_VM_LOAD_DATA0;
                    SDF_VM_LOAD_DATA1;
                    float3 gaussianPush = float3(data0.w, data1.w, asfloat(SDF_INSTRUCTION_SHAPE(instructionHeader)));
                    float3 gaussianOffset = ((localPosition - data0.xyz) / data1.xyz);
                    float gaussianWeight = exp(-dot(gaussianOffset, gaussianOffset));
                    localPosition -= (gaussianPush * gaussianWeight);
                    break;
                }
#endif
                // Per-shape lane-driven erosion (KEEP IN SYNC with SdfProgramBuilder.LaneErode): data0 = (lane index
                // 0..3, from, to, noiseScale), data1.x = the target shape's HOST-BAKED reach (its bound radius).
                // t = saturate((lane - from) / (to - from)) (a reversed from > to range runs the fold the other way);
                // t >= 1 sets the cheap early-out (SDF_OP_SHAPE_BLEND skips its own evaluation entirely — no field cost);
                // otherwise a 3D noise sample ragged-fronts t before it scales the reach into a world-unit candidate
                // erosion, consumed by the immediately-following SDF_OP_SHAPE_BLEND (whatever ordinary point ops the
                // shape's own chain still applies in between) and cleared there.
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_LANE_ERODE: {
                    SDF_VM_LOAD_DATA0;
                    SDF_VM_LOAD_DATA1;
                    float lane = ((data0.x < 0.5) ? currentLanes.x : ((data0.x < 1.5) ? currentLanes.y : ((data0.x < 2.5) ? currentLanes.z : currentLanes.w)));
                    float t = saturate((lane - data0.y) / (data0.z - data0.y));

                    if (t >= 1.0) {
                        laneErodeSkipShape = true;
                        laneErodeAmount = 0.0;
                    } else {
                        float noiseSample = 0.5 + 0.5 * sdfValueNoise3((localPosition * data0.w), SDF_LANE_ERODE_SEED);
                        float raggedT = saturate(t + ((noiseSample - 0.5) * SDF_LANE_ERODE_RAGGED_AMOUNT * (4.0 * t * (1.0 - t))));

                        laneErodeSkipShape = false;
                        laneErodeAmount = (raggedT * data1.x);
                    }

                    break;
                }
#endif
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_ELONGATE: {
                    SDF_VM_LOAD_DATA0;
                    localPosition -= clamp(localPosition, -data0.xyz, data0.xyz);
                    break;
                }
                // The FIELD ops act on the running result, not the point: Onion shells everything accumulated so far
                // into a hollow skin; Dilate inflates it. Both operate in world units (candidates were distance-scaled
                // before blending).
#endif
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_ONION: {
                    SDF_VM_LOAD_DATA0;
#ifdef SDF_TAPE_BUILD
                    sdfTapeUnary(index, op, data0.x);
#endif
                    result.distance = (abs(result.distance) - data0.x);
                    break;
                }
#endif
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_DILATE: {
                    SDF_VM_LOAD_DATA0;
#ifdef SDF_TAPE_BUILD
                    sdfTapeUnary(index, op, data0.x);
#endif
                    result.distance -= data0.x;
                    break;
                }
                // FIELD op (KEEP IN SYNC with SdfProgramBuilder.Displace): add a bounded sinusoidal RELIEF to the
                // accumulated field, evaluated at the current folded point — the SDF-native height/parallax map (the
                // relief is real geometry). data0.xyz = per-axis frequency, data0.w = amplitude. The separable
                // sin-product basis is deterministic (float trig, ±1 LSB like the twist/bend warps), so it stays
                // cross-backend parity-safe without a hashed noise table. Its squared gradient norm is MULTILINEAR in the three
                // squared sines, so it maximizes at a cube vertex and reaches exactly amp*max|freq_i| (the infinity norm,
                // not the Euclidean length): the field can overestimate by (1 + amp*max|freq_i|), which
                // SdfProgram.AnalyzeLipschitz bakes as the step clamp.
#endif
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_DISPLACE: {
                    SDF_VM_LOAD_DATA0;
                    // Evaluate the same continuous field at every distance. Switching to f-|amplitude| outside
                    // a band introduces a jump that corrupts footprint hits and finite-difference curvature.
                    float3 df = (data0.xyz * localPosition);
                    result.distance += (data0.w * ((sin(df.x) * sin(df.y)) * sin(df.z)));
                    break;
                }
                // fBm hash-lattice value-noise relief (KEEP IN SYNC with Puck.SignedDistance.SdfOp.NoiseDisplace /
                // SdfProgramBuilder.NoiseDisplace / SdfProgram.NoiseDisplaceLipschitz). data0 = (frequency, amplitude,
                // gain, lacunarity), data1.x = HOST-BAKED 1/sum(gain^k) normalization, header.y = seed, header.z =
                // octave count. The corner hash is INTEGER-ONLY (sdfValueNoise3), so cell decisions are bit-identical
                // across both DXC targets; the blend is float mul/add (+-1 LSB). AnalyzeLipschitz bakes the gradient
                // bound as the step clamp; the normalized sum stays in [-1, 1], so the surface reach is bounded by
                // |amplitude| (the scoped-reach / cull-margin channels read that).
#endif
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_CELL_DISPLACE: {
                    SDF_VM_LOAD_DATA0;
                    float3 ignoredGradient;
                    float value = sdfCellDistanceGrad(localPosition * data0.x, SDF_INSTRUCTION_SHAPE(instructionHeader),
                        SDF_INSTRUCTION_BLEND(instructionHeader), data0.z, ignoredGradient);
                    result.distance += data0.y * (value - 0.5);
                    break;
                }
                case SDF_OP_NOISE_DISPLACE: {
                    SDF_VM_LOAD_DATA0;
                    SDF_VM_LOAD_DATA1;
                    // Keep the field continuous, including outside the zero-set neighborhood: primary footprint
                    // hits, material selection and curvature all consume these values (see SDF_OP_DISPLACE).
                    float3 q = (localPosition * data0.x);
                    float octaveAmplitude = 1.0;
                    float noiseSum = 0.0;
                    uint octaveCount = SDF_INSTRUCTION_BLEND(instructionHeader);
                    for (uint octave = 0u; octave < SDF_MAX_NOISE_OCTAVES; octave++) {
                        if (octave >= octaveCount) { break; }
                        uint octaveSeed = (SDF_INSTRUCTION_SHAPE(instructionHeader) + octave);
                        noiseSum += (octaveAmplitude * sdfValueNoise3(q, uint3(octaveSeed, (octaveSeed * SDF_HASH_STREAM_A), (octaveSeed * SDF_HASH_STREAM_B))));
                        q *= data0.w;
                        octaveAmplitude *= data0.z;
                    }
                    result.distance += ((data0.y * data1.x) * noiseSum);
                    break;
                }
#endif
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_WALLPAPER_FOLD: {
                    SDF_VM_LOAD_DATA0;
                    SDF_VM_LOAD_DATA1;
                    // header lanes: y = the wallpaper group, z = the fold plane (0 = XZ, 1 = XY, 2 = YZ), w = the
                    // parity-material stride. The fold is an isometry, so distanceScale is untouched.
                    uint group = SDF_INSTRUCTION_SHAPE(instructionHeader);
                    uint plane = SDF_INSTRUCTION_BLEND(instructionHeader);
                    int axisA = ((plane == SDF_PLANE_YZ) ? 1 : 0);
                    int axisB = ((plane == SDF_PLANE_XY) ? 1 : 2);
                    float2 cellIndex;
                    float2 folded = sdfWallpaperFoldCell(float2(localPosition[axisA], localPosition[axisB]), group, data0.xy, data0.zw, data1.xy, cellIndex);

                    localPosition[axisA] = folded.x;
                    localPosition[axisB] = folded.y;

                    // The stride recolors the shapes the fold repeats: the cell key strides their declared material, so
                    // each lattice cell selects its own row of the palette. Distances never depend on it.
                    if (trackMaterial && (SDF_INSTRUCTION_MATERIAL(instructionHeader) != 0u)) {
                        parityMaterialDelta = (sdfWallpaperCellKey(group, cellIndex) * (int)SDF_INSTRUCTION_MATERIAL(instructionHeader));
                    }

                    break;
                }
#endif
                case SDF_OP_SHAPE_BLEND: {
                    // Consume the pending SDF_OP_LANE_ERODE effect (if any) FIRST, before every other skip path below,
                    // so it never leaks onto a later, unrelated shape (a bound-culled or Detail/Secondary-skipped
                    // shape still clears it here).
                    bool laneEroded = laneErodeSkipShape;
                    float laneErosion = laneErodeAmount;
                    laneErodeSkipShape = false;
                    laneErodeAmount = 0.0;

                    if (laneEroded) {
                        break;
                    }

                    if (!sdfShapeEnabled(SDF_INSTRUCTION_SHAPE(instructionHeader))) {
                        break;
                    }
                    SDF_VM_LOAD_DATA0;
                    SDF_VM_LOAD_DATA1;
                    // The per-shape flavour of the segment early-out above (same exactness argument): inside an
                    // EVALUATED segment, a Union shape whose own (tighter) sphere cannot beat the running minimum
                    // skips just its evaluation — always sound, because shape ops never mutate chain state.
                    uint4 shapeBoundMeta = sdfProgramWord(boundsOffset + (SDF_BOUND_RECORD_VECTORS * index) + 1u);

                    [branch]
                    if (shapeBoundMeta.x != SDF_BOUND_NONE
#ifdef SDF_TAPE_BUILD
                        && !sdfTapeBuilding
#endif
                    ) {
                        float4 shapeBound = asfloat(sdfProgramWord(boundsOffset + (SDF_BOUND_RECORD_VECTORS * index)));
                        float3 shapeBoundCenter = shapeBound.xyz;
                        bool shapeBoundReady = (shapeBoundMeta.x == SDF_BOUND_STATIC);

#ifdef SDF_DYNAMIC_TRANSFORMS
                        if (shapeBoundMeta.x == SDF_BOUND_DYNAMIC) {
                            shapeBoundCenter += sdfDynamicTransformRow(3u * shapeBoundMeta.y).xyz;
                            shapeBoundReady = true;
                        }
#endif

                        if (shapeBoundReady && (result.distance <= SDF_FAR_DISTANCE)) {
                            float3 toShapeCenter = (worldPosition - shapeBoundCenter);
                            float shapeClearance = max((result.distance + shapeBound.w), 0.0);

                            if (dot(toShapeCenter, toShapeCenter) >= (shapeClearance * shapeClearance)) {
                                break;
                            }
                        }
                    }

                    if (!sdfTapeShapeLive(index)) { sdfTapeSkippedShape(instructionHeader); break; }
                    uint shapeType = (SDF_INSTRUCTION_SHAPE(instructionHeader) & SDF_SHAPE_TYPE_MASK);
                    float candidate = ((evaluateShape(shapeType, localPosition, data0, data1) * distanceScale) + laneErosion);
#ifdef SDF_TAPE_BUILD
                    sdfTapeCandidate(segment, index, candidate, SDF_INSTRUCTION_BLEND(instructionHeader), data1.x, currentSlot, data0.w);
#endif

                    // Hand the DISTANCE-SCALED (plus any pending lane erosion) candidate to the shared blend tail below.
                    composeCandidate = candidate;
                    composeBlend = SDF_INSTRUCTION_BLEND(instructionHeader);
                    composeSmooth = data1.x;
                    composePending = true;

                    if (trackMaterial) {
                        int material = (int)SDF_INSTRUCTION_MATERIAL(instructionHeader);

                        // An active wallpaper stride recolors this shape by its cell key — never a screen sentinel (the whole
                        // >= SDF_SCREEN_MATERIAL range, not just the exact value: a screen-instance id must survive intact).
                        if ((material < SDF_SCREEN_MATERIAL) && (parityMaterialDelta != 0)) {
                            material += parityMaterialDelta;
                        }

                        composeMaterial = material;
                        composeLanes = currentLanes;
                        composeInstance = segmentInstance;
                        composeSlot = currentSlot;
                    }
                    break;
                }
#ifndef SDF_STRIP_ALL_EXOTIC
                // Scoped field accumulator (KEEP IN SYNC with Puck.SignedDistance.SdfOp.PushField/PopField). A scope touches ONLY
                // the FIELD — never localPosition / distanceScale / parityMaterialDelta — so the point chain is untouched
                // and ResetPoint semantics are unchanged.
                case SDF_OP_PUSH_FIELD: {
                    if (fieldDepth >= SDF_MAX_FIELD_SCOPE_DEPTH) { return sdfIsaErrorHit(); }
                    fieldParents[fieldDepth] = result;
                    fieldBlendWeights[fieldDepth] = trackMaterial ? sdfMaterialBlendWeight : 0.0;
                    fieldBlendOthers[fieldDepth] = trackMaterial ? sdfMaterialBlendOther : 0;
                    if (trackMaterial) {
                        sdfMaterialBlendWeight = 0.0;
                        sdfMaterialBlendOther = 0;
                    }
                    fieldDepth++;
                    result.distance = SDF_FAR_DISTANCE;
                    if (trackMaterial) {
                        result.material = 0;
                        result.lanes = float4(0.0, 0.0, 0.0, 0.0);
                        result.instanceIndex = -1;
                        result.frameSlot = SDF_TRANSFORM_SLOT_NONE;
                    }
                    break;
                }
                case SDF_OP_POP_FIELD: {
                    if (fieldDepth == 0u) { return sdfIsaErrorHit(); }
                    fieldDepth--;
                    float savedFieldDistance = fieldParents[fieldDepth].distance;
                    int savedFieldMaterial = fieldParents[fieldDepth].material;
                    float4 savedFieldLanes = fieldParents[fieldDepth].lanes;
                    int savedFieldInstance = fieldParents[fieldDepth].instanceIndex;
                    int savedFieldSlot = fieldParents[fieldDepth].frameSlot;
                    float savedFieldBlendWeight = fieldBlendWeights[fieldDepth];
                    int savedFieldBlendOther = fieldBlendOthers[fieldDepth];
                    SDF_VM_LOAD_DATA1;
                    // The scope's accumulated field IS the candidate — ALREADY in world units (its shapes were
                    // distance-scaled as they blended in), so it is NOT re-multiplied by distanceScale, and the point
                    // parityMaterialDelta must NOT touch it (the fusion trap). Restore the parent accumulator as the
                    // blend LHS, then fall into the SAME material-winner + blendShape tail a SHAPE uses. The compose blend
                    // + smooth ride the POP instruction (header.z / data1.x, baked by SdfProgramBuilder.PushField).
                    // data1.y is the scope's baked 1/L candidate scale on every pop; a stairs pop carries its step count
                    // in data1.z (KEEP IN SYNC with AnalyzeLipschitz and the fixed-point mirror).
                    composeBlend = SDF_INSTRUCTION_BLEND(instructionHeader);
#ifdef SDF_TAPE_BUILD
                    sdfTapePop(index, composeBlend, data1);
#endif
                    bool isStairs = (composeBlend == SDF_BLEND_STAIRS_UNION || composeBlend == SDF_BLEND_STAIRS_SUBTRACTION);
                    float candidateScale = data1.y;
                    composeCandidate = result.distance;
                    if (candidateScale > 0.0) {
                        composeCandidate *= candidateScale;
                    }
                    if (trackMaterial) {
                        composeMaterial = result.material;
                        composeLanes = result.lanes;
                        composeInstance = result.instanceIndex;
                        composeSlot = result.frameSlot;
                    }
                    composeSmooth = data1.x;
                    float scopeBlendWeight = sdfMaterialBlendWeight;
                    int scopeBlendOther = sdfMaterialBlendOther;
                    result.distance = savedFieldDistance;
                    if (trackMaterial) {
                        result.material = savedFieldMaterial;
                        result.lanes = savedFieldLanes;
                        result.instanceIndex = savedFieldInstance;
                        result.frameSlot = savedFieldSlot;
                        sdfMaterialBlendWeight = savedFieldBlendWeight;
                        sdfMaterialBlendOther = savedFieldBlendOther;
                    }

                    if (sdfTapeDecided(index)) { sdfTapeSkippedShape(instructionHeader); break; }

                    if (composeBlend == SDF_BLEND_MORPH) {
                        SDF_VM_LOAD_DATA0;
                        float lane = ((data0.x < 0.5) ? currentLanes.x : ((data0.x < 1.5) ? currentLanes.y : ((data0.x < 2.5) ? currentLanes.z : currentLanes.w)));
                        float t = saturate((lane - data0.y) / (data0.z - data0.y));
                        result.distance = lerp(savedFieldDistance, composeCandidate, t);
                        if (trackMaterial) {
                            bool candidateWins = (t >= 0.5);
                            result.material = candidateWins ? composeMaterial : savedFieldMaterial;
                            result.lanes = candidateWins ? composeLanes : savedFieldLanes;
                            result.instanceIndex = candidateWins ? composeInstance : savedFieldInstance;
                            result.frameSlot = candidateWins ? composeSlot : savedFieldSlot;
                            bool tableSeam = ((savedFieldMaterial < SDF_SCREEN_MATERIAL) && (composeMaterial < SDF_SCREEN_MATERIAL));
                            sdfMaterialBlendWeight = tableSeam ? min(t, 1.0 - t) : 0.0;
                            sdfMaterialBlendOther = candidateWins ? savedFieldMaterial : composeMaterial;
                        }
                        composePending = false;
                        break;
                    }

                    if (isStairs) {
                        float r = composeSmooth;
                        float n = data1.z;
                        if (n >= 1.0 && r > 0.0) {
                            float s = r / n;
                            float period = 2.0 * s;
                            bool isSub = (composeBlend == SDF_BLEND_STAIRS_SUBTRACTION);
                            float a = savedFieldDistance;
                            float b = composeCandidate;
                            float u = isSub ? (-b - r) : (b - r);
                            float arg = u - a + s;
                            float m = arg - period * floor(arg / period);
                            float w = m - s;
                            float dStairs = 0.5 * (u + a + abs(w));
                            if (isSub) {
                                result.distance = max(max(a, -b), -dStairs);
                                if (trackMaterial) {
                                    bool candidateWins = (-b > a);
                                    result.material = candidateWins ? composeMaterial : savedFieldMaterial;
                                    result.lanes = candidateWins ? composeLanes : savedFieldLanes;
                                    result.instanceIndex = candidateWins ? composeInstance : savedFieldInstance;
                                    result.frameSlot = candidateWins ? composeSlot : savedFieldSlot;
                                    // A losing scope leaves the parent's restored seam intact, as the shared tail does.
                                    if (candidateWins) {
                                        sdfMaterialBlendWeight = 0.0;
                                    }
                                }
                            } else {
                                result.distance = min(min(a, b), dStairs);
                                if (trackMaterial) {
                                    bool candidateWins = (b < a);
                                    result.material = candidateWins ? composeMaterial : savedFieldMaterial;
                                    result.lanes = candidateWins ? composeLanes : savedFieldLanes;
                                    result.instanceIndex = candidateWins ? composeInstance : savedFieldInstance;
                                    result.frameSlot = candidateWins ? composeSlot : savedFieldSlot;
                                    if (candidateWins) {
                                        sdfMaterialBlendWeight = 0.0;
                                    }
                                }
                            }
                            composePending = false;
                            break;
                        }
                    }

                    // A losing scope cannot tint its parent. A winning hard union carries its own internal seam;
                    // a smooth outer composition instead creates a new two-material seam in the shared helper.
                    bool scopeWinsUnion = (composeBlend == SDF_BLEND_UNION) && (composeCandidate < result.distance);
                    sdfComposeCandidate(result, composeCandidate, composeBlend, composeMaterial, composeLanes, composeInstance, composeSlot, composeSmooth, trackMaterial);
                    if (trackMaterial && scopeWinsUnion) {
                        sdfMaterialBlendWeight = scopeBlendWeight;
                        sdfMaterialBlendOther = scopeBlendOther;
                    }
                    composePending = false;
                    break;
                }
#endif
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
                if (sdfTapeDecided(index)) { result.distance = sdfTapeWinnerSeed(composeBlend); }
                sdfComposeCandidate(result, composeCandidate, composeBlend, composeMaterial, composeLanes, composeInstance, composeSlot, composeSmooth, trackMaterial);
            }
        }
#ifdef SDF_TAPE_BUILD
        sdfTapeEndSegment(segment);
#endif
    }

    // The Lipschitz clamp: scale the FINAL nearest-surface distance by the per-program 1/L so every marcher that funnels
    // through mapCore (the beam cone-march, the pixel march, the RT debug marcher, the shadow marches, the normal probes)
    // takes field-rate-safe steps. Applied ONCE here, AFTER the walk — the per-candidate blends, the material-winner
    // compares, the Onion/Dilate/Displace field ops, and the exact-cull skip tests all ran on UNCORRECTED candidates, so
    // the zero set, the seams, the materials, and the cull contract are all preserved; only the step LENGTH shrinks. A
    // uniform positive scale never moves the zero crossing, and a normal probe's common factor cancels under normalize
    // (true of both the 4-tap tetrahedron and the 6-tap central difference), so shading is unchanged. stepScale == 1.0
    // leaves the result bit-identical.
    //
    // Consumers receive the clamped field. Primary acceptance uses that field with its footprint threshold; shadow
    // penumbra estimation separately removes the global clamp — see softShadowVisibility in surface/sdf-occlusion.hlsli.
    result.distance *= stepScale;
    // Publish the fold-safe step bound in the SAME clamped units as the returned distance: stepScale = 1/L covers the
    // whole chain's worst-case expansion, so the clamped gap remains a conservative world-travel bound even when a
    // non-conformal warp (twist/bend) sits upstream of the fold. SDF_STEP_BOUND_NONE stays effectively unbounded.
    sdfMapStepBound = (walkStepBound * stepScale);
    sdfMapFoldGap = foldGap;
    sdfMapFoldCenter = foldCenter;
    sdfMapFoldInner = foldInner;
    sdfMapFoldOuter = foldOuter;

    return result;
}

#undef SDF_VM_LOAD_DATA0
#undef SDF_VM_LOAD_DATA1

// The universal entry point every consumer outside the world path's Stage 1 calls (the beam cone-march): every
// instance visible, so an instanced program still renders its complete picture — only Stage 1 narrows the mask (see
// mapMasked).
SdfHit map(float3 worldPosition) {
    return mapCore(worldPosition, SDF_INSTANCE_MASK_ALL, true);
}
// The per-tile MASKED entry point (world render path only, sdf-world-views.comp): evaluates the WORLD set (segments
// owned by no instance) plus only the instances the per-tile mask at `instanceMaskBase` (an element offset into
// sdfInstanceMasks, written by the tile-cull beam prepass) marks visible — culling the shapes AND transform chains
// of instances the tile's rays cannot reach, without ever touching their segments. Exact, not approximate: a
// masked-out instance's true contribution to the tile's rays is provably absent (the beam prepass tested every
// ray's cone against the instance's own bounding sphere), so the result is bit-identical to a full
// mapCore(worldPosition, SDF_INSTANCE_MASK_ALL) call for every ray the mask keeps correct.
SdfHit mapMasked(float3 worldPosition, uint instanceMaskBase) {
    return mapCore(worldPosition, instanceMaskBase, true);
}

// Distance-only entry points for secondary rays and probes. The compile-time literal false lets DXC erase the
// material winner, smooth-material seam bookkeeping, and positional material variation from these VM walks while
// preserving the complete geometric accumulator, bounds, masks, field scopes, and fold-safe step bound.
float mapDistance(float3 worldPosition) {
    return mapCore(worldPosition, SDF_INSTANCE_MASK_ALL, false).distance;
}

float mapDistanceMasked(float3 worldPosition, uint instanceMaskBase) {
    return mapCore(worldPosition, instanceMaskBase, false).distance;
}

// The ball clearance of the last map sample: no geometry of any lattice, on either side of any fold wall, lies within
// it of the sample. A proof by one ball (the beam's cone, a reprojected march seed, the relaxed march's
// disjoint-sphere test) reads this.
float sdfMapBallClearance(float distance) {
    return min(distance, min(sdfMapStepBound, sdfMapFoldGap));
}

// Where the ray rayOrigin + t * rayDirection (unit; offset = rayOrigin - center, along = dot(offset, rayDirection))
// meets the sphere of `radius` about a wall's center, in ray parameter; false when it misses. Taken from the ray's
// origin, never from a sample, so every sample of one ray reads the same two roots. q = -(b + sign(b) sqrt(D)) and
// c / q avoid the cancellation of -b +- sqrt(D).
bool sdfWallRoots(float3 offset, float along, float radius, out float nearRoot, out float farRoot) {
    float c = (dot(offset, offset) - (radius * radius));
    float discriminant = ((along * along) - c);

    nearRoot = SDF_STEP_BOUND_NONE;
    farRoot = SDF_STEP_BOUND_NONE;

    if (discriminant < 0.0) {
        return false;
    }

    float root = sqrt(discriminant);
    float q = -(along + ((along >= 0.0) ? root : -root));
    float other = ((q != 0.0) ? (c / q) : 0.0);

    nearRoot = min(q, other);
    farRoot = max(q, other);

    return true;
}

// Where the ray leaves the shell between two concentric walls about `center` that holds the sample, if sooner than
// `next`: through the outer wall (which the sample lies within, SDF_STEP_BOUND_NONE when none) at its far root, or here
// at the latest; or into the inner wall (which the sample lies past, 0 when none) at its near root, or here when the
// ray already runs inside its chord. `after` is where the ray leaves the far side of a wall it enters.
void sdfShellExit(float3 rayOrigin, float3 rayDirection, float traveled, float3 center, float inner, float outer,
    inout float next, inout float after) {
    float3 offset = (rayOrigin - center);
    float along = dot(offset, rayDirection);
    float nearRoot;
    float farRoot;

    if (outer < SDF_STEP_BOUND_NONE) {
        float exit = (sdfWallRoots(offset, along, outer, nearRoot, farRoot) ? max(farRoot, traveled) : traveled);

        if (exit < next) {
            next = exit;
            after = SDF_STEP_BOUND_NONE;
        }
    }
    if (inner > 0.0) {
        if (sdfWallRoots(offset, along, inner, nearRoot, farRoot)) {
            float entry = max(nearRoot, traveled);

            if ((nearRoot < farRoot) && (entry < farRoot) && (entry < next)) {
                next = entry;
                after = farRoot;
            }
        }
    }
}

// The next float above a non-negative ray parameter.
float sdfWallNextUp(float value) {
    return asfloat(asuint(max(value, 1.0e-30)) + 1u);
}

// THE MARCH STEP ACROSS A FOLD WALL (see sdfMapStepBound), which the fine marches take (primary, soft shadows and the
// overshoot view). A marcher samples the field at rayOrigin + traveled * rayDirection and asks here where its next
// sample goes, in ray parameter:
//   clearance    how far along the ray the sample's own side is proven clear: the field (a soft shadow passes its own
//                stride), never limited by a wall's gap;
//   advance      the step the marcher would take with no wall (over-relaxed, or raised to a minimum stride);
//   tolerance    the marcher's acceptance distance: a surface within it of a sample is accepted there;
//   limit        the ray parameter the march ends at.
// A step that stays inside every wall's ball (sdfMapBallClearance) is returned unchanged with `proven` false, so a
// relaxed step is still validated by the marcher's own test; so is one the march would relax across a ball wall,
// which the disjoint-sphere test covers. Otherwise:
// - THE FOLD SHELL: the ray leaves the sample's shell at a wall's root. When the sample's side is clear to it, the
//   march CROSSES: it lands `beyond` past the root, the least of the tolerance, half the arrival chord and half the
//   way to the limit, and samples the arrival side there before stepping on. When it is not, the step is the
//   clearance, which stays in the shell.
// - BALL WALLS: a step reaches at most the tolerance past their ball, so arrival-side geometry it passes lies within
//   the tolerance of the landing sample. Near such a wall a march advances at least the tolerance a step.
// Either is `proven`: the marcher validates nothing and resets any relaxation. switchAt is the earliest depth the
// step leaves unproven (the shell wall's root, or a ball wall's gap), SDF_STEP_BOUND_NONE when it proves the whole
// step.
// NO SKIP: the sample's side is clear up to the wall, and arrival-side geometry within the beyond segment lies within
// the tolerance of the landing sample, which accepts it. A chord shorter than twice the tolerance is landed on at its
// middle, so a grazing pass through a shell wall is still sampled on its arrival side.
// AT MOST TWO CROSSINGS PER SHELL WALL: a line meets a sphere at most twice, every sample of a ray reads the same
// roots, a crossing lands strictly past its root, and a marcher never retreats behind a proven step; the next
// crossing needs a root at or past the sample. A march crosses each shell sphere at most twice (in and out), one
// budgeted step each. The walls are taken from the sample's side, so a sample a wall test places on the far side of a
// root by rounding crosses from where it stands: the march never steps a shell's clearance across the wrong side,
// and only a landing that rounds back onto the departure side (a grazing ray within float spacing of the sphere)
// spends one more crossing, each a tolerance further on.
// A sample whose walls lie beyond both its clearance and its advance pays one compare.
float sdfMarchAdvance(float3 rayOrigin, float3 rayDirection, float traveled, float clearance, float advance,
    float tolerance, float limit, out bool proven, out float switchAt) {
    proven = false;
    switchAt = SDF_STEP_BOUND_NONE;

    float ballGap = min(sdfMapStepBound, sdfMapFoldGap);

    if (max(clearance, advance) <= ballGap) {
        return (traveled + advance);
    }

    float next = SDF_STEP_BOUND_NONE;
    float after = SDF_STEP_BOUND_NONE;

    if (sdfMapFoldGap < SDF_STEP_BOUND_NONE) {
        sdfShellExit(rayOrigin, rayDirection, traveled, sdfMapFoldCenter, sdfMapFoldInner, sdfMapFoldOuter, next, after);
    }

    if (!(ballGap < clearance) && ((traveled + advance) < next)) {
        return (traveled + advance);
    }

    proven = true;

    float landing = (traveled + clearance);

    if ((next < SDF_STEP_BOUND_NONE) && ((next - traveled) <= clearance)) {
        switchAt = next;
        float beyond = ((next < limit) ? min(tolerance, (0.5 * (min(after, limit) - next))) : tolerance);

        landing = max((next + beyond), sdfWallNextUp(next));
    }

    // A ball wall nearer than the landing: reach at most the tolerance past it, still strictly past a root it crosses.
    if ((sdfMapStepBound < SDF_STEP_BOUND_NONE) && ((traveled + sdfMapStepBound + tolerance) < landing)) {
        float reach = max((traveled + sdfMapStepBound + tolerance), sdfWallNextUp(traveled));

        landing = ((reach >= next) ? max(reach, sdfWallNextUp(next)) : reach);
        switchAt = min(((reach >= next) ? next : SDF_STEP_BOUND_NONE), (traveled + sdfMapStepBound));
    }

    return landing;
}

#endif
