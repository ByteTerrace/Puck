// What an invocation's pass counts of its own work, which every kernel adds to its pass's row of the node's kernel
// counters once its stage returns (puckCountWork, generated into the sdf-world interface).
#ifndef FRAME_SDF_WORK_HLSLI
#define FRAME_SDF_WORK_HLSLI

// The field evaluations the pass makes, one for each sample of a march (the beam's, primary's, the occlusion's, the soft
// shadow's, a bounded volume's) and each query (a normal's taps). Never reset: it counts this pass alone, whatever
// sdfEvalCount carries over from the visibility record.
static uint sdfWorkSteps = 0u;
// Output image texels written, or one for a pixel whose visibility record changed (sdfVisibilityStoreWord).
// Separate image outputs count separately; the visibility record's words share one pixel count.
static uint sdfWorkTexels = 0u;
#endif
