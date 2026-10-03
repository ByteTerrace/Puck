// The hit-stage dispatch domain and the probe diagnostic's scene occlusion limit.
#ifndef FRAME_SDF_VIEW_DOMAIN_HLSLI
#define FRAME_SDF_VIEW_DOMAIN_HLSLI

static const int DebugViewModeIndirectProbes = 13;
static const int DebugViewModeIndirectCells = 14;

uint4 sdfViewDispatchBox(uint4 surviving, uint2 tileGrid, uint meshDraws, int viewMode) {
    if ((meshDraws != 0u) || (viewMode == DebugViewModeIndirectProbes)) {
        return uint4(0u, 0u, tileGrid.x - 1u, tileGrid.y - 1u);
    }
    // With no surviving tile, one degenerate tile keeps the indirect dispatch valid.
    return surviving.x == 0xFFFFFFFFu ? uint4(0u, 0u, 0u, 0u) : surviving;
}

float sdfIndirectDebugMaximum(bool hit, float hitDistance, float farDistance) {
    return hit ? hitDistance : farDistance;
}

#endif
