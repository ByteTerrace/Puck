// puck.probe.v1 kernel: Color is the camera's converted color frame (t0, the trigger and the output extent); Lit and
// Unlit are the infrared strobe pair (t1 the lit frame, t2 the unlit frame kept before it). Their difference, scaled
// by gain, is the subject's illumination response: proportional to albedo * cos(theta) / distance^2 under the strobe,
// so it falls off with distance from the camera and is ~0 on the background. The response stands in for depth — a
// height field rising toward the camera as sqrt(response) — and its gradient gives the normals the light shades.
//
// Position space is frame-width units: x = u, y = v * height / width, z = height-field value; the light sits at
// z = height above the zero plane. Config order matches the manifest's declaration order (scalar packing).
//
// Channel sign conventions: x is -1 at the left edge and 1 at the right; y is -1 at the bottom edge and 1 at the
// top (the same y-up convention a stick axis carries), so a channel can feed another kind's anchor unchanged.

Texture2D<float4> Color : register(t0);
Texture2D<float4> Lit : register(t1);
Texture2D<float4> Unlit : register(t2);

cbuffer ProbeConfig : register(b0) {
    float anchorX;
    float anchorY;
    float lightHeight;
    float orbitRadius;
    float orbitSpeed;
    float intensity;
    float radius;
    float ambient;
    float tintR;
    float tintG;
    float tintB;
    float relief;
    float responseFloor;
    float gain;
    float irScale;
    float irOffsetX;
    float irOffsetY;
    float spriteSize;
};

cbuffer ProbeFrame : register(b1) {
    float time;
    float deltaTime;
    uint frame;
    uint pad;
};

RWStructuredBuffer<uint> Accumulate : register(u0);
RWStructuredBuffer<float> Channels : register(u1);
RWTexture2D<float4> Output : register(u2);

static const uint MaxAccumulateScale = 1024;
static const float Fresnel0 = 0.06;
static const float ShadowSteps = 6.0;
static const float ShadowStepLength = 0.02;

groupshared uint GroupResponse;
groupshared uint GroupCount;

uint AccumulateScale(uint width, uint height) {
    return min(MaxAccumulateScale, 0xFFFFFFFFu / max(width * height, 1u));
}

float SampleResponse(int2 p, int2 size) {
    p = clamp(p, int2(0, 0), size - 1);

    float lit = Lit.Load(int3(p, 0)).r;
    float unlit = Unlit.Load(int3(p, 0)).r;

    return saturate((lit - unlit) * gain);
}

// Bilinear response at a color-frame uv, through the infrared-to-color alignment: the two frames are assumed to
// share their vertical field of view about a common center (square pixels on both), so the infrared frame's
// horizontal span over the color frame follows from the two aspect ratios; irScale and the offsets tune the rest.
// 0 outside the infrared frame.
float Response(float2 uv) {
    uint width;
    uint height;
    uint colorWidth;
    uint colorHeight;

    Lit.GetDimensions(width, height);
    Color.GetDimensions(colorWidth, colorHeight);

    float2 fov = float2((float(colorWidth) / float(colorHeight)) / (float(width) / float(height)), 1.0);
    float2 irUv = ((uv - 0.5) * fov * irScale) + 0.5 + float2(irOffsetX, irOffsetY);

    if (any(irUv < 0.0) || any(irUv > 1.0)) {
        return 0.0;
    }

    int2 size = int2(width, height);
    float2 p = (irUv * float2(size)) - 0.5;
    int2 p0 = int2(floor(p));
    float2 f = p - float2(p0);
    float r00 = SampleResponse(p0, size);
    float r10 = SampleResponse(p0 + int2(1, 0), size);
    float r01 = SampleResponse(p0 + int2(0, 1), size);
    float r11 = SampleResponse(p0 + int2(1, 1), size);

    return lerp(lerp(r00, r10, f.x), lerp(r01, r11, f.x), f.y);
}

float Height(float2 uv) {
    return relief * sqrt(Response(uv));
}

// Where the light is this cycle, in position space: an orbit about the anchor with a slow bob.
float3 LightPosition(float aspect) {
    float2 anchor = float2((anchorX * 0.5) + 0.5, (0.5 - (anchorY * 0.5)) * aspect);
    float angle = time * orbitSpeed;
    float2 orbit = orbitRadius * float2(cos(angle), 0.6 * sin(angle));
    float bob = 0.015 * sin(time * 3.1);

    return float3(anchor + orbit + float2(0.0, bob), lightHeight);
}

[numthreads(8, 8, 1)]
void accumulate(uint3 dispatchId : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex) {
    uint width;
    uint height;

    Color.GetDimensions(width, height);

    if (groupIndex == 0) {
        GroupResponse = 0;
        GroupCount = 0;
    }

    GroupMemoryBarrierWithGroupSync();

    if ((dispatchId.x < width) && (dispatchId.y < height)) {
        float aspect = float(height) / float(width);
        float2 uv = (float2(dispatchId.xy) + 0.5) / float2(width, height);
        float scale = float(AccumulateScale(width, height));

        uint irWidth;
        uint irHeight;

        Lit.GetDimensions(irWidth, irHeight);

        // The gradient step spans ~1.5 infrared texels in color-frame uv, so the normals read the strobe's own
        // resolution rather than the color frame's.
        float2 step = 1.5 / (float2(irWidth, irHeight) * irScale);
        float response = Response(uv);
        float h = relief * sqrt(response);
        float dx = (Height(uv + float2(step.x, 0.0)) - Height(uv - float2(step.x, 0.0))) / (2.0 * step.x);
        float dy = (Height(uv + float2(0.0, step.y)) - Height(uv - float2(0.0, step.y))) / (2.0 * step.y * aspect);
        float3 normal = normalize(float3(-dx, -dy, 1.0));

        float3 position = float3(uv.x, uv.y * aspect, h);
        float3 light = LightPosition(aspect);
        float3 toLight = light - position;
        float distance = max(length(toLight), 0.0001);
        float3 l = toLight / distance;
        float falloff = 1.0 / (1.0 + ((distance / radius) * (distance / radius)));

        float wrapped = saturate((dot(normal, l) + 0.25) / 1.25);
        float lambert = wrapped * wrapped;
        float3 view = float3(0.0, 0.0, 1.0);
        float3 halfway = normalize(l + view);
        float fresnel = Fresnel0 + ((1.0 - Fresnel0) * pow(1.0 - saturate(dot(normal, view)), 5.0));
        float highlight = pow(saturate(dot(normal, halfway)), 36.0) * fresnel;

        // A short march up the height field toward the light; every sample that rises above the ray darkens.
        float shadow = 1.0;
        float3 ray = position;

        for (float i = 1.0; i <= ShadowSteps; i += 1.0) {
            ray += l * ShadowStepLength;

            float2 rayUv = float2(ray.x, ray.y / aspect);

            if (Height(rayUv) > (ray.z + 0.004)) {
                shadow *= 0.72;
            }
        }

        // Height-field occlusion: a pixel below its neighbours' mean sits in a crease.
        float neighbours = (Height(uv + float2(step.x, 0.0)) + Height(uv - float2(step.x, 0.0)) + Height(uv + float2(0.0, step.y)) + Height(uv - float2(0.0, step.y))) * 0.25;
        float occlusion = 1.0 - saturate((neighbours - h) * 6.0);

        // Background (no strobe response) is a flat wall at z = 0: ambient plus a faint spill of the light.
        float mask = smoothstep(responseFloor, responseFloor * 2.5, response);
        float flatFalloff = 1.0 / (1.0 + ((length(light - float3(uv.x, uv.y * aspect, 0.0)) / radius) * (length(light - float3(uv.x, uv.y * aspect, 0.0)) / radius)));
        float subjectLight = lambert * falloff * shadow * occlusion;
        float wallLight = 0.3 * flatFalloff;
        float diffuse = lerp(wallLight, subjectLight, mask);
        float specular = highlight * falloff * shadow * mask;

        float3 albedo = Color.Load(int3(int(dispatchId.x), int(dispatchId.y), 0)).rgb;
        float3 tint = float3(tintR, tintG, tintB);
        float3 shaded = (albedo * ambient) + (albedo * tint * diffuse * intensity) + (tint * specular * intensity * 0.6);

        // The sprite: a white core inside a tinted halo, twinkling, drawn where the light hangs in the frame.
        float sprite = length(position.xy - light.xy);
        float twinkle = 0.85 + (0.15 * sin(time * 11.0));
        float core = exp(-(sprite * sprite) / (spriteSize * spriteSize * 0.25));
        float halo = exp(-(sprite * sprite) / (spriteSize * spriteSize * 9.0));

        shaded += (tint * halo * 0.7 * twinkle) + (core * 1.6);

        Output[dispatchId.xy] = float4(saturate(shaded), 1.0);

        if (response > responseFloor) {
            InterlockedAdd(GroupResponse, uint(round(response * scale)));
            InterlockedAdd(GroupCount, 1);
        }
    }

    GroupMemoryBarrierWithGroupSync();

    if (groupIndex == 0) {
        InterlockedAdd(Accumulate[0], GroupResponse);
        InterlockedAdd(Accumulate[1], GroupCount);
    }
}

[numthreads(1, 1, 1)]
void finalize(uint3 dispatchId : SV_DispatchThreadID) {
    uint width;
    uint height;

    Color.GetDimensions(width, height);

    float aspect = float(height) / float(width);
    float scale = float(AccumulateScale(width, height));
    float pixelCount = (float(width) * float(height));
    float countAbove = float(Accumulate[1]);
    float3 light = LightPosition(aspect);
    float coverage = saturate(countAbove / pixelCount);

    Channels[0] = clamp((light.x * 2.0) - 1.0, -1.0, 1.0);
    Channels[1] = clamp(1.0 - ((light.y / aspect) * 2.0), -1.0, 1.0);
    Channels[2] = ((countAbove > 0.0) ? saturate((Accumulate[0] / scale) / countAbove) : 0.0);
    Channels[3] = coverage;
    Channels[4] = saturate(coverage / 0.02);
}
