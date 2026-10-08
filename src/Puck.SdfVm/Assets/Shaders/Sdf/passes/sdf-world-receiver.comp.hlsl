// The indirect receiver, between shadow and views: each lit pixel's receiver proof, its certificate in the visibility
// record, and the near-field or comparison replacement of its incoming light, which views reads from the receiver buffer.
// It compiles the full instruction set once; views keeps no field query of the cache.
#define SDF_RECEIVER_PASS
#include "sdf-world-views.comp.hlsl"
