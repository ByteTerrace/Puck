// History surfaces carry exact identity and unfiltered ray distance. A reset never reads these words.
#ifndef SDF_HISTORY_HLSLI
#define SDF_HISTORY_HLSLI
static const float SdfHistoryRelativeDepthTolerance = 0.02;
bool sdfHistoryAccept(uint2 previous, uint identity, float expectedDistance) {
    if (previous.y != identity) return false;
    if (identity == 0u) return true;
    float distance = asfloat(previous.x);
    return isfinite(distance) && abs(distance - expectedDistance) <=
        max(0.002, expectedDistance * SdfHistoryRelativeDepthTolerance);
}
#endif
