#ifndef SDF_VISIBILITY_CURRENT_HLSLI
#define SDF_VISIBILITY_CURRENT_HLSLI
#include "../isa/sdf-isa.hlsli"
// Whether `pixel`'s visibility record belongs to this frame. The hit passes share one indirect dispatch box, and primary
// writes a record for every active pixel inside it, misses included, so a record is current exactly inside the box.
// Outside it a record is whatever an earlier frame left, and every tile there is one the beam proved empty this frame.
// The rule is SdfVisibility.IsCurrent's, generated as SDF_VISIBILITY_CURRENT, which a pick applies to the box it reads back.
bool worldVisibilityCurrent(uint2 pixel) {
    return SDF_VISIBILITY_CURRENT(pixel, cullBounds);
}
#endif
