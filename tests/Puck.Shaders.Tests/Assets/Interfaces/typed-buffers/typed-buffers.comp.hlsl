// A pass reading typed and raw buffers and a pushed index through its generated pass interface.
// typed-buffers.interface.hlsli is generated from the interface ShaderInterfaceSpike declares and written beside this
// file at test time; it is never checked in. The frame group carries the tick and the element count, the pass group a
// structured buffer of a 4-byte and of a 16-byte element, a raw buffer, a structured and a raw buffer it writes, and an
// array read through its generated accessor; the pushed index offsets every element the dispatch touches.
#include "typed-buffers.interface.hlsli"

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint element = (pushedIndex.index + id.x);

    if (element >= frameGroup.extent.x) {
        return;
    }

    float4 wideValue = wide[element];
    uint value = (narrow[element] + raw.Load(element * 4u) + frameGroup.tick + asuint(wideValue.x + wideValue.w) + offsetsAt(element % 4u));

    output[element] = uint2(value, rawOutput.Load(element * 4u));
    rawOutput.Store((element * 4u), value);
}
