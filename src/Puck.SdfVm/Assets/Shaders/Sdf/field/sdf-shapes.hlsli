// The primitive distance functions, the glyph and sampled-region primitives, the sweep, and evaluateShape.
#ifndef FIELD_SDF_SHAPES_HLSLI
#define FIELD_SDF_SHAPES_HLSLI
float sdfSphere(float3 p, float radius) {
    return (length(p) - radius);
}
// `cornerRadius` deliberately does NOT shadow the HLSL round() intrinsic (which this file's fold ops rely on).
float sdfBox(float3 p, float3 halfExtents, float cornerRadius) {
    float3 q = (abs(p) - (halfExtents - cornerRadius));
    return ((length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0)) - cornerRadius);
}
float sdfTorus(float3 p, float major, float minor) {
    float2 q = float2((length(p.xz) - major), p.y);
    return (length(q) - minor);
}
float sdfPlane(float3 p, float3 normal, float offset) {
    return (dot(p, normal) + offset);
}
// inverseLengthSquared = 1/dot(endpoint, endpoint), HOST-BAKED (data1.y) by SdfProgramBuilder.Capsule.
float sdfCapsule(float3 p, float3 endpoint, float radius, float inverseLengthSquared) {
    float h = clamp((dot(p, endpoint) * inverseLengthSquared), 0.0, 1.0);
    return (length(p - (h * endpoint)) - radius);
}
float sdfCylinder(float3 p, float radius, float halfHeight) {
    float2 d = (float2(length(p.xz), abs(p.y)) - float2(radius, halfHeight));
    return (min(max(d.x, d.y), 0.0) + length(max(d, 0.0)));
}
// The ellipsoid: the superellipsoid's scaled gauge at e = 2, (|p/r| - 1) * min(r). EXACTLY 1-Lipschitz at every
// eccentricity (SdfProgramBuilder.Superellipsoid's proof covers e = 2), so it needs no step clamp and no field scope;
// it shares its zero set with the true ellipsoid and underestimates Euclidean distance away from it. The center
// returns -min(r) like the general path's m <= 0 branch (length(0) = 0). KEEP IN SYNC with the e == 2 fast path of
// Puck.SignedDistance.Queries.SdfFieldEvaluator.SdfSuperellipsoid.
float sdfEllipsoidGauge(float3 p, float3 radii, float3 inverseRadii) {
    float minRadius = min(radii.x, min(radii.y, radii.z));

    return ((length(p * inverseRadii) - 1.0) * minRadius);
}
#ifndef SDF_STRIP_HEAVY
float sdfSuperellipsoidNormFactor(float exponent) {
    return 1.0 - (7.0 / 25.0) * (exponent - 2.0);
}
bool sdfSuperellipsoidNormIsClamped(float approximate, float norm, float exponent) {
    return ((asuint(approximate) & 0x7F800000u) == 0x7F800000u)
        || approximate < sdfSuperellipsoidNormFactor(exponent) * norm || approximate > norm;
}
// For 2 < e <= 3 the exact Lp norm lies between (1-(7/25)*(e-2))*L2 and L2. The rational lower
// factor follows from convexity of 3^(1/e-1/2) and ln(3)/4 < 7/25. The tape certificate charges
// the complete band width plus endpoint arithmetic error, without assuming an error bound for pow.
float sdfClampSuperellipsoidNorm(float approximate, float norm, float exponent) {
    if ((asuint(approximate) & 0x7F800000u) == 0x7F800000u) { return norm; }
    return clamp(approximate, sdfSuperellipsoidNormFactor(exponent) * norm, norm);
}
float sdfSuperellipsoidPowerNorm(float3 q, float m, float exponent) {
    float3 u = pow(q / m, exponent);
    return m * pow((u.x + u.y) + u.z, 1.0 / exponent);
}
// The superellipsoid: q = pow(abs(p) * inverseRadii, e); d = (pow(q.x+q.y+q.z, 1/e) - 1) * min(r). EXACTLY
// 1-Lipschitz for every radius and every e >= 1 (see SdfProgramBuilder.Superellipsoid's remarks for the proof) — no
// AnalyzeLipschitz step clamp is needed. e == 2 (every ellipsoid) takes sdfEllipsoidGauge's pow-free form, the same
// field. inverseRadii = 1/max(abs(radii), eps),
// HOST-BAKED (data1.yzw); minRadius = min(abs(radii)) is cheap enough to read straight off data0.xyz per eval.
// The l_e gauge is computed in its FACTORED form, m * (sum((q_i/m)^e))^(1/e) with m = max(q): every pow() argument
// stays in [0, 1] (the sum in [1, 3]), so a far query never overflows — the plain sum(q_i^e) reaches float infinity at
// |p|/r ~ 1e5 for e = 8 (and saturates the fixed-point mirror's Q48.16 carrier at |p|/r ~ 60), which would collapse
// the far field. Mathematically identical to sum(q_i^e)^(1/e). KEEP IN SYNC with
// Puck.SignedDistance.Queries.SdfFieldEvaluator.SdfSuperellipsoid.
float sdfSuperellipsoid(float3 p, float3 radii, float3 inverseRadii, float exponent) {
    if (exponent == 2.0) {
        return sdfEllipsoidGauge(p, radii, inverseRadii);
    }

    float3 q = (abs(p) * inverseRadii);
    float m = max(q.x, max(q.y, q.z));
    float minRadius = min(radii.x, min(radii.y, radii.z));

    if (m <= 0.0) {
        return -minRadius;
    }

    float norm = sdfSuperellipsoidPowerNorm(q, m, exponent);
    if (exponent > 2.0 && exponent <= 3.0) {
        norm = sdfClampSuperellipsoidNorm(norm, length(q), exponent);
    }
    return (norm - 1.0) * minRadius;
}
#endif
// slope b = (lowerRadius - upperRadius)/height and its complement a = sqrt(1 - b*b) are HOST-BAKED (data0.w / data1.y).
float sdfRoundCone(float3 p, float lowerRadius, float upperRadius, float height, float b, float a) {
    float2 q = float2(length(p.xz), p.y);
    float k = dot(q, float2(-b, a));

    if (k < 0.0) {
        return (length(q) - lowerRadius);
    }

    if (k > (a * height)) {
        return (length(q - float2(0.0, height)) - upperRadius);
    }

    return (dot(q, float2(a, b)) - lowerRadius);
}
// The vesica (lens): the exact 2D vesica revolved around Y. r = the two circles' radius, d = their half-separation (d < r),
// b = sqrt(r*r - d*d) = the tip half-height, HOST-BAKED by SdfProgramBuilder.Vesica to skip the per-eval sqrt. Exact
// and convex, so revolving the exact 2D field yields a true 3D distance (earns a cull bound, factor-1 Lipschitz). The
// lens is pointed along +/-Y and is a disc of radius (r - d) in the XZ plane.
float sdfVesica(float3 p, float r, float d, float b) {
    float2 q = float2(length(p.xz), abs(p.y));

    return (((q.y - b) * d) > (q.x * b))
        ? (length(q - float2(0.0, b)))
        : (length(q - float2(-d, 0.0)) - r);
}

// === The 2D-primitive family: exact 2D SDFs lifted to 3D solids =====================================================
// Each primitive is an exact 2D signed-distance field, then LIFTED to 3D one of two ways (data1.y): EXTRUDE along Z
// (a prism/slab) or REVOLVE around Y (a lathe). Extrusion of an exact 2D field is exact; revolution is exact when the
// profile clears the axis and a harmless conservative underestimate near it. Both are 1-Lipschitz, so no
// AnalyzeLipschitz step clamp is needed and each earns a real cull bound. The lifted wrappers below are what
// evaluateShape calls; the 2D cores are reusable.

// --- lift operators (extrude along Z / revolve around Y) ---
// Extrude an exact 2D distance d (evaluated on the XY plane) to half-height h along Z: exact for any exact d.
float sdfExtrude2D(float d, float pz, float h) {
    float2 w = float2(d, (abs(pz) - h));
    return (min(max(w.x, w.y), 0.0) + length(max(w, 0.0)));
}
// The chamfered extrude join: sdfExtrude2D's box/slab intersection, further intersected with a 45-degree bevel plane
// across the cap seam — the chamfer-intersection of the 2D field and the +/-Z slab, so an extruded 2D shape's TOP/
// BOTTOM edges bevel by c as well as whatever its own profile does at the sides. Exact inside/on the surface, a
// 1-Lipschitz lower bound outside past the bevel's vertex (see sdfChamferBox2D). c = 0 collapses to sdfExtrude2D
// exactly: w.x + w.y is bounded above by sqrt(2)*length(max(w,0)) (equality only on-axis) plus the (always
// non-positive) `min(max(w.x,w.y),0)` inside term, so the bevel arm never exceeds the plain join and max() picks the
// plain value unchanged to the bit.
float sdfExtrudeChamfer2D(float d, float pz, float h, float c) {
    float2 w = float2(d, (abs(pz) - h));
    float plain = (min(max(w.x, w.y), 0.0) + length(max(w, 0.0)));
    float bevel = ((w.x + w.y + c) * SDF_SQRT_HALF);
    return max(plain, bevel);
}
// The meridian point for revolving around Y at radial offset o: the 2D core is evaluated at (length(p.xz) - o, p.y).
float2 sdfRevolve2D(float3 p, float o) {
    return float2((length(p.xz) - o), p.y);
}

// --- exact 2D cores ---
// Rounded box, single corner radius r: the box half-extents are b, corners rounded by r (staying within b).
float sdfRoundBox2D(float2 p, float2 b, float r) {
    float2 q = ((abs(p) - b) + r);
    return ((min(max(q.x, q.y), 0.0) + length(max(q, 0.0))) - r);
}
// 45-degree-chamfered box: the plain box field intersected (max) with a diagonal bevel plane offset by chamfer c.
// Exact inside and on the surface and a 1-Lipschitz conservative LOWER BOUND outside, in the wedge past each bevel
// vertex where the nearest point is the vertex rather than either plane (the same class of bound the chamfer blend
// carries; march- and contact-safe, not the branchy true-distance form). c = 0 collapses to the plain box exactly, by
// the same triangle-inequality argument as sdfExtrudeChamfer2D (q.x + q.y is bounded above by
// sqrt(2)*length(max(q,0)) for q outside the box, so the bevel arm never wins).
float sdfChamferBox2D(float2 p, float2 b, float c) {
    float2 q = (abs(p) - b);
    float boxDistance = (min(max(q.x, q.y), 0.0) + length(max(q, 0.0)));
    float bevel = ((q.x + q.y + c) * SDF_SQRT_HALF);
    return max(boxDistance, bevel);
}
// Isosceles trapezoid: r1 = bottom half-width, r2 = top half-width, he = half-height.
float sdfTrapezoid2D(float2 p, float r1, float r2, float he) {
    float2 k1 = float2(r2, he);
    float2 k2 = float2((r2 - r1), (2.0 * he));
    p.x = abs(p.x);
    float2 ca = float2((p.x - min(p.x, ((p.y < 0.0) ? r1 : r2))), (abs(p.y) - he));
    float2 cb = ((p - k1) + (k2 * clamp((dot((k1 - p), k2) / dot(k2, k2)), 0.0, 1.0)));
    float s = (((cb.x < 0.0) && (ca.y < 0.0)) ? -1.0 : 1.0);
    return (s * sqrt(min(dot(ca, ca), dot(cb, cb))));
}
// Exact star-polygon SDF (n points, inner-radius control m): r = outer radius, an = pi/n (baked), ecs = (cos(pi/m), sin(pi/m))
// (baked). ecs = (0, 1) collapses this to the exact regular n-gon (m = 2). The GLSL `mod` is FLOOR modulo — use the
// house sdfFloorMod so a negative atan2 sector index folds correctly (HLSL fmod truncates and would mis-fold).
float sdfStar2D(float2 p, float r, float an, float2 ecs) {
    float2 acs = float2(cos(an), sin(an));
    float bn = (sdfFloorMod(atan2(p.x, p.y), (2.0 * an)) - an);
    p = (length(p) * float2(cos(bn), abs(sin(bn))));
    p = (p - (r * acs));
    p = (p + (ecs * clamp((-dot(p, ecs)), 0.0, ((r * acs.y) / ecs.y))));
    return (length(p) * sign(p.x));
}
// Exact distance to an ellipse with semi-axes ab (the analytic depressed-cubic solve; branches on the
// discriminant). The host guards ab.x != ab.y (l = ab.y^2 - ab.x^2 divides), so a perfect circle never reaches here.
// The final sqrt is SATURATED: at extreme eccentricity (measured: aspect > ~100:1) rounding pushes `co` past 1, and an
// unguarded sqrt(1 - co*co) returns NaN — which then poisons the blend min() and diverges between backends, whose
// min(NaN, x) differ. saturate() only ever clamps the negative side (co*co >= 0 bounds the argument above by 1), so it
// leaves finite inputs unchanged. At the exact centre p == (0,0) this returns 0 rather than
// -min(ab): sign(p.y - r.y) is 0 there. That is a deliberate convention at a measure-zero point, and only observable
// through a Subtraction/Onion of an ellipse exactly at its centre.
#include "sdf-ellipse.hlsli"

// --- lifted wrappers (data1.y > 0.5 selects EXTRUDE; else REVOLVE) — what evaluateShape dispatches to ---
// data1.w is the family-wide EDGE-ROUNDING radius r. The host already inset the Data0 profile params (and, for an
// extrude, the lift half-height) by r, so subtracting r here is the outward half of a morphological opening: the
// solid keeps the authored outer extent and its edges fillet at radius r. Subtracting exactly 0 is the identity on
// every finite float, so a shape that authors no rounding evaluates to the same bits as before this lane had meaning.
// data1.z is a CAP-chamfer radius bevelling the extrude's top/bottom rims — 0 (every program predating this lane)
// takes the plain join exactly, so an unchamfered rounded rectangle is unchanged. Revolve has no cap seam to bevel
// and ignores it, matching the family's existing per-shape-constant convention for data1.z (RegularPolygon/Star bake
// ecs.y there instead; RoundedRectangle's lane was unused before this).
float sdfRoundedRect(float3 p, float4 data0, float4 data1) {
    return (((data1.y > 0.5)
        ? sdfExtrudeChamfer2D(sdfRoundBox2D(p.xy, data0.xy, data0.z), p.z, data0.w, data1.z)
        : sdfRoundBox2D(sdfRevolve2D(p, data0.w), data0.xy, data0.z)) - data1.w);
}
// The chamfered rectangle: the 2D core already bevels its own four corners at c (data0.z); the extrude join bevels
// the two cap rims at the SAME c, so a chamfered box reads chamfered on all twelve edges from one parameter. data1.w
// is the family-wide edge-rounding radius, applied on top exactly as sdfRoundedRect's is.
float sdfChamferedRect(float3 p, float4 data0, float4 data1) {
    return (((data1.y > 0.5)
        ? sdfExtrudeChamfer2D(sdfChamferBox2D(p.xy, data0.xy, data0.z), p.z, data0.w, data0.z)
        : sdfChamferBox2D(sdfRevolve2D(p, data0.w), data0.xy, data0.z)) - data1.w);
}
// Regular polygon AND star share this: data0 = (r, an, ecs.x, lift), data1.z = ecs.y (the polygon bakes ecs = (0, 1)).
float sdfPolyStar(float3 p, float4 data0, float4 data1) {
    float2 ecs = float2(data0.z, data1.z);

    return (((data1.y > 0.5)
        ? sdfExtrude2D(sdfStar2D(p.xy, data0.x, data0.y, ecs), p.z, data0.w)
        : sdfStar2D(sdfRevolve2D(p, data0.w), data0.x, data0.y, ecs)) - data1.w);
}
// data1.z is a CAP-chamfer radius (0 = the plain extrude join exactly), unused before this lane and free here — the
// Trapezoid shape never baked a per-shape constant into it.
float sdfTrapezoidSolid(float3 p, float4 data0, float4 data1) {
    return (((data1.y > 0.5)
        ? sdfExtrudeChamfer2D(sdfTrapezoid2D(p.xy, data0.x, data0.y, data0.z), p.z, data0.w, data1.z)
        : sdfTrapezoid2D(sdfRevolve2D(p, data0.w), data0.x, data0.y, data0.z)) - data1.w);
}
// data1.z is a CAP-chamfer radius (0 = the plain extrude join exactly), unused before this lane.
float sdfEllipseSolid(float3 p, float4 data0, float4 data1) {
    return (((data1.y > 0.5)
        ? sdfExtrudeChamfer2D(sdfEllipse2D(p.xy, data0.xy), p.z, data0.w, data1.z)
        : sdfEllipse2D(sdfRevolve2D(p, data0.w), data0.xy)) - data1.w);
}
// One vertex of a SDF_SHAPE_CONVEX_POLYGON side table (see its own define comment): two packed (x, y) vertices per
// uvec4 word in sdfWords, so vertex index i lives at word (tableOffset + (i >> 1)), lane .xy for an even index and
// .zw for an odd one.
float2 sdfPolygonVertex(uint tableOffset, uint index) {
    uint4 packed = sdfWords[(tableOffset + (index >> 1u))];

    return (((index & 1u) != 0u) ? asfloat(packed.zw) : asfloat(packed.xy));
}
// Exact signed distance to the convex polygon (`count` vertices starting at `tableOffset`, clockwise — this form is
// correct for any simple polygon, convex or not, which is why convexity is validated at authoring time rather than
// here). The running minimum squared distance to every edge SEGMENT (projection clamped to [0, 1], not the infinite
// line), signed by one even/odd crossing-parity flip per edge (iq's sdPolygon) — sqrt-free per edge, one sqrt total.
float sdfConvexPolygon2D(float2 p, uint tableOffset, uint count) {
    float2 firstVertex = sdfPolygonVertex(tableOffset, 0u);
    float2 previous = sdfPolygonVertex(tableOffset, (count - 1u));
    float2 delta0 = (p - firstVertex);
    float d = dot(delta0, delta0);
    float s = 1.0;

    for (uint i = 0u; (i < count); i++) {
        float2 vertex = sdfPolygonVertex(tableOffset, i);
        float2 e = (previous - vertex);
        float2 w = (p - vertex);
        float2 b = (w - (e * clamp((dot(w, e) / dot(e, e)), 0.0, 1.0)));

        d = min(d, dot(b, b));

        bool3 c = bool3((p.y >= vertex.y), (p.y < previous.y), ((e.x * w.y) > (e.y * w.x)));

        if (all(c) || all(!c)) {
            s = -s;
        }

        previous = vertex;
    }

    return (s * sqrt(d));
}
// Lifted wrapper, the family's usual convention: data0.w = lift amount, data1.y = lift mode, data1.z = cap chamfer,
// data1.w = edge-rounding radius. tableOffset/count are packed into data0.x's reinterpreted uint bits (KEEP IN SYNC
// with SdfProgramBuilder.ConvexPolygon / SdfProgram's table-offset patch).
float sdfConvexPolygonSolid(float3 p, float4 data0, float4 data1) {
    uint packed = asuint(data0.x);
    uint count = (packed & 0xFu);
    uint tableOffset = (packed >> 4u);

    return (((data1.y > 0.5)
        ? sdfExtrudeChamfer2D(sdfConvexPolygon2D(p.xy, tableOffset, count), p.z, data0.w, data1.z)
        : sdfConvexPolygon2D(sdfRevolve2D(p, data0.w), tableOffset, count)) - data1.w);
}
// Path tables: two float4 words per edge, (A.xy,B.xy), (radiusA,radiusB,0,0).
// The stroke segment is the exact convex hull of endpoint disks, including containment when one radius
// dominates the entire segment. It does not subtract an approximation margin or move the zero surface.
float sdfPathStrokeEdge(float2 p, float2 a, float2 b, float ra, float rb) {
    float2 ab = b - a;
    float len = length(ab);
    if (abs(ra - rb) >= len) {
        return (ra >= rb) ? length(p - a) - ra : length(p - b) - rb;
    }
    float2 u = ab / len;
    float2 v = p - a;
    float x = dot(v, u);
    float y = abs(v.x * u.y - v.y * u.x);
    float k = (ra - rb) / len;
    float c = sqrt(max(0.0, 1.0 - k * k));
    float along = x * c - y * k;
    if (along < 0.0) return length(v) - ra;
    if (along > len * c) return length(p - b) - rb;
    return x * k + y * c - ra;
}
float sdfPathSolid(float3 p, float4 data0, float4 data1) {
    uint offset = asuint(data0.x);
    uint count = (uint)data0.y;
    float distance = SDF_FAR_DISTANCE;
    float squared = SDF_FAR_DISTANCE * SDF_FAR_DISTANCE;
    bool inside = false;
    bool stroke = data1.y > 0.5;
    for (uint i = 0u; i < count; ++i) {
        float4 edge = asfloat(sdfWords[offset + 2u * i]);
        float2 a = edge.xy;
        float2 b = edge.zw;
        if (stroke) {
            float2 radii = asfloat(sdfWords[offset + 2u * i + 1u].xy);
            distance = min(distance, sdfPathStrokeEdge(p.xy, a, b, radii.x, radii.y));
        } else {
            float2 e = b - a;
            float2 w = p.xy - a;
            float2 d = w - e * saturate(dot(w, e) / dot(e, e));
            squared = min(squared, dot(d, d));
            bool3 crossing = bool3(p.y >= a.y, p.y < b.y, e.x * w.y > e.y * w.x);
            if (all(crossing) || all(!crossing)) inside = !inside;
        }
    }
    if (!stroke) distance = (inside ? -1.0 : 1.0) * sqrt(squared);
    return sdfExtrudeChamfer2D(distance, p.z, data0.w, 0.0);
}
// === end 2D-primitive family =======================================================================================

// === SDF_SHAPE_GLYPH: a font atlas sampled as a DISTANCE-level field =================================================
// Text as REAL geometry: a glyph cell's exact 2D quad (the box in XY) extruded along Z, its interior refined by the
// atlas's true signed distance so the LETTER — not the cell — is the surface. It marches, blends, extrudes, and (with
// Subtraction) ENGRAVES into any surface. data0 = (packedUvMin, packedUvMax, distanceScale, extrudeHalfDepth); data1 =
// (smooth, halfWidth, halfHeight, samplingCorrection). distanceScale = atlas distanceRange(texels) × worldPerTexel
// (host-baked), so the encoded [0,1] field maps to LOCAL units before its derivative-bound correction.
//
// LIPSCHITZ: bilinear alpha is NOT generally 1-Lipschitz. Data1.w carries the host's reciprocal derivative bound,
// computed from decoded pixels (or worst-case RGBA8 differences), packed UVs and cell scale. Correct only the
// sampled term before max with the exact quad: both terms then have bound <=1, including the far-field seam.
// Extrusion preserves that bound and the reconstructed zero set. KEEP IN SYNC with GlyphSamplingCorrection.

// Host-baked unorm2x16 of an atlas UV, unpacked: the low 16 bits are u, the high 16 v, each /65535. An integer bit op,
// so it is bit-identical across DXC's SPIR-V and DXIL targets. Packing the four UV-rect components into two lanes frees
// data1.x for the ISA-wide smooth-blend radius (the ≤0.06-texel error at 4K is sub-texel at any authoring scale).
float2 sdfGlyphUnpackUv(float packed) {
    uint bits = asuint(packed);

    return (float2((bits & 0xFFFFu), (bits >> 16u)) * (1.0 / 65535.0));
}
// The extruded-quad FALLBACK: the glyph cell as a plain box, exact and 1-Lipschitz. Every kernel WITHOUT the atlas
// bound (the beam cull) evaluates this — a conservative UNDERESTIMATE of the true glyph distance, since the letter is
// strictly inside its cell, so the cull never holes. halfWidth/halfHeight in data1.yz, extrudeHalfDepth in data0.w.
float sdfGlyphQuad(float3 p, float4 data0, float4 data1) {
    float2 b = (abs(p.xy) - float2(data1.y, data1.z));
    float dQuad = (length(max(b, 0.0)) + min(max(b.x, b.y), 0.0));
    float2 w = float2(dQuad, (abs(p.z) - data0.w));

    return (min(max(w.x, w.y), 0.0) + length(max(w, 0.0)));
}

#ifdef SDF_GLYPH_ATLAS
// Alpha holds single-channel boundary-distance samples from `puck font-atlas` or a compatible imported atlas.
// RGB median is coverage-only: channel conflicts need not define a conservative march field.
// Edge-clamped; SampleLevel(…, 0) because
// implicit-derivative filtering is undefined inside the march's non-uniform control flow.
float sdfGlyphTexelAlpha(int2 texel, int2 dims) {
    int2 clamped = clamp(texel, int2(0, 0), (dims - int2(1, 1)));
    float2 uv = ((float2(clamped) + 0.5) / float2(dims));

    return sdfGlyphAtlas.SampleLevel(samplers[SDF_FILTER_NEAREST], uv, 0.0).a;
}
// Manual bilinear of the true single-channel field: four point taps + arithmetic lerp, NOT a hardware LINEAR sampler,
// so the reconstruction is bit-stable across both DXC backends (a driver's bilinear can differ ±1 LSB — the exact
// class WorldHighContrast is calibrated for, but manual keeps the field itself identical).
float sdfGlyphSampleField(float2 uv) {
    uint2 udims;
    sdfGlyphAtlas.GetDimensions(udims.x, udims.y);

    int2 dims = int2(udims);
    float2 texelF = ((uv * float2(dims)) - 0.5);
    float2 baseF = floor(texelF);
    float2 f = (texelF - baseF);
    int2 b = int2(baseF);
    float a00 = sdfGlyphTexelAlpha((b + int2(0, 0)), dims);
    float a10 = sdfGlyphTexelAlpha((b + int2(1, 0)), dims);
    float a01 = sdfGlyphTexelAlpha((b + int2(0, 1)), dims);
    float a11 = sdfGlyphTexelAlpha((b + int2(1, 1)), dims);

    return lerp(lerp(a00, a10, f.x), lerp(a01, a11, f.x), f.y);
}
// One texel's RGB pseudo-distances (edge-clamped, same tap discipline as sdfGlyphTexelAlpha).
float3 sdfGlyphTexelRgb(int2 texel, int2 dims) {
    int2 clamped = clamp(texel, int2(0, 0), (dims - int2(1, 1)));
    float2 uv = ((float2(clamped) + 0.5) / float2(dims));

    return sdfGlyphAtlas.SampleLevel(samplers[SDF_FILTER_NEAREST], uv, 0.0).rgb;
}
// Per-channel manual bilinear then MEDIAN-OF-3 — the classic MSDF reconstruction, for SHADE-TIME consumers ONLY (the
// GlyphDecal tier): median restores the sharp corners the single channel rounds, and its C0 kinks at channel-crossover
// lines are harmless to a coverage threshold — but they kink a marched field, so GEOMETRY keeps sdfGlyphSampleField.
// A replicated single-channel atlas (the runtime exact-EDT generator) medians to exactly its own value, so this decode
// is bit-identical to the alpha path there and only diverges — toward crisper corners — on a true MTSDF atlas.
float sdfGlyphSampleFieldMedian(float2 uv) {
    uint2 udims;
    sdfGlyphAtlas.GetDimensions(udims.x, udims.y);

    int2 dims = int2(udims);
    float2 texelF = ((uv * float2(dims)) - 0.5);
    float2 baseF = floor(texelF);
    float2 f = (texelF - baseF);
    int2 b = int2(baseF);
    float3 s00 = sdfGlyphTexelRgb((b + int2(0, 0)), dims);
    float3 s10 = sdfGlyphTexelRgb((b + int2(1, 0)), dims);
    float3 s01 = sdfGlyphTexelRgb((b + int2(0, 1)), dims);
    float3 s11 = sdfGlyphTexelRgb((b + int2(1, 1)), dims);
    float3 s = lerp(lerp(s00, s10, f.x), lerp(s01, s11, f.x), f.y);

    return max(min(s.r, s.g), min(max(s.r, s.g), s.b));
}
float sdfGlyph(float3 p, float4 data0, float4 data1) {
    float2 halfSize = float2(data1.y, data1.z);
    float2 b = (abs(p.xy) - halfSize);
    float dQuad = (length(max(b, 0.0)) + min(max(b.x, b.y), 0.0));
    float distanceScale = data0.z;
    float dPlane = dQuad;

    // Band cull: beyond ±distanceRange/2 of an edge the encoded field saturates, so a tap there tells us nothing the
    // exact quad distance dQuad does not already bound — skip the texture read entirely (the whole perf trick, and the
    // conservative far-field at once). Inside the band, refine to the letter's true distance. The max() with dQuad
    // keeps the band→proxy seam a valid UNDERESTIMATE: the saturated atlas UNDER-reports, so the exact quad wins there.
    if (dQuad < (0.5 * distanceScale)) {
        float2 uvMin = sdfGlyphUnpackUv(data0.x);
        float2 uvMax = sdfGlyphUnpackUv(data0.y);
        float2 uv = lerp(uvMin, uvMax, clamp((((p.xy / halfSize) * 0.5) + 0.5), 0.0, 1.0));
        float encoded = sdfGlyphSampleField(uv);   // true single-channel distance; 0.5 = edge, > 0.5 inside the glyph

        dPlane = max(((0.5 - encoded) * distanceScale * data1.w), dQuad);
    }

    float2 w = float2(dPlane, (abs(p.z) - data0.w));

    return (min(max(w.x, w.y), 0.0) + length(max(w, 0.0)));
}
#endif
// === end SDF_SHAPE_GLYPH ============================================================================================

// === SDF_SHAPE_SAMPLED_REGION: a baked carve-union field sampled O(1) =================================================
// The settled-carve UNION distance field (min_i(|p-c_i|-r_i)), baked once into a cubic-voxel brick and composed as ONE
// ordinary Subtraction-blend instance so the marches stop paying O(carve-count). data0 = (boxMin.xyz, cellSize); data1 =
// (smooth, packedDims, brickWordOffset, boundaryFloor). Stored values are pre-scaled c/lambda (lambda = sqrt(3) folded in
// at BAKE time), which makes the trilinear interpolant 1-Lipschitz and march-safe with NO stepScale change and an
// unchanged zero set. Determinism: manual trilinear (8 explicit loads + a precise lerp chain,
// fp-contraction pinned OFF) is bit-stable across SPIR-V/DXIL by the same argument as the point evaluator; the baked
// VALUES carry the familiar +-1-LSB WorldLsbExact class. KEEP IN SYNC with SdfProgramBuilder.SampledRegion.
#ifdef SDF_SAMPLED_REGIONS
// One brick voxel, clamped to the brick's own [0, dims-1] lattice so the trilinear stencil never reads past the brick's
// words (clamp-to-edge). Sound because the brick's zero set sits strictly inside the box by the bake margin, so the
// clamped border half-voxel is always deep-positive far field. Linear index is x-fastest: base + i + j*dx + k*dx*dy —
// KEEP IN SYNC with the bake kernel's write ordering (sdf-brick-bake.comp).
float sdfBrickVoxel(uint baseWord, uint3 dims, int3 coord) {
    int3 c = clamp(coord, int3(0, 0, 0), (int3(dims) - int3(1, 1, 1)));

    return sdfBrickPool[(baseWord + (uint)c.x + ((uint)c.y * dims.x) + ((uint)c.z * (dims.x * dims.y)))];
}
#endif
float sdfSampledRegion(float3 p, float4 data0, float4 data1) {
#ifdef SDF_SAMPLED_REGIONS
    // A POOL-LESS engine (SdfWorldTablesOptions.BrickPoolVoxelCapacity 0 — every camera or session view) binds a
    // single-float FILLER for sdfBrickPool so the always-present binding stays valid, yet it
    // never bakes a brick. Sampling that filler would read 0 (its lone/OOB word), and a stored 0 is distance 0 = the box
    // interior sitting entirely on the carve surface, so the Subtraction compose would carve a box-shaped HOLE across the
    // whole region (the same defect an allocated-but-unbaked pool has). Detect the filler by element count and return the
    // conservative union-hull far field — byte-identical to the pool-UNBOUND #else path — so the region renders as its
    // uncarved hull, never a hole. A real pool holds one f32 per voxel (>= a full brick), so numVoxels > 1 there; the
    // check is loop-invariant (the descriptor is fixed per dispatch) so it costs a real baked-carve scene nothing.
    uint numVoxels;
    uint voxelStride;
    sdfBrickPool.GetDimensions(numVoxels, voxelStride);
    if (numVoxels <= 1u) {
        return SDF_FAR_DISTANCE;
    }

    float cellSize = data0.w;
    uint packedDims = asuint(data1.y);
    uint3 dims = uint3((packedDims & SDF_SAMPLED_REGION_DIM_MASK), ((packedDims >> 10) & SDF_SAMPLED_REGION_DIM_MASK), ((packedDims >> 20) & SDF_SAMPLED_REGION_DIM_MASK));
    uint baseWord = asuint(data1.z);
    float3 boxMin = data0.xyz;
    float3 extent = (float3(dims) * cellSize);
    // Local coordinate in voxel units: [0, dims] spans the box.
    float3 local = ((p - boxMin) / cellSize);

    // OUTSIDE the box: dist(p, box) + boundaryFloor is a valid (scaled) lower bound on distance to any interior zero
    // — positive, so a Subtraction compose stays saturated and the accumulator is exact.
    // Discontinuous at the box face, but legal under Boolean composition and the surface is strictly interior (margin),
    // so no rendered seam can exist.
    if (any(local < 0.0) || any(local > float3(dims))) {
        float3 outside = max((boxMin - p), (p - (boxMin + extent)));

        return (length(max(outside, 0.0)) + data1.w);
    }

    // Sample CENTRES sit at integer voxel indices (voxel i is centred at local = i + 0.5), so the trilinear stencil
    // works in (local - 0.5) space; the half-voxel inset means the border half-voxel clamps to the edge voxel (deep far
    // field there by the bake margin, so the clamp is never surface-critical).
    float3 sampleCoord = (local - 0.5);
    float3 baseF = floor(sampleCoord);
    float3 f = (sampleCoord - baseF);
    int3 b = int3(baseF);

    float c000 = sdfBrickVoxel(baseWord, dims, (b + int3(0, 0, 0)));
    float c100 = sdfBrickVoxel(baseWord, dims, (b + int3(1, 0, 0)));
    float c010 = sdfBrickVoxel(baseWord, dims, (b + int3(0, 1, 0)));
    float c110 = sdfBrickVoxel(baseWord, dims, (b + int3(1, 1, 0)));
    float c001 = sdfBrickVoxel(baseWord, dims, (b + int3(0, 0, 1)));
    float c101 = sdfBrickVoxel(baseWord, dims, (b + int3(1, 0, 1)));
    float c011 = sdfBrickVoxel(baseWord, dims, (b + int3(0, 1, 1)));
    float c111 = sdfBrickVoxel(baseWord, dims, (b + int3(1, 1, 1)));

    // `precise` pins fp-contraction OFF across the whole interpolation chain, so DXC's SPIR-V and DXIL backends cannot
    // contract a lerp into a differently-rounded FMA — the manual-bilinear discipline (sdfGlyphSampleField's sibling).
    precise float c00 = lerp(c000, c100, f.x);
    precise float c10 = lerp(c010, c110, f.x);
    precise float c01 = lerp(c001, c101, f.x);
    precise float c11 = lerp(c011, c111, f.x);
    precise float c0 = lerp(c00, c10, f.y);
    precise float c1 = lerp(c01, c11, f.y);
    precise float result = lerp(c0, c1, f.z);

    return result;
#else
    // The pool is NOT bound: the conservative UNION-HULL fallback. A Subtraction compose of SDF_FAR_DISTANCE never bites
    // (max(acc, -1e9) == acc), so the region renders as its uncarved hull — solid, never a hole (the Glyph quad-fallback
    // precedent). The instance-cull and diagnostic kernels take this path (only the world-views/core-ops/beam
    // kernels bind the pool), and a program with no SampledRegion never reaches this arm at all.
    return SDF_FAR_DISTANCE;
#endif
}
// === end SDF_SHAPE_SAMPLED_REGION ====================================================================================

// === SDF_SHAPE_SWEEP: a quadratic Bezier curve swept with a tapering, bulging radius, optionally as helical strands
// (KEEP IN SYNC with Puck.SignedDistance.SdfProgramBuilder.Sweep / Puck.SignedDistance.Queries.SdfFieldEvaluator).
// One curve's table entry: 3 fixed uvec4 words at tableOffset in sdfWords — (A.xyz, radiusStart), (B.xyz,
// radiusEnd), (C.xyz, bulge). KEEP IN SYNC with SdfProgram.PackSweepCurves.
void sdfSweepCurve(uint tableOffset, out float3 a, out float3 b, out float3 c, out float radiusStart, out float radiusEnd, out float bulge) {
    uint4 wordsA = sdfWords[tableOffset];
    uint4 wordsB = sdfWords[(tableOffset + 1u)];
    uint4 wordsC = sdfWords[(tableOffset + 2u)];

    a = asfloat(wordsA.xyz);
    radiusStart = asfloat(wordsA.w);
    b = asfloat(wordsB.xyz);
    radiusEnd = asfloat(wordsB.w);
    c = asfloat(wordsC.xyz);
    bulge = asfloat(wordsC.w);
}
float3 sdfBezierPoint(float3 a, float3 b, float3 c, float t) {
    float3 ab = lerp(a, b, t);
    float3 bc = lerp(b, c, t);

    return lerp(ab, bc, t);
}
float3 sdfBezierDerivative(float3 a, float3 b, float3 c, float t) {
    return (2.0 * (lerp((b - a), (c - b), t)));
}
// The closest parameter t in [0, 1] on the quadratic Bezier (a, b, c) to p — the standard closed-form
// closest-point-on-quadratic-bezier (a depressed-cubic solve, iq's construction), degenerating to the A-C line
// segment's own closest point when b is (numerically) the exact midpoint of a and c — the quadratic coefficient
// vanishes there and the cubic solve's 1/dot(coefficient, coefficient) would divide by zero.
float sdfSweepClosestT(float3 p, float3 a, float3 b, float3 c) {
    float3 coefA = (b - a);
    float3 coefB = ((a - (2.0 * b)) + c);
    float3 coefC = (coefA * 2.0);
    float3 d = (a - p);
    float bb = dot(coefB, coefB);

    if (bb < 1e-10) {
        float3 ac = (c - a);

        return saturate(dot((p - a), ac) / max(dot(ac, ac), 1e-20));
    }

    float kk = (1.0 / bb);
    float kx = (kk * dot(coefA, coefB));
    float ky = ((kk * ((2.0 * dot(coefA, coefA)) + dot(d, coefB))) / 3.0);
    float kz = (kk * dot(d, coefA));
    float p1 = (ky - (kx * kx));
    float p3 = ((p1 * p1) * p1);
    float q = ((kx * ((2.0 * kx * kx) - (3.0 * ky))) + kz);
    float h = ((q * q) + (4.0 * p3));

    if (h >= 0.0) {
        h = sqrt(h);

        float2 x = ((float2(h, -h) - q) * 0.5);
        float2 uv = (sign(x) * pow(abs(x), float2(1.0 / 3.0, 1.0 / 3.0)));

        return saturate((uv.x + uv.y) - kx);
    }

    float z = sqrt(-p1);
    float v = (acos(clamp((q / ((p1 * z) * 2.0)), -1.0, 1.0)) / 3.0);
    float m = cos(v);
    float n = (sin(v) * 1.7320508);
    float3 ts = saturate(((float3((m + m), (-n - m), (n - m)) * z) - kx));
    float3 qa = (d + ((coefC + (coefB * ts.x)) * ts.x));
    float3 qb = (d + ((coefC + (coefB * ts.y)) * ts.y));
    float3 qc = (d + ((coefC + (coefB * ts.z)) * ts.z));
    float da = dot(qa, qa);
    float db = dot(qb, qb);
    float dc = dot(qc, qc);
    float best = min(da, min(db, dc));

    return ((best == da) ? ts.x : ((best == db) ? ts.y : ts.z));
}
// The sweep's radius profile: a linear taper plus a mid-span bulge that vanishes at both ends (the exponent softens
// the bulge's rise off zero — KEEP IN SYNC with SdfProgramBuilder.Sweep's remarks).
float sdfSweepRadiusAt(float t, float radiusStart, float radiusEnd, float bulge) {
    float taper = lerp(radiusStart, radiusEnd, t);
    float s = max(sin((SDF_PI * t)), 0.0);

    return (taper + (bulge * pow(s, 0.65)));
}
// The conservative margin subtracted from the raw closest-point candidate — the closed-form closest point is taken
// on the CENTERLINE alone (ignoring how the radius/orbit vary elsewhere along the curve), so the raw candidate can
// OVERESTIMATE true distance near strong radius/orbit variation; this margin, calibrated over a randomized grid
// against a fine-sampled reference tube (SweepLawTests), restores a conservative (never-overestimating, within the
// calibrated envelope) field. KEEP IN SYNC with SdfProgramBuilder.SweepConservativeMargin and the fixed-point mirror.
float sdfSweepConservativeMargin(float bulge, float strandOffset, float twist, float radiusStart, float radiusEnd) {
    return (((1.0 * abs(bulge)) + ((0.7 * strandOffset) * (1.0 + abs(twist)))) + (0.9 * abs((radiusEnd - radiusStart))));
}
float sdfSweep(float3 p, float4 data0, float4 data1) {
    uint tableOffset = asuint(data0.x);
    float strandsFloat = data0.y;
    float twist = data0.z;
    float strandOffset = data0.w;
    float3 a, b, c;
    float radiusStart, radiusEnd, bulge;

    sdfSweepCurve(tableOffset, a, b, c, radiusStart, radiusEnd, bulge);

    float t = sdfSweepClosestT(p, a, b, c);
    float3 base = sdfBezierPoint(a, b, c, t);
    float radius = sdfSweepRadiusAt(t, radiusStart, radiusEnd, bulge);
    float3 tangent = sdfBezierDerivative(a, b, c, t);
    float tangentLength = length(tangent);
    float3 tangentDir = ((tangentLength > 1e-8) ? (tangent / tangentLength) : float3(0.0, 1.0, 0.0));
    float3 refAxis = ((abs(tangentDir.y) < 0.999) ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0));
    float3 u = normalize(cross(tangentDir, refAxis));
    float3 v = cross(tangentDir, u);
    uint strandCount = max(((uint)(strandsFloat + 0.5)), 1u);
    float best = SDF_FAR_DISTANCE;

    for (uint strand = 0u; (strand < strandCount); strand++) {
        float phase = ((t * twist * SDF_TAU) + ((float(strand) * SDF_TAU) / float(strandCount)));
        float sinP, cosP;

        sincos(phase, sinP, cosP);

        float3 offsetPoint = (base + (strandOffset * ((u * cosP) + (v * sinP))));

        best = min(best, (length(p - offsetPoint) - radius));
    }

    return (best - sdfSweepConservativeMargin(bulge, strandOffset, twist, radiusStart, radiusEnd));
}
// === end SDF_SHAPE_SWEEP =============================================================================================

// The ONE shape dispatch. Single-return (a result variable rather than returning inside the switch) so the compiler's
// flow analysis sees the value is always initialized. data1's .x lane is the ISA-wide smooth-blend radius; lanes .yzw
// carry HOST-BAKED derived constants per shape (see SdfProgramBuilder). Ids that share a body FALL THROUGH: DXC inlines
// each `case` separately, so a duplicated arm duplicates the whole primitive (the Box/ScreenSlab and Polygon/Star pairs
// cost ~10% of this kernel's instructions when written twice).
float evaluateShape(uint shapeType, float3 p, float4 data0, float4 data1) {
    sdfWorkShapes++;
    float result = SDF_FAR_DISTANCE;

    switch (shapeType) {
        case SDF_SHAPE_SPHERE:      result = sdfSphere(p, data0.x); break;
        // A ScreenSlab IS a box; only its material sentinel distinguishes it (see SDF_SCREEN_MATERIAL).
        case SDF_SHAPE_BOX:
        case SDF_SHAPE_SCREEN_SLAB: result = sdfBox(p, data0.xyz, data0.w); break;
#ifndef SDF_STRIP_ALL_EXOTIC
        case SDF_SHAPE_TORUS:       result = sdfTorus(p, data0.x, data0.y); break;
#endif
        // The plane normal is normalized HOST-SIDE (SdfProgramBuilder.Plane) — the biggest per-eval saving of all:
        // the plane is in nearly every scene, and a per-sample normalize would run for EVERY map() sample.
        case SDF_SHAPE_PLANE:       result = sdfPlane(p, data0.xyz, data0.w); break;
#ifndef SDF_STRIP_ALL_EXOTIC
        case SDF_SHAPE_ROUND_CONE:  result = sdfRoundCone(p, data0.x, data0.y, data0.z, data0.w, data1.y); break;
        case SDF_SHAPE_VESICA:      result = sdfVesica(p, data0.x, data0.y, data0.z); break;
        // The fold tier admits a superellipsoid only at e == 2 (SdfViewsKernelVariants), so the stripped build
        // compiles just the ellipsoid's pow-free gauge; the full build dispatches on the exponent.
#ifdef SDF_STRIP_HEAVY
        case SDF_SHAPE_SUPERELLIPSOID:  result = sdfEllipsoidGauge(p, data0.xyz, data1.yzw); break;
#else
        case SDF_SHAPE_SUPERELLIPSOID:  result = sdfSuperellipsoid(p, data0.xyz, data1.yzw, data0.w); break;
#endif
#endif
        // Articulated-character core: limbs overwhelmingly lower to capsules/cylinders. Keeping these two inexpensive
        // primitives in CoreOps avoids promoting an otherwise rigid humanoid program to the register-heavy full ISA.
        case SDF_SHAPE_CAPSULE:     result = sdfCapsule(p, data0.xyz, data0.w, data1.y); break;
        // data0.xy arrive inset by the edge-rounding radius data1.w; subtracting it offsets the rims back out.
        case SDF_SHAPE_CYLINDER:    result = (sdfCylinder(p, data0.x, data0.y) - data1.w); break;
        // The 2D-primitive family: each lifted wrapper reads its lift mode (data1.y) and lift amount (data0.w) itself.
        // A regular polygon is sdfStar2D's m = 2 case, so it shares the star's body verbatim. RoundedRectangle is a
        // CORE shape (the room's cabinetry is built from it); the rest of the family is exotic.
        case SDF_SHAPE_ROUNDED_RECTANGLE:    result = sdfRoundedRect(p, data0, data1); break;
        case SDF_SHAPE_CHAMFERED_RECTANGLE:  result = sdfChamferedRect(p, data0, data1); break;
#ifndef SDF_STRIP_HEAVY
        case SDF_SHAPE_REGULAR_POLYGON:
        case SDF_SHAPE_STAR:            result = sdfPolyStar(p, data0, data1); break;
        case SDF_SHAPE_TRAPEZOID:       result = sdfTrapezoidSolid(p, data0, data1); break;
        case SDF_SHAPE_ELLIPSE:         result = sdfEllipseSolid(p, data0, data1); break;
        case SDF_SHAPE_CONVEX_POLYGON:  result = sdfConvexPolygonSolid(p, data0, data1); break;
        case SDF_SHAPE_PATH: result = sdfPathSolid(p, data0, data1); break;
        case SDF_SHAPE_SWEEP:           result = sdfSweep(p, data0, data1); break;
#endif
        // A glyph is the atlas-sampled letter where the atlas is bound (the world-views kernel), else the conservative
        // extruded quad — so the beam cull sees a solid cell box (never a hole), and only the lit render
        // resolves the true lettering.
        case SDF_SHAPE_GLYPH:
#ifdef SDF_GLYPH_ATLAS
            result = sdfGlyph(p, data0, data1);
#else
            result = sdfGlyphQuad(p, data0, data1);
#endif
            break;
        // A sampled brick where the pool is bound (the world-views + beam kernels), else the conservative union-hull
        // fallback — so a kernel without the pool sees SDF_FAR_DISTANCE and the
        // Subtraction compose leaves the accumulator untouched (uncarved hull, never a hole). NOT stripped under
        // SDF_CORE_OPS: the core-ops views variant binds the pool and must evaluate bricks. sdfSampledRegion self-guards
        // on SDF_SAMPLED_REGIONS, so this case is a plain call in both the full and core-ops variants.
        case SDF_SHAPE_SAMPLED_REGION:  result = sdfSampledRegion(p, data0, data1); break;
    }

    return result;
}
#endif
