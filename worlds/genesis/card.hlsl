// =============================================================================
// GENESIS - 3D Classical Playing Card Prototype
// Component Focus Mode: Isolated study of Letter 'Q' and Heart Symbol
// - Authentic Didone/Caslon 'Q' with thick-thin bowl and calligraphic swash tail
// - Voluptuous playing card Heart with top cleavage notch and tapering flanks
// - Toggle VIEW_ISOLATE_COMPONENTS (1 = Component Focus, 0 = Full Card)
// =============================================================================

#define VIEW_ISOLATE_COMPONENTS 0

// =============================================================================
// QUEEN RELIEF & POP-UP MODES:
// 0 = Full 3D Chibi Queen (Sculptural Bas-Relief Figurine)
// 1 = Flat Printed Card Mode (Flush 2.5D Embossed Playing Card Illustration)
// 2 = Animated Pop-Up Effect (Smooth dynamic pop-up spring bloom 0 -> 1)
// =============================================================================
#define POPUP_MODE 0

// =============================================================================
// CAMERA SHOT ANGLE PRESETS:
// 0 = Idle Floating Animation (Full dynamic tilt, bob & sway)
// 1 = Front Flat View (Zero tilt, head-on orthographic framing)
// 2 = Left Grazing Tilt (Reproduces terminator shading on left shoulder)
// 3 = Right Grazing Tilt (Reproduces terminator shading on right shoulder)
// 4 = Macro Center Sash (Close zoom on waist, sash and mantle corners)
// 5 = Macro Left Shoulder (Close zoom on left purple mantle corner)
// 6 = 3D Layer Profile (Steep 45-degree angle inspecting relief depth)
// =============================================================================
#define SHOT_ANGLE 1

// =============================================================================
// SDF DEBUG SYSTEM:
// 0 = Beauty Render (Full PBR Shading, Soft Shadows, AO)
// 1 = Step Count Heatmap (Raymarch iteration cost & under-stepping bottlenecks)
// 2 = Distance Field Isolines (Field metric, gradient smoothness & contours)
// 3 = Surface Normal Map (Surface orientation & curvature)
// 4 = Material ID False-Color Map (Material assignment & boundary segmentation)
// 5 = Coarse Bounding Box Wireframe Overlay (Visualizes coarse bounds clearance)
// 6 = Interactive Split Screen (Left: Beauty Render, Right: Step Count Heatmap)
// 7 = Direct Key Shadow Mask (Visualizes shadowKey term directly in greyscale)
// =============================================================================
#define DEBUG_MODE 0

#define PI 3.14159265359
#define MAX_STEPS 160
#define SURF_DIST 0.0006
#define MAX_DIST 20.0

// Material IDs
#define MAT_FLOOR         0.0
#define MAT_CARD_BODY     1.0
#define MAT_CORE_EDGE     2.0
#define MAT_GOLD_BORDER   3.0
#define MAT_COURT_FIELD   4.0
#define MAT_ROYAL_MANTLE  5.0
#define MAT_ROYAL_ACCENT  6.0
#define MAT_FACE_SKIN     7.0
#define MAT_FACE_HAIR     8.0
#define MAT_AETHER_GEM    9.0
#define MAT_PIP_GOLD      10.0
#define MAT_CARD_BACK     11.0
#define MAT_IVORY_ARMOR   12.0
#define MAT_PIP_HEART     13.0

// The frame block is the pass's generated interface: the frame values. This pass declares no config fields.
#include "card.interface.hlsli"

[[vk::binding(0, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> image : register(u0);

// The output image's extent in pixels, assigned once at the top of main.
static float2 resolution;

// Floored modulo: the result takes the sign of y, unlike fmod, which truncates.
float floorMod(float x, float y) {
    return x - y * floor(x / y);
}

// 2D Rotation (a row vector v rotates as mul(v, rot2D(a)))
float2x2 rot2D(float a) {
    float c = cos(a), s = sin(a);
    return float2x2(c, -s, s, c);
}

// 2D Rounded Box SDF
float sdRoundBox2D(float2 p, float2 b, float r) {
    float2 q = abs(p) - b + float2(r, r);
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
}

// 3D Rounded Box SDF
float sdRoundBox3D(float3 p, float3 b, float r) {
    float3 q = abs(p) - b + float3(r, r, r);
    return min(max(q.x, max(q.y, q.z)), 0.0) + length(max(q, 0.0)) - r;
}

// 2D Rhombus / Diamond SDF
float ndot(float2 a, float2 b) { return a.x*b.x - a.y*b.y; }
float sdRhombus2D(float2 p, float2 b) {
    float2 q = abs(p);
    float h = clamp((-2.0 * ndot(q, b) + ndot(b, b)) / dot(b, b), -1.0, 1.0);
    float d = length(q - 0.5 * b * float2(1.0 - h, 1.0 + h));
    return d * float(sign(q.x * b.y + q.y * b.x - b.x * b.y));
}

// Smooth Minimum (Polynomial)
float smin(float a, float b, float k) {
    float h = clamp(0.5 + 0.5 * (b - a) / k, 0.0, 1.0);
    return lerp(b, a, h) - k * h * (1.0 - h);
}

// Combine two distance-material pairs
float2 opUnion(float2 a, float2 b) {
    return (a.x < b.x) ? a : b;
}

// =============================================================================
// COMPONENT 1: AUTHENTIC LETTER 'Q' (Didone/Caslon Court Typography)
// =============================================================================

// 2D Capsule Segment SDF
float sdSegment2D(float2 p, float2 a, float2 b, float r) {
    float2 pa = p - a, ba = b - a;
    float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
    return length(pa - ba * h) - r;
}

// Inigo Quilez 2D Quadratic Bezier SDF with parameter t output
float2 sdBezierT(float2 pos, float2 A, float2 B, float2 C) {
    float2 a = B - A;
    float2 b = A - 2.0*B + C;
    float2 c = a * 2.0;
    float2 d = A - pos;
    float kk = 1.0 / max(dot(b,b), 0.00001);
    float kx = kk * dot(a,b);
    float ky = kk * (2.0*dot(a,a)+dot(d,b)) / 3.0;
    float kz = kk * dot(d,a);
    float res = 0.0;
    float p = ky - kx*kx;
    float p3 = p*p*p;
    float q = kx*(2.0*kx*kx - 3.0*ky) + kz;
    float h = q*q + 4.0*p3;
    float bestT = 0.0;
    if (h >= 0.0) {
        float h1 = sqrt(h);
        float2 x = (float2(h1, -h1) - q)*0.5;
        float2 uv = float2(sign(x))*pow(abs(x), float2(1.0/3.0, 1.0/3.0));
        bestT = clamp(uv.x + uv.y - kx, 0.0, 1.0);
        float2 posOnCurve = d + (c + b*bestT)*bestT;
        res = dot(posOnCurve, posOnCurve);
    } else {
        float z = sqrt(-p);
        float v = acos(clamp(q/(p*z*2.0), -1.0, 1.0)) / 3.0;
        float m = cos(v);
        float n = sin(v)*1.732050808;
        float3 t = clamp(float3(m + m, -n - m, n - m)*z - kx, 0.0, 1.0);
        float d0 = dot(d+(c+b*t.x)*t.x, d+(c+b*t.x)*t.x);
        float d1 = dot(d+(c+b*t.y)*t.y, d+(c+b*t.y)*t.y);
        if (d0 < d1) {
            res = d0;
            bestT = t.x;
        } else {
            res = d1;
            bestT = t.y;
        }
    }
    return float2(sqrt(res), bestT);
}

// =============================================================================
// COMPONENT 1: AUTHENTIC LETTER 'Q' (Classical Caslon / Didone Typography)
// =============================================================================

float sdLetterQ(float2 p, float scale) {
    p /= scale;

    // 1. Classical Roman / Didone Oval Bowl
    // Architectural vertical axis with stately stroke contrast
    // Left & right vertical stems: thick (~0.088)
    // Top & bottom horizontal arches: delicate (~0.034)
    float2 pInner = mul(p, rot2D(0.03));
    float dOuter = (length(p / float2(0.20, 0.27)) - 1.0) * 0.20;
    float dInner = (length(pInner / float2(0.112, 0.236)) - 1.0) * 0.112;
    float dBowl = max(dOuter, -dInner);

    // 2. Classical Calligraphic Swept Swash Tail
    // Restrained, dignified sweep along the baseline with subtle, elegant lift
    float dTail = 10.0;
    if (p.x > 0.01 && p.y < -0.08) {
        float2 pA = float2(0.05, -0.16);
        float2 pB = float2(0.16, -0.27);
        float2 pC = float2(0.24, -0.21);

        float2 bz = sdBezierT(p, pA, pB, pC);
        // Calligraphic stroke modulation:
        // Solid weight along the baseline (0.038), tapering smoothly to a delicate, restrained tip (0.014)
        float rStroke = lerp(0.038, 0.014, smoothstep(0.20, 0.95, bz.y));
        float dCurve = bz.x - rStroke;

        // Crucial: Carve away any part of the tail that enters the inner counter
        dTail = max(dCurve, -dInner);
    }

    return min(dBowl, dTail) * scale;
}

// =============================================================================
// COMPONENT 2: AUTHENTIC PLAYING CARD HEART PIP (Beloved Organic Character)
// =============================================================================

float sdHeartIQ(float2 p) {
    p.x = abs(p.x);
    if (p.y + p.x > 1.0) {
        return sqrt(dot(p - float2(0.25, 0.75), p - float2(0.25, 0.75))) - 0.35355339;
    }
    return sqrt(min(dot(p - float2(0.0, 1.0), p - float2(0.0, 1.0)),
                    dot(p - 0.5 * max(p.x + p.y, 0.0), p - 0.5 * max(p.x + p.y, 0.0)))) * float(sign(p.x - p.y));
}

float sdHeartPip(float2 p, float scale) {
    float2 q = p / scale;
    // Organic vertical balance from the version user praised
    q.y += 0.48;

    // Slender classical playing card proportion:
    q.x *= 1.35;

    // Subtle organic waist pinch (hand-cut intaglio character that user praised)
    if (q.y > 0.0 && q.y < 1.0) {
        q.x += 0.28 * q.y * (1.0 - q.y);
    }

    return sdHeartIQ(q) * (scale / 1.35);
}

// =============================================================================
// COMPONENT 3: ISOLATED FOCUS SHOWCASE
// =============================================================================

float2 mapIsolatedComponents(float3 p) {
    float2 res = float2(10.0, 0.0);

    // Left Showcase Plinth: Letter 'Q' (x = -0.70)
    float3 pLeft = p - float3(-0.70, 0.0, 0.0);
    float dPlinthL = sdRoundBox3D(pLeft, float3(0.52, 0.68, 0.020), 0.035);
    res = opUnion(res, float2(dPlinthL, MAT_CARD_BODY));

    float dRimL = max(
        sdRoundBox2D(pLeft.xy, float2(0.50, 0.66), 0.030),
        -sdRoundBox2D(pLeft.xy, float2(0.47, 0.63), 0.020)
    );
    res = opUnion(res, float2(max(dRimL, abs(pLeft.z - 0.024) - 0.003), MAT_GOLD_BORDER));

    // 3D Sculpt of Letter 'Q' (Restrained swash, perfectly balanced margins)
    float2 pQ = pLeft.xy + float2(0.035, 0.016);
    float dQ2D = sdLetterQ(pQ, 1.62);
    float dQ3D = max(dQ2D, abs(pLeft.z - 0.032) - 0.010);
    res = opUnion(res, float2(dQ3D, MAT_PIP_GOLD));

    // Right Showcase Plinth: Heart Symbol (x = +0.70)
    float3 pRight = p - float3(0.70, 0.0, 0.0);
    float dPlinthR = sdRoundBox3D(pRight, float3(0.52, 0.68, 0.020), 0.035);
    res = opUnion(res, float2(dPlinthR, MAT_CARD_BODY));

    float dRimR = max(
        sdRoundBox2D(pRight.xy, float2(0.50, 0.66), 0.030),
        -sdRoundBox2D(pRight.xy, float2(0.47, 0.63), 0.020)
    );
    res = opUnion(res, float2(max(dRimR, abs(pRight.z - 0.024) - 0.003), MAT_GOLD_BORDER));

    // 3D Sculpt of Heart Symbol (Characterful organic shape centered perfectly within frame)
    float2 pHeart = pRight.xy - float2(0.045, -0.060);
    float dHeart2D = sdHeartPip(pHeart, 0.85);
    float dHeartRim = max(dHeart2D, abs(pRight.z - 0.032) - 0.010);
    res = opUnion(res, float2(dHeartRim, MAT_GOLD_BORDER));

    float dHeartCore = max(dHeart2D + 0.015, abs(pRight.z - 0.034) - 0.008);
    res = opUnion(res, float2(dHeartCore, MAT_PIP_HEART));

    return res;
}

// =============================================================================
// COMPONENT 4: COURT FIGURE & FACE (Abstracted Royal Persona)
// Both Flat 2.5D Embossed Illustration and Full 3D Bas-Relief Sculpture
// =============================================================================

// Flat 2.5D Embossed Playing Card Illustration (Crisp, layered foil-print relief)
float2 mapCourtFigureFlat(float3 p) {
    float2 res = float2(10.0, MAT_COURT_FIELD);
    float2 pHead = p.xy - float2(0.0, 0.38);

    // Layer 1: Mantle Base & Ear Housings (h = 0.0030)
    float dEar = sdRoundBox2D(float2(abs(pHead.x) - 0.22, pHead.y - 0.01), float2(0.030, 0.070), 0.015);
    float dShoulder = sdRoundBox2D(float2(abs(p.x) - 0.26, p.y - 0.10), float2(0.10, 0.09), 0.032);
    float dMantle = min(dEar, dShoulder);
    float dMantle3D = max(dMantle, p.z - 0.0030);
    res = opUnion(res, float2(dMantle3D, MAT_ROYAL_MANTLE));

    // Layer 2: Ivory Cowl Frame & Cuirass (h = 0.0046)
    float2 pHood = pHead - float2(0.0, 0.03);
    float dOuterHood = sdRoundBox2D(pHood, float2(0.22, 0.21), 0.075);
    float2 pChest = p.xy - float2(0.0, 0.12);
    float dChest = sdRoundBox2D(pChest, float2(0.18, 0.10), 0.035);
    float dIvoryBase = min(dOuterHood, dChest);
    float dIvory3D = max(dIvoryBase, p.z - 0.0046);
    res = opUnion(res, float2(dIvory3D, MAT_IVORY_ARMOR));

    // Layer 3: Face Skin & Nose (h = 0.0062)
    float dCranium = (length(pHead / float2(0.20, 0.19)) - 1.0) * 0.19;
    float dJaw = (length((pHead - float2(0.0, -0.10)) / float2(0.13, 0.11)) - 1.0) * 0.11;
    float dFace = smin(dCranium, dJaw, 0.04);
    float dNose = length(pHead - float2(0.0, -0.032)) - 0.018;
    dFace = smin(dFace, dNose, 0.015);
    float dFace3D = max(dFace, p.z - 0.0062);
    res = opUnion(res, float2(dFace3D, MAT_FACE_SKIN));

    // Layer 4: Eye Whites / Sclera (h = 0.0076)
    float2 pEye = float2(abs(pHead.x) - 0.080, pHead.y - 0.005);
    pEye = mul(pEye, rot2D(-0.08));
    float dEyeWhite = (length(pEye / float2(0.038, 0.028)) - 1.0) * 0.028;
    float dEye3D = max(dEyeWhite, p.z - 0.0076);
    res = opUnion(res, float2(dEye3D, MAT_IVORY_ARMOR));

    // Layer 5: Amber Irises & Bangs (h = 0.0088)
    float2 pIris = pEye - float2(0.004, -0.002);
    float dIris = (length(pIris / float2(0.022, 0.022)) - 1.0) * 0.022;
    float dIris3D = max(dIris, p.z - 0.0088);
    res = opUnion(res, float2(dIris3D, MAT_PIP_GOLD));

    float dBrow = sdRoundBox2D(mul(pEye - float2(0.0, 0.032), rot2D(-0.10)), float2(0.034, 0.0045), 0.002);
    float2 pHair = float2(abs(pHead.x) - 0.060, pHead.y - 0.090);
    float dHair = sdRoundBox2D(mul(pHair, rot2D(-0.22)), float2(0.055, 0.012), 0.004);
    float dHairTotal = min(dBrow, dHair);
    float dHair3D = max(dHairTotal, p.z - 0.0088);
    res = opUnion(res, float2(dHair3D, MAT_FACE_HAIR));

    // Layer 6: Dark Pupils (h = 0.0098)
    float dPupil = length(pIris) - 0.010;
    float dPupil3D = max(dPupil, p.z - 0.0098);
    res = opUnion(res, float2(dPupil3D, MAT_CARD_BODY));

    // Layer 7: Gilded Crown, Brow Band, Collar & Sceptre (h = 0.0112)
    float dBrowBand = sdRoundBox2D(pHead - float2(0.0, 0.15), float2(0.15, 0.025), 0.010);
    float dCollar = sdRoundBox2D(pChest - float2(0.0, 0.08), float2(0.13, 0.014), 0.006);
    float2 pCrown = pHead - float2(0.0, 0.20);
    float dCrownBase = sdRoundBox2D(pCrown, float2(0.12, 0.012), 0.005);
    float dCenterPeak = sdRhombus2D(pCrown - float2(0.0, 0.030), float2(0.030, 0.048));
    float dSidePeaks = sdRhombus2D(float2(abs(pCrown.x) - 0.075, pCrown.y - 0.018), float2(0.020, 0.032));
    float dCrown = min(dCrownBase, min(dCenterPeak, dSidePeaks));
    float2 pBrooch = pChest - float2(0.0, 0.02);
    float dBroochRim = abs(length(pBrooch) - 0.035) - 0.006;

    float2 pSc = p.xy - float2(0.32, 0.22);
    float dShaft = sdRoundBox2D(pSc, float2(0.008, 0.18), 0.004);
    float2 pScHeart = pSc - float2(0.0, 0.18) - float2(0.006, -0.008);
    float dScHeart2D = sdHeartPip(pScHeart, 0.13);

    float dGold = min(min(min(dCrown, dBrowBand), min(dCollar, dBroochRim)), min(dShaft, dScHeart2D));
    float dGold3D = max(dGold, p.z - 0.0112);
    res = opUnion(res, float2(dGold3D, MAT_GOLD_BORDER));

    // Layer 8: Ruby Sceptre Heart Pip (h = 0.0124)
    float dHeartCore = max(dScHeart2D + 0.0025, p.z - 0.0124);
    res = opUnion(res, float2(dHeartCore, MAT_PIP_HEART));

    // Layer 9: Aether Jewels (h = 0.0134)
    float dCrownGem = length(pCrown - float2(0.0, 0.020)) - 0.015;
    float dBroochGem = length(pBrooch) - 0.028;
    float dGems = min(dCrownGem, dBroochGem);
    float dGems3D = max(dGems, p.z - 0.0134);
    res = opUnion(res, float2(dGems3D, MAT_AETHER_GEM));

    // Anchor securely to card substrate (p.z >= 0)
    res.x = max(res.x, -p.z);
    return res;
}

// Full 3D Bas-Relief Sculptural Queen (Strictly Euclidean, no inverted CSG normals)
float2 mapCourtFigure3D(float3 p) {
    float2 res = float2(10.0, 0.0);

    // 1. Head & Face Planes
    float3 pHead = p - float3(0.0, 0.38, 0.020);

    // Cranium & Chibi Jaw (Sculptural bas-relief dome)
    float dCranium = (length(pHead / float3(0.20, 0.19, 0.11)) - 1.0) * 0.11;
    float dJaw = (length((pHead - float3(0.0, -0.10, 0.0)) / float3(0.13, 0.11, 0.09)) - 1.0) * 0.09;
    float dFace = smin(dCranium, dJaw, 0.05);

    // Cute Defined Nose (Integrated seamlessly into face skin, protruding forward)
    float3 pNose = pHead - float3(0.0, -0.035, 0.110);
    float dNose = length(pNose) - 0.016;
    dFace = smin(dFace, dNose, 0.012);
    dFace = max(dFace, -p.z);
    res = opUnion(res, float2(dFace, MAT_FACE_SKIN));

    // 2. Eyes: Expressive Sclera Eyeball (Proud of face skin, no inverted socket normal)
    float3 pEye = pHead;
    pEye.x = abs(pEye.x) - 0.080;
    pEye.y -= -0.005;
    pEye.z -= 0.088;
    pEye.xy = mul(pEye.xy, rot2D(-0.08));

    float dEyeWhite = (length(pEye / float3(0.038, 0.028, 0.022)) - 1.0) * 0.022;
    res = opUnion(res, float2(dEyeWhite, MAT_IVORY_ARMOR));

    // Amber Iris (Embossed on front of eyeball)
    float3 pIris = pEye - float3(0.002, -0.001, 0.012);
    float dIris = (length(pIris / float3(0.022, 0.022, 0.014)) - 1.0) * 0.014;
    res = opUnion(res, float2(dIris, MAT_PIP_GOLD));

    // Dark Pupil (Proud of iris center)
    float3 pPupil = pIris - float3(0.0, 0.0, 0.006);
    float dPupil = (length(pPupil / float3(0.010, 0.010, 0.010)) - 1.0) * 0.010;
    res = opUnion(res, float2(dPupil, MAT_CARD_BODY));

    // Arched Eyebrows (Embossed gracefully above socket)
    float2 pBr = mul(pEye.xy - float2(0.0, 0.034), rot2D(-0.10));
    float dBrow2D = sdRoundBox2D(pBr, float2(0.034, 0.0045), 0.002);
    float dBrow3D = max(dBrow2D, abs(pHead.z - 0.095) - 0.008);
    res = opUnion(res, float2(dBrow3D, MAT_FACE_HAIR));

    // Forehead Hair Bangs (Delicate parted locks framing the brow)
    float3 pHair = pHead - float3(0.0, 0.095, 0.080);
    float dHairL = sdRoundBox3D(float3(mul(pHair.xy, rot2D(0.25)) - float2(-0.055, 0.0), pHair.z), float3(0.060, 0.014, 0.020), 0.006);
    float dHairR = sdRoundBox3D(float3(mul(pHair.xy, rot2D(-0.25)) - float2(0.055, 0.0), pHair.z), float3(0.060, 0.014, 0.020), 0.006);
    res = opUnion(res, float2(min(dHairL, dHairR), MAT_FACE_HAIR));

    // 3. Open Royal Hood / Cowl (Ivory cowl framing cranium, anchored to p.z >= 0)
    float3 pHood = pHead - float3(0.0, 0.03, 0.01);
    float dOuterHood = sdRoundBox3D(pHood, float3(0.22, 0.21, 0.05), 0.075);
    dOuterHood = max(dOuterHood, -p.z);
    res = opUnion(res, float2(dOuterHood, MAT_IVORY_ARMOR));

    // Hood Ear Housings / Vane Anchors
    float3 pEar = pHead;
    pEar.x = abs(pEar.x) - 0.22;
    pEar.y -= 0.01;
    float dEar = sdRoundBox3D(pEar, float3(0.030, 0.070, 0.045), 0.015);
    dEar = max(dEar, -p.z);
    res = opUnion(res, float2(dEar, MAT_ROYAL_MANTLE));

    // Brow Band
    float3 pBrow = pHead - float3(0.0, 0.15, 0.070);
    float dBrowBand = sdRoundBox3D(pBrow, float3(0.15, 0.025, 0.022), 0.010);
    res = opUnion(res, float2(dBrowBand, MAT_ROYAL_MANTLE));

    // 4. Golden Queen's Diadem / Crown (Bilateral symmetry)
    float3 pCrown = pHead - float3(0.0, 0.20, 0.070);
    float dCrownBase = sdRoundBox3D(pCrown, float3(0.12, 0.012, 0.018), 0.005);
    float dCenterPeak = sdRhombus2D(pCrown.xy - float2(0.0, 0.030), float2(0.030, 0.048));
    float dSidePeaks = sdRhombus2D(float2(abs(pCrown.x) - 0.075, pCrown.y - 0.018), float2(0.020, 0.032));
    float dPeaks = min(dCenterPeak, dSidePeaks);
    float dCrownPeaks = max(dPeaks, abs(pCrown.z) - 0.014);
    res = opUnion(res, float2(min(dCrownBase, dCrownPeaks), MAT_GOLD_BORDER));

    // Crown Center Gem
    float dCrownGem = length(pCrown - float3(0.0, 0.020, 0.014)) - 0.015;
    res = opUnion(res, float2(dCrownGem, MAT_AETHER_GEM));

    // 5. Royal Mantle, Collar & Robes
    float3 pChest = p - float3(0.0, 0.12, 0.025);
    float dChestPlate = sdRoundBox3D(pChest, float3(0.18, 0.10, 0.045), 0.035);
    dChestPlate = max(dChestPlate, -p.z);
    res = opUnion(res, float2(dChestPlate, MAT_IVORY_ARMOR));

    // Royal Lilac Mantle / Robe Fold (tucked gracefully into waist sash)
    float3 pShoulder = p - float3(0.0, 0.10, 0.020);
    pShoulder.x = abs(pShoulder.x) - 0.26;
    float waistDrape = max(0.0, -pShoulder.y - 0.01);
    pShoulder.z += waistDrape * 0.35;
    pShoulder.x += waistDrape * 0.10;
    float dShoulder = sdRoundBox3D(pShoulder, float3(0.10, 0.09, 0.040), 0.032);
    dShoulder = max(dShoulder, -p.z);
    res = opUnion(res, float2(dShoulder, MAT_ROYAL_MANTLE));

    // Gilded Collar Chevron Trim
    float dCollar = sdRoundBox3D(pChest - float3(0.0, 0.08, 0.015), float3(0.13, 0.014, 0.020), 0.006);
    res = opUnion(res, float2(dCollar, MAT_GOLD_BORDER));

    // Royal Aether Brooch / Medallion
    float3 pBrooch = pChest - float3(0.0, 0.02, 0.045);
    float dBroochRim = max(abs(length(pBrooch.xy) - 0.035) - 0.006, abs(pBrooch.z) - 0.008);
    res = opUnion(res, float2(dBroochRim, MAT_GOLD_BORDER));
    float dBroochGem = length(pBrooch) - 0.026;
    res = opUnion(res, float2(dBroochGem, MAT_AETHER_GEM));

    // 6. Royal Regalia: Sceptre of Hearts (Held in right hand)
    float3 pSceptre = p - float3(0.32, 0.22, 0.035);
    float dShaft = sdRoundBox3D(pSceptre, float3(0.008, 0.18, 0.008), 0.004);
    float2 pScHeart = pSceptre.xy - float2(0.0, 0.18) - float2(0.006, -0.008);
    float dScHeart2D = sdHeartPip(pScHeart, 0.13);
    float dScHeartRim = max(dScHeart2D, abs(pSceptre.z) - 0.010);
    res = opUnion(res, float2(min(dShaft, dScHeartRim), MAT_GOLD_BORDER));
    float dScHeartCore = max(dScHeart2D + 0.0025, abs(pSceptre.z - 0.004) - 0.008);
    res = opUnion(res, float2(dScHeartCore, MAT_PIP_HEART));

    // Clamp entire figure strictly against canvas plane
    res.x = max(res.x, -p.z);
    return res;
}

// =============================================================================
// COMPONENT 5: COMPLETE CARD SCENE
// =============================================================================

float2 mapCard(float3 p) {
    float2 cardHalfSize = float2(0.88, 1.24);
    float cardHalfThick = 0.020;
    float cornerRadius = 0.080;

    // Classical architectural court chamber: finely tuned framing around the Queen
    float2 courtHalfSize = float2(0.590, 0.925);
    float dCourtBox2D = sdRoundBox2D(p.xy, courtHalfSize, 0.034);

    // Base Substrate with Cavity
    float dCardBase = sdRoundBox3D(p, float3(cardHalfSize, cardHalfThick), cornerRadius);
    float dCavity = max(dCourtBox2D, (cardHalfThick - 0.008) - p.z);
    dCardBase = max(dCardBase, -dCavity);

    float2 res = float2(dCardBase, (abs(p.z) > cardHalfThick - 0.004 && dCourtBox2D > 0.0) ? MAT_CARD_BODY : MAT_CORE_EDGE);

    // 1. Two-fold rotational symmetry (180° inversion: maps bottom Queen & bottom-right index into upper quadrant)
    float3 p2 = (p.y < 0.0) ? float3(-p.x, -p.y, p.z) : p;

    // 2. Four-fold reflection symmetry for perimeter frames and borders
    float2 pAbs = abs(p.xy);

    // FRONT FACE (Z > -0.002)
    if (p.z > -0.002) {
        // =====================================================================
        // ORNAMENTAL GILDED BORDER SYSTEM (Rich Baroque Character & Harmonious Spacing)
        // =====================================================================

        // 1. Primary Outer Gilded Bevel Band (4-Fold Symmetry)
        float dOuterBorder2D = sdRoundBox2D(pAbs, cardHalfSize - float2(0.016, 0.016), cornerRadius - 0.008);
        float dInnerMargin2D = sdRoundBox2D(pAbs, cardHalfSize - float2(0.038, 0.038), cornerRadius - 0.020);
        float dBorderBand2D = max(dOuterBorder2D, -dInnerMargin2D);
        float dGoldBorder = max(dBorderBand2D, abs(p.z - (cardHalfThick + 0.0040)) - 0.0035);
        res = opUnion(res, float2(dGoldBorder, MAT_GOLD_BORDER));

        // 2. Secondary Delicate Gilded Hairline (4-Fold Symmetry)
        float dHairline2D = abs(sdRoundBox2D(pAbs, cardHalfSize - float2(0.050, 0.050), cornerRadius - 0.026)) - 0.0015;
        float dHairline = max(dHairline2D, abs(p.z - (cardHalfThick + 0.0022)) - 0.0016);
        res = opUnion(res, float2(dHairline, MAT_PIP_GOLD));

        // 3. Classical Gilded Beaded Pearl Rail (Only evaluated near perimeter)
        if (pAbs.x > cardHalfSize.x - 0.12 || pAbs.y > cardHalfSize.y - 0.12) {
            float beadDist = 10.0;
            float beadRailSpacing = 0.048;
            // Top & bottom rails
            if (pAbs.x < cardHalfSize.x - 0.12 && abs(pAbs.y - (cardHalfSize.y - 0.063)) < 0.015) {
                float bx = floorMod(p.x + beadRailSpacing * 0.5, beadRailSpacing) - beadRailSpacing * 0.5;
                float by = pAbs.y - (cardHalfSize.y - 0.063);
                beadDist = length(float2(bx, by)) - 0.0048;
            }
            // Left & right rails
            if (pAbs.y < cardHalfSize.y - 0.12 && abs(pAbs.x - (cardHalfSize.x - 0.063)) < 0.015) {
                float by = floorMod(p.y + beadRailSpacing * 0.5, beadRailSpacing) - beadRailSpacing * 0.5;
                float bx = pAbs.x - (cardHalfSize.x - 0.063);
                beadDist = min(beadDist, length(float2(bx, by)) - 0.0048);
            }
            if (beadDist < 0.02) {
                float dBeads = max(beadDist, abs(p.z - (cardHalfThick + 0.0032)) - 0.0022);
                res = opUnion(res, float2(dBeads, MAT_PIP_GOLD));
            }
        }

        // 4. Inner Gilded Pinstripe (4-Fold Symmetry)
        float dMarginLine2D = abs(sdRoundBox2D(pAbs, cardHalfSize - float2(0.076, 0.076), cornerRadius - 0.038)) - 0.0022;
        float dMarginLine = max(dMarginLine2D, abs(p.z - (cardHalfThick + 0.0028)) - 0.0020);
        res = opUnion(res, float2(dMarginLine, MAT_GOLD_BORDER));

        // 5. Baroque Corner Arabesque Spandrels (4-Fold Symmetry + Diagonal Symmetry)
        if (pAbs.x > cardHalfSize.x - 0.18 && pAbs.y > cardHalfSize.y - 0.18) {
            float2 pCorner = pAbs - cardHalfSize + float2(0.088, 0.088);
            float dCornerBoss = length(pCorner) - 0.012;
            float dCornerStar = sdRhombus2D(pCorner, float2(0.022, 0.022));
            float2 pDiag = mul(pCorner + float2(0.018, 0.018), rot2D(-PI * 0.25));
            float dPalmette = sdRhombus2D(pDiag, float2(0.010, 0.026));
            float dStraps = min(
                sdRoundBox2D(pCorner - float2(0.0, -0.038), float2(0.0045, 0.034), 0.002),
                sdRoundBox2D(pCorner.yx - float2(0.0, -0.038), float2(0.0045, 0.034), 0.002)
            );
            float dCornerScroll = min(min(dCornerBoss, min(dCornerStar, dPalmette)), dStraps);
            float dFiligree = max(dCornerScroll, abs(p.z - (cardHalfThick + 0.0044)) - 0.0024);
            res = opUnion(res, float2(dFiligree, MAT_GOLD_BORDER));
        }

        // =====================================================================
        // CORNER INDICES (Top-Left & Bottom-Right 180° Rotational Symmetry)
        // Under 180° inversion fold (p2), BOTH corners map to p2.x < -0.54, p2.y > 0.68!
        // =====================================================================
        if (p2.x < -0.54 && p2.y > 0.68) {
            float2 pPipQ = p2.xy - float2(-0.697, 1.045);

            // Rank 'Q' (Centered on the diagonal miter line)
            float2 pRank = pPipQ + float2(0.0043, 0.0020);
            float dRank2D = sdLetterQ(pRank, 0.200);
            float dRank = max(dRank2D, abs(p.z - (cardHalfThick + 0.0048)) - 0.0024);
            res = opUnion(res, float2(dRank, MAT_PIP_GOLD));

            // Heart Suit Pip (Translated downward by 0.190 along the same vertical axis)
            float2 pPipHeart = pPipQ - float2(0.0, -0.190);
            float2 pSuit = pPipHeart - float2(0.0070, -0.0095);
            float dSuit2D = sdHeartPip(pSuit, 0.135);
            float dSuitRim = max(dSuit2D, abs(p.z - (cardHalfThick + 0.0048)) - 0.0024);
            res = opUnion(res, float2(dSuitRim, MAT_PIP_GOLD));
            float dSuitCore = max(dSuit2D + 0.0024, abs(p.z - (cardHalfThick + 0.0058)) - 0.0020);
            res = opUnion(res, float2(dSuitCore, MAT_PIP_HEART));
        }

        // =====================================================================
        // ARCHITECTURAL COURT CHAMBER (Stepped Portico Moulding)
        // =====================================================================

        // Recessed Court Field Canvas
        float dCourtCanvas = max(dCourtBox2D, abs(p.z - (cardHalfThick - 0.005)) - 0.0025);
        res = opUnion(res, float2(dCourtCanvas, MAT_COURT_FIELD));

        if (abs(dCourtBox2D) < 0.04) {
            // Stepped Moulding: Outer Torus Bead (4-Fold Symmetry)
            float dCourtOuter = max(dCourtBox2D, -sdRoundBox2D(pAbs, courtHalfSize - float2(0.016, 0.016), 0.026));
            float dCourtFrame = max(dCourtOuter, abs(p.z - (cardHalfThick + 0.0052)) - 0.0035);
            res = opUnion(res, float2(dCourtFrame, MAT_GOLD_BORDER));

            // Stepped Moulding: Shadow Inlay Groove (4-Fold Symmetry)
            float dCourtGroove = max(sdRoundBox2D(pAbs, courtHalfSize - float2(0.016, 0.016), 0.026), -sdRoundBox2D(pAbs, courtHalfSize - float2(0.024, 0.024), 0.020));
            float dGroove3D = max(dCourtGroove, abs(p.z - (cardHalfThick + 0.0022)) - 0.0018);
            res = opUnion(res, float2(dGroove3D, MAT_CARD_BODY));

            // Stepped Moulding: Inner Bevel Lip (4-Fold Symmetry)
            float dCourtInner = max(sdRoundBox2D(pAbs, courtHalfSize - float2(0.024, 0.024), 0.020), -sdRoundBox2D(pAbs, courtHalfSize - float2(0.034, 0.034), 0.014));
            float dInnerFrame = max(dCourtInner, abs(p.z - (cardHalfThick + 0.0042)) - 0.0025);
            res = opUnion(res, float2(dInnerFrame, MAT_PIP_GOLD));

            // Chamber Corner Rosettes (4-Fold Symmetry in 4 corners)
            if (pAbs.x > courtHalfSize.x - 0.04 && pAbs.y > courtHalfSize.y - 0.04) {
                float2 pChamberCorner = pAbs - courtHalfSize + float2(0.014, 0.014);
                float dChamberRosette = sdRhombus2D(pChamberCorner, float2(0.016, 0.016));
                float dRosette3D = max(dChamberRosette, abs(p.z - (cardHalfThick + 0.0058)) - 0.0026);
                res = opUnion(res, float2(dRosette3D, MAT_GOLD_BORDER));
            }
        }

        // Royal Crown Pediment Finial (Bilateral X symmetry & 2-fold Y symmetry)
        if (pAbs.x < 0.10 && abs(pAbs.y - courtHalfSize.y) < 0.06) {
            float2 pPediment = float2(pAbs.x, pAbs.y - (courtHalfSize.y - 0.004));
            float dCrownCenter = sdRhombus2D(pPediment - float2(0.0, 0.014), float2(0.022, 0.028));
            float dCrownSide = sdRhombus2D(pPediment - float2(0.030, 0.006), float2(0.016, 0.020));
            float dCrownBase = sdRoundBox2D(pPediment - float2(0.0, -0.006), float2(0.046, 0.007), 0.003);
            float dFinial = min(min(dCrownCenter, dCrownSide), dCrownBase);
            float dFinial3D = max(dFinial, abs(p.z - (cardHalfThick + 0.0062)) - 0.0030);
            res = opUnion(res, float2(dFinial3D, MAT_GOLD_BORDER));
        }

        // Two-Way Symmetrical Court Figure (Uniform Euclidean metric with zero clipping discontinuity)
        float figScale = 1.24;
        float3 pFig = float3(p2.xy, p2.z - cardHalfThick) / figScale;

#if POPUP_MODE == 0
        // Full 3D Bas-Relief Figurine
        float2 resFig = mapCourtFigure3D(pFig);
        resFig.x *= figScale;
#elif POPUP_MODE == 1
        // Flat 2.5D Embossed Playing Card Illustration
        float2 resFig = mapCourtFigureFlat(pFig);
        resFig.x *= figScale;
#elif POPUP_MODE == 2
        // Dynamic Pop-Up Effect: Smooth organic bloom from flat card to full 3D sculpture
        float cycle = floorMod(frameGroup.time * 0.70, 3.5);
        float tUp = clamp(cycle, 0.0, 1.0);
        float popFactor = (tUp < 1.0) ? sin(tUp * PI * 0.5) : 1.0;
        if (cycle > 2.3) {
            float tDown = (cycle - 2.3) / 1.2;
            popFactor = 1.0 - smoothstep(0.0, 1.0, tDown);
        }
        float zPop = lerp(0.12, 1.0, popFactor);
        float3 pPop = float3(pFig.xy, pFig.z / zPop);
        float2 resFig = mapCourtFigure3D(pPop);
        resFig.x *= zPop * figScale;
#endif

        res = opUnion(res, resFig);

        // Central Dividing Court Sash (Bilateral X symmetry, only evaluated near y = 0)
        if (pAbs.y < 0.055 && pAbs.x < courtHalfSize.x + 0.02) {
            float2 pSash = float2(pAbs.x, p.y);
            float dSashBand = sdRoundBox2D(pSash, float2(courtHalfSize.x, 0.035), 0.008);
            float dSashFrame = max(dSashBand, -sdRoundBox2D(pSash, float2(courtHalfSize.x, 0.022), 0.004));
            float dSashFrame3D = max(dSashFrame, abs(p.z - (cardHalfThick + 0.006)) - 0.003);
            res = opUnion(res, float2(dSashFrame3D, MAT_GOLD_BORDER));

            float dSashInlay = max(sdRoundBox2D(pSash, float2(courtHalfSize.x, 0.022), 0.004), abs(p.z - (cardHalfThick + 0.004)) - 0.002);
            res = opUnion(res, float2(dSashInlay, MAT_ROYAL_MANTLE));

            float dSashPip = sdRhombus2D(pSash, float2(0.040, 0.040));
            float dSashPip3D = max(dSashPip, abs(p.z - (cardHalfThick + 0.008)) - 0.004);
            res = opUnion(res, float2(dSashPip3D, MAT_GOLD_BORDER));
            float dSashGem = length(float3(pSash, p.z - (cardHalfThick + 0.009))) - 0.022;
            res = opUnion(res, float2(dSashGem, MAT_AETHER_GEM));
        }
    }

    // BACK FACE (Z < 0.005)
    if (p.z < 0.005) {
        float dBackBorder2D = max(
            sdRoundBox2D(pAbs, cardHalfSize - float2(0.022, 0.022), cornerRadius - 0.010),
            -sdRoundBox2D(pAbs, cardHalfSize - float2(0.065, 0.065), cornerRadius - 0.035)
        );
        float dBackBorder = max(dBackBorder2D, abs(p.z - (-cardHalfThick - 0.003)) - 0.0025);
        res = opUnion(res, float2(dBackBorder, MAT_GOLD_BORDER));

        float2 pTwin = float2(p.x, pAbs.y - 0.44);
        float dMedallionRing = abs(length(pTwin) - 0.26) - 0.015;
        float dMedallionPip = sdHeartPip(pTwin, 1.2);
        float dMedallion = min(dMedallionRing, dMedallionPip);
        float dMedallionEmboss = max(dMedallion, abs(p.z - (-cardHalfThick - 0.0035)) - 0.0022);
        res = opUnion(res, float2(dMedallionEmboss, MAT_GOLD_BORDER));

        float dCenterRing = abs(length(pAbs) - 0.16) - 0.014;
        float dCenterStar = sdRhombus2D(pAbs, float2(0.12, 0.12));
        float dCenterEmboss = max(min(dCenterRing, dCenterStar), abs(p.z - (-cardHalfThick - 0.004)) - 0.0025);
        res = opUnion(res, float2(dCenterEmboss, MAT_CARD_BACK));
    }

    return res;
}

// Scene selector (the pointer is reported with its origin at the bottom-left corner, like the pixel coordinates)
float2 mapScene(float3 p) {
#if VIEW_ISOLATE_COMPONENTS == 1
    float3 pIso = p;
    float yaw = sin(frameGroup.time * 0.40) * 0.18;
    float pitch = cos(frameGroup.time * 0.50) * 0.08 - 0.02;
    if (frameGroup.pointerDown != 0) {
        yaw = (frameGroup.pointer.x / resolution.x - 0.5) * 3.5;
        pitch = (frameGroup.pointer.y / resolution.y - 0.5) * 2.0;
    }
    pIso.xz = mul(pIso.xz, rot2D(yaw));
    pIso.yz = mul(pIso.yz, rot2D(pitch));

    float2 resIso = mapIsolatedComponents(pIso);
    float dFloor = p.y + 2.5;
    float2 resFloor = float2(dFloor, MAT_FLOOR);
    return (resIso.x < resFloor.x) ? resIso : resFloor;
#else
    float3 pCard = p;
#if SHOT_ANGLE == 0
    // Idle Floating Animation (Full dynamic tilt, bob & sway)
    pCard.y -= 0.02 + 0.020 * sin(frameGroup.time * 1.2);
    float yaw = sin(frameGroup.time * 0.45) * 0.26;
    float pitch = cos(frameGroup.time * 0.55) * 0.10 - 0.02;
    float roll = sin(frameGroup.time * 0.35) * 0.03;
    if (frameGroup.pointerDown != 0) {
        yaw = (frameGroup.pointer.x / resolution.x - 0.5) * 3.5;
        pitch = (frameGroup.pointer.y / resolution.y - 0.5) * 2.0;
        roll = 0.0;
    }
#elif SHOT_ANGLE == 1
    // Front Flat View (Zero tilt, head-on orthographic framing)
    float yaw = 0.0;
    float pitch = 0.0;
    float roll = 0.0;
#elif SHOT_ANGLE == 2
    // Left Grazing Tilt (Reproduces terminator shading on left shoulder)
    float yaw = -0.28;
    float pitch = 0.06;
    float roll = 0.0;
#elif SHOT_ANGLE == 3
    // Right Grazing Tilt (Reproduces terminator shading on right shoulder)
    float yaw = 0.28;
    float pitch = -0.06;
    float roll = 0.0;
#elif SHOT_ANGLE == 4
    // Macro Center Sash (Close zoom on waist, sash and mantle corners)
    float yaw = 0.0;
    float pitch = 0.0;
    float roll = 0.0;
#elif SHOT_ANGLE == 5
    // Macro Left Shoulder (Close zoom on left purple mantle corner)
    float yaw = -0.24;
    float pitch = 0.05;
    float roll = 0.0;
#elif SHOT_ANGLE == 6
    // 3D Layer Profile (Steep 45-degree angle inspecting relief depth)
    float yaw = 0.70;
    float pitch = 0.18;
    float roll = -0.04;
#endif
    pCard.xz = mul(pCard.xz, rot2D(yaw));
    pCard.yz = mul(pCard.yz, rot2D(pitch));
    pCard.xy = mul(pCard.xy, rot2D(roll));

    float2 resCard = mapCard(pCard);
    float dFloor = p.y + 2.5;
    float2 resFloor = float2(dFloor, MAT_FLOOR);
    return (resCard.x < resFloor.x) ? resCard : resFloor;
#endif
}

// Normal calculation via tetrahedron
float3 calcNormal(float3 p) {
    const float2 e = float2(0.0006, -0.0006);
    return normalize(
        e.xyy * mapScene(p + e.xyy).x +
        e.yyx * mapScene(p + e.yyx).x +
        e.yxy * mapScene(p + e.yxy).x +
        e.xxx * mapScene(p + e.xxx).x
    );
}

// Ambient Occlusion tuned for fine card relief micro-cavities
float calcAO(float3 p, float3 n) {
    float occ = 0.0;
    float sca = 1.0;
    for (int i = 0; i < 4; i++) {
        float h = 0.003 + 0.006 * float(i);
        float d = mapScene(p + h * n).x;
        occ += max(0.0, h - d) * sca;
        sca *= 0.70;
    }
    return clamp(1.0 - 1.2 * occ, 0.25, 1.0);
}

// Soft Shadow with adaptive step progression & diagnostic best-material
float2 calcSoftShadow(float3 ro, float3 rd, float mint, float maxt, float k) {
    float res = 1.0;
    float bestMat = -1.0;
    float t = mint;
    for (int i = 0; i < 24; i++) {
        float2 hit = mapScene(ro + rd * t);
        float h = hit.x;
        float val = k * h / t;
        if (val < res) {
            res = val;
            bestMat = hit.y;
        }
        t += clamp(h, 0.012, 0.35);
        if (res < 0.005 || t > maxt) break;
    }
    return float2(clamp(res, 0.0, 1.0), bestMat);
}

// =============================================================================
// SDF DEBUG HELPER FUNCTIONS
// =============================================================================

// False-color thermal ramp for step count heatmap
float3 debugStepHeatmap(float steps, float maxSteps) {
    float f = clamp(steps / maxSteps, 0.0, 1.0);
    float3 c = lerp(float3(0.02, 0.04, 0.25), float3(0.0, 0.45, 0.85), smoothstep(0.0, 0.20, f));
    c = lerp(c, float3(0.0, 0.85, 0.45), smoothstep(0.20, 0.40, f));
    c = lerp(c, float3(0.95, 0.90, 0.10), smoothstep(0.40, 0.65, f));
    c = lerp(c, float3(0.95, 0.25, 0.05), smoothstep(0.65, 0.85, f));
    c = lerp(c, float3(1.0, 1.0, 1.0), smoothstep(0.85, 1.00, f));
    if (steps >= maxSteps - 1.0) c = float3(1.0, 0.0, 0.4); // Stalled / unconverged rays in neon magenta
    return c;
}

// False-color material segmentation map
float3 debugMaterialColor(float id) {
    if (id < 0.0)               return float3(0.04, 0.05, 0.07);
    if (id == MAT_FLOOR)        return float3(0.18, 0.18, 0.22);
    if (id == MAT_CARD_BODY)    return float3(0.12, 0.28, 0.55);
    if (id == MAT_CORE_EDGE)    return float3(0.35, 0.35, 0.42);
    if (id == MAT_GOLD_BORDER)  return float3(1.00, 0.82, 0.12);
    if (id == MAT_COURT_FIELD)  return float3(0.08, 0.58, 0.82);
    if (id == MAT_ROYAL_MANTLE) return float3(0.68, 0.22, 0.88);
    if (id == MAT_ROYAL_ACCENT) return float3(0.95, 0.45, 0.12);
    if (id == MAT_FACE_SKIN)    return float3(1.00, 0.76, 0.68);
    if (id == MAT_FACE_HAIR)    return float3(0.52, 0.28, 0.12);
    if (id == MAT_AETHER_GEM)   return float3(0.12, 0.98, 0.88);
    if (id == MAT_PIP_GOLD)     return float3(1.00, 0.62, 0.05);
    if (id == MAT_CARD_BACK)    return float3(0.85, 0.15, 0.15);
    if (id == MAT_IVORY_ARMOR)  return float3(0.92, 0.94, 0.88);
    if (id == MAT_PIP_HEART)    return float3(0.95, 0.04, 0.18);
    return float3(0.6, 0.6, 0.6);
}

// Shades one pixel; pixel coordinates have their origin at the bottom-left corner and y grows up the image.
float4 shade(float2 pixel) {
    float2 uv = (pixel - 0.5 * resolution) / resolution.y;

#if VIEW_ISOLATE_COMPONENTS == 1
    // Closer study framing for isolated components
    float3 ro = float3(0.0, 0.0, 3.4);
    float3 rd = normalize(float3(uv, -1.8));
#elif SHOT_ANGLE == 4
    // Macro framing on center waist & sash
    float3 ro = float3(0.0, 0.0, 2.2);
    float3 rd = normalize(float3(uv, -1.8));
#elif SHOT_ANGLE == 5
    // Macro framing on left purple shoulder corner
    float3 ro = float3(-0.30, 0.08, 1.8);
    float3 rd = normalize(float3(uv, -1.8));
#else
    float3 ro = float3(0.0, 0.0, 4.8);
    float3 rd = normalize(float3(uv, -1.8));
#endif

    // Raymarch with adaptive stepping
    float t = 0.5;
    float matId = -1.0;
    float stepsTaken = 0.0;
    for (int i = 0; i < 140; i++) {
        stepsTaken += 1.0;
        float3 p = ro + rd * t;
        float2 hit = mapScene(p);
        if (hit.x < SURF_DIST) {
            matId = hit.y;
            break;
        }
        t += (hit.x > 0.08) ? hit.x * 0.95 : hit.x * 0.82;
        if (t > MAX_DIST) break;
    }

    // Studio 3-Point Lights
    float3 lightKeyDir = normalize(float3(0.7, 1.0, 1.4));
    float3 lightFillDir = normalize(float3(-0.9, -0.3, 0.8));
    float3 lightRimDir = normalize(float3(0.0, 1.4, -1.0));

    // Deep studio vignette background
    float3 col = lerp(float3(0.07, 0.08, 0.11), float3(0.015, 0.018, 0.025), length(uv) * 1.15);
    float2 debugShadowDiag = float2(1.0, 1.0);

    if (matId >= 0.0) {
        float3 p = ro + rd * t;
        float3 n = calcNormal(p);
        float3 v = -rd;

        float ao = calcAO(p, n);
        float diffKey = max(dot(n, lightKeyDir), 0.0);
        float normalBias = 0.008 + 0.008 * (1.0 - smoothstep(0.0, 0.40, diffKey));
        float2 sDiag = (diffKey > 0.002) ? calcSoftShadow(p + n * normalBias, lightKeyDir, 0.024, 3.5, 18.0) : float2(0.0, -1.0);
        float rawShadow = sDiag.x;
        float bestMat = sDiag.y;
        float terminatorFade = smoothstep(0.002, 0.16, diffKey);
        float shadowKey = lerp(1.0, rawShadow, terminatorFade);
        debugShadowDiag = float2(shadowKey, bestMat);

        // Material PBR properties
        float3 albedo = float3(0.15, 0.15, 0.15);
        float roughness = 0.35;
        float metallic = 0.0;
        float foil = 0.0;
        float3 emit = float3(0.0, 0.0, 0.0);

        if (matId == MAT_FLOOR) {
            albedo = float3(0.025, 0.028, 0.035);
            roughness = 0.85;
            metallic = 0.0;
        } else if (matId == MAT_CARD_BODY) {
            // Midnight Obsidian Linen Cardstock
            albedo = float3(0.08, 0.09, 0.13);
            roughness = 0.40;
            metallic = 0.04;
        } else if (matId == MAT_CORE_EDGE) {
            // Multi-ply dark graphite core edge `#252630`
            albedo = float3(0.035, 0.035, 0.045);
            roughness = 0.65;
            metallic = 0.0;
        } else if (matId == MAT_GOLD_BORDER) {
            // Radiant polished 24K gilded gold leaf
            albedo = float3(1.0, 0.84, 0.36);
            roughness = 0.12;
            metallic = 0.98;
            foil = 0.35;
        } else if (matId == MAT_COURT_FIELD) {
            // Royal Imperial Sapphire / Midnight Indigo court tapestry
            float2 pBG = p.xy;
            float r = length(pBG);
            float3 deepSapphire = lerp(float3(0.07, 0.09, 0.18), float3(0.025, 0.030, 0.075), smoothstep(0.15, 0.70, r));
            float haloTop = exp(-4.5 * length(pBG - float2(0.0, 0.47)));
            float haloBot = exp(-4.5 * length(pBG - float2(0.0, -0.47)));
            albedo = deepSapphire + float3(0.35, 0.24, 0.08) * (haloTop + haloBot);
            roughness = 0.30;
            metallic = 0.10;
        } else if (matId == MAT_ROYAL_MANTLE) {
            albedo = float3(0.62, 0.40, 0.78);
            roughness = 0.22;
            metallic = 0.15;
        } else if (matId == MAT_ROYAL_ACCENT) {
            albedo = float3(0.72, 0.42, 0.24);
            roughness = 0.25;
            metallic = 0.10;
        } else if (matId == MAT_FACE_SKIN) {
            albedo = float3(0.95, 0.81, 0.66);
            roughness = 0.50;
            metallic = 0.0;
        } else if (matId == MAT_FACE_HAIR) {
            albedo = float3(0.48, 0.26, 0.12);
            roughness = 0.45;
            metallic = 0.05;
        } else if (matId == MAT_AETHER_GEM) {
            albedo = float3(0.18, 0.84, 0.93);
            roughness = 0.04;
            metallic = 0.50;
            emit = float3(0.18, 0.84, 0.93) * 2.2;
        } else if (matId == MAT_PIP_GOLD) {
            // Radiant gold court lettering & pips
            albedo = float3(1.0, 0.88, 0.42);
            roughness = 0.10;
            metallic = 0.96;
            foil = 0.45;
            emit = float3(0.20, 0.16, 0.05);
        } else if (matId == MAT_PIP_HEART) {
            // Royal Ruby Carmine Heart Pip `#C81D25`
            albedo = float3(0.85, 0.09, 0.15);
            roughness = 0.16;
            metallic = 0.20;
            foil = 0.50;
            emit = float3(0.18, 0.02, 0.04);
        } else if (matId == MAT_CARD_BACK) {
            albedo = float3(0.98, 0.80, 0.34);
            roughness = 0.18;
            metallic = 0.90;
            foil = 0.35;
        } else if (matId == MAT_IVORY_ARMOR) {
            albedo = float3(0.96, 0.94, 0.90);
            roughness = 0.22;
            metallic = 0.08;
        }

        // Shading: Key Diffuse + Cook-Torrance Specular
        float3 hKey = normalize(lightKeyDir + v);
        float specKey = pow(max(dot(n, hKey), 0.0), lerp(16.0, 220.0, 1.0 - roughness));

        // Fill Light
        float diffFill = max(dot(n, lightFillDir), 0.0) * 0.32;
        float3 hFill = normalize(lightFillDir + v);
        float specFill = pow(max(dot(n, hFill), 0.0), 32.0) * 0.15;

        // Rim Light
        float diffRim = max(dot(n, lightRimDir), 0.0) * 0.35;
        float3 hRim = normalize(lightRimDir + v);
        float specRim = pow(max(dot(n, hRim), 0.0), 48.0) * 0.50;

        // Anisotropic / Prismatic Rainbow Shimmer on Foil surfaces
        if (foil > 0.0) {
            float viewAngle = dot(n, v);
            float3 holoSpectrum = 0.5 + 0.5 * cos(2.0 * PI * (viewAngle * 4.0 + float3(0.0, 0.33, 0.67)));
            albedo = lerp(albedo, holoSpectrum * 1.3, foil * 0.40 * (1.0 - roughness));
        }

        // Combine Lighting components
        float3 ambient = float3(0.03, 0.035, 0.05) * albedo * ao;
        float3 diffuse = (diffKey * shadowKey + diffFill + diffRim) * albedo;
        float3 specular = (specKey * shadowKey + specFill + specRim) * lerp(float3(1.0, 1.0, 1.0), albedo, metallic);

        col = ambient + diffuse + specular + emit;

        // Specular fresnel on edges
        float fresnel = pow(1.0 - max(dot(n, v), 0.0), 4.0);
        col += fresnel * lerp(float3(0.08, 0.08, 0.08), albedo, metallic) * ao;

    }

    // Tone mapping & Gamma correction
    col = col / (col + float3(1.0, 1.0, 1.0));
    col = pow(col, float3(1.0 / 2.2, 1.0 / 2.2, 1.0 / 2.2));

    // =========================================================================
    // SDF DEBUG SYSTEM OUTPUT MODES
    // =========================================================================
#if DEBUG_MODE == 1
    // Step Count Heatmap
    col = debugStepHeatmap(stepsTaken, 140.0);
#elif DEBUG_MODE == 2
    // Distance Field Isolines & Contours
    if (matId >= 0.0) {
        float3 p = ro + rd * t;
        float3 n = calcNormal(p);
        float d = mapScene(p + n * 0.015).x;
        float3 colField = (d > 0.0) ? float3(0.95, 0.60, 0.25) : float3(0.25, 0.60, 0.95);
        colField *= 0.70 + 0.30 * cos(d * 300.0);
        col = colField;
    } else {
        col = float3(0.04, 0.05, 0.07);
    }
#elif DEBUG_MODE == 3
    // Surface Normal Map
    if (matId >= 0.0) {
        float3 p = ro + rd * t;
        float3 n = calcNormal(p);
        col = n * 0.5 + 0.5;
    } else {
        col = float3(0.04, 0.05, 0.07);
    }
#elif DEBUG_MODE == 4
    // Material ID Segmentation Map
    col = debugMaterialColor(matId);
#elif DEBUG_MODE == 5
    // Coarse Bounding Box Clearance Overlay
    if (matId >= 0.0) {
        float3 p = ro + rd * t;
        float3 pCard = p;
        pCard.y -= 0.02 + 0.020 * sin(frameGroup.time * 1.2);
        float yaw = sin(frameGroup.time * 0.45) * 0.26;
        float pitch = cos(frameGroup.time * 0.55) * 0.10 - 0.02;
        float roll = sin(frameGroup.time * 0.35) * 0.03;
        if (frameGroup.pointerDown != 0) {
            yaw = (frameGroup.pointer.x / resolution.x - 0.5) * 3.5;
            pitch = (frameGroup.pointer.y / resolution.y - 0.5) * 2.0;
            roll = 0.0;
        }
        pCard.xz = mul(pCard.xz, rot2D(yaw));
        pCard.yz = mul(pCard.yz, rot2D(pitch));
        pCard.xy = mul(pCard.xy, rot2D(roll));
        float3 pRel = abs(pCard - float3(0.0, 0.0, 0.10)) - float3(0.92, 1.28, 0.15);
        float dBox = length(max(pRel, 0.0)) + min(max(pRel.x, max(pRel.y, pRel.z)), 0.0);
        float wire = 1.0 - smoothstep(0.0, 0.015, abs(dBox));
        col = lerp(col, float3(0.2, 1.0, 0.3), wire * 0.85);
    }
#elif DEBUG_MODE == 6
    // Interactive Split Screen (Left: Beauty, Right: Heatmap)
    float divider = 0.0;
    if (frameGroup.pointerDown != 0) {
        divider = (frameGroup.pointer.x / resolution.x - 0.5) * (resolution.x / resolution.y);
    }
    if (uv.x > divider) {
        col = debugStepHeatmap(stepsTaken, 140.0);
    }
#elif DEBUG_MODE == 7
    // Direct Occluder Material ID
    if (matId >= 0.0) {
        if (debugShadowDiag.x < 0.95) {
            col = debugMaterialColor(debugShadowDiag.y);
        } else {
            col = float3(1.0, 1.0, 1.0); // Lit
        }
    } else {
        col = float3(0.04, 0.05, 0.07);
    }
#endif

    return float4(col, 1.0);
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint width;
    uint height;

    image.GetDimensions(width, height);
    if ((id.x >= width) || (id.y >= height)) {
        return;
    }
    resolution = float2(width, height);
    // y grows up the image in the shading functions: the pixel's origin is the bottom-left corner.
    float2 pixel = float2(float(id.x) + 0.5, float(height) - (float(id.y) + 0.5));
    image[id.xy] = shade(pixel);
}
