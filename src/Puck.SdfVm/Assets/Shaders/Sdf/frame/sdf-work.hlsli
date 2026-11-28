// What an invocation's pass counts of its own work, which every kernel adds to its pass's row of the node's kernel
// counters once its stage returns (puckCountWork, generated into the sdf-world interface).
#ifndef FRAME_SDF_WORK_HLSLI
#define FRAME_SDF_WORK_HLSLI

// The spatial steps the pass marches, one for each bake sample and gradient tap, each bounded volume sample and each
// reflection ray step. Never reset: it counts this pass alone.
static uint sdfWorkSteps = 0u;
// The values an authored shade program executed for this invocation (the interpreter's instructions and a specialized
// program's generated values), which the views and glass passes add to GpuWork.ShadeOperations (puckCountShading).
static uint sdfWorkShadeOperations = 0u;
// The reads of the cached environment this invocation made (sdfSkyPhysicalIrradiance, sdfSkyMapRadiance), which
// puckCountShading adds to GpuWork.EnvironmentLookups.
static uint sdfWorkEnvironmentLookups = 0u;
// Outputs written for this pixel: the visibility record or the output texel counts once. Zero means this invocation
// wrote no output.
static uint sdfWorkTexels = 0u;
// The lights this invocation's shading walks visited (sdfLightNext answering a light), which the views and glass passes
// add to GpuWork.LightsVisited (puckCountLights).
static uint sdfWorkLights = 0u;
#endif
