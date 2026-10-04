// The echo pass of shader interface 'sdf-world-perturbed' (sha256/3c54069c4d3acacf11fc36e5e658cc6f3f5751ed0e41729c584b9b92c2adcfe4), generated from the interface and perturbed by hand: its last member's first word expects the sentinel of the word after it.
// Pixel i of 'echo' is green when every word of the ith block member, in set order, reads back as the sentinel the host wrote there.
#include "sdf-world-perturbed.interface.hlsli"

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
    echo[uint2(12, 0)] = ((asuint(frameGroup.placedExtent.x) == 0x400190A5u) && (asuint(frameGroup.placedExtent.y) == 0x4001A0A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(13, 0)] = ((asuint(passGroup.extent.x) == 0x400013A5u) && (asuint(passGroup.extent.y) == 0x400023A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(14, 0)] = ((asuint(passGroup.aspectRatio) == 0x400033A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(15, 0)] = ((asuint(passGroup.cameraTileShadowMask) == 0x400043A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(16, 0)] = ((asuint(passGroup.curvatureCavity) == 0x400053A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(17, 0)] = ((asuint(passGroup.curvatureInk) == 0x400063A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(18, 0)] = ((asuint(passGroup.curvatureInkColor.x) == 0x400093A5u) && (asuint(passGroup.curvatureInkColor.y) == 0x4000A3A5u) && (asuint(passGroup.curvatureInkColor.z) == 0x4000B3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(19, 0)] = ((asuint(passGroup.curvatureInkHigh) == 0x4000C3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(20, 0)] = ((asuint(passGroup.curvatureInkLow) == 0x4000D3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(21, 0)] = ((asuint(passGroup.curvatureRim) == 0x4000E3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(22, 0)] = ((asuint(passGroup.debugMode) == 0x4000F3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(23, 0)] = ((asuint(passGroup.debugSliceAxis) == 0x400103A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(24, 0)] = ((asuint(passGroup.debugSliceOffset) == 0x400113A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(25, 0)] = ((asuint(passGroup.disableAmbientOcclusion) == 0x400123A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(26, 0)] = ((asuint(passGroup.disableFarBound) == 0x400133A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(27, 0)] = ((asuint(passGroup.disableScreenLights) == 0x400143A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(28, 0)] = ((asuint(passGroup.disableShadowCull) == 0x400153A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(29, 0)] = ((asuint(passGroup.disableSoftShadows) == 0x400163A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(30, 0)] = ((asuint(passGroup.enableShadowProxy) == 0x400173A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(31, 0)] = ((asuint(passGroup.farDistance) == 0x400183A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(32, 0)] = ((asuint(passGroup.fastAmbientOcclusion) == 0x400193A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(33, 0)] = ((asuint(passGroup.fastSoftShadowMarch) == 0x4001A3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(34, 0)] = ((asuint(passGroup.finiteDifferenceNormals) == 0x4001B3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(35, 0)] = ((asuint(passGroup.frustumOffset.x) == 0x4001D3A5u) && (asuint(passGroup.frustumOffset.y) == 0x4001E3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(36, 0)] = ((asuint(passGroup.gridFlags) == 0x4001F3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(37, 0)] = ((asuint(passGroup.gridLineWidth) == 0x400203A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(38, 0)] = ((asuint(passGroup.gridObjectFrame.x) == 0x400213A5u) && (asuint(passGroup.gridObjectFrame.y) == 0x400223A5u) && (asuint(passGroup.gridObjectFrame.z) == 0x400233A5u) && (asuint(passGroup.gridObjectFrame.w) == 0x400243A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(39, 0)] = ((asuint(passGroup.gridObjectOrigin.x) == 0x400253A5u) && (asuint(passGroup.gridObjectOrigin.y) == 0x400263A5u) && (asuint(passGroup.gridObjectOrigin.z) == 0x400273A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(40, 0)] = ((asuint(passGroup.gridObjectPatchRadius) == 0x400283A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(41, 0)] = ((asuint(passGroup.gridObjectPitch.x) == 0x400293A5u) && (asuint(passGroup.gridObjectPitch.y) == 0x4002A3A5u) && (asuint(passGroup.gridObjectPitch.z) == 0x4002B3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(42, 0)] = ((asuint(passGroup.gridPlaneY) == 0x4002C3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(43, 0)] = ((asuint(passGroup.gridWorldFrame.x) == 0x4002D3A5u) && (asuint(passGroup.gridWorldFrame.y) == 0x4002E3A5u) && (asuint(passGroup.gridWorldFrame.z) == 0x4002F3A5u) && (asuint(passGroup.gridWorldFrame.w) == 0x400303A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(44, 0)] = ((asuint(passGroup.gridWorldOrigin.x) == 0x400313A5u) && (asuint(passGroup.gridWorldOrigin.y) == 0x400323A5u) && (asuint(passGroup.gridWorldOrigin.z) == 0x400333A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(45, 0)] = ((asuint(passGroup.gridWorldPitch.x) == 0x400353A5u) && (asuint(passGroup.gridWorldPitch.y) == 0x400363A5u) && (asuint(passGroup.gridWorldPitch.z) == 0x400373A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(46, 0)] = ((asuint(passGroup.historyFrames) == 0x400383A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(47, 0)] = ((asuint(passGroup.imageExtent.x) == 0x400393A5u) && (asuint(passGroup.imageExtent.y) == 0x4003A3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(48, 0)] = ((asuint(passGroup.instanceMaskWordCount) == 0x4003B3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(49, 0)] = ((asuint(passGroup.jitter.x) == 0x4003D3A5u) && (asuint(passGroup.jitter.y) == 0x4003E3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(50, 0)] = ((asuint(passGroup.lightCount) == 0x4003F3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(51, 0)] = ((asuint(passGroup.meshDraws) == 0x400403A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(52, 0)] = ((asuint(passGroup.nearDistance) == 0x400413A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(53, 0)] = ((asuint(passGroup.previousView[0].x) == 0x400453A5u) && (asuint(passGroup.previousView[0].y) == 0x400463A5u) && (asuint(passGroup.previousView[0].z) == 0x400473A5u) && (asuint(passGroup.previousView[0].w) == 0x400483A5u) && (asuint(passGroup.previousView[1].x) == 0x400493A5u) && (asuint(passGroup.previousView[1].y) == 0x4004A3A5u) && (asuint(passGroup.previousView[1].z) == 0x4004B3A5u) && (asuint(passGroup.previousView[1].w) == 0x4004C3A5u) && (asuint(passGroup.previousView[2].x) == 0x4004D3A5u) && (asuint(passGroup.previousView[2].y) == 0x4004E3A5u) && (asuint(passGroup.previousView[2].z) == 0x4004F3A5u) && (asuint(passGroup.previousView[2].w) == 0x400503A5u) && (asuint(passGroup.previousView[3].x) == 0x400513A5u) && (asuint(passGroup.previousView[3].y) == 0x400523A5u) && (asuint(passGroup.previousView[3].z) == 0x400533A5u) && (asuint(passGroup.previousView[3].w) == 0x400543A5u) && (asuint(passGroup.previousView[4].x) == 0x400553A5u) && (asuint(passGroup.previousView[4].y) == 0x400563A5u) && (asuint(passGroup.previousView[4].z) == 0x400573A5u) && (asuint(passGroup.previousView[4].w) == 0x400583A5u) && (asuint(passGroup.previousView[5].x) == 0x400593A5u) && (asuint(passGroup.previousView[5].y) == 0x4005A3A5u) && (asuint(passGroup.previousView[5].z) == 0x4005B3A5u) && (asuint(passGroup.previousView[5].w) == 0x4005C3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(54, 0)] = ((asuint(passGroup.screenCount) == 0x4005D3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(55, 0)] = ((asuint(passGroup.shadowAmortize) == 0x4005E3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(56, 0)] = ((asuint(passGroup.shadowDistanceScale) == 0x4005F3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(57, 0)] = ((asuint(passGroup.shadowFadeCount) == 0x400603A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(58, 0)] = ((asuint(passGroup.shadowLightReject) == 0x400613A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(59, 0)] = ((asuint(passGroup.shadowOwnershipReject) == 0x400623A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(60, 0)] = ((asuint(passGroup.shadowSlotCount) == 0x400633A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(61, 0)] = ((asuint(passGroup.shadowSlots.x) == 0x400653A5u) && (asuint(passGroup.shadowSlots.y) == 0x400663A5u) && (asuint(passGroup.shadowSlots.z) == 0x400673A5u) && (asuint(passGroup.shadowSlots.w) == 0x400683A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(62, 0)] = ((asuint(passGroup.tanHalfFieldOfView) == 0x400693A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(63, 0)] = ((asuint(passGroup.temporal) == 0x4006A3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(64, 0)] = ((asuint(passGroup.tileGrid.x) == 0x4006B3A5u) && (asuint(passGroup.tileGrid.y) == 0x4006C3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(65, 0)] = ((asuint(passGroup.viewBase) == 0x4006D3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(66, 0)] = ((asuint(passGroup.viewForward.x) == 0x400713A5u) && (asuint(passGroup.viewForward.y) == 0x400723A5u) && (asuint(passGroup.viewForward.z) == 0x400733A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(67, 0)] = ((asuint(passGroup.viewPosition.x) == 0x400753A5u) && (asuint(passGroup.viewPosition.y) == 0x400763A5u) && (asuint(passGroup.viewPosition.z) == 0x400773A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(68, 0)] = ((asuint(passGroup.viewRight.x) == 0x400793A5u) && (asuint(passGroup.viewRight.y) == 0x4007A3A5u) && (asuint(passGroup.viewRight.z) == 0x4007B3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(69, 0)] = ((asuint(passGroup.viewUp.x) == 0x4007D3A5u) && (asuint(passGroup.viewUp.y) == 0x4007E3A5u) && (asuint(passGroup.viewUp.z) == 0x4007F3A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(70, 0)] = ((asuint(passGroup.viewportCount) == 0x400803A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(71, 0)] = ((asuint(passGroup.workCounterRow) == 0x400813A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
    echo[uint2(72, 0)] = ((asuint(passGroup.workCounterRowDetail) == 0x400833A5u)) ? float4(0.0, 1.0, 0.0, 1.0) : float4(1.0, 0.0, 0.0, 1.0);
}
