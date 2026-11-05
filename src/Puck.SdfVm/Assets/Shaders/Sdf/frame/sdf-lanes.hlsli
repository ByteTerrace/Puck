// The render lanes a shading-time consumer reads (SdfLanes): lanes below SDF_FIRST_WORLD_LANE are the shaded surface's
// own, its frame slot's lanes (zero for a surface with no slot), and the rest the world's, the pass block's worldLanes
// (SdfWorldPackage.WorldLanes), which the host resolves from render.lanes at the presented tick. sdfLane is the one
// reader of worldLanes, so a world lane enters the kernels by one door. Bake-time consumers (an erosion, a morph) read
// the riding slot's lanes in mapCore and never come here.
#ifndef SDF_LANES_HLSLI
#define SDF_LANES_HLSLI

#include "../isa/sdf-render.hlsli"

// A float4's component chosen by comparison, never a dynamic index (kernels.md: index local arrays by constants only).
float sdfLaneComponent(float4 lanes, uint component) {
    return ((component == 0u) ? lanes.x : ((component == 1u) ? lanes.y : ((component == 2u) ? lanes.z : lanes.w)));
}
// The value of render lane `lane` in [0, SDF_LANE_COUNT); `slotLanes` are the surface's frame-slot lanes.
float sdfLane(float4 slotLanes, uint lane) {
    return ((lane < SDF_FIRST_WORLD_LANE)
        ? sdfLaneComponent(slotLanes, lane)
        : sdfLaneComponent(passGroup.worldLanes, (lane - SDF_FIRST_WORLD_LANE)));
}

#endif
