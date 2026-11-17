// The scale every probe kernel gives a value in [0, 1] before its InterlockedAdd into the uint Accumulate buffer
// (RWStructuredBuffer<uint> has no float atomic). It derives from the frame's pixel count, so a sum over the frame stays
// at or under width * height * scale <= uint.MaxValue at any resolution.
static const uint MaxAccumulateScale = 1024;

uint AccumulateScale(uint width, uint height) {
    return min(MaxAccumulateScale, 0xFFFFFFFFu / max(width * height, 1u));
}
