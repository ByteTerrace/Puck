// The LOD-switch march probe: runs a shipped march along one ray per case over the program the host packed into
// sdfWords, with no instance mask and no beam-proven gap, and writes where it stopped. SdfMarchLodDeviceLawTests packs
// each program with SdfProgram, binds it at the world interface's sdfWords and dispatches the probe once per case,
// pushing its index. The probe's own two resources sit in the Pass group at bindings the world interface leaves free;
// the pushed index is Direct3D 12's root constant at b0, space 4.
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-tile.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-vm.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-viewport.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-work.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-levers.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/march/sdf-march-constants.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/march/sdf-primary.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/march/sdf-cone.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/surface/sdf-ambient.hlsli"

// Four rows per case: the ray's origin and its far distance; its unit direction and the pixel footprint; the LOD origin
// and the march; then the march's own inputs (x: the primary march's start, the cone's near distance, or the
// shadow's reach; y: the cone's chord; z: the shadow's sharpness).
#define SDF_MARCH_LOD_PRIMARY 0
#define SDF_MARCH_LOD_CONE 1
#define SDF_MARCH_LOD_CONE_FAR 2
#define SDF_MARCH_LOD_SHADOW 3
[[vk::binding(60, 3)]] StructuredBuffer<float4> marchCases : register(t60, space3);
// One result per case. Primary: where the march stopped, whether it found a surface, its steps and its material's
// bits. Cone: the tile bounds' entry, first exit, second entry and far bound. Cone far: the far bound. Shadow: the
// visibility.
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> marchResults : register(u61, space3);
struct MarchProbeIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<MarchProbeIndex> marchProbeIndex : register(b0, space4);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint index = marchProbeIndex.index;
    float4 origin = marchCases[(4u * index)];
    float4 direction = marchCases[((4u * index) + 1u)];
    float4 lod = marchCases[((4u * index) + 2u)];
    float4 inputs = marchCases[((4u * index) + 3u)];
    int mode = (int)round(lod.w);
    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    sdfProgramLayout = sdfLoadProgramLayout();
    sdfLodOrigin = lod.xyz;

    if (mode == SDF_MARCH_LOD_PRIMARY) {
        SdfPrimaryMarch march = sdfTracePrimaryField(origin.xyz, direction.xyz, inputs.x, origin.w, origin.w, origin.w, origin.w,
            SDF_INSTANCE_MASK_ALL, direction.w, uint4(0u, 0u, 0u, 0u), false, false);

        result = float4(march.traveled, (march.found ? 1.0 : 0.0), (float)march.steps, asfloat(march.material));
    }
    else if ((mode == SDF_MARCH_LOD_CONE) || (mode == SDF_MARCH_LOD_CONE_FAR)) {
        // A camera at the ray's origin whose near plane is the march's start; the cone reads nothing else of it.
        ViewportData view = (ViewportData)0;
        view.position = float4(origin.xyz, 0.0);
        view.lens = float4(inputs.x, 0.0, 0.0, origin.w);
        TileCone cone;
        cone.centerDirection = direction.xyz;
        cone.chord = inputs.y;
        cone.inverseAperture = rsqrt(max((1.0 - (cone.chord * cone.chord)), 1.0e-6));

        TileBounds bounds = coneMarchTileBounds(view, cone, SDF_INSTANCE_MASK_ALL, direction.w);

        // The far bound is the last phase of the one cone march, so its case reads that bound alone.
        result = ((mode == SDF_MARCH_LOD_CONE)
            ? float4(bounds.entry, bounds.firstExit, bounds.secondEntry, bounds.farBound)
            : float4(bounds.farBound, 0.0, 0.0, 0.0));
    }
    else {
        // The ray leaves the surface along its normal, toward a light along the same direction.
        result.x = softShadowVisibilityMarch(origin.xyz, direction.xyz, direction.xyz, SDF_INSTANCE_MASK_ALL, 1.0, inputs.x, false, inputs.z);
    }

    marchResults[uint2(index, 0u)] = result;
}
