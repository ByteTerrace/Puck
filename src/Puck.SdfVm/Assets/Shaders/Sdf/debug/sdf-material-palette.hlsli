// The material-id debug view's categorical colors.
#ifndef DEBUG_SDF_MATERIAL_PALETTE_HLSLI
#define DEBUG_SDF_MATERIAL_PALETTE_HLSLI
float3 materialPalette(int material) {
    float hue = frac(float(material) * 0.61803399);
    float3 ramp = (abs((frac(hue + float3(0.0, 0.33333333, 0.66666667)) * 6.0) - 3.0) - 1.0);

    return saturate(ramp);
}
#endif
