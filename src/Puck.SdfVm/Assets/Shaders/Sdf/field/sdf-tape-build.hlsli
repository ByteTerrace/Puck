#ifndef FIELD_SDF_TAPE_BUILD_HLSLI
#define FIELD_SDF_TAPE_BUILD_HLSLI
#ifdef SDF_TAPE_BUILD
uint sdfTapeMaskedInstructions(uint maskBase) {
    uint count = 0u;
    uint segmentCount = sdfProgramLayout.segmentCount;
    if (!sdfProgramLayout.hasInstances) { return SDF_PROGRAM_INSTRUCTION_COUNT(sdfWords[0]); }
    uint word = 0xFFFFFFFFu;
    uint bits = 0u;
    uint first, end, instance;
    [loop]
    for (;;) {
        sdfNextVisibleInstanceRange(maskBase, sdfProgramLayout.instanceOffset, sdfProgramLayout.instanceCount,
            word, bits, first, end, instance);
        if (first == SDF_SEGMENT_NONE) { break; }
        uint4 start = sdfWords[sdfProgramLayout.segmentOffset + SDF_DIRECTORY_HEADER_VECTORS + SDF_BOUND_RECORD_VECTORS * first + 1u];
        uint4 last = sdfWords[sdfProgramLayout.segmentOffset + SDF_DIRECTORY_HEADER_VECTORS + SDF_BOUND_RECORD_VECTORS * (end - 1u) + 1u];
        count += last.w - start.z;
        if (count >= SDF_TAPE_INSTRUCTION_THRESHOLD) { return count; }
    }
    uint worldCount = SDF_WORLD_SEGMENT_COUNT(sdfWords[sdfProgramLayout.worldSegmentOffset]);
    [loop]
    for (uint world = 0u; world < worldCount; world++) {
        uint segment = sdfWords[sdfProgramLayout.worldSegmentOffset + SDF_DIRECTORY_HEADER_VECTORS + world].x;
        uint4 meta = sdfWords[sdfProgramLayout.segmentOffset + SDF_DIRECTORY_HEADER_VECTORS + SDF_BOUND_RECORD_VECTORS * segment + 1u];
        count += meta.w - meta.z;
    }
    return count;
}

// A tile's tape build in pieces, so a kernel that also reads the field elsewhere runs each slab's evaluation through its
// one interpreter call site: sdfTapeBuildBegin, then per slab sdfTapeSlabCentre, mapCore(centre, maskBase, false) and
// sdfTapeSlabEnd, then sdfTapeBuildEnd. sdfBuildTileTape is that sequence for a kernel that only builds.
bool sdfTapeBuildBegin(uint tileIndex, uint maskBase, float entry, float farBound) {
    sdfTapeBase = tileIndex * sdfTapeStride();
    sdfTapeActive = false;
    sdfSegmentTapesRW[sdfTapeBase] = 0u;
    if (entry < 0.0 || !isfinite(entry) || !isfinite(farBound) || farBound < entry ||
        SDF_SEGMENT_TAPE_OFFSET(sdfWords[sdfProgramLayout.segmentOffset]) == 0u ||
        sdfTapeMaskedInstructions(maskBase) < SDF_TAPE_INSTRUCTION_THRESHOLD) { return false; }
    sdfTapeBuilding = true;
    return true;
}
float3 sdfTapeSlabCentre(uint slab, float3 origin, float3 direction, float chord, float entry, float farBound) {
    sdfTapeSlab = slab;
    uint mask = sdfTapeMaskBase();
    [loop]
    for (uint word = 0u; word < sdfTapeSlabWords(); word++) { sdfSegmentTapesRW[mask + word] = 0u; }
    float nearFraction = float(slab) / float(SDF_TAPE_SLAB_COUNT);
    float farFraction = float(slab + 1u) / float(SDF_TAPE_SLAB_COUNT);
    // The beam has already approached the first surface; concentrate precision at that entry.
    nearFraction *= nearFraction;
    farFraction *= farFraction;
    float near = lerp(entry, farBound, nearFraction * nearFraction);
    float far = lerp(entry, farBound, farFraction * farFraction);
    float middle = 0.5 * (near + far);
    float3 center = origin + direction * middle;
    float magnitude = max(max(abs(center.x), abs(center.y)), abs(center.z));
    sdfTapeCentreMagnitude = magnitude;
    // For unit directions u,d, |t*u-middle*d|^2 = (t-middle)^2 + t*middle*|u-d|^2.
    // This convex quadratic reaches its maximum at far. It avoids adding axial and radial extents as lengths.
    float guard = sdfTapeMulUp(max(max(abs(origin.x), abs(origin.y)), abs(origin.z)) + abs(far) + 1.0, SDF_TAPE_BLEND_ROUNDOFF_MARGIN);
    float halfDepth = sdfTapeUp(0.5 * (far - near));
    float radiusSquared = sdfTapeAddUp(sdfTapeMulUp(halfDepth, halfDepth),
        sdfTapeMulUp(sdfTapeMulUp(middle, far), sdfTapeMulUp(chord, chord)));
    sdfTapeRadius = sdfTapeAddUp(sdfTapeUp(sqrt(radiusSquared)), guard);
    sdfTapeMagnitude = sdfTapeAddUp(magnitude, sdfTapeRadius);
    uint ball = sdfTapeBase + 1u + 4u * slab;
    sdfSegmentTapesRW[ball] = asuint(center.x);
    sdfSegmentTapesRW[ball + 1u] = asuint(center.y);
    sdfSegmentTapesRW[ball + 2u] = asuint(center.z);
    sdfSegmentTapesRW[ball + 3u] = asuint(sdfTapeRadius);
    sdfTapeInterval = float2(SDF_FAR_DISTANCE, SDF_FAR_DISTANCE);
    sdfTapeFieldKnown = true;
    sdfTapeScopeDepth = 0u;
    sdfTapeUnionStart = 0u;
    return center;
}
void sdfTapeSlabEnd() {
    uint mask = sdfTapeMaskBase();
    uint words = sdfTapeMaskWords();
    [loop]
    for (uint word = 0u; word < words; word++) {
        if (sdfSegmentTapesRW[mask + word] != 0u) { sdfSegmentTapesRW[mask + words + (word >> 5u)] |= 1u << (word & 31u); }
    }
}
void sdfTapeBuildEnd() {
    sdfTapeBuilding = false;
    sdfSegmentTapesRW[sdfTapeBase] = 1u;
}
void sdfBuildTileTape(uint tileIndex, uint maskBase, float3 origin, float3 direction, float chord, float entry, float farBound) {
    if (!sdfTapeBuildBegin(tileIndex, maskBase, entry, farBound)) { return; }
    [loop]
    for (uint slab = 0u; slab < SDF_TAPE_SLAB_COUNT; slab++) {
        mapCore(sdfTapeSlabCentre(slab, origin, direction, chord, entry, farBound), maskBase, false);
        sdfTapeSlabEnd();
    }
    sdfTapeBuildEnd();
}
#endif
#endif
