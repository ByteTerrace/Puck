// What an invocation's pass counts of its own work, which every kernel adds to its pass's row of the node's kernel
// counters once its stage returns (puckCountWork, generated into the sdf-world interface).
#ifndef FRAME_SDF_WORK_HLSLI
#define FRAME_SDF_WORK_HLSLI

// The field evaluations the pass makes, one for each bake sample and gradient tap and each bounded volume sample.
// Never reset: it counts this pass alone.
static uint sdfWorkSteps = 0u;
// Outputs written for this pixel: the visibility record or the output texel counts once. Zero means this invocation
// wrote no output.
static uint sdfWorkTexels = 0u;
// The lights this invocation's shading walks visited (sdfLightNext answering a light), which the views and glass passes
// add to GpuWork.LightsVisited (puckCountLights).
static uint sdfWorkLights = 0u;
#endif
