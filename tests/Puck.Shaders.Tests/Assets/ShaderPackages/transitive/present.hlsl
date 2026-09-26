// The fullscreen finish. Its includes sit below the first line, so only an include walk that reads every line
// reaches tone.hlsli.
#include "lib/common.hlsli"
#include "lib/tone.hlsli"
#include "present.interface.hlsli"

float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 {
    return Tone(field.Sample(fieldSampler, uv) * Brighten(1.0));
}
