// The echo pass of shader interface 'ink-finish-perturbed' (sha256/9cb1935900485a0bc7f51814f98f72f655b79eebba276feb9ec4c659c5bccc4c), generated from the interface and perturbed by hand: its last member's first word expects the sentinel of the word after it.
// Pixel i of 'echo' is green when every word of the ith block member, in set order, reads back as the sentinel the host wrote there.
#include "ink-finish-perturbed.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy != 0)) {
        return;
    }
    echo[uint2(0, 0)] = ((asuint(frameGroup.pointer.x) == 0x400010A5u) && (asuint(frameGroup.pointer.y) == 0x400020A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(1, 0)] = ((asuint(frameGroup.tick.x) == 0x400030A5u) && (asuint(frameGroup.tick.y) == 0x400040A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(2, 0)] = ((asuint(frameGroup.time) == 0x400050A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(3, 0)] = ((asuint(frameGroup.timeDelta) == 0x400060A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(4, 0)] = ((asuint(frameGroup.frame) == 0x400070A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(5, 0)] = ((asuint(frameGroup.tickRate) == 0x400080A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(6, 0)] = ((asuint(frameGroup.pointerDown) == 0x400090A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(7, 0)] = ((asuint(frameGroup.pointerPresses) == 0x4000A0A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(8, 0)] = ((asuint(frameGroup.cameraPosition.x) == 0x4000D0A5u) && (asuint(frameGroup.cameraPosition.y) == 0x4000E0A5u) && (asuint(frameGroup.cameraPosition.z) == 0x4000F0A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(9, 0)] = ((asuint(frameGroup.cameraFov) == 0x400100A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(10, 0)] = ((asuint(frameGroup.cameraTarget.x) == 0x400110A5u) && (asuint(frameGroup.cameraTarget.y) == 0x400120A5u) && (asuint(frameGroup.cameraTarget.z) == 0x400130A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(11, 0)] = ((asuint(frameGroup.cameraUp.x) == 0x400150A5u) && (asuint(frameGroup.cameraUp.y) == 0x400160A5u) && (asuint(frameGroup.cameraUp.z) == 0x400170A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(12, 0)] = ((asuint(passGroup.extent.x) == 0x400023A5u) && (asuint(passGroup.extent.y) == 0x400023A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
}
