// mapGradCore, the forward-mode twin of mapCore that carries the analytic gradient.
#ifndef FIELD_SDF_MAP_GRAD_HLSLI
#define FIELD_SDF_MAP_GRAD_HLSLI
// Applies a RepeatPolar sector fold's local orthogonal map (a rotation by -angle*sector in the fold plane, then an
// optional reflection across the sector bisector — both isometries, piecewise-constant per sector) to a Jacobian
// column vector `b`, identity on the axial coordinate. `rc`/`rs` = cos/sin(angle*sector); `flip` = -1 when the point's
// rotated v-coordinate was negative and the op mirrors, else 1.
float3 sdfApplyPolarJacobian(float3 b, uint axis, float rc, float rs, float flip) {
    float2 uv;

    if (axis == SDF_AXIS_X) { uv = b.yz; }
    else if (axis == SDF_AXIS_Z) { uv = b.xy; }
    else { uv = b.xz; }

    float2 nuv = float2(((rc * uv.x) + (rs * uv.y)), (((-rs * uv.x) + (rc * uv.y)) * flip));

    if (axis == SDF_AXIS_X) { b.yz = nuv; }
    else if (axis == SDF_AXIS_Z) { b.xy = nuv; }
    else { b.xz = nuv; }

    return b;
}
// Applies a wallpaper fold's local 2x2 in-plane Jacobian (its two columns, recovered by a shape-local finite difference
// of the fold map — the fold is an isometry but composes many conditional reflections/rotations no single closed form
// spells) to a Jacobian column vector `b`, identity on the third axis.
float3 sdfApplyPlaneJacobian(float3 b, int axisA, int axisB, float2 col0, float2 col1) {
    float bA = b[axisA];
    float bB = b[axisB];

    b[axisA] = ((col0.x * bA) + (col1.x * bB));
    b[axisB] = ((col0.y * bA) + (col1.y * bB));

    return b;
}

// The forward-mode dual twin of mapCore (see the "Forward-mode gradient dual" banner above blendShapeDual). Walks the
// SAME segment/instance merge and the SAME op stream, tracking — beside the scalar accumulator — the transform-chain
// Jacobian columns jx/jy/jz (= d(localPosition)/d(worldPosition.{x,y,z}); the point ops build them, RESET restores
// identity) and the world-space accumulator gradient `resultGradient`. `gradient` (out) is the UN-normalized surface
// gradient at the hit; the consumer normalizes (which cancels the stepScale the scalar distance still carries). HIT-
// ONLY: never called from the march, so its ~2x cost is paid once per lit pixel. KEEP the walk skeleton IN SYNC with
// mapCore — this is a parallel evaluation path, not a rewrite: only the gradient lane is added.
SdfHit mapGradCore(float3 worldPosition, uint instanceMaskBase, out float3 gradient) {
    // KEEP-IN-SYNC with mapCore's decode above: same sdfLoadProgramLayout-cached static, same fields.
    uint dataOffset = sdfProgramLayout.dataOffset;
    uint boundsOffset = sdfProgramLayout.boundsOffset;
    uint segmentOffset = sdfProgramLayout.segmentOffset;
    uint segmentCount = sdfProgramLayout.segmentCount;
    uint rigidPlanOffset = sdfProgramLayout.rigidPlanOffset;
    float stepScale = sdfProgramLayout.stepScale;
    uint instanceOffset = sdfProgramLayout.instanceOffset;
    uint instanceCount = sdfProgramLayout.instanceCount;
    uint worldSegmentOffset = sdfProgramLayout.worldSegmentOffset;
    bool hasInstances = sdfProgramLayout.hasInstances;

    if (instanceCount > SDF_MAX_INSTANCES) {
        gradient = float3(0.0, 0.0, 1.0);
        return sdfIsaErrorHit();
    }

    uint linearCursor = 0u;
    uint worldCursor = 0u;
    uint worldCount = 0u;
    uint worldNext = SDF_SEGMENT_NONE;
    uint maskWordIndex = 0xFFFFFFFFu;
    uint maskWordBits = 0u;
    uint instanceSegment = SDF_SEGMENT_NONE;
    uint instanceSegmentEnd = SDF_SEGMENT_NONE;
    uint pendingInstance = SDF_SEGMENT_NONE;

    if (hasInstances) {
        worldCount = SDF_WORLD_SEGMENT_COUNT(sdfWords[worldSegmentOffset]);
        worldNext = ((0u < worldCount) ? sdfWords[worldSegmentOffset + SDF_DIRECTORY_HEADER_VECTORS].x : SDF_SEGMENT_NONE);
        sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex, maskWordBits, instanceSegment, instanceSegmentEnd, pendingInstance);
    }

    float3 localPosition = worldPosition;
    float distanceScale = 1.0;
    // The transform-chain Jacobian columns: jx = d(localPosition)/d(worldPosition.x), etc. Identity at the start of
    // every chain segment (RESET restores them, exactly as it restores localPosition). worldGrad_j = dot(localGrad, j_).
    float3 jx = float3(1.0, 0.0, 0.0);
    float3 jy = float3(0.0, 1.0, 0.0);
    float3 jz = float3(0.0, 0.0, 1.0);
    // KEEP IN SYNC with mapCore's currentLanes/laneErodeSkipShape/laneErodeAmount — same reset/set/consume points.
    float4 currentLanes = float4(0.0, 0.0, 0.0, 0.0);
    int currentSlot = SDF_TRANSFORM_SLOT_NONE;
    bool laneErodeSkipShape = false;
    float laneErodeAmount = 0.0;
    SdfHit result;

    result.distance = SDF_FAR_DISTANCE;
    result.material = 0;
    result.lanes = float4(0.0, 0.0, 0.0, 0.0);
    result.instanceIndex = -1;
    result.frameSlot = SDF_TRANSFORM_SLOT_NONE;
    float3 resultGradient = float3(0.0, 0.0, 0.0);

    // The one-deep scoped-accumulator save slot carries distance, material, lanes, and gradient together.
    SdfFieldSave saved;
    saved.distance = SDF_FAR_DISTANCE;
    saved.material = 0;
    saved.lanes = float4(0.0, 0.0, 0.0, 0.0);
    saved.instanceIndex = -1;
    saved.frameSlot = SDF_TRANSFORM_SLOT_NONE;
    saved.gradient = float3(0.0, 0.0, 0.0);

    [loop]
    for (;;) {
        uint segment;
        int segmentInstance = -1;

        if (!hasInstances) {
            if (linearCursor >= segmentCount) {
                break;
            }

            segment = linearCursor++;
        } else if (worldNext < instanceSegment) {
            segment = worldNext;
            worldCursor++;
            worldNext = ((worldCursor < worldCount) ? sdfWords[worldSegmentOffset + SDF_DIRECTORY_HEADER_VECTORS + worldCursor].x : SDF_SEGMENT_NONE);
        } else if (instanceSegment < instanceSegmentEnd) {
            segmentInstance = (int)pendingInstance;
            segment = instanceSegment++;

            if (instanceSegment == instanceSegmentEnd) {
                sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex, maskWordBits, instanceSegment, instanceSegmentEnd, pendingInstance);
            }
        } else {
            break;
        }

        uint4 segmentMeta = sdfWords[segmentOffset + SDF_DIRECTORY_HEADER_VECTORS + (SDF_BOUND_RECORD_VECTORS * segment) + 1u];
        uint segmentBoundMode = (segmentMeta.x & SDF_SEGMENT_BOUND_MASK);

        [branch]
        if (segmentBoundMode != SDF_BOUND_NONE) {
            float4 segmentBound = asfloat(sdfWords[segmentOffset + SDF_DIRECTORY_HEADER_VECTORS + (SDF_BOUND_RECORD_VECTORS * segment)]);
            float3 boundCenter = segmentBound.xyz;
            bool boundReady = (segmentBoundMode == SDF_BOUND_STATIC);

#ifdef SDF_DYNAMIC_TRANSFORMS
            if (segmentBoundMode == SDF_BOUND_DYNAMIC) {
                boundCenter += sdfDynamicTransforms[3u * segmentMeta.y].xyz;
                boundReady = true;
            }
#endif

            if (boundReady && (result.distance <= SDF_FAR_DISTANCE)) {
                float3 toCenter = (worldPosition - boundCenter);
                float clearance = max((result.distance + segmentBound.w), 0.0);

                if (dot(toCenter, toCenter) >= (clearance * clearance)) {
                    continue;
                }
            }
        }

        // Rigid-leaf dual fast path — the analytic-gradient twin of mapCore's SDF_SEGMENT_RIGID_PLAN walk. KEEP-IN-SYNC
        // PAIR with mapCore (the same plan decode, the same shared dynamic-slot pose, the same per-leaf tight-sphere
        // reject, the same subtract/inverse-rotate point math). distanceScale is 1 on a rigid chain (the host rejects
        // Scale), so the leaf's world gradient is its shape-LOCAL gradient forward-rotated to world by the leaf rotation
        // — the baked leaf quaternion for a static leaf, the composition dynamicOrientation ∘ leafQuat for a
        // TransformDynamic leaf (mirroring the inverse rotations the point walk applies). The candidate then feeds the
        // SAME sdfComposeDualCandidate tail the interpreted dual uses, so blend order/material semantics are identical.
#ifndef SDF_VM_DISABLE_RIGID_PLAN
        [branch]
        if ((segmentMeta.x & SDF_SEGMENT_RIGID_PLAN) != 0u) {
            uint4 plan = sdfWords[rigidPlanOffset + segment];
            bool planReady = true;

#ifndef SDF_DYNAMIC_TRANSFORMS
            planReady = (plan.z == SDF_TRANSFORM_SLOT_STATIC_WORD);
#endif

            if (planReady) {
                float3 rigidBasePosition = worldPosition;
                float4 rigidLanes = 0.0;
                int rigidSlot = SDF_TRANSFORM_SLOT_NONE;

                float4 rigidDynamicOrientation = float4(0.0, 0.0, 0.0, 1.0);
                bool rigidDynamic = false;

#ifdef SDF_DYNAMIC_TRANSFORMS
                if (plan.z != SDF_TRANSFORM_SLOT_STATIC_WORD) {
                    uint dynamicSlot = (uint)SDF_TRANSFORM_SLOT_UNPACK(plan.z);
                    rigidLanes = sdfDynamicTransforms[3u * dynamicSlot + 2u];
                    rigidSlot = (int)dynamicSlot;
                    float4 dynamicPosition = sdfDynamicTransforms[3u * dynamicSlot];
                    rigidDynamicOrientation = sdfDynamicTransforms[(3u * dynamicSlot) + 1u];
                    rigidBasePosition = rotatePointByInverseQuaternion((worldPosition - dynamicPosition.xyz), rigidDynamicOrientation);
                    rigidDynamic = true;
                }
#endif

                [loop]
                for (uint leaf = 0u; (leaf < plan.y); leaf++) {
                    uint leafOffset = (plan.x + (3u * leaf));
                    uint4 packedPose = sdfWords[leafOffset];
                    uint packedShape = packedPose.w;
                    uint shapeIndex = (packedShape & SDF_RIGID_LEAF_SHAPE_MASK);
                    bool folded = ((packedShape & SDF_RIGID_LEAF_FOLDED) != 0u);

                    if (folded) {
                        leaf++; // the extension slot
                    }

                    // Same tight-sphere reject mapCore's rigid walk applies, in the same chain frame. Negative radius
                    // marks a non-Union/unbounded leaf that is always evaluated.
                    float4 leafBound = asfloat(sdfWords[leafOffset + 2u]);

                    if ((leafBound.w >= 0.0) && (result.distance <= SDF_FAR_DISTANCE)) {
                        float3 toLeafCenter = (rigidBasePosition - leafBound.xyz);
                        float leafClearance = max((result.distance + leafBound.w), 0.0);

                        if (dot(toLeafCenter, toLeafCenter) >= (leafClearance * leafClearance)) {
                            continue;
                        }
                    }

                    uint4 shapeHeader = sdfWords[SDF_PROGRAM_HEADER_VECTORS + shapeIndex];

                    if (!sdfShapeEnabled(SDF_INSTRUCTION_SHAPE(shapeHeader))) {
                        continue;
                    }

                    float3 leafBasePosition = rigidBasePosition;
                    uint reflected = 0u;

                    if (folded) {
                        leafBasePosition = sdfRigidFoldPoint(rigidBasePosition, (leafOffset + 3u), dataOffset, reflected);
                    }

                    float3 rigidPosition = (leafBasePosition - asfloat(packedPose.xyz));
                    bool leafIdentity = ((packedShape & SDF_RIGID_LEAF_IDENTITY_ROTATION) != 0u);
                    float4 leafQuat = asfloat(sdfWords[leafOffset + 1u]);

                    if (!leafIdentity) {
                        rigidPosition = rotatePointByInverseQuaternion(rigidPosition, leafQuat);
                    }

                    float4 shapeData0 = asfloat(sdfWords[dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * shapeIndex)]);
                    float4 shapeData1 = asfloat(sdfWords[dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * shapeIndex) + 1u]);
                    uint shapeType = (SDF_INSTRUCTION_SHAPE(shapeHeader) & SDF_SHAPE_TYPE_MASK);
                    float candidate = evaluateShape(shapeType, rigidPosition, shapeData0, shapeData1);
                    // The shape-LOCAL gradient, forward-rotated to world by the leaf rotation then (for a dynamic leaf)
                    // the entity orientation — R_dyn * R_leaf * localGrad = R(dynamicOrientation ∘ leafQuat) * localGrad.
                    float3 leafGrad = evaluateShapeGradient(shapeType, rigidPosition, shapeData0, shapeData1);

                    if (!leafIdentity) {
                        leafGrad = rotatePointByQuaternion(leafGrad, leafQuat);
                    }

                    if (folded) {
                        leafGrad = sdfRigidFoldGradient(leafGrad, (leafOffset + 3u), dataOffset, reflected);
                    }

#ifdef SDF_DYNAMIC_TRANSFORMS
                    if (rigidDynamic) {
                        leafGrad = rotatePointByQuaternion(leafGrad, rigidDynamicOrientation);
                    }
#endif

                    sdfComposeDualCandidate(result, resultGradient, candidate, leafGrad, SDF_INSTRUCTION_BLEND(shapeHeader), (int)SDF_INSTRUCTION_MATERIAL(shapeHeader), rigidLanes, segmentInstance, rigidSlot, shapeData1.x);
                }

                continue;
            }
        }
#endif

        [loop]
        for (uint index = segmentMeta.z; (index < segmentMeta.w); index++) {
            uint4 instructionHeader = sdfWords[SDF_PROGRAM_HEADER_VECTORS + index];
            uint op = SDF_INSTRUCTION_OP(instructionHeader);
            float4 data0 = asfloat(sdfWords[dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * index)]);
            float4 data1 = asfloat(sdfWords[dataOffset + (SDF_INSTRUCTION_DATA_VECTORS * index) + 1u]);

            bool composePending = false;
            float composeCandidate = SDF_FAR_DISTANCE;
            float3 composeGradient = float3(0.0, 0.0, 0.0);
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
                    jx = float3(1.0, 0.0, 0.0);
                    jy = float3(0.0, 1.0, 0.0);
                    jz = float3(0.0, 0.0, 1.0);
                    currentLanes = float4(0.0, 0.0, 0.0, 0.0);
                    currentSlot = SDF_TRANSFORM_SLOT_NONE;
                    laneErodeSkipShape = false;
                    laneErodeAmount = 0.0;
                    break;
                }
                case SDF_OP_TRANSLATE: {
                    localPosition -= data0.xyz;   // A = I (a constant offset has zero Jacobian)
                    break;
                }
                case SDF_OP_ROTATE: {
                    // A = R^T (the same inverse rotation the point takes), applied to each Jacobian column.
                    localPosition = rotatePointByInverseQuaternion(localPosition, data0);
                    jx = rotatePointByInverseQuaternion(jx, data0);
                    jy = rotatePointByInverseQuaternion(jy, data0);
                    jz = rotatePointByInverseQuaternion(jz, data0);
                    break;
                }
                case SDF_OP_SCALE: {
                    localPosition /= data0.xyz;   // A = diag(1/scale)
                    jx /= data0.xyz;
                    jy /= data0.xyz;
                    jz /= data0.xyz;
                    distanceScale *= data0.w;
                    break;
                }
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_LOG_SPHERE: {
                    // KEEP-IN-SYNC with mapCore's case, EXCEPT the fold-safe step bound: this dual twin is HIT-ONLY
                    // (normals at an accepted sample), so it neither steps nor publishes sdfMapStepBound.
                    float logRadius = log(max(length(localPosition), SDF_LOGSPHERE_MIN_RADIUS));
                    float shell = round(logRadius * data0.z);
                    float shellScale = exp(shell * data0.x);
                    float spinAngle = (shell * data0.y);
                    float spinCos = cos(spinAngle);
                    float spinSin = sin(spinAngle);
                    float invShell = (1.0 / shellScale);

                    // A = (1/shellScale) * Rz(spin) — the shell is locally constant (round), so this is the exact linear
                    // part; the shellScale factor rides distanceScale below and cancels the 1/shellScale here under norm.
                    jx = float3(((spinCos * jx.x) - (spinSin * jx.y)), ((spinSin * jx.x) + (spinCos * jx.y)), jx.z) * invShell;
                    jy = float3(((spinCos * jy.x) - (spinSin * jy.y)), ((spinSin * jy.x) + (spinCos * jy.y)), jy.z) * invShell;
                    jz = float3(((spinCos * jz.x) - (spinSin * jz.y)), ((spinSin * jz.x) + (spinCos * jz.y)), jz.z) * invShell;

                    localPosition /= shellScale;
                    localPosition.xy = float2(
                        ((spinCos * localPosition.x) - (spinSin * localPosition.y)),
                        ((spinSin * localPosition.x) + (spinCos * localPosition.y)));
                    distanceScale *= shellScale;
                    break;
                }
#endif
#ifdef SDF_DYNAMIC_TRANSFORMS
                case SDF_OP_TRANSFORM_DYNAMIC: {
                    uint dynamicSlot = (uint)data0.x;
                    float4 dynamicPosition = sdfDynamicTransforms[(3u * dynamicSlot)];
                    float4 dynamicOrientation = sdfDynamicTransforms[((3u * dynamicSlot) + 1u)];
                    localPosition = rotatePointByInverseQuaternion((localPosition - dynamicPosition.xyz), dynamicOrientation);
                    jx = rotatePointByInverseQuaternion(jx, dynamicOrientation);
                    jy = rotatePointByInverseQuaternion(jy, dynamicOrientation);
                    jz = rotatePointByInverseQuaternion(jz, dynamicOrientation);
                    currentLanes = sdfDynamicTransforms[((3u * dynamicSlot) + 2u)];
                    currentSlot = (int)dynamicSlot;
                    break;
                }
#endif
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_REPEAT: {
                    localPosition -= (data0.xyz * round(localPosition * data1.xyz));   // A = I (round is locally constant)
                    break;
                }
#endif
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_REPEAT_LIMITED: {
                    localPosition -= (data0.xyz * clamp(round(localPosition / data0.xyz), -data1.xyz, data1.xyz));
                    break;
                }
#endif
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_CELL_JITTER: {
                    float3 cell = round(localPosition * data1.xyz);
                    uint3 seed = uint3(SDF_INSTRUCTION_SHAPE(instructionHeader), (SDF_INSTRUCTION_SHAPE(instructionHeader) * SDF_HASH_STREAM_A), (SDF_INSTRUCTION_SHAPE(instructionHeader) * SDF_HASH_STREAM_B));
                    uint3 key = (asuint(int3(cell)) ^ seed);
                    uint3 h0 = sdfPcg3d(key);

                    uint noiseFlavor = SDF_INSTRUCTION_BLEND(instructionHeader);
                    float3 r0;
                    if (noiseFlavor == SDF_NOISE_BLUE) {
                        uint3 uc = (asuint(int3(cell)) + seed);
                        uint bx = ((uc.x * SDF_R3_ALPHA1) + (uc.y * SDF_R3_ALPHA2) + (uc.z * SDF_R3_ALPHA3));
                        uint by = ((uc.x * SDF_R3_ALPHA2) + (uc.y * SDF_R3_ALPHA3) + (uc.z * SDF_R3_ALPHA1));
                        uint bz = ((uc.x * SDF_R3_ALPHA3) + (uc.y * SDF_R3_ALPHA1) + (uc.z * SDF_R3_ALPHA2));
                        r0 = (float3(bx, by, bz) * SDF_INV_2POW32);
                    } else if (noiseFlavor == SDF_NOISE_GAUSSIAN) {
                        uint3 g1 = sdfPcg3d(key ^ SDF_HASH_STREAM_A);
                        uint3 g2 = sdfPcg3d(key ^ SDF_HASH_STREAM_B);
                        r0 = (((float3)h0 + (float3)g1 + (float3)g2) * (SDF_INV_2POW32 / 3.0));
                    } else {
                        r0 = ((float3)h0 * SDF_INV_2POW32);
                    }

                    localPosition -= (data0.xyz * cell);          // A = I (fold + constant jitter)
                    localPosition -= ((r0 - 0.5) * data0.w);

                    // Tumble is a per-cell isometry: apply the same rotation to the Jacobian columns.
                    if (data1.w > 0.0) {
                        uint3 h1 = sdfPcg3d(key ^ SDF_HASH_TUMBLE);
                        float3 r1 = ((float3)h1 * SDF_INV_2POW32);
                        float zz = ((2.0 * r1.x) - 1.0);
                        float rr = sqrt(max(0.0, (1.0 - (zz * zz))));
                        float phi = (SDF_TAU * r1.y);
                        float3 axis = float3((rr * cos(phi)), (rr * sin(phi)), zz);
                        float angle = ((r1.z * data1.w) * SDF_PI);
                        float ha = (0.5 * angle);
                        float sa = sin(ha);
                        float4 q = float4((axis * sa), cos(ha));
                        localPosition = rotatePointByInverseQuaternion(localPosition, q);
                        jx = rotatePointByInverseQuaternion(jx, q);
                        jy = rotatePointByInverseQuaternion(jy, q);
                        jz = rotatePointByInverseQuaternion(jz, q);
                    }

                    break;
                }
#endif
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_REPEAT_POLAR: {
                    uint polarAxis = SDF_INSTRUCTION_SHAPE(instructionHeader);
                    float2 pv;
                    if (polarAxis == SDF_AXIS_X) { pv = localPosition.yz; }
                    else if (polarAxis == SDF_AXIS_Z) { pv = localPosition.xy; }
                    else { pv = localPosition.xz; }

                    float sectorAngle = data0.x;
                    float a = (atan2(pv.y, pv.x) + (0.5 * sectorAngle));
                    float r = length(pv);
                    float sector = floor(a * data0.y);
                    a = ((a - (sectorAngle * sector)) - (0.5 * sectorAngle));

                    // The local linear map = rotation by -sectorAngle*sector, then an optional v-flip (the mirror). rc/rs
                    // = cos/sin(sectorAngle*sector); the flip fires where the rotated v (== r*sin(a)) is negative.
                    float rc = cos(sectorAngle * sector);
                    float rs = sin(sectorAngle * sector);
                    float flip = (((SDF_INSTRUCTION_BLEND(instructionHeader) != 0u) && (a < 0.0)) ? -1.0 : 1.0);
                    jx = sdfApplyPolarJacobian(jx, polarAxis, rc, rs, flip);
                    jy = sdfApplyPolarJacobian(jy, polarAxis, rc, rs, flip);
                    jz = sdfApplyPolarJacobian(jz, polarAxis, rc, rs, flip);

                    if (SDF_INSTRUCTION_BLEND(instructionHeader) != 0u) { a = abs(a); }
                    pv = (float2(cos(a), sin(a)) * r);

                    if (polarAxis == SDF_AXIS_X) { localPosition.yz = pv; }
                    else if (polarAxis == SDF_AXIS_Z) { localPosition.xy = pv; }
                    else { localPosition.xz = pv; }

                    break;
                }
#endif
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_DOMAIN_WARP: {
                    // A = I + amp * M, M a scaled cyclic permutation (each axis driven by the next); rows below.
                    float cx = cos(data0.x * localPosition.y);
                    float cy = cos(data0.y * localPosition.z);
                    float cz = cos(data0.z * localPosition.x);
                    float3 ax = float3(1.0, (data0.w * data0.x * cx), 0.0);
                    float3 ay = float3(0.0, 1.0, (data0.w * data0.y * cy));
                    float3 az = float3((data0.w * data0.z * cz), 0.0, 1.0);
                    sdfApplyJacobian(ax, ay, az, jx, jy, jz);

                    localPosition += (data0.w * float3(
                        sin(data0.x * localPosition.y),
                        sin(data0.y * localPosition.z),
                        sin(data0.z * localPosition.x)));
                    break;
                }
#endif
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_SYMMETRY_PLANE: {
                    float spT = (dot(localPosition, data0.xyz) + data0.w);
                    // Reflection A = I - 2 n n^T, applied only on the negative side (spT < 0) — the same half-space fold.
                    float reflect = ((spT < 0.0) ? 2.0 : 0.0);
                    jx -= ((reflect * dot(jx, data0.xyz)) * data0.xyz);
                    jy -= ((reflect * dot(jy, data0.xyz)) * data0.xyz);
                    jz -= ((reflect * dot(jz, data0.xyz)) * data0.xyz);
                    localPosition -= ((2.0 * min(spT, 0.0)) * data0.xyz);
                    break;
                }
#endif
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_ROTATE_PLANE: {
                    uint u = (SDF_INSTRUCTION_SHAPE(instructionHeader) == SDF_PLANE_YZ) ? SDF_AXIS_Y : SDF_AXIS_X;
                    uint v = (SDF_INSTRUCTION_SHAPE(instructionHeader) == SDF_PLANE_XY) ? SDF_AXIS_Y : SDF_AXIS_Z;
                    uint driver = SDF_INSTRUCTION_BLEND(instructionHeader);
                    float angle = data0.x * (localPosition[driver] - data0.y);
                    float c = cos(angle), sn = sin(angle);
                    float pu = localPosition[u], pv = localPosition[v];
                    float nu = c * pu + sn * pv, nv = -sn * pu + c * pv;
                    float3 rows[3] = { float3(1,0,0), float3(0,1,0), float3(0,0,1) };
                    rows[u] = float3(0,0,0); rows[v] = float3(0,0,0);
                    rows[u][u] = c; rows[u][v] = sn;
                    rows[v][u] = -sn; rows[v][v] = c;
                    rows[u][driver] += data0.x * nv;
                    rows[v][driver] -= data0.x * nu;
                    sdfApplyJacobian(rows[0], rows[1], rows[2], jx, jy, jz);
                    localPosition[u] = nu; localPosition[v] = nv;
                    break;
                }
#endif
                // KEEP-IN-SYNC with mapCore's SDF_OP_AXIAL_PROFILE case. A = diag(1/s, 1, 1/s) plus a rank-1 shear from
                // ds/dy (moving along y rescales x and z): d(x')/dy = -x*(ds/dy)/s^2, d(z')/dy = -z*(ds/dy)/s^2. The
                // shear is zero on the clamp plateau (t == 0 or t == 1) and wherever the floor is active (s pinned
                // constant there, not truly varying) — both measure-zero in t but real wherever the floor clamps.
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_AXIAL_PROFILE: {
                    uint axis = SDF_INSTRUCTION_SHAPE(instructionHeader);
                    float rawT = (data0.z - localPosition[axis]) * data0.w;
                    float t = saturate(rawT);
                    float rawS = data1.y + data0.x * t + data0.y * sin(SDF_PI * t);
                    float scale = max(rawS, SDF_FLARE_MIN_SCALE);
                    float invScale = 1.0 / scale;
                    float dsdy = (rawT > 0.0 && rawT < 1.0 && rawS > SDF_FLARE_MIN_SCALE)
                        ? -data0.w * (data0.x + data0.y * SDF_PI * cos(SDF_PI * t)) : 0.0;
                    float3 rows[3] = { float3(1,0,0), float3(0,1,0), float3(0,0,1) };
                    [unroll] for (uint component = 0u; component < 3u; component++) {
                        if (component != axis) {
                            rows[component][component] = invScale;
                            rows[component][axis] = -localPosition[component] * dsdy * invScale * invScale;
                        }
                    }
                    sdfApplyJacobian(rows[0], rows[1], rows[2], jx, jy, jz);
                    [unroll] for (uint component = 0u; component < 3u; component++) {
                        if (component != axis) { localPosition[component] *= invScale; }
                    }
                    distanceScale *= data1.x;
                    break;
                }
#endif
                // KEEP IN SYNC with mapCore's SDF_OP_SHEAR case. A = [[1, linear + 2*quadratic*y, 0], [0,1,0], [0,0,1]]
                // (exact everywhere, not a local linearization — only x depends on y, and that dependence is exactly
                // this polynomial).
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_SHEAR: {
                    uint target = SDF_INSTRUCTION_SHAPE(instructionHeader), driver = SDF_INSTRUCTION_BLEND(instructionHeader);
                    float t = localPosition[driver];
                    float slope = data0.x + 2.0 * data0.y * t + 3.0 * data0.z * t * t;
                    float3 rows[3] = { float3(1,0,0), float3(0,1,0), float3(0,0,1) };
                    rows[target][driver] = slope;
                    sdfApplyJacobian(rows[0], rows[1], rows[2], jx, jy, jz);
                    localPosition[target] += ((data0.z * t + data0.y) * t + data0.x) * t;
                    break;
                }
#endif
                // KEEP IN SYNC with mapCore's SDF_OP_GAUSSIAN_PUSH case. A = I - Push (outer) gradG, gradG = -2*g*
                // (offset/Radii) (the Gaussian's world-space gradient); reads the TAIL instruction's Data0 one
                // instruction ahead, exactly as mapCore does.
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_GAUSSIAN_PUSH: {
                    float3 gaussianPush = float3(data0.w, data1.w, asfloat(SDF_INSTRUCTION_SHAPE(instructionHeader)));
                    float3 gaussianOffset = ((localPosition - data0.xyz) / data1.xyz);
                    float gaussianWeight = exp(-dot(gaussianOffset, gaussianOffset));
                    float3 gaussianGrad = ((-2.0 * gaussianWeight) * (gaussianOffset / data1.xyz));
                    float3 ax = (float3(1.0, 0.0, 0.0) - (gaussianPush.x * gaussianGrad));
                    float3 ay = (float3(0.0, 1.0, 0.0) - (gaussianPush.y * gaussianGrad));
                    float3 az = (float3(0.0, 0.0, 1.0) - (gaussianPush.z * gaussianGrad));
                    sdfApplyJacobian(ax, ay, az, jx, jy, jz);
                    localPosition -= (gaussianPush * gaussianWeight);
                    break;
                }
#endif
                // KEEP IN SYNC with mapCore's SDF_OP_LANE_ERODE case (same t/skip/candidate math). The noise term
                // contributes no gradient here (a documented approximation, like the exotic shapes' shape-local FD
                // gradient) — only the scalar distance is corrected; a hit-only hit hides the flat normal at the
                // noise's own frequency inside the analytic normal's usual tap-vs-analytic tolerance.
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_LANE_ERODE: {
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
                    // A = diag(indicator(|p_i| > h_i)): the interior of the swept region collapses onto the core (zero
                    // derivative there), the outside translates rigidly (unit derivative).
                    float3 a = step(data0.xyz, abs(localPosition));
                    jx *= a;
                    jy *= a;
                    jz *= a;
                    localPosition -= clamp(localPosition, -data0.xyz, data0.xyz);
                    break;
                }
#endif
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_ONION: {
                    float onionSign = ((result.distance < 0.0) ? -1.0 : 1.0);   // d -> |d| - t flips the gradient by sign(d)
                    result.distance = (abs(result.distance) - data0.x);
                    resultGradient *= onionSign;
                    break;
                }
#endif
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_DILATE: {
                    result.distance -= data0.x;   // gradient unchanged (a uniform outward offset)
                    break;
                }
#endif
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_DISPLACE: {
                    // gradient += amp * grad(sin*sin*sin) — analytic and exact (the FD-cancellation win), mapped to world.
                    // The scalar and dual evaluate the same continuous relief at every distance.
                    float3 df = (data0.xyz * localPosition);
                    float sx = sin(df.x);
                    float sy = sin(df.y);
                    float sz = sin(df.z);
                    result.distance += (data0.w * ((sx * sy) * sz));
                    float3 localGradProduct = (data0.w * float3(
                        ((data0.x * cos(df.x)) * (sy * sz)),
                        ((sx * data0.y * cos(df.y)) * sz),
                        ((sx * sy) * (data0.z * cos(df.z)))));
                    resultGradient += float3(dot(localGradProduct, jx), dot(localGradProduct, jy), dot(localGradProduct, jz));
                    break;
                }
#endif
#ifndef SDF_STRIP_HEAVY
                case SDF_OP_CELL_DISPLACE: {
                    float3 gradient;
                    float value = sdfCellDistanceGrad(localPosition * data0.x, SDF_INSTRUCTION_SHAPE(instructionHeader),
                        SDF_INSTRUCTION_BLEND(instructionHeader), data0.z, gradient);
                    result.distance += data0.y * (value - 0.5);
                    float3 localGradient = (data0.y * data0.x) * gradient;
                    resultGradient += float3(dot(localGradient, jx), dot(localGradient, jy), dot(localGradient, jz));
                    break;
                }
                case SDF_OP_NOISE_DISPLACE: {
                    // distance += amp*invNorm*fbm; gradient += the analytic octave-summed lattice gradient, mapped to
                    // world through the chain Jacobian columns (KEEP IN SYNC with mapCore's case above).
                    // KEEP IN SYNC with mapCore: no discontinuous far-band substitution.
                    float3 q = (localPosition * data0.x);
                    float octaveAmplitude = 1.0;
                    float octaveFrequency = data0.x;
                    float noiseSum = 0.0;
                    float3 noiseGradSum = float3(0.0, 0.0, 0.0);
                    uint octaveCount = SDF_INSTRUCTION_BLEND(instructionHeader);
                    for (uint octave = 0u; (octave < octaveCount); octave++) {
                        uint octaveSeed = (SDF_INSTRUCTION_SHAPE(instructionHeader) + octave);
                        float3 octaveGrad;
                        noiseSum += (octaveAmplitude * sdfValueNoise3Grad(q, uint3(octaveSeed, (octaveSeed * SDF_HASH_STREAM_A), (octaveSeed * SDF_HASH_STREAM_B)), octaveGrad));
                        noiseGradSum += ((octaveAmplitude * octaveFrequency) * octaveGrad);
                        q *= data0.w;
                        octaveFrequency *= data0.w;
                        octaveAmplitude *= data0.z;
                    }
                    float noiseScale = (data0.y * data1.x);
                    result.distance += (noiseScale * noiseSum);
                    float3 localGradProduct = (noiseScale * noiseGradSum);
                    resultGradient += float3(dot(localGradProduct, jx), dot(localGradProduct, jy), dot(localGradProduct, jz));
                    break;
                }
#endif
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_WALLPAPER_FOLD: {
                    uint group = SDF_INSTRUCTION_SHAPE(instructionHeader);
                    uint plane = SDF_INSTRUCTION_BLEND(instructionHeader);
                    int axisA = ((plane == SDF_PLANE_YZ) ? 1 : 0);
                    int axisB = ((plane == SDF_PLANE_XY) ? 1 : 2);
                    bool lodSimplify = ((data1.z > 0.0) && (distance(worldPosition, sdfLodOrigin) > data1.z));
                    float2 cellIndex;
                    float2 in2 = float2(localPosition[axisA], localPosition[axisB]);
                    float2 folded = sdfWallpaperFoldCell(in2, group, data0.xy, data0.zw, data1.xy, lodSimplify, cellIndex);

                    // The fold is a composed isometry with no single closed-form linear part; recover its in-plane 2x2 by
                    // a shape-local finite difference (both output columns), then transport the Jacobian columns through it.
                    float2 idxScratch;
                    float2 foldedA = sdfWallpaperFoldCell(in2 + float2(SDF_SHAPE_GRAD_EPSILON, 0.0), group, data0.xy, data0.zw, data1.xy, lodSimplify, idxScratch);
                    float2 foldedB = sdfWallpaperFoldCell(in2 + float2(0.0, SDF_SHAPE_GRAD_EPSILON), group, data0.xy, data0.zw, data1.xy, lodSimplify, idxScratch);
                    float2 col0 = ((foldedA - folded) * (1.0 / SDF_SHAPE_GRAD_EPSILON));
                    float2 col1 = ((foldedB - folded) * (1.0 / SDF_SHAPE_GRAD_EPSILON));
                    jx = sdfApplyPlaneJacobian(jx, axisA, axisB, col0, col1);
                    jy = sdfApplyPlaneJacobian(jy, axisA, axisB, col0, col1);
                    jz = sdfApplyPlaneJacobian(jz, axisA, axisB, col0, col1);

                    localPosition[axisA] = folded.x;
                    localPosition[axisB] = folded.y;
                    break;
                }
#endif
                case SDF_OP_SHAPE_BLEND: {
                    // KEEP IN SYNC with mapCore's SDF_OP_SHAPE_BLEND lane-erode/detail/secondary skips — the dual twin must
                    // agree on which shapes are visible, and a pending lane-erode effect must clear here exactly like
                    // mapCore's, whichever skip path (if any) consumes it.
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

                    uint4 shapeBoundMeta = sdfWords[boundsOffset + (SDF_BOUND_RECORD_VECTORS * index) + 1u];

                    [branch]
                    if (shapeBoundMeta.x != SDF_BOUND_NONE) {
                        float4 shapeBound = asfloat(sdfWords[boundsOffset + (SDF_BOUND_RECORD_VECTORS * index)]);
                        float3 shapeBoundCenter = shapeBound.xyz;
                        bool shapeBoundReady = (shapeBoundMeta.x == SDF_BOUND_STATIC);

#ifdef SDF_DYNAMIC_TRANSFORMS
                        if (shapeBoundMeta.x == SDF_BOUND_DYNAMIC) {
                            shapeBoundCenter += sdfDynamicTransforms[3u * shapeBoundMeta.y].xyz;
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

                    uint shapeType = (SDF_INSTRUCTION_SHAPE(instructionHeader) & SDF_SHAPE_TYPE_MASK);
                    int material = (int)SDF_INSTRUCTION_MATERIAL(instructionHeader);
                    float candidate = ((evaluateShape(shapeType, localPosition, data0, data1) * distanceScale) + laneErosion);
                    // The primitive's LOCAL gradient, mapped to world through the transform-chain Jacobian columns and
                    // scaled by the same distanceScale the candidate distance took (Scale/LogSphere's metric factor).
                    float3 localGrad = evaluateShapeGradient(shapeType, localPosition, data0, data1);
                    float3 worldGrad = (float3(dot(localGrad, jx), dot(localGrad, jy), dot(localGrad, jz)) * distanceScale);

                    composeCandidate = candidate;
                    composeGradient = worldGrad;
                    composeMaterial = material;
                    composeLanes = currentLanes;
                    composeInstance = segmentInstance;
                    composeSlot = currentSlot;
                    composeBlend = SDF_INSTRUCTION_BLEND(instructionHeader);
                    composeSmooth = data1.x;
                    composePending = true;
                    break;
                }
#ifndef SDF_STRIP_ALL_EXOTIC
                case SDF_OP_PUSH_FIELD: {
                    saved.distance = result.distance;
                    saved.material = result.material;
                    saved.lanes = result.lanes;
                    saved.instanceIndex = result.instanceIndex;
                    saved.frameSlot = result.frameSlot;
                    saved.gradient = resultGradient;
                    result.distance = SDF_FAR_DISTANCE;
                    result.material = 0;
                    result.lanes = float4(0.0, 0.0, 0.0, 0.0);
                    result.instanceIndex = -1;
                    result.frameSlot = SDF_TRANSFORM_SLOT_NONE;
                    resultGradient = float3(0.0, 0.0, 0.0);
                    break;
                }
                case SDF_OP_POP_FIELD: {
                    // data1.y = the scope's baked 1/L candidate scale on every pop; a stairs pop carries its step count
                    // in data1.z (KEEP IN SYNC with mapCore's pop and AnalyzeLipschitz).
                    composeBlend = SDF_INSTRUCTION_BLEND(instructionHeader);
                    bool isStairs = (composeBlend == SDF_BLEND_STAIRS_UNION || composeBlend == SDF_BLEND_STAIRS_SUBTRACTION);
                    float candidateScale = data1.y;
                    composeCandidate = result.distance;
                    composeGradient = resultGradient;
                    if (candidateScale > 0.0) {
                        composeCandidate *= candidateScale;
                        composeGradient *= candidateScale;
                    }
                    composeMaterial = result.material;
                    composeLanes = result.lanes;
                    composeInstance = result.instanceIndex;
                    composeSlot = result.frameSlot;
                    composeSmooth = data1.x;
                    result.distance = saved.distance;
                    result.material = saved.material;
                    result.lanes = saved.lanes;
                    result.instanceIndex = saved.instanceIndex;
                    result.frameSlot = saved.frameSlot;
                    resultGradient = saved.gradient;

                    if (composeBlend == SDF_BLEND_MORPH) {
                        float lane = ((data0.x < 0.5) ? currentLanes.x : ((data0.x < 1.5) ? currentLanes.y : ((data0.x < 2.5) ? currentLanes.z : currentLanes.w)));
                        float t = saturate((lane - data0.y) / (data0.z - data0.y));
                        result.distance = lerp(saved.distance, composeCandidate, t);
                        resultGradient = lerp(saved.gradient, composeGradient, t);
                        bool candidateWins = (t >= 0.5);
                        result.material = candidateWins ? composeMaterial : saved.material;
                        result.lanes = candidateWins ? composeLanes : saved.lanes;
                        result.instanceIndex = candidateWins ? composeInstance : saved.instanceIndex;
                        result.frameSlot = candidateWins ? composeSlot : saved.frameSlot;
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
                            float a = saved.distance;
                            float b = composeCandidate;
                            float u = isSub ? (-b - r) : (b - r);
                            float arg = u - a + s;
                            float m = arg - period * floor(arg / period);
                            float w = m - s;
                            float dStairs = 0.5 * (u + a + abs(w));
                            float3 gradStairs = (m >= s) ? (isSub ? (-composeGradient) : composeGradient) : saved.gradient;
                            if (isSub) {
                                float baseDist = max(a, -b);
                                float3 baseGrad = ((-b) > a) ? (-composeGradient) : saved.gradient;
                                if (-dStairs > baseDist) {
                                    result.distance = -dStairs;
                                    resultGradient = -gradStairs;
                                } else {
                                    result.distance = baseDist;
                                    resultGradient = baseGrad;
                                }
                                bool candidateWins = (-b > a);
                                result.material = candidateWins ? composeMaterial : saved.material;
                                result.lanes = candidateWins ? composeLanes : saved.lanes;
                                result.instanceIndex = candidateWins ? composeInstance : saved.instanceIndex;
                                result.frameSlot = candidateWins ? composeSlot : saved.frameSlot;
                            } else {
                                float baseDist = min(a, b);
                                float3 baseGrad = (b < a) ? composeGradient : saved.gradient;
                                if (dStairs < baseDist) {
                                    result.distance = dStairs;
                                    resultGradient = gradStairs;
                                } else {
                                    result.distance = baseDist;
                                    resultGradient = baseGrad;
                                }
                                bool candidateWins = (b < a);
                                result.material = candidateWins ? composeMaterial : saved.material;
                                result.lanes = candidateWins ? composeLanes : saved.lanes;
                                result.instanceIndex = candidateWins ? composeInstance : saved.instanceIndex;
                                result.frameSlot = candidateWins ? composeSlot : saved.frameSlot;
                            }
                            composePending = false;
                            break;
                        }
                    }

                    composePending = true;
                    break;
                }
#endif
                default: {
                    gradient = float3(0.0, 0.0, 1.0);
                    return sdfIsaErrorHit();
                }
            }

            if (composePending) {
                // The shared dual compose tail (also mapGradCore's rigid fast path). KEEP-IN-SYNC with mapCore's tail
                // EXCEPT the material blend channel: this dual twin is HIT-ONLY and resolves the NORMAL, not the shaded
                // albedo (the primary stage captures sdfMaterialBlendWeight from the scalar accept-sample march), so it neither
                // computes nor publishes the channel — exactly as it skips sdfMapStepBound for being hit-only.
                sdfComposeDualCandidate(result, resultGradient, composeCandidate, composeGradient, composeBlend, composeMaterial, composeLanes, composeInstance, composeSlot, composeSmooth);
            }
        }
    }

    result.distance *= stepScale;
    // The gradient is NOT scaled by stepScale: it is a uniform positive factor on the whole field, and the consumer
    // normalizes the gradient (which cancels it) — so applying it here would be undone anyway.
    gradient = resultGradient;

    return result;
}
// The per-tile MASKED dual entry (world render path, hit-only). Analytic surface gradient at `worldPosition` under the
// same tile instance mask the primary march used (self-consistent with the hit). Consumers normalize `gradient`.
SdfHit mapGradMasked(float3 worldPosition, uint instanceMaskBase, out float3 gradient) {
    return mapGradCore(worldPosition, instanceMaskBase, gradient);
}

#endif
