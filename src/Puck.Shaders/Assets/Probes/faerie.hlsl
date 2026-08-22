// puck.probe.v1 kernel: Color is the camera's converted color frame (t0, the trigger and the output extent); Lit and
// Unlit are the infrared strobe pair (t1 the lit frame, t2 the unlit frame kept before it); Painting is an optional
// fourth frame (t3) — bound when ((boundMask & 4u) != 0) — a tracked or authored rectangle shown as a wall painting.
// Lit-minus-unlit, scaled by gain, is the subject's illumination response: proportional to albedo * cos(theta) /
// distance^2 under the strobe, so it falls off with distance from the camera and is ~0 on the background. The
// response stands in for depth — a height field rising toward the camera as sqrt(response) — and its gradient gives
// the normals the light shades.
//
// Position space is frame-width units: x = u, y = v * height / width, z = height-field value; the light sits at
// z = height above the zero plane. Config order matches the manifest's declaration order (scalar packing; every
// field is a 4-byte float, so the cbuffer is simply sequential, padded to a 16-byte multiple).
//
// Channel and config-corner sign conventions: x is -1 at the left edge and 1 at the right; y is -1 at the bottom
// edge and 1 at the top (the same y-up convention a stick axis carries), so a channel can feed another kind's
// anchor unchanged. paintingX0..paintingY3 are frame coordinates in this same convention, in image corner order
// top-left, top-right, bottom-right, bottom-left — the order ir-marker's channels use, so its corners bind directly.

Texture2D<float4> Color : register(t0);
Texture2D<float4> Lit : register(t1);
Texture2D<float4> Unlit : register(t2);
Texture2D<float4> Painting : register(t3);

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
    float paintingX0;
    float paintingY0;
    float paintingX1;
    float paintingY1;
    float paintingX2;
    float paintingY2;
    float paintingX3;
    float paintingY3;
    float paintingOpacity;
    float journey;
    float portalThreshold;
};

cbuffer ProbeFrame : register(b1) {
    float time;
    float deltaTime;
    uint frame;
    uint boundMask;
};

RWStructuredBuffer<uint> Accumulate : register(u0);
RWStructuredBuffer<float> Channels : register(u1);
RWTexture2D<float4> Output : register(u2);

static const uint MaxAccumulateScale = 1024;
static const float Fresnel0 = 0.06;
static const float ShadowSteps = 6.0;
static const float ShadowStepLength = 0.02;
static const uint PaintingBoundBit = 4u;

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

// Converts a color-frame uv (v = 0 at the top row, D3D texture-load convention) to frame coordinates: x right-
// positive, y up-positive, both in [-1, 1] — the same convention the channels and the painting corners use.
float2 FrameCoordinates(float2 uv) {
    return float2((uv.x * 2.0) - 1.0, 1.0 - (uv.y * 2.0));
}

// The painting quad's centroid, as a color-frame uv (v = 0 at the top row) — the mean of its four frame-coordinate
// corners, converted through the same mapping FrameCoordinates inverts.
float2 PaintingCentreUv() {
    float2 frameCentre = (float2(paintingX0, paintingY0) + float2(paintingX1, paintingY1) + float2(paintingX2, paintingY2) + float2(paintingX3, paintingY3)) * 0.25;

    return float2((frameCentre.x + 1.0) * 0.5, (1.0 - frameCentre.y) * 0.5);
}

// Solves the projective (homography) inverse of the quad Q(u, v) -> frame coordinates whose corners are
// paintingX0..paintingY3 in top-left, top-right, bottom-right, bottom-left order: Heckbert's closed form for the
// forward unit-square-to-quadrilateral projective map gives eight coefficients from the four corners, and the
// inverse for one point is then an exact 2x2 linear solve (Cramer's rule) rather than the coarser two-triangle
// barycentric approximation. Returns false outside the quad, or when the quad is degenerate (near-zero
// determinant), in which case uv is left at a sentinel outside [0, 1].
bool TryPaintingUv(float2 frame, out float2 uv) {
    float2 p0 = float2(paintingX0, paintingY0);
    float2 p1 = float2(paintingX1, paintingY1);
    float2 p2 = float2(paintingX2, paintingY2);
    float2 p3 = float2(paintingX3, paintingY3);
    float2 d1 = p1 - p2;
    float2 d2 = p3 - p2;
    float2 d3 = p0 - p1 + p2 - p3;
    float denom = (d1.x * d2.y) - (d1.y * d2.x);
    float a13 = 0.0;
    float a23 = 0.0;

    if (abs(denom) > 1e-8) {
        a13 = ((d3.x * d2.y) - (d3.y * d2.x)) / denom;
        a23 = ((d1.x * d3.y) - (d1.y * d3.x)) / denom;
    }

    float a11 = p1.x - p0.x + (a13 * p1.x);
    float a21 = p3.x - p0.x + (a23 * p3.x);
    float a31 = p0.x;
    float a12 = p1.y - p0.y + (a13 * p1.y);
    float a22 = p3.y - p0.y + (a23 * p3.y);
    float a32 = p0.y;
    float A = a11 - (frame.x * a13);
    float B = a21 - (frame.x * a23);
    float C = frame.x - a31;
    float D = a12 - (frame.y * a13);
    float E = a22 - (frame.y * a23);
    float F = frame.y - a32;
    float det = (A * E) - (B * D);

    if (abs(det) < 1e-8) {
        uv = float2(-1.0, -1.0);

        return false;
    }

    uv = float2(((C * E) - (B * F)) / det, ((A * F) - (C * D)) / det);

    return (all(uv >= 0.0) && all(uv <= 1.0));
}

// Where the light is this cycle, in position space: an orbit about the anchor with a slow bob, lerped toward the
// painting quad's centre (on the zero plane, the canvas surface) as journey goes from 0 (orbiting) to 1 (sitting
// in the painting) — journey is config, so it composes whether or not the painting socket is bound.
float3 LightPosition(float aspect) {
    float2 anchor = float2((anchorX * 0.5) + 0.5, (0.5 - (anchorY * 0.5)) * aspect);
    float angle = time * orbitSpeed;
    float2 orbit = orbitRadius * float2(cos(angle), 0.6 * sin(angle));
    float bob = 0.015 * sin(time * 3.1);
    float3 orbitPosition = float3(anchor + orbit + float2(0.0, bob), lightHeight);
    float2 centreUv = PaintingCentreUv();
    float3 paintingPosition = float3(centreUv.x, centreUv.y * aspect, 0.0);

    return lerp(orbitPosition, paintingPosition, journey);
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

        // The painting: only where it is bound, only on background pixels (a subject in front occludes it), and
        // only inside the quad. It replaces the wall's crude falloff-only shading with a proper flat-canvas
        // Lambert term (normal (0, 0, 1), no relief) under the same light, and is composited under paintingOpacity.
        if (((boundMask & PaintingBoundBit) != 0) && (response < responseFloor)) {
            float2 paintingUv;

            if (TryPaintingUv(FrameCoordinates(uv), paintingUv)) {
                uint paintingWidth;
                uint paintingHeight;

                Painting.GetDimensions(paintingWidth, paintingHeight);

                int2 paintingTexel = clamp(int2(paintingUv * float2(paintingWidth, paintingHeight)), int2(0, 0), int2(paintingWidth, paintingHeight) - 1);
                float3 paintingColor = Painting.Load(int3(paintingTexel, 0)).rgb;

                albedo = lerp(albedo, paintingColor, paintingOpacity);

                float3 canvasPosition = float3(uv.x, uv.y * aspect, 0.0);
                float3 canvasToLight = light - canvasPosition;
                float canvasDistance = max(length(canvasToLight), 0.0001);
                float3 canvasL = canvasToLight / canvasDistance;
                float canvasFalloff = 1.0 / (1.0 + ((canvasDistance / radius) * (canvasDistance / radius)));

                diffuse = saturate(dot(float3(0.0, 0.0, 1.0), canvasL)) * canvasFalloff;
                specular = 0.0;
            }
        }

        float3 tint = float3(tintR, tintG, tintB);
        float3 shaded = (albedo * ambient) + (albedo * tint * diffuse * intensity) + (tint * specular * intensity * 0.6);

        // The sprite: a white core inside a tinted halo, twinkling, drawn where the light hangs in the frame, and
        // shrinking as journey carries it into the canvas.
        float sprite = length(position.xy - light.xy);
        float twinkle = 0.85 + (0.15 * sin(time * 11.0));
        float journeySize = spriteSize * (1.0 - (0.6 * journey));
        float core = exp(-(sprite * sprite) / (journeySize * journeySize * 0.25));
        float halo = exp(-(sprite * sprite) / (journeySize * journeySize * 9.0));

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
    float portal = ((journey >= portalThreshold) ? 1.0 : 0.0);

    Channels[0] = clamp((light.x * 2.0) - 1.0, -1.0, 1.0);
    Channels[1] = clamp(1.0 - ((light.y / aspect) * 2.0), -1.0, 1.0);
    Channels[2] = ((countAbove > 0.0) ? saturate((Accumulate[0] / scale) / countAbove) : 0.0);
    Channels[3] = coverage;
    Channels[4] = portal;
    Channels[5] = saturate(coverage / 0.02);
}
