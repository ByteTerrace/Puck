// The comparison receiver: the indirect receiver with the screen-space and cone comparison methods
// (indirect/sdf-indirect-alternatives.hlsli) in place of the near-field sample, which only the cache method admits.
// It is built and leased once a view first selects a comparison method (world.indirect-method screen|cone), so the
// default receiver carries none of their field and shadow queries.
#define SDF_INDIRECT_COMPARISON
#include "sdf-world-receiver.comp.hlsl"
