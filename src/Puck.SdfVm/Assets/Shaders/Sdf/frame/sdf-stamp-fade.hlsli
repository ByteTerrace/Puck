// A view-local screen-door fade, before either opaque depth or glass sums are written. All layers of the stamp
// use the same pixel mask, so holes reveal the room rather than another layer of the avatar. Shadow passes do not
// call this; mirrors and portals construct their own view snapshots with no faded slot range.
void sdfStampFade(uint draw, uint2 pixel) {
    float3 fade = passGroup.stampFade;
    int slot = asint(sdfMeshRegion[(sdfMeshRecord(draw) + SDF_MESH_FRAME_SLOT_WORD)]);
    if ((fade.y <= 0.0) || (slot < int(fade.x)) || (slot >= int(fade.x + fade.y)) || (fade.z >= 1.0)) {
        return;
    }
    uint low = ((((pixel.x ^ pixel.y) & 1u) * 2u) + (pixel.y & 1u));
    uint high = (((((pixel.x ^ pixel.y) >> 1u) & 1u) * 2u) + ((pixel.y >> 1u) & 1u));
    float threshold = ((float((4u * low) + high) + 0.5) / 16.0);
    clip(fade.z - threshold);
}
