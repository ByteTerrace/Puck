// The views stage's field reads, through the kernel's one interpreter call site.
#ifndef PASSES_SDF_VIEWS_FIELD_HLSLI
#define PASSES_SDF_VIEWS_FIELD_HLSLI
#ifdef SDF_VIEWS_PASS
#include "../debug/sdf-debug-reads.hlsli"

// A shaded mesh pixel's surface: a textured mesh's atlas texel, whose material the pixel shades with, or an impostor
// card's views. The field never resolves a mesh pixel's material.
struct SdfLightMesh {
    bool textured;
    bool impostor;
    SdfMeshTexel texel;
    SdfImpostorSurface impostorSurface;
};

SdfLightMesh sdfLightMeshAt(SdfPixel p, SdfSurfaceSample s, float3 surfacePoint, inout int material) {
    SdfLightMesh mesh = (SdfLightMesh)0;

    if (sdfMeshIsImpostor(s.meshDraw)) {
        mesh.impostor = true;
        mesh.impostorSurface = sdfImpostorSurfaceAt(s.meshDraw, p.rayOrigin, p.rayDirection, s.t, p.pixelFootprint);
    } else if (sdfMeshTextured(s.meshDraw)) {
        mesh.textured = true;
        mesh.texel = sdfMeshTexelAt(s.meshDraw, s.meshTriangle, surfacePoint, (p.pixelFootprint * s.t));
        material = sdfMeshTexelMaterial(s.meshDraw, mesh.texel);
    }

    return mesh;
}

// What the views stage's field reads found. A shaded field pixel's detail re-resolve (detailed): its material, lanes,
// dynamic frame and seam blend with the detail shapes included. The probes its shading material asks for (probed): the
// weathering's curvature taps and centre tap when the surface stage did not measure curvature, and the soften's
// wide-stencil taps. The debug views' first and second read.
struct SdfViewsFieldReads {
    bool detailed;
    int material;
    float4 lanes;
    int frameSlot;
    float blendWeight;
    int blendOther;
    bool weatheringCurvature;
    bool softens;
    bool centerTap;
    SdfFieldProbes probes;
    float firstRead;
    float secondRead;
};

// Every field read of the views stage, in one rolled loop around the kernel's one interpreter call site: each map call
// DXC sees inlines the whole interpreter, so the detail re-resolve, the shading probes and the debug reads are phases
// of this loop, never calls of their own. `shaded` is a hit the light stage shades with a material below the screen
// range, and `material` its material before any detail re-resolve (a textured mesh's texel material). The detail
// re-resolve, for a shaded field pixel whose program has detail shapes, runs first, since its material decides the
// probes; the probes follow in slot order (sdfProbeSlots), and the debug reads last. Each read is the evaluation its own
// loop took: the detail re-resolve tracks the material, the probes and debug reads the distance alone.
SdfViewsFieldReads sdfViewsFieldReads(SdfPixel p, SdfSurfaceSample s, bool shaded, float3 surfacePoint, int material, bool curvatureShading, SdfDebugSlice slice) {
    static const uint DetailPhase = 0u;
    static const uint ProbePhase = 1u;
    static const uint DebugPhase = 2u;
    SdfViewsFieldReads reads = (SdfViewsFieldReads)0;
    reads.material = material;
    reads.lanes = s.lanes;
    reads.frameSlot = s.frameSlot;
    reads.blendWeight = s.blendWeight;
    reads.blendOther = s.blendOther;
    uint phase = ((shaded && !s.mesh && !sdfProgramLayout.noDetailShapes) ? DetailPhase : ProbePhase);
    bool probesPlanned = false;
    uint slot = 0u;
    uint slotEnd = 0u;
    uint readCount = sdfDebugReadCount(p, slice);
    uint read = 0u;
    bool reading = false;
    SdfOvershootMarch march = (SdfOvershootMarch)0;

    [loop]
    for (;;) {
        float3 at = surfacePoint;
        uint mask = p.instanceMaskBase;
        bool trackMaterial = false;

        if (phase == ProbePhase) {
            if (!probesPlanned) {
                // The probes the shading material asks for, decided once the detail re-resolve has its material.
                probesPlanned = true;
                if (shaded) {
                    SdfMaterialData probed = sdfMaterialLoad(reads.material);
                    reads.weatheringCurvature = ((probed.weathering.x > 0.0) && !curvatureShading);
                    reads.softens = !(probed.soften <= 0.0);
                    if (reads.weatheringCurvature || reads.softens) {
                        reads.centerTap = (reads.weatheringCurvature && !sdfProgramLayout.noDetailShapes);
                        sdfProbeSlots(reads.weatheringCurvature, reads.centerTap, reads.softens, slot, slotEnd);
                    }
                }
            }
            [loop]
            while ((slot < slotEnd) && sdfProbeSlotSkipped(slot, reads.centerTap)) {
                slot++;
            }
            if (slot < slotEnd) {
                at = sdfProbeSlotPoint(surfacePoint, slot);
            } else {
                phase = DebugPhase;
            }
        }
        if (phase == DebugPhase) {
            bool sampling = false;

            [loop]
            while (!sampling && (read < readCount)) {
                if (!reading) {
                    march = sdfDebugReadBegin(p, slice, read);
                    reading = true;
                }
                sampling = sdfOvershootSample(march, at);
                if (!sampling) {
                    if (read == 0u) {
                        reads.firstRead = march.depth;
                    } else {
                        reads.secondRead = march.depth;
                    }
                    read++;
                    reading = false;
                }
            }
            if (!sampling) {
                break;
            }
            mask = march.instanceMaskBase;
        }
        if (phase == DetailPhase) {
            trackMaterial = true;
            sdfDetailShadingActive = true;
        }

        SdfHit hit = mapCore(at, mask, trackMaterial);

        if (phase == DetailPhase) {
            sdfEvalCount += 1.0;
            sdfWorkSteps += 1u;
            sdfDetailShadingActive = false;
            reads.detailed = true;
            reads.material = hit.material;
            reads.lanes = hit.lanes;
            reads.frameSlot = hit.frameSlot;
            reads.blendWeight = sdfMaterialBlendWeight;
            reads.blendOther = sdfMaterialBlendOther;
            phase = ProbePhase;
        } else if (phase == ProbePhase) {
            sdfProbeSlotTake(reads.probes, slot, hit.distance);
            slot++;
        } else {
            sdfOvershootTake(march, hit.distance);
        }
    }

    return reads;
}

#endif
#endif
