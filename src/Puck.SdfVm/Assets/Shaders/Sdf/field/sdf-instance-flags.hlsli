// The instance table's bounds and shadow participation flags.
#ifndef FIELD_SDF_INSTANCE_FLAGS_HLSLI
#define FIELD_SDF_INSTANCE_FLAGS_HLSLI
// Instance `index`'s bound within the directory at `instanceOffset` (the caller resolves
// sdfInstanceDirectoryOffset() ONCE and passes it down — the beam prepass hoists it out of its per-instance cull
// loop): center (STATIC) or pre-dynamic offset (DYNAMIC, resolved against sdfDynamicTransforms — requires
// SDF_DYNAMIC_TRANSFORMS) and radius. Mirrors the per-segment/per-shape bound resolve in mapCore.
float4 sdfInstanceBoundAt(uint instanceOffset, uint index) {
    uint entryBase = sdfInstanceEntryOffset(instanceOffset, index);
    float4 bound = asfloat(sdfWords[entryBase]);
    uint4 meta = sdfWords[entryBase + 1u];

#ifdef SDF_DYNAMIC_TRANSFORMS
    if (meta.x == SDF_BOUND_DYNAMIC) {
        bound.xyz += sdfDynamicTransforms[3u * meta.y].xyz;
    }
#endif

    return bound;
}

// Whether instance `index` is SHADOW-TRANSPARENT — a pure Subtraction-family carve host-classified as never OCCLUDING
// (see SDF_INSTANCE_SHADOW_TRANSPARENT_BIT). sdfShadowGather reads this under the sdf.shadow-proxy lever to OMIT the
// instance from the soft-shadow occluder set, so the shadow ray marches the pre-carve union hull instead of the carved
// solid. The bit rides i1.w's high bit; mapCore masks it off, so this is the ONLY consumer that observes it.
bool sdfInstanceShadowTransparent(uint instanceOffset, uint index) {
    uint entryBase = sdfInstanceEntryOffset(instanceOffset, index);

    return (0u != (sdfWords[entryBase + 1u].w & SDF_INSTANCE_SHADOW_TRANSPARENT_BIT));
}

#ifdef SDF_DYNAMIC_TRANSFORMS
// Whether DYNAMIC instance `index` is SHADOW-SUPPRESSED this frame — the host packed its per-frame soft-shadow
// participation into the dynamic transform's position.w (0 = casts, 1 = suppressed; see PackDynamicTransforms). Read by
// sdfShadowGather to keep a suppressed instance out of the local shadow mask (the cheaper-mask twin of the enumeration
// skip sdfShadowParticipationActive drives in sdfNextVisibleInstanceRange). Static instances (no dynamic slot) are never
// suppressed — they always cast. Inherently shadow-scoped: the gather runs only to build the soft-shadow occluder set,
// so no participation flag gates this (unlike the enumeration skip, which the camera/AO marches share).
bool sdfInstanceShadowSuppressed(uint instanceOffset, uint index) {
    uint entryBase = sdfInstanceEntryOffset(instanceOffset, index);
    uint4 meta = sdfWords[entryBase + 1u];

    return ((meta.x == SDF_BOUND_DYNAMIC) && (sdfDynamicTransforms[3u * meta.y].w > 0.5));
}
#endif

#endif
