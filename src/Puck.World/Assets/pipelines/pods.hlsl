// PODS - a free-form lab for Moth's twin thruster shells, matched to the owner's thruster references
// (docs/game/art/moth-concept-pack/08-thrusters-rear-low.png first, then 09 to 11).
// The avatars/moth world runs this file as the pods-lab source; pipeline.watch pods-lab on reloads saved edits.
// What settles here is rebuilt in moth.puck's constrained creation shapes; this file is never the shipped pack.
// Frame: x to the viewer's right seen from behind, y up, +z out of her back toward a viewer behind her. The pair
// mirrors about x = 0; a pod's local u = |x| runs from the spine outward.
// Drag the pointer to orbit. VIEW picks the start: 0 rear-low (reference 08), 1 rear three-quarter, 2 side,
// 3 straight behind, 4 below, 5 a close-up of the right-hand nozzle.
#define VIEW 0
#define AA 2
#define MAX_STEPS 160
#define SPIN 0

#include "pods.interface.hlsli"

static float2 resolution;
static float aspect;
static const float PI = 3.14159265;

// Materials.
static const float VIOLET = 1., CREAM = 2., GUNMETAL = 3., BLACK = 4., CYAN = 5., GREY = 6., CORE = 7.;

// ---- the shape's parameters, in metres ----
// Each pod is a long shell whose axis leans out at the bottom (ROLL, radians about z, bottom away from the spine),
// so the plates run across that axis and the bottom cut runs along its inner, lower side.
static const float3 POD_C = float3(0.19, 1.27, -0.02);
static const float ROLL = 0.3;
static const float3 SHELL_R = float3(0.155, 0.37, 0.1);
static const float SHELL_T = 0.024;          // plate thickness
static const float INNER_X = 0.04;           // the straight inner edge, beside the spine (world x)
// The cut, in the pod's frame: a plane from the inner side at mid-height to the bottom tip, facing in, down and back.
static const float3 CUT_P = float3(-0.15, -0.12, 0.0);
static const float3 CUT_N = float3(-0.65, -0.76, 0.3);
static const float TRIM_W = 0.008;           // the gunmetal lip along the cut
// Plate edges along the pod's axis, from the top; the cap, band, mid plate, band and skirt fall between them.
static const float SEAM = 0.004;
static const float GROOVE = 0.008;
static const float COLUMN_X = -0.035;        // the seam splitting each plate into an inner and an outer column
// The nozzle housing, in the pod's frame: centred NOZ_ALONG metres along the cut line from CUT_P toward the outer tip and
// NOZ_DROP below the underside, so the wing hoods its top; its axis NOZ_AXIS (world frame) points down and back.
static const float NOZ_ALONG = 0.105;
static const float NOZ_DROP = -0.004;
static const float3 NOZ_AXIS = float3(0.0, -0.72, 0.69);
static const float NOZ_R = 0.062;
static const float NOZ_H = 0.05;
static const float NOZ_ROUND = 0.03;
static const float DISH_R = 0.07;            // the face's concave dish: a sphere this big...
static const float DISH_DEPTH = 0.03;        // ...sunk this far into the face
static const float LENS_R = 0.014;
// The inner core the casing wraps: an ellipsoid in the pod's frame, blended into the nozzle housing.
static const float3 CORE_R = float3(0.105, 0.3, 0.06);
static const float3 CORE_SHIFT = float3(-0.01, -0.02, -0.005);
static const float CORE_BLEND = 0.025;
static const float RING_R = 0.03;          // the inner ring's opening
static const float RING_DEPTH = 0.012;
float2x2 rot(float a) { float c = cos(a), s = sin(a); return float2x2(c, -s, s, c); }
float sdEllipsoid(float3 p, float3 r) {
    float k0 = length(p / r), k1 = length(p / (r * r));
    return k0 * (k0 - 1.) / max(k1, 1e-5);
}
float sdBox(float3 p, float3 b, float r) {
    float3 q = abs(p) - b;
    return length(max(q, 0.)) + min(max(q.x, max(q.y, q.z)), 0.) - r;
}
float sdCappedCylinder(float3 p, float3 c, float3 axis, float r, float h) {
    float3 d = p - c; float y = dot(d, axis); float x = length(d - axis * y);
    float2 q = float2(x - r, abs(y) - h);
    return min(max(q.x, q.y), 0.) + length(max(q, 0.));
}
float smin(float a, float b, float k) { float h = clamp(.5 + .5 * (b - a) / k, 0., 1.); return lerp(b, a, h) - k * h * (1. - h); }
float smax(float a, float b, float k) { return -smin(-a, -b, k); }
void add(inout float2 h, float d, float m) { if (d < h.x) h = float2(d, m); }

float3 nozzleAxis() { float3 a = normalize(NOZ_AXIS); a.xy = mul(a.xy, rot(ROLL)); return a; }
float3 nozzleCentre() {
    float3 n = normalize(CUT_N);
    float3 p = CUT_P + NOZ_ALONG * normalize(float3(-CUT_N.y, CUT_N.x, 0.));
    return p - n * dot(p - CUT_P, n) + n * NOZ_DROP;
}
float3 lensCentre() { return nozzleCentre() + nozzleAxis() * (NOZ_H - DISH_DEPTH - RING_DEPTH + LENS_R * 0.4); }
// A world point in one pod's frame (the pod on the point's side).
float3 podFrame(float3 p) { float3 s = float3(abs(p.x), p.y, p.z) - POD_C; s.xy = mul(s.xy, rot(ROLL)); return s; }
// One pod, q in the mirrored world frame (q.x = |x|).
float2 pod(float3 q) {
    float2 h = float2(1e9, 0.);
    // The pod's own frame: centred, rolled so its axis is local y; local -x faces the spine.
    float3 s = q - POD_C;
    s.xy = mul(s.xy, rot(ROLL));
    float outer = sdEllipsoid(s, SHELL_R);
    float back = -(s.z + 0.01);
    float edge = INNER_X - q.x;
    float cut = dot(s - CUT_P, normalize(CUT_N));
    // The casing: a shell SHELL_T thick over the back half, straight along the spine, open under the cut.
    float inner = sdEllipsoid(s, SHELL_R - SHELL_T);
    float solid = smax(smax(smax(max(outer, -inner), back, 0.004), edge, 0.006), cut, 0.004);
    // Seams are grooves SEAM wide and GROOVE deep in the armour face, between plates and between the two columns.
    const float edges[4] = { 0.2, 0.09, -0.01, -0.15 };
    float seamD = 1e9;
    for (int i = 0; i < 4; i++) seamD = min(seamD, abs(s.y - edges[i]));
    seamD = min(seamD, abs(s.x - COLUMN_X));
    float groove = max(seamD - SEAM, -GROOVE - outer);
    groove = max(groove, cut + TRIM_W);   // seams stop at the trimmed rim
    float plates = smax(solid, -groove, 0.0015);
    // The vent: a framed slot in the cap's inner column.
    float vent = max(sdBox(s - float3(-0.075, 0.27, 0.12), float3(0.022, 0.012, 0.08), 0.006), -0.014 - outer);
    plates = smax(plates, -vent, 0.003);
    float mat = VIOLET;
    if (s.y < edges[0] && s.y > edges[1]) mat = CREAM;
    if (s.y < edges[2] && s.y > edges[3]) mat = CREAM;
    if (seamD < SEAM + 0.002 && outer < -0.003) mat = CORE;
    if (vent < 0.004) mat = CORE;
    // The casing's open rim is trimmed in gunmetal.
    if (cut > -TRIM_W) mat = GUNMETAL;
    add(h, plates, mat);
    // The nozzle: a soft black housing tucked under the wing's trimmed edge, which hoods its top, with a concave dish
    // face and a small glowing lens deep in the dish.
    float3 c = nozzleCentre(); float3 ax = nozzleAxis();
    float3 d = s - c; float y = dot(d, ax); float rr = length(d - ax * y);
    float2 w = float2(rr - (NOZ_R - NOZ_ROUND), abs(y) - (NOZ_H - NOZ_ROUND));
    float housing = min(max(w.x, w.y), 0.) + length(max(w, 0.)) - NOZ_ROUND;
    float body = housing;
    float dish = length(s - (c + ax * (NOZ_H + DISH_R - DISH_DEPTH))) - DISH_R;
    housing = smax(housing, -dish, 0.006);
    // The dish steps down through an inner ring to the lens.
    float3 floorC = c + ax * (NOZ_H - DISH_DEPTH);
    float well = sdCappedCylinder(s, floorC, ax, RING_R, RING_DEPTH);
    housing = smax(housing, -well, 0.003);
    // The inner core: a dark body inside the casing whose lower end swells out of the open rim as the thruster.
    float core = sdEllipsoid(s - CORE_SHIFT, CORE_R);
    core = smax(core, edge + 0.006, 0.006);
    core = smin(core, body, CORE_BLEND);
    core = smax(core, -dish, 0.006);
    core = smax(core, -well, 0.003);
    add(h, core, BLACK);
    add(h, length(s - lensCentre()) - LENS_R, CYAN);    return h;
}
// The spine between the pods: a ribbed black column.
float spine(float3 p) {
    float3 q = p - float3(0., 1.29, -0.07);
    float d = sdBox(q, float3(0.045, 0.25, 0.05), 0.01);
    float ribs = sdBox(float3(q.x, (frac(q.y / 0.06 + 0.5) - 0.5) * 0.06, q.z - 0.05), float3(0.032, 0.012, 0.01), 0.003);
    return min(d, max(ribs, abs(q.y) - 0.25));
}

float2 map(float3 p) {
    float3 q = float3(abs(p.x), p.y, p.z);
    float2 h = pod(q);
    add(h, spine(p), BLACK);
    return h;
}

float3 normalAt(float3 p) {
    const float e = 0.0006;
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
    if (m == GUNMETAL) return float3(0.2, 0.21, 0.24);
    if (m == BLACK) return float3(0.035, 0.035, 0.04);
    if (m == CORE) return float3(0.05, 0.05, 0.06);
    if (m == GREY) return float3(0.55, 0.57, 0.62);
    return float3(0.1, 0.8, 1.0);
}

float3 render(float2 uv) {
    float3 target = float3(0., 1.25, -0.02);
    float yaw = 0., pitch = -0.72, dist = 1.75;
    if (VIEW == 1) { yaw = 0.62; pitch = -0.32; }
    if (VIEW == 2) { yaw = 1.5708; pitch = -0.1; }
    if (VIEW == 3) { yaw = 0.; pitch = 0.; }
    if (VIEW == 4) { yaw = 0.; pitch = -1.45; }
    if (VIEW == 5) { yaw = 0.35; pitch = -0.7; dist = 0.55; target = float3(0.15, 1.08, 0.0); }
    if (SPIN == 1) yaw += frameGroup.time * 0.4;
    if (frameGroup.pointerPresses != 0) {
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
        if (h.x < 0.0004 * t) { hit = true; break; }
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
    float spec = pow(saturate(dot(n, hv)), h.y == GUNMETAL ? 40. : 60.) * (h.y == BLACK ? 0.25 : 0.35) * dif;
    float rim = pow(1. - saturate(dot(n, v)), 4.) * 0.25;
    float3 light = float3(1.0, 0.97, 0.93) * dif * 1.25 + float3(0.55, 0.6, 0.7) * fil * 0.45 + float3(0.6, 0.62, 0.68) * sky * 0.35 * occ;
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
    float3 color = 0.;
    for (int y = 0; y < AA; y++) for (int x = 0; x < AA; x++) {
        float2 o = (float2(x, y) + 0.5) / AA - 0.5;
        float2 uv = ((pixel + o) / resolution - 0.5) * float2(aspect, 1.);
        color += render(uv);
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
