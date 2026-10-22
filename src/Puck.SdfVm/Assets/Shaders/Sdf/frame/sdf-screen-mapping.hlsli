// The same packed screen mapping serves surface samples, sky panoramas and the shared lighting environment.
#ifndef FRAME_SDF_SCREEN_MAPPING_HLSLI
#define FRAME_SDF_SCREEN_MAPPING_HLSLI

// SdfWorldTables.SetScreenMapping writes the draw form of SourceMapping, indexed by screen slot. The warp maps a
// surface point to the glass, the image rows map it to the source, and crop/sampleClamp bound every image sample.
struct ScreenMappingData {
    float4 warpU;       // xyz = the warped u's coefficients of u, v and 1; w = the face distance one warped unit spans
    float4 warpV;       // xyz = the warped v's coefficients of u, v and 1; w = the face distance one warped unit spans
    float4 imageU;      // xyz = the source u's coefficients of the warped u, v and 1; w = 1 when the screen is mapped
    float4 imageV;      // xyz = the source v's coefficients of the warped u, v and 1; w = 1 when the fit letterboxes
    float4 crop;        // the crop: left, top, right, bottom
    float4 sampleClamp; // the crop inset by half a source pixel: left, top, right, bottom
    float4 state;       // x = bound source, y = sampler (SDF_FILTER_*), z = emits light, w = drawn through the screen glass (0 for an opening)
};
// A screen's record is its ScreenMappingData rows, then its ScreenRippleData rows. KEEP IN SYNC with
// SdfWorldTables.ScreenMappingStride and SdfScreenRipple.Rows.
static const uint WorldScreenMappingDataRows = 7u;
static const uint WorldScreenRippleRows = 5u;
static const uint WorldScreenMappingRows = (WorldScreenMappingDataRows + WorldScreenRippleRows);
// SdfWorldTables.SetScreenRipple writes the opening's ripple, indexed by screen slot.
struct ScreenRippleData {
    float4 state;       // x = 1 when the opening ripples, y = the ripple clock in seconds, wrapped at RippleClockPeriod
    float4 contacts[4]; // per contact: xy = the surface UV it touched, z = its age in seconds, w = its strength (0 for none)
};
ScreenRippleData worldScreenRipple(uint screenIndex) {
    uint row = ((screenIndex * WorldScreenMappingRows) + WorldScreenMappingDataRows);
    ScreenRippleData data;
    data.state = screenMappings[row];
    data.contacts[0] = screenMappings[(row + 1u)];
    data.contacts[1] = screenMappings[(row + 2u)];
    data.contacts[2] = screenMappings[(row + 3u)];
    data.contacts[3] = screenMappings[(row + 4u)];
    return data;
}
ScreenMappingData worldScreenMapping(uint screenIndex) {
    uint row = (screenIndex * WorldScreenMappingRows);
    ScreenMappingData data;
    data.warpU = screenMappings[row];
    data.warpV = screenMappings[(row + 1u)];
    data.imageU = screenMappings[(row + 2u)];
    data.imageV = screenMappings[(row + 3u)];
    data.crop = screenMappings[(row + 4u)];
    data.sampleClamp = screenMappings[(row + 5u)];
    data.state = screenMappings[(row + 6u)];
    return data;
}
#endif
