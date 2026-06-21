// Demo cursor overlay (single-source HLSL; DXC compiles it to SPIR-V for Vulkan and DXIL for DirectX): sample the
// SDF render the producer made and blend, per controller, a colored cursor disc; plus, for the first controller,
// three independent needles (pitch / yaw / roll) driven by the fused IMU orientation so each axis reads
// unambiguously (a pure turn moves only the yaw needle). This lives in Puck.Demo so no cursor concept leaks into
// the reusable SDF engine.
// KEEP IN SYNC with Puck.Demo.CursorOverlayNode's push-constant packing.
//
// On Vulkan the texture+sampler fuse into one combined image sampler at set 0, binding 0; on DirectX they stay
// separate at t0/s0. One declaration, both bindings — same as the terminal blit.
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] Texture2D sourceTexture : register(t0);
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] SamplerState sourceSampler : register(s0);

#define MAX_CURSORS 4

// On Vulkan this is the push-constant block; on DirectX the root-constant cbuffer at b0. 144 bytes.
struct OverlayData {
    float4 header;                       // x = cursor count, y = disc radius, z = needle length, w = line width
    float4 cursors[MAX_CURSORS];         // xy = position 0..1 (origin top-left), z = asfloat(0x00RRGGBB), w unused
    float4 orientations[MAX_CURSORS];    // xyzw = fused orientation quaternion
};
[[vk::push_constant]] ConstantBuffer<OverlayData> overlay;

float3 unpackColor(float packed) {
    uint bits = asuint(packed);

    return (float3(((bits >> 16) & 0xFF), ((bits >> 8) & 0xFF), (bits & 0xFF)) / 255.0);
}

// Tait-Bryan angles (radians) from the orientation quaternion: x = pitch, y = yaw, z = roll.
float3 quatToEuler(float4 q) {
    float sinPitch = (2.0 * ((q.w * q.x) - (q.y * q.z)));
    float pitch = ((abs(sinPitch) >= 1.0) ? (sign(sinPitch) * 1.5707963) : asin(sinPitch));
    float yaw = atan2((2.0 * ((q.w * q.y) + (q.x * q.z))), (1.0 - (2.0 * ((q.x * q.x) + (q.y * q.y)))));
    float roll = atan2((2.0 * ((q.w * q.z) + (q.x * q.y))), (1.0 - (2.0 * ((q.x * q.x) + (q.z * q.z)))));

    return float3(pitch, yaw, roll);
}

float distanceToSegment(float2 p, float2 a, float2 b) {
    float2 ab = (b - a);
    float t = saturate(dot((p - a), ab) / max(dot(ab, ab), 1e-6));

    return length(p - (a + (t * ab)));
}

// A clock-style needle: angle 0 points up, increasing angle sweeps clockwise. The zero reference tick is tinted
// with the owning controller's color so each row's gauges are identifiable when several controllers are present.
float3 drawNeedle(float3 color, float2 pointA, float2 pivot, float angle, float3 needleColor, float3 referenceColor, float needleLength, float needleWidth) {
    float2 zeroTip = (pivot + (float2(0.0, -1.0) * needleLength));
    float reference = (1.0 - smoothstep(needleWidth, (needleWidth + 0.0025), distanceToSegment(pointA, pivot, zeroTip)));

    color = lerp(color, referenceColor, (reference * 0.55));

    float2 tip = (pivot + (float2(sin(angle), -cos(angle)) * needleLength));
    float needle = (1.0 - smoothstep(needleWidth, (needleWidth + 0.0025), distanceToSegment(pointA, pivot, tip)));
    float hub = (1.0 - smoothstep((needleWidth * 1.5), (needleWidth * 2.5), length(pointA - pivot)));

    color = lerp(color, needleColor, max(needle, hub));

    return color;
}

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    uint width;
    uint height;

    sourceTexture.GetDimensions(width, height);

    float2 uv = (fragCoord.xy / float2(width, height));
    float3 color = sourceTexture.Sample(sourceSampler, uv).rgb;

    float aspect = (float(width) / float(height));
    float2 pointA = float2((uv.x * aspect), uv.y);
    float radius = overlay.header.y;
    float needleLength = overlay.header.z;
    float lineWidth = overlay.header.w;
    float aa = (radius * 0.18);
    int count = (int)overlay.header.x;

    // Cursor discs: one per controller, a position marker at its screen position.
    for (int i = 0; i < count; ++i) {
        float3 cursorColor = unpackColor(overlay.cursors[i].z);
        float2 center = float2((overlay.cursors[i].xy.x * aspect), overlay.cursors[i].xy.y);
        float dist = length(pointA - center);
        float disc = (1.0 - smoothstep((radius - aa), (radius + aa), dist));
        float rim = ((1.0 - smoothstep((radius - aa), (radius + aa), dist))
                     * smoothstep((radius - (aa * 3.0)), (radius - aa), dist));

        color = lerp(color, cursorColor, (disc * 0.9));
        color = lerp(color, float3(0.0, 0.0, 0.0), (rim * 0.5));
    }

    // One row of three independent needles per controller — pitch (red), yaw (green), roll (blue) — with the zero
    // ticks tinted in that controller's color. A pure motion on one axis deflects only its needle.
    for (int g = 0; g < count; ++g) {
        float3 euler = quatToEuler(overlay.orientations[g]);
        float3 tint = unpackColor(overlay.cursors[g].z);
        float row = (0.60 + (float(g) * 0.17));

        color = drawNeedle(color, pointA, float2((0.35 * aspect), row), euler.x, float3(0.95, 0.30, 0.30), tint, needleLength, lineWidth); // pitch
        color = drawNeedle(color, pointA, float2((0.50 * aspect), row), euler.y, float3(0.35, 0.90, 0.40), tint, needleLength, lineWidth); // yaw
        color = drawNeedle(color, pointA, float2((0.65 * aspect), row), euler.z, float3(0.40, 0.60, 1.00), tint, needleLength, lineWidth); // roll
    }

    return float4(color, 1.0);
}
