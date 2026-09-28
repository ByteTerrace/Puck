#ifndef SDF_VISIBILITY_CURRENT_HLSLI
#define SDF_VISIBILITY_CURRENT_HLSLI
// Whether `pixel`'s visibility record belongs to this frame. The hit passes share one indirect dispatch box, and primary
// writes a record for every active pixel inside it, misses included, so a record is current exactly inside the box.
// Outside it a record is whatever an earlier frame left, and every tile there is one the beam proved empty this frame.
bool worldVisibilityCurrent(uint2 pixel) {
    uint2 boxOrigin = (uint2(cullBounds[0], cullBounds[1]) * 8u);
    uint2 boxEnd = (uint2(cullBounds[2], cullBounds[3]) * 8u);
    return (all(pixel >= boxOrigin) && all(pixel < boxEnd));
}
#endif
