// The blends and the material winner that compose each shape's candidate into the running field.
#ifndef FIELD_SDF_BLEND_HLSLI
#define FIELD_SDF_BLEND_HLSLI
// Each blend's arithmetic, smooth minimum and radius floor included, is a shared formula generated with its fixed-point
// twin (Puck.SignedDistance's SdfFieldFormulas).
#include "../isa/sdf-field-blend-formulas.hlsli"
// operand is a scope pop's own lane (a stairs pop's step count, a morph pop's weight), zero at a shape's join.
float blendShape(float current, float candidate, uint blendOp, float smoothRadius, float operand) {
    float result;

    switch (blendOp) {
#include "../isa/sdf-field-blends.hlsli"
        default: return asfloat(0x7fc00000u);
    }

    return result;
}

// The scalar interpreter and the host-compiled rigid-leaf path meet here. Keeping the material winner and distance
// compose in one helper prevents the fast path from becoming a subtly different VM. DXC sees trackMaterial as a
// literal at every call and erases this whole material half for the bake's gradient taps.
void sdfComposeCandidate(inout SdfHit result, float candidate, uint blend, int material, int instanceIndex, int frameSlot, float smooth, float operand, bool trackMaterial) {
    if (trackMaterial) {
        bool candidateWins;
        // A cut or an intersection shapes the surface it wins on, which still belongs to the shape it shapes: the mesh
        // bake's vertex there rides that shape's slot rather than the cutter's (SdfMesh.Members). Only a union-family
        // winner hands its slot on.
        bool handsSlot = true;

        switch (blend) {
#include "../isa/sdf-field-material.hlsli"
            default: result.distance = asfloat(0x7fc00000u); return;
        }

        if (candidateWins) {
            result.material = material;
            result.instanceIndex = instanceIndex;
            result.frameSlot = (handsSlot ? frameSlot : result.frameSlot);
        }
    }

    result.distance = blendShape(result.distance, candidate, blend, smooth, operand);
}

#endif
