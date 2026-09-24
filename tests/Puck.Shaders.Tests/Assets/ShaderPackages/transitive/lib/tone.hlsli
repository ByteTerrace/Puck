#ifndef FIXTURE_TONE_HLSLI
#define FIXTURE_TONE_HLSLI

float4 Tone(float4 color) {
    return float4(saturate(color.rgb), 1.0);
}

#endif
