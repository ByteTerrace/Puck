// PODS - a free-form lab for Moth's twin thruster shells, matched to the owner's thruster references
// (docs/game/art/moth-concept-pack/08-thrusters-rear-low.png first, then 09 to 12).
// The avatars/moth world runs this file as the pods-lab source; pipeline.watch pods-lab on reloads saved edits.
// What settles here is rebuilt in moth.puck's constrained creation shapes; this file is never the shipped pack.
// Frame: x to the viewer's right seen from behind, y up, +z out of her back toward a viewer behind her. The pair
// mirrors about x = 0. Each pod has its own frame s: centred on POD_C, rolled by ROLL so its long axis is s.y, with
// -s.x toward the spine and +s.z out of her back.
// VIEW picks the camera: -1 a sheet of six (rear, rear-low, rear three-quarter / side, inner section, nozzle close-up),
// 0 rear-low (reference 08), 1 rear three-quarter, 2 side, 3 straight behind, 4 below, 5 nozzle close-up,
// 6 the inner section (spine, inner columns, vents) close up.
// Dragging the pointer orbits a single view.
#define VIEW -1
#define AA 2
#define MAX_STEPS 160

#include "pods.interface.hlsli"

static float2 resolution;
static float aspect;
static const float PI = 3.14159265;

static const float VIOLET = 1., CREAM = 2., GUNMETAL = 3., BLACK = 4., CYAN = 5., GREY = 6., CORE = 7., FRAME = 8., SPINE = 9.;

// ---- the casing, in metres ----
static const float3 POD_C = float3(0.185, 1.24, -0.02);
static const float ROLL = 0.2;
static const float POD_HEIGHT = 0.95;         // the pod's height relative to the frame its features are authored in              // radians about z: the bottom leans away from the spine
static const float3 SHELL_R = float3(0.155, 0.6, 0.13);
static const float FACE_Z = 0.09;            // the casing's back is flattened into a broad face this far out
static const float BEVEL_D = 0.145;
static const float INNER_BEVEL_D = 0.142;     // the chamfer along the inner edge, facing the spine
static const float TAPER = 0.7;             // how much narrower the casing is at its bottom than at its waist
static const float TOP_Y = 0.44;             // the shoulder's top edge
static const float TOP_OUTER_D = 0.26;        // its broad outer chamfer
static const float TOP_INNER_D = 0.38;       // its small chamfer at the spine
static const float SHELL_T = 0.022;          // casing thickness
static const float WRAP = 0.055;             // how far the casing wraps around its sides toward her body
static const float INNER_X = 0.04;          // the straight inner edge, beside the spine (world x)
// Plates: bands run across the casing on planes tilted BAND_TILT (falling toward the outer edge), split by grooves
// into an inner and an outer column at COLUMN_X.
static const float BAND_TILT = 0.3;
static const float BAND_ARCH = 1.6;          // the bands arch, highest near the inner edge
static const float SEAM = 0.004;
static const float GROOVE = 0.008;
static const float PLATE_ROUND = 0.005;      // how soft each plate's edge is where it meets a groove
static const float COLUMN_X = 0.0;
// The bottom cut, in the pod's frame: a plane from the inner edge part-way down to the pointed outer tip, facing in,
// down and back; the casing is open under it and its rim is trimmed in gunmetal.
static const float3 CUT_P = float3(-0.125, -0.08, 0.0);
static const float3 CUT_N = float3(-0.74, -0.56, 0.37);
static const float TRIM_W = 0.009;
// The inner core the casing wraps, blended into the thruster housing that swells out of the open rim.
static const float3 CORE_R = float3(0.1, 0.43, 0.075);
static const float3 CORE_SHIFT = float3(-0.01, -0.02, -0.01);
static const float CORE_BLEND = 0.025;
static const float CORE_BELOW = -0.008;
// The thruster housing: NOZ_ALONG along the cut line from CUT_P toward the tip, its axis square to the opening.
static const float NOZ_ALONG = 0.13;
static const float NOZ_PROUD = 0.004;          // how far the nozzle's face stands out of the opening's plane

static const float NOZ_R = 0.075;
static const float NOZ_H = 0.055;
static const float NOZ_ROUND = 0.012;
static const float DISH_R = 0.09;
static const float DISH_DEPTH = 0.02;
static const float LENS_R = 0.02;
static const float RING_R = 0.03;
static const float RING_DEPTH = 0.012;
// Details on the face: a framed vent in the cap's inner column, a grey panel on the lower cream band's outer column.
static const float2 VENT_C = float2(-0.045, 0.35);
// The spine.
static const float3 SPINE_C = float3(0., 1.33, -0.03);
static const float3 SPINE_HALF = float3(0.052, 0.25, 0.05);
static const float SPINE_BLOCK = 0.095;
static const float2 VENT_FRAME = float2(0.034, 0.028);
static const float2 VENT_SLOT = float2(0.022, 0.011);
static const float2 PANEL_C = float2(0.05, -0.085);
static const float2 PANEL = float2(0.04, 0.012);
static const float2 RIVET_ROUND = float2(-0.06, 0.08);
static const float2 RIVET_SQUARE = float2(-0.07, -0.06);

float2x2 rot(float a) { float c = cos(a), s = sin(a); return float2x2(c, -s, s, c); }
float sdEllipsoid(float3 p, float3 r) {
    float k0 = length(p / r), k1 = length(p / (r * r));
    return k0 * (k0 - 1.) / max(k1, 1e-5);
}
float sdBox(float3 p, float3 b, float r) {
    float3 q = abs(p) - b;
    return length(max(q, 0.)) + min(max(q.x, max(q.y, q.z)), 0.) - r;
}
float sdBox2(float2 p, float2 b, float r) {
    float2 q = abs(p) - b + r;
    return length(max(q, 0.)) + min(max(q.x, q.y), 0.) - r;
}
float sdCappedCylinder(float3 p, float3 c, float3 axis, float r, float h) {
    float3 d = p - c; float y = dot(d, axis); float x = length(d - axis * y);
    float2 q = float2(x - r, abs(y) - h);
    return min(max(q.x, q.y), 0.) + length(max(q, 0.));
}
float smin(float a, float b, float k) { float h = clamp(.5 + .5 * (b - a) / k, 0., 1.); return lerp(b, a, h) - k * h * (1. - h); }
float smax(float a, float b, float k) { return -smin(-a, -b, k); }
// Intersection with a 45-degree chamfer r wide along the crease.
float cmax(float a, float b, float r) { return max(max(a, b), (a + b + r) * 0.7071); }
void add(inout float2 h, float d, float m) { if (d < h.x) h = float2(d, m); }

float3 podFrame(float3 p) { float3 s = float3(abs(p.x), p.y, p.z) - POD_C; s.xy = mul(s.xy, rot(ROLL)); s.y /= POD_HEIGHT; return s; }
float3 nozzleAxis() { return normalize(CUT_N); }   // square to the opening, so the face lies flush in it
float3 nozzleCentre() {
    float3 n = normalize(CUT_N);
    float3 p = CUT_P + NOZ_ALONG * normalize(float3(-CUT_N.y, CUT_N.x, 0.));
    return p - n * dot(p - CUT_P, n) - n * (NOZ_H - NOZ_PROUD);
}
float3 lensCentre() { return nozzleCentre() + nozzleAxis() * (NOZ_H - DISH_DEPTH - LENS_R * 0.35); }
// The casing's outer surface: an ellipsoid with a broad flat back and a bevelled outer edge.
float casingOuter(float3 s) {
    // Below the waist the casing narrows toward its pointed tip.
    float3 r = s;
    r.x /= 1. + TAPER * min(s.y, 0.) / SHELL_R.y;
    float d = sdEllipsoid(r, SHELL_R) * min(1., 1. + TAPER * min(s.y, 0.) / SHELL_R.y);
    // The top is an angular shoulder: a nearly flat edge, a broad chamfer down to the outer side and a small one at
    // the spine.
    d = smax(d, s.y - TOP_Y, 0.03);
    d = smax(d, dot(s.xy, normalize(float2(0.77, 0.63))) - TOP_OUTER_D, 0.08);
    d = smax(d, dot(s.xy, normalize(float2(-0.707, 0.707))) - TOP_INNER_D, 0.02);
    d = smax(d, s.z - FACE_Z, 0.02);
    d = smax(d, dot(s.xz, normalize(float2(0.8, 0.6))) - BEVEL_D, 0.025);
    d = smax(d, dot(s.xz, normalize(float2(-0.75, 0.66))) - INNER_BEVEL_D, 0.02);
    return d;
}

// One pod, q in the mirrored world frame (q.x = |x|).
float2 pod(float3 q) {
    float2 h = float2(1e9, 0.);
    float3 s = q - POD_C;
    s.xy = mul(s.xy, rot(ROLL));
    s.y /= POD_HEIGHT;
    float outer = casingOuter(s);
    float inner = casingOuter(s * (SHELL_R / (SHELL_R - SHELL_T)) ) * ((SHELL_R.x - SHELL_T) / SHELL_R.x);
    float back = -(s.z + WRAP);
    float edge = INNER_X - q.x;
    float cut = dot(s - CUT_P, normalize(CUT_N));
    float casing = smax(smax(smax(max(outer, -inner), back, 0.004), edge, 0.005), cut, 0.003);
    // Grooves between plates and between the columns, stopping short of the trimmed rim.
    float b = s.y + BAND_TILT * s.x + BAND_ARCH * s.x * s.x;
    const float edges[4] = { 0.27, 0.16, 0.01, -0.14 };
    float seamD = 1e9;
    for (int i = 0; i < 4; i++) seamD = min(seamD, abs(b - edges[i]));
    seamD = min(seamD, abs(s.x - COLUMN_X));
    float groove = max(max(seamD - SEAM, -GROOVE - outer), cut + TRIM_W);
    float plates = smax(casing, -groove, PLATE_ROUND);
    // The vent: a gunmetal frame recessed into the cap, a black slot deeper in it.
    float2 vp = s.xy - VENT_C;
    float frame = max(sdBox2(vp, VENT_FRAME, 0.007), -0.006 - outer);
    float hook = min(sdBox2(vp - float2(0.004, 0.006), VENT_SLOT, 0.004), sdBox2(vp - float2(-0.016, -0.004), float2(0.008, 0.016), 0.004));
    float slot = max(hook, -0.016 - outer);
    plates = smax(plates, -min(frame, slot), 0.0015);
    // The panel: a grey inset a hair below the band's face.
    float panel = max(sdBox2(s.xy - PANEL_C, PANEL, 0.006), -0.0015 - outer);
    plates = smax(plates, -panel, 0.001);
    // Rivets: a round dimple on the violet inner plate, a square one on the cream plate below it.
    float rivet = min(length(s.xy - RIVET_ROUND) - 0.0045, sdBox2(s.xy - RIVET_SQUARE, float2(0.005, 0.005), 0.0015));
    plates = max(plates, -max(rivet, -0.003 - outer));
    float mat = VIOLET;
    if (b < edges[0] && b > edges[1]) mat = CREAM;
    if (b < edges[2] && b > edges[3]) mat = CREAM;
    if (seamD < SEAM + 0.002 && outer < -0.003) mat = CORE;
    if (panel < 0.002) mat = GREY;
    if (frame < 0.002) mat = FRAME;
    if (slot < 0.002) mat = CORE;
    if (cut > -TRIM_W) mat = GUNMETAL;
    add(h, plates, mat);
    // The inner core stays inside the casing; the thruster is a clean rounded can tucked under the trimmed rim, its face
    // a smooth lip around a shallow dish with the lens at its centre.
    float3 c = nozzleCentre(); float3 ax = nozzleAxis();
    float3 d = s - c; float y = dot(d, ax); float rr = length(d - ax * y);
    float2 w = float2(rr - (NOZ_R - NOZ_ROUND), abs(y) - (NOZ_H - NOZ_ROUND));
    float housing = min(max(w.x, w.y), 0.) + length(max(w, 0.)) - NOZ_ROUND;
    h.x = max(h.x, -housing);
    float core = smax(sdEllipsoid(s - CORE_SHIFT, CORE_R), edge + 0.006, 0.006);
    core = smax(core, cut - CORE_BELOW, 0.01);
    add(h, core, BLACK);
    float dish = length(s - (c + ax * (NOZ_H + DISH_R - DISH_DEPTH))) - DISH_R;
    housing = smax(housing, -dish, 0.008);
    add(h, housing, BLACK);
    add(h, length(s - lensCentre()) - LENS_R, CYAN);    return h;
}

// The spine between the pods, recessed behind the casings' faces and starting just below their shoulders: a stack of
// separate blocks with chamfered edges and gaps between them, each carrying a raised centre strip; the top block is a
// broad chamfered cap, the bottom one a wider block above the thrusters, with a narrower core running on below it.
float chamferBox(float3 p, float3 b, float c) {
    float d = sdBox(p, b, 0.002);
    d = max(d, (abs(p.x) + abs(p.z) - b.x - b.z + c) * 0.7071);
    d = max(d, (abs(p.y) + abs(p.z) - b.y - b.z + c) * 0.7071);
    return max(d, (abs(p.x) + abs(p.y) - b.x - b.y + c * 0.6) * 0.7071);
}
float roundBox(float3 p, float3 b, float r) { return sdBox(p, b - r, r); }
float spine(float3 p) {
    float3 q = p - SPINE_C;
    // The blocks between the cap and the bottom block, repeated down the column.
    float top = SPINE_HALF.y - 0.09, bottom = -SPINE_HALF.y + 0.12;
    float k = clamp(round((q.y - bottom) / SPINE_BLOCK - 0.5), 0., floor((top - bottom) / SPINE_BLOCK) - 1.);
    float3 bq = q - float3(0., bottom + (k + 0.5) * SPINE_BLOCK, 0.);
    float block = roundBox(bq, float3(SPINE_HALF.x * 0.85, SPINE_BLOCK * 0.5 - 0.004, SPINE_HALF.z - 0.008), 0.012);
    float strip = roundBox(bq - float3(0., 0., 0.006), float3(SPINE_HALF.x * 0.32, SPINE_BLOCK * 0.5 - 0.012, SPINE_HALF.z - 0.008), 0.006);
    float d = min(block, strip);
    float cap = roundBox(q - float3(0., SPINE_HALF.y - 0.045, 0.004), float3(SPINE_HALF.x, 0.045, SPINE_HALF.z - 0.004), 0.018);
    float lower = roundBox(q - float3(0., -SPINE_HALF.y + 0.06, 0.), float3(SPINE_HALF.x * 1.05, 0.06, SPINE_HALF.z), 0.016);
    float core = sdBox(q - float3(0., -SPINE_HALF.y - 0.02, -0.012), float3(SPINE_HALF.x * 0.7, 0.04, SPINE_HALF.z - 0.014), 0.012);
    // The column behind the blocks, set well back, closes the gaps.
    float back = sdBox(q - float3(0., 0., -0.02), float3(SPINE_HALF.x * 0.75, SPINE_HALF.y, SPINE_HALF.z - 0.02), 0.004);
    return min(min(min(d, cap), min(lower, core)), back);
}
float2 map(float3 p) {
    float3 q = float3(abs(p.x), p.y, p.z);
    float2 h = pod(q);
    h.x *= min(POD_HEIGHT, 1.);
    add(h, spine(p), SPINE);
    return h;
}

float3 normalAt(float3 p) {
    const float e = 0.0005;
    float2 k = float2(1, -1);
    return normalize(k.xyy * map(p + k.xyy * e).x + k.yyx * map(p + k.yyx * e).x + k.yxy * map(p + k.yxy * e).x + k.xxx * map(p + k.xxx * e).x);
}

float shadow(float3 ro, float3 rd) {
    float res = 1., t = 0.01;
    for (int i = 0; i < 48 && t < 2.; i++) {
        float d = map(ro + rd * t).x;
        res = min(res, 10. * d / t);
        if (res < 0.002) break;
        t += clamp(d, 0.004, 0.08);
    }
    return saturate(res);
}

float ao(float3 p, float3 n) {
    float occ = 0., w = 1.;
    for (int i = 1; i <= 5; i++) { float h = 0.012 * i; occ += w * (h - map(p + n * h).x); w *= 0.7; }
    return saturate(1. - 6. * occ);
}

float3 albedo(float m) {
    if (m == VIOLET) return float3(0.36, 0.17, 0.56);
    if (m == CREAM) return float3(0.78, 0.58, 0.38);
    if (m == GUNMETAL) return float3(0.17, 0.18, 0.2);
    if (m == BLACK) return float3(0.035, 0.035, 0.04);
    if (m == CORE) return float3(0.03, 0.03, 0.035);
    if (m == GREY) return float3(0.42, 0.44, 0.52);
    if (m == FRAME) return float3(0.07, 0.07, 0.08);
    if (m == SPINE) return float3(0.075, 0.075, 0.085);
    return float3(0.1, 0.8, 1.0);
}

// A camera by view index: its target, yaw, pitch and distance.
void camera(int view, out float3 target, out float yaw, out float pitch, out float dist) {
    target = float3(0., 1.25, -0.02); yaw = 0.; pitch = -0.45; dist = 1.75;
    if (view == 1) { yaw = 0.62; pitch = -0.32; }
    if (view == 2) { yaw = 1.5708; pitch = -0.1; }
    if (view == 3) { yaw = 0.; pitch = 0.; }
    if (view == 4) { yaw = 0.; pitch = -1.45; }
    if (view == 6) { yaw = 0.; pitch = -0.05; dist = 0.85; target = float3(0., 1.42, 0.0); }
    if (view == 5) { yaw = 0.3; pitch = -0.55; dist = 0.6; target = float3(0.13, 0.98, 0.02); }
}

float3 render(float2 uv, int view, bool orbit) {
    float3 target; float yaw, pitch, dist;
    camera(view, target, yaw, pitch, dist);
    if (orbit && frameGroup.pointerPresses != 0) {
        yaw = (frameGroup.pointer.x / resolution.x - 0.5) * 2. * PI;
        pitch = clamp((0.5 - frameGroup.pointer.y / resolution.y) * 3.0, -1.5, 1.5);
    }
    float3 ro = target + dist * float3(sin(yaw) * cos(pitch), sin(pitch), cos(yaw) * cos(pitch));
    float3 fw = normalize(target - ro);
    float3 rt = normalize(cross(fw, float3(0, 1, 0)));
    float3 up = cross(rt, fw);
    float3 rd = normalize(fw * 1.9 + uv.x * rt + uv.y * up);

    float3 background = lerp(float3(0.70, 0.68, 0.67), float3(0.80, 0.78, 0.77), uv.y + 0.5);
    float t = 0.; float2 h = float2(1., 0.); bool hit = false;
    for (int i = 0; i < MAX_STEPS; i++) {
        h = map(ro + rd * t);
        if (h.x < 0.0003 * t) { hit = true; break; }
        t += h.x * 0.8;
        if (t > 4.) break;
    }
    if (!hit) return background;
    float3 p = ro + rd * t, n = normalAt(p), v = -rd;
    float3 key = normalize(float3(-0.4, 0.5, 0.75));
    float3 fill = normalize(float3(0.6, -0.4, 0.6));
    float3 col = albedo(h.y);
    float occ = ao(p, n);
    float dif = saturate(dot(n, key)) * shadow(p + n * 0.002, key);
    float fil = saturate(dot(n, fill));
    float sky = 0.5 + 0.5 * n.y;
    float3 hv = normalize(key + v);
    float spec = pow(saturate(dot(n, hv)), h.y == GUNMETAL ? 40. : 60.) * (h.y == BLACK ? 0.25 : 0.3) * dif;
    float rim = pow(1. - saturate(dot(n, v)), 4.) * 0.22;
    float3 light = float3(1.0, 0.97, 0.93) * dif * 1.2 + float3(0.55, 0.6, 0.7) * fil * 0.5 + float3(0.6, 0.62, 0.68) * sky * 0.35 * occ;
    float3 c = col * light + spec + rim * occ * float3(0.8, 0.82, 0.9);
    float lensD = length(podFrame(p) - lensCentre());
    if (h.y == BLACK) c += float3(0.05, 0.6, 1.0) * 0.35 * exp(-lensD * 70.);
    if (h.y == CYAN) {
        float facing = saturate(dot(n, v));
        c = float3(0.0, 0.06, 0.14) + float3(0.0, 0.45, 0.9) * pow(facing, 3.) + float3(0.35, 0.85, 1.0) * pow(facing, 30.);
    }
    return c;
}

float4 shade(float2 pixel) {
    // The sheet splits the image into three columns and two rows, each its own camera.
    int view = VIEW; float2 tile = resolution, origin = 0.;
    if (VIEW < 0) {
        tile = resolution / float2(3., 2.);
        int2 cell = int2(min(floor(pixel / tile), float2(2., 1.)));
        origin = float2(cell) * tile;
        const int sheet[6] = { 2, 6, 5, 3, 0, 1 };   // bottom row first: side, inner section, nozzle; top: rear, rear-low, three-quarter
        view = sheet[cell.y * 3 + cell.x];
    }
    float tileAspect = aspect * (tile.x / resolution.x) / (tile.y / resolution.y);
    float3 color = 0.;
    for (int y = 0; y < AA; y++) for (int x = 0; x < AA; x++) {
        float2 o = (float2(x, y) + 0.5) / AA - 0.5;
        float2 uv = ((pixel - origin + o) / tile - 0.5) * float2(tileAspect, 1.);
        color += render(uv, view, VIEW >= 0);
    }
    color /= AA * AA;
    color = (color * (2.51 * color + 0.03)) / (color * (2.43 * color + 0.59) + 0.14);
    return float4(pow(saturate(color), 1. / 2.2), 1.);
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint width, height;
    output.GetDimensions(width, height);
    if ((id.x >= width) || (id.y >= height)) return;
    resolution = float2(width, height);
    aspect = frameGroup.placedExtent.x / frameGroup.placedExtent.y;
    output[id.xy] = shade(float2(id.x + 0.5, height - (id.y + 0.5)));
}
