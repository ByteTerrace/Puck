// Forward-mode values for the field walk's gradient reader (SDF_FIELD_DUAL): each value carried with its derivative
// along the three axes of the sample's world point. A scalar's derivative is its gradient; a vector's is its Jacobian,
// row i the gradient of component i, so mul(n, J) is the gradient of dot(n, v). The generated formula overloads
// (isa/sdf-field-*-dual.hlsli, SdfFormula's dual lowering) call these; an operand a formula reads from its instruction
// stays a plain float and the mixed overloads take it as a constant. Where a value has no derivative (a hard min's
// crease, a clamp's end, a select) the active branch's derivative stands.
#ifndef FIELD_SDF_DUAL_HLSLI
#define FIELD_SDF_DUAL_HLSLI

struct SdfDual {
    float v;
    float3 d;
};
struct SdfDual2 {
    float2 v;
    float2x3 d;
};
struct SdfDual3 {
    float3 v;
    float3x3 d;
};

SdfDual sdfdMake(float v, float3 d) { SdfDual r; r.v = v; r.d = d; return r; }
SdfDual2 sdfdMake(float2 v, float2x3 d) { SdfDual2 r; r.v = v; r.d = d; return r; }
SdfDual3 sdfdMake(float3 v, float3x3 d) { SdfDual3 r; r.v = v; r.d = d; return r; }
SdfDual sdfdLift(float v) { return sdfdMake(v, float3(0.0, 0.0, 0.0)); }
SdfDual2 sdfdLift(float2 v) { return sdfdMake(v, (float2x3)0.0); }
SdfDual3 sdfdLift(float3 v) { return sdfdMake(v, (float3x3)0.0); }
SdfDual sdfdLift(SdfDual v) { return v; }
SdfDual2 sdfdLift(SdfDual2 v) { return v; }
SdfDual3 sdfdLift(SdfDual3 v) { return v; }
static const float3x3 SDF_DUAL_IDENTITY = float3x3(1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0);
// A point that is its own variable: the world sample a gradient walk starts from.
SdfDual3 sdfdSeed(float3 v) { return sdfdMake(v, SDF_DUAL_IDENTITY); }

// Each row of a derivative scaled by its own component.
float2x3 sdfdRows(float2x3 m, float2 s) { return float2x3(m[0] * s.x, m[1] * s.y); }
float3x3 sdfdRows(float3x3 m, float3 s) { return float3x3(m[0] * s.x, m[1] * s.y, m[2] * s.z); }
// The derivative of a vector a times a scalar whose gradient is g.
float2x3 sdfdOuter(float2 a, float3 g) { return float2x3(g * a.x, g * a.y); }
float3x3 sdfdOuter(float3 a, float3 g) { return float3x3(g * a.x, g * a.y, g * a.z); }

// === Components ======================================================================================================
SdfDual sdfdX(SdfDual2 a) { return sdfdMake(a.v.x, a.d[0]); }
SdfDual sdfdY(SdfDual2 a) { return sdfdMake(a.v.y, a.d[1]); }
SdfDual sdfdX(SdfDual3 a) { return sdfdMake(a.v.x, a.d[0]); }
SdfDual sdfdY(SdfDual3 a) { return sdfdMake(a.v.y, a.d[1]); }
SdfDual sdfdZ(SdfDual3 a) { return sdfdMake(a.v.z, a.d[2]); }
SdfDual2 sdfdVec2(SdfDual x, SdfDual y) { return sdfdMake(float2(x.v, y.v), float2x3(x.d, y.d)); }
SdfDual3 sdfdVec3(SdfDual x, SdfDual y, SdfDual z) { return sdfdMake(float3(x.v, y.v, z.v), float3x3(x.d, y.d, z.d)); }

// === Arithmetic ======================================================================================================
// Each over two duals, a dual and a constant, and a constant and a dual.
#define SDF_DUAL_ADDITIVE(T, V)                                                                         \
    T sdfdAdd(T a, T b) { return sdfdMake(a.v + b.v, a.d + b.d); }                                      \
    T sdfdAdd(T a, V b) { return sdfdMake(a.v + b, a.d); }                                              \
    T sdfdAdd(V a, T b) { return sdfdMake(a + b.v, b.d); }                                              \
    T sdfdSubtract(T a, T b) { return sdfdMake(a.v - b.v, a.d - b.d); }                                 \
    T sdfdSubtract(T a, V b) { return sdfdMake(a.v - b, a.d); }                                         \
    T sdfdSubtract(V a, T b) { return sdfdMake(a - b.v, -b.d); }                                        \
    T sdfdNegate(T a) { return sdfdMake(-a.v, -a.d); }
SDF_DUAL_ADDITIVE(SdfDual, float)
SDF_DUAL_ADDITIVE(SdfDual2, float2)
SDF_DUAL_ADDITIVE(SdfDual3, float3)
#undef SDF_DUAL_ADDITIVE

SdfDual sdfdMultiply(SdfDual a, SdfDual b) { return sdfdMake(a.v * b.v, (a.d * b.v) + (b.d * a.v)); }
SdfDual sdfdMultiply(SdfDual a, float b) { return sdfdMake(a.v * b, a.d * b); }
SdfDual sdfdMultiply(float a, SdfDual b) { return sdfdMake(a * b.v, b.d * a); }
SdfDual sdfdDivide(SdfDual a, SdfDual b) { return sdfdMake(a.v / b.v, ((a.d * b.v) - (b.d * a.v)) / (b.v * b.v)); }
SdfDual sdfdDivide(SdfDual a, float b) { return sdfdMake(a.v / b, a.d / b); }
SdfDual sdfdDivide(float a, SdfDual b) { return sdfdMake(a / b.v, b.d * (-a / (b.v * b.v))); }
// A vector scaled by a scalar.
#define SDF_DUAL_SCALE(T, V)                                                                            \
    T sdfdMultiply(T a, SdfDual b) { return sdfdMake(a.v * b.v, (a.d * b.v) + sdfdOuter(a.v, b.d)); }  \
    T sdfdMultiply(T a, float b) { return sdfdMake(a.v * b, a.d * b); }                                 \
    T sdfdMultiply(V a, SdfDual b) { return sdfdMake(a * b.v, sdfdOuter(a, b.d)); }
SDF_DUAL_SCALE(SdfDual2, float2)
SDF_DUAL_SCALE(SdfDual3, float3)
#undef SDF_DUAL_SCALE
// Component by component.
SdfDual3 sdfdMultiplyComponents(SdfDual3 a, SdfDual3 b) { return sdfdMake(a.v * b.v, sdfdRows(a.d, b.v) + sdfdRows(b.d, a.v)); }
SdfDual3 sdfdMultiplyComponents(SdfDual3 a, float3 b) { return sdfdMake(a.v * b, sdfdRows(a.d, b)); }
SdfDual3 sdfdMultiplyComponents(float3 a, SdfDual3 b) { return sdfdMake(a * b.v, sdfdRows(b.d, a)); }
SdfDual3 sdfdDivideComponents(SdfDual3 a, SdfDual3 b) { return sdfdMake(a.v / b.v, sdfdRows(sdfdRows(a.d, b.v) - sdfdRows(b.d, a.v), 1.0 / (b.v * b.v))); }
SdfDual3 sdfdDivideComponents(SdfDual3 a, float3 b) { return sdfdMake(a.v / b, sdfdRows(a.d, 1.0 / b)); }
SdfDual3 sdfdDivideComponents(float3 a, SdfDual3 b) { return sdfdMake(a / b.v, sdfdRows(b.d, -a / (b.v * b.v))); }

// === Piecewise functions: the active branch's derivative =============================================================
SdfDual sdfdAbs(SdfDual a) { return sdfdMake(abs(a.v), (a.v < 0.0) ? -a.d : a.d); }
SdfDual2 sdfdAbs(SdfDual2 a) { return sdfdMake(abs(a.v), sdfdRows(a.d, float2((a.v.x < 0.0) ? -1.0 : 1.0, (a.v.y < 0.0) ? -1.0 : 1.0))); }
SdfDual3 sdfdAbs(SdfDual3 a) {
    return sdfdMake(abs(a.v), sdfdRows(a.d, float3((a.v.x < 0.0) ? -1.0 : 1.0, (a.v.y < 0.0) ? -1.0 : 1.0, (a.v.z < 0.0) ? -1.0 : 1.0)));
}
// HLSL's conditional operator takes no structure, so each choice is a branch.
SdfDual sdfdSelect(bool condition, SdfDual a, SdfDual b) { if (condition) { return a; } return b; }
SdfDual2 sdfdSelect(bool condition, SdfDual2 a, SdfDual2 b) { if (condition) { return a; } return b; }
SdfDual3 sdfdSelect(bool condition, SdfDual3 a, SdfDual3 b) { if (condition) { return a; } return b; }
SdfDual sdfdMin(SdfDual a, SdfDual b) { return sdfdSelect(b.v < a.v, b, a); }
SdfDual sdfdMin(SdfDual a, float b) { return sdfdSelect(b < a.v, sdfdLift(b), a); }
SdfDual sdfdMin(float a, SdfDual b) { return sdfdSelect(b.v < a, b, sdfdLift(a)); }
SdfDual sdfdMax(SdfDual a, SdfDual b) { return sdfdSelect(b.v > a.v, b, a); }
SdfDual sdfdMax(SdfDual a, float b) { return sdfdSelect(b > a.v, sdfdLift(b), a); }
SdfDual sdfdMax(float a, SdfDual b) { return sdfdSelect(b.v > a, b, sdfdLift(a)); }
SdfDual sdfdClamp(SdfDual a, float low, float high) { return sdfdMin(sdfdMax(a, low), high); }
SdfDual sdfdClamp(SdfDual a, SdfDual low, SdfDual high) { return sdfdMin(sdfdMax(a, low), high); }
SdfDual3 sdfdClamp(SdfDual3 a, float3 low, float3 high) {
    float3 v = clamp(a.v, low, high);
    return sdfdMake(v, sdfdRows(a.d, float3((v.x == a.v.x) ? 1.0 : 0.0, (v.y == a.v.y) ? 1.0 : 0.0, (v.z == a.v.z) ? 1.0 : 0.0)));
}
SdfDual3 sdfdClamp(SdfDual3 a, SdfDual3 low, SdfDual3 high) {
    SdfDual x = sdfdClamp(sdfdX(a), sdfdX(low), sdfdX(high));
    SdfDual y = sdfdClamp(sdfdY(a), sdfdY(low), sdfdY(high));
    SdfDual z = sdfdClamp(sdfdZ(a), sdfdZ(low), sdfdZ(high));
    return sdfdVec3(x, y, z);
}
// A float that overflowed to infinity or NaN takes the fallback.
SdfDual sdfdFiniteOr(SdfDual a, SdfDual b) { return sdfdSelect((asuint(a.v) & 0x7F800000u) == 0x7F800000u, b, a); }
SdfDual sdfdLerp(SdfDual a, SdfDual b, SdfDual t) { return sdfdAdd(a, sdfdMultiply(sdfdSubtract(b, a), t)); }
SdfDual sdfdSmoothstep(float edge0, float edge1, SdfDual x) {
    float t = saturate((x.v - edge0) / (edge1 - edge0));
    float slope = ((t > 0.0) && (t < 1.0)) ? ((6.0 * t * (1.0 - t)) / (edge1 - edge0)) : 0.0;
    return sdfdMake(smoothstep(edge0, edge1, x.v), x.d * slope);
}

// === Lengths and projections =========================================================================================
// A zero length's derivative is zero: every direction leaves it alike.
SdfDual sdfdLength(SdfDual2 a) {
    float l = length(a.v);
    return sdfdMake(l, (l > 0.0) ? mul(a.v / l, a.d) : float3(0.0, 0.0, 0.0));
}
SdfDual sdfdLength(SdfDual3 a) {
    float l = length(a.v);
    return sdfdMake(l, (l > 0.0) ? mul(a.v / l, a.d) : float3(0.0, 0.0, 0.0));
}
SdfDual sdfdDot(SdfDual2 a, SdfDual2 b) { return sdfdMake(dot(a.v, b.v), mul(b.v, a.d) + mul(a.v, b.d)); }
SdfDual sdfdDot(SdfDual2 a, float2 b) { return sdfdMake(dot(a.v, b), mul(b, a.d)); }
SdfDual sdfdDot(float2 a, SdfDual2 b) { return sdfdMake(dot(a, b.v), mul(a, b.d)); }
SdfDual sdfdDot(SdfDual3 a, SdfDual3 b) { return sdfdMake(dot(a.v, b.v), mul(b.v, a.d) + mul(a.v, b.d)); }
SdfDual sdfdDot(SdfDual3 a, float3 b) { return sdfdMake(dot(a.v, b), mul(b, a.d)); }
SdfDual sdfdDot(float3 a, SdfDual3 b) { return sdfdMake(dot(a, b.v), mul(a, b.d)); }
// The saturated projection parameter of a point on a segment from the origin; a zero segment is its origin.
SdfDual sdfdSegmentParameter(SdfDual2 p, float2 s) {
    float ss = dot(s, s);
    if (!(ss > 0.0)) { return sdfdLift(0.0); }
    float t = dot(p.v, s) / ss;
    return sdfdSelect((t > 0.0) && (t < 1.0), sdfdMake(t, mul(s, p.d) / ss), sdfdLift(saturate(t)));
}
SdfDual sdfdSegmentParameter(SdfDual3 p, float3 s) {
    float ss = dot(s, s);
    if (!(ss > 0.0)) { return sdfdLift(0.0); }
    float t = dot(p.v, s) / ss;
    return sdfdSelect((t > 0.0) && (t < 1.0), sdfdMake(t, mul(s, p.d) / ss), sdfdLift(saturate(t)));
}
SdfDual sdfdSegmentParameter(SdfDual2 p, SdfDual2 s) {
    SdfDual ss = sdfdDot(s, s);
    if (!(ss.v > 0.0)) { return sdfdLift(0.0); }
    SdfDual t = sdfdDivide(sdfdDot(p, s), ss);
    return sdfdSelect((t.v > 0.0) && (t.v < 1.0), t, sdfdLift(saturate(t.v)));
}
SdfDual sdfdSegmentParameter(SdfDual3 p, SdfDual3 s) {
    SdfDual ss = sdfdDot(s, s);
    if (!(ss.v > 0.0)) { return sdfdLift(0.0); }
    SdfDual t = sdfdDivide(sdfdDot(p, s), ss);
    return sdfdSelect((t.v > 0.0) && (t.v < 1.0), t, sdfdLift(saturate(t.v)));
}
// The smaller of two lengths.
SdfDual sdfdMinLength(SdfDual2 a, SdfDual2 b) { return sdfdLength(sdfdSelect(dot(b.v, b.v) < dot(a.v, a.v), b, a)); }
SdfDual sdfdPower(SdfDual a, float exponent) {
    float v = pow(a.v, exponent);
    float slope = (a.v > 0.0) ? (exponent * pow(a.v, exponent - 1.0)) : 0.0;
    return sdfdMake(v, a.d * slope);
}
SdfDual sdfdPower(SdfDual a, SdfDual exponent) {
    SdfDual r = sdfdPower(a, exponent.v);
    if (a.v > 0.0) { r.d += exponent.d * (r.v * log(a.v)); }
    return r;
}

// === Point maps ======================================================================================================
// A point moved into a frame rotated by q: every column of the Jacobian (the point's derivative along one world axis)
// turns with it.
float3x3 sdfdRotateJacobian(float3x3 jacobian, float4 q) {
    float3x3 columns = transpose(jacobian);
    return transpose(float3x3(rotatePointByInverseQuaternion(columns[0], q), rotatePointByInverseQuaternion(columns[1], q), rotatePointByInverseQuaternion(columns[2], q)));
}
SdfDual3 sdfdRotateInverse(SdfDual3 p, float4 q) {
    return sdfdMake(rotatePointByInverseQuaternion(p.v, q), sdfdRotateJacobian(p.d, q));
}
// A gradient in a frame rotated by q, back in the frame the point came from.
float3 sdfdRotateGradient(float3 gradient, float4 q) { return rotatePointByQuaternion(gradient, q); }

// === Escapes =========================================================================================================
SdfDual sdfEllipse2D(SdfDual2 p, float2 ab) {
    float2 gradient;
    float value = sdfEllipse2DGrad(p.v, ab, gradient);
    return sdfdMake(value, mul(gradient, p.d));
}
// sdfFbm3's octave sum with each octave's analytic gradient.
SdfDual sdfFbm3(SdfDual3 q, uint seed, uint octaves, float gain, float lacunarity) {
    float3 at = q.v;
    float amplitude = 1.0;
    float frequency = 1.0;
    float sum = 0.0;
    float3 gradient = float3(0.0, 0.0, 0.0);
    for (uint octave = 0u; octave < SDF_MAX_NOISE_OCTAVES; octave++) {
        if (octave >= octaves) { break; }
        uint octaveSeed = (seed + octave);
        float4 noise = sdfValueNoise3Gradient(at, uint3(octaveSeed, (octaveSeed * SDF_HASH_STREAM_A), (octaveSeed * SDF_HASH_STREAM_B)));
        sum += (amplitude * noise.x);
        gradient += ((amplitude * frequency) * noise.yzw);
        at *= lacunarity;
        frequency *= lacunarity;
        amplitude *= gain;
    }
    return sdfdMake(sum, mul(gradient, q.d));
}
SdfDual sdfCellDistance(SdfDual3 q, uint seed, uint mode, float randomness) {
    float3 gradient;
    float value = sdfCellDistanceGrad(q.v, seed, mode, randomness, gradient);
    return sdfdMake(value, mul(gradient, q.d));
}

// === Primitives the formulas do not carry ============================================================================
// The tap spacing of the lattice the gradient reader samples (the bake kernel sets it per lattice). A primitive whose
// reader is hand-written (a target escape) takes its local gradient from four tetrahedral taps of that primitive alone
// this far apart about its local point, then turns it through the walk's Jacobian: the taps read one primitive, never
// the program.
static float sdfDualLocalSpacing = 1.0e-3;
#define SDF_DUAL_LOCAL_TAPS(name)                                                                                     \
    SdfDual name(SdfDual3 p, float4 data0, float4 data1) {                                                            \
        float h = sdfDualLocalSpacing;                                                                                \
        float3 g = (float3(1.0, -1.0, -1.0) * name(p.v + (float3(1.0, -1.0, -1.0) * h), data0, data1))               \
            + (float3(-1.0, -1.0, 1.0) * name(p.v + (float3(-1.0, -1.0, 1.0) * h), data0, data1))                     \
            + (float3(-1.0, 1.0, -1.0) * name(p.v + (float3(-1.0, 1.0, -1.0) * h), data0, data1))                     \
            + (float3(1.0, 1.0, 1.0) * name(p.v + (float3(1.0, 1.0, 1.0) * h), data0, data1));                         \
        return sdfdMake(name(p.v, data0, data1), mul(g * (0.25 / h), p.d));                                           \
    }

#endif
