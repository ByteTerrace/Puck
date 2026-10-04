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
    float4 state;       // x = 1 while a source is bound this frame, y = the sampler (an SDF_FILTER_* value), zw = 0
};
static const uint WorldScreenMappingRows = 7u;
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
