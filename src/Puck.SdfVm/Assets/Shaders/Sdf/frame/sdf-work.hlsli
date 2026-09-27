// A pass's own work, counted into its node's work counters (sdfWorkCountersRW) at the row its pass block names
// (passGroup.workCounterRow). A row is the pass's march steps, then its texels written, each a 64-bit count in two
// words, low word first. KEEP IN SYNC with GpuKernelCounters (Puck.Abstractions), which reads the rows back.
#ifndef FRAME_SDF_WORK_HLSLI
#define FRAME_SDF_WORK_HLSLI

static const uint SdfWorkRowWords = 4u;
static const uint SdfWorkStepsWord = 0u;
static const uint SdfWorkTexelsWord = 2u;

// The field evaluations this invocation's pass makes, march samples and queries alike, which the pass counts once its
// stage returns. Never reset: it counts this pass alone, whatever sdfEvalCount carries over from the visibility record.
static uint sdfWorkSteps = 0u;

// Adds to one 64-bit count: the low word atomically, then the high word by one when that addition carries.
void sdfWorkAdd(uint word, uint amount) {
    if (amount == 0u) {
        return;
    }

    uint before;

    InterlockedAdd(sdfWorkCountersRW[word], amount, before);

    if (before > (0xFFFFFFFFu - amount)) {
        InterlockedAdd(sdfWorkCountersRW[word + 1u], 1u);
    }
}

// Counts this invocation's march steps (sdfWorkSteps) and the texels it wrote into its pass's row: the wave sums both,
// and its first active lane adds each sum, so a pass pays one atomic a wave per count. Every lane that did work must
// reach the call, since a lane that returned before it counts nothing.
void sdfCountWork(uint texels) {
    uint steps = WaveActiveSum(sdfWorkSteps);
    uint written = WaveActiveSum(texels);

    if (WaveIsFirstLane()) {
        uint row = (passGroup.workCounterRow * SdfWorkRowWords);

        sdfWorkAdd((row + SdfWorkStepsWord), steps);
        sdfWorkAdd((row + SdfWorkTexelsWord), written);
    }
}
#endif
