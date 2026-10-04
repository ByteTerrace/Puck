// Deferred shape derivatives share the dual blend's exact branch and weight rules.
#ifndef FIELD_SDF_GRADIENT_PLAN_HLSLI
#define FIELD_SDF_GRADIENT_PLAN_HLSLI

// Each field and every saved parent hold at most 32 contributing leaves.
// An overflow stays live until a zero blend weight discards that entire field.
#define SDF_GRADIENT_CONTRIBUTORS 32u
static uint sdfGradientMode = 0u; // full dual, collect decisions, replay selected derivatives
static uint sdfGradientIds[(SDF_MAX_FIELD_SCOPE_DEPTH + 1u) * SDF_GRADIENT_CONTRIBUTORS];
static float sdfGradientWeights[(SDF_MAX_FIELD_SCOPE_DEPTH + 1u) * SDF_GRADIENT_CONTRIBUTORS];
static uint sdfGradientCount = 0u;
static uint sdfGradientScope = 0u;
static bool sdfGradientOverflow = false;
static uint sdfGradientParentStarts[SDF_MAX_FIELD_SCOPE_DEPTH];
static bool sdfGradientParentOverflows[SDF_MAX_FIELD_SCOPE_DEPTH];
static uint sdfGradientDepth = 0u;
static float3 sdfGradientSelected = 0.0;
#ifdef SDF_GRADIENT_LAW_REFERENCE
static uint sdfGradientReferenceShape = 0u;
#endif

void sdfGradientScale(float weight) {
    if (sdfGradientMode != 1u) return;
    if (weight == 0.0) {
        sdfGradientCount = sdfGradientScope;
        sdfGradientOverflow = false;
        return;
    }
    uint write = sdfGradientScope;
    [loop]
    for (uint read = sdfGradientScope; read < sdfGradientCount; read++) {
        float scaled = sdfGradientWeights[read] * weight;
        if (scaled != 0.0) {
            sdfGradientIds[write] = sdfGradientIds[read];
            sdfGradientWeights[write++] = scaled;
        }
    }
    sdfGradientCount = write;
}

void sdfGradientPushScope() {
    if (sdfGradientMode != 1u) return;
    sdfGradientParentStarts[sdfGradientDepth] = sdfGradientScope;
    sdfGradientParentOverflows[sdfGradientDepth++] = sdfGradientOverflow;
    sdfGradientScope = sdfGradientCount;
    sdfGradientOverflow = false;
}

void sdfGradientScopeWeights(float parentWeight, float childWeight) {
    if (sdfGradientMode != 1u) return;
    uint parentStart = sdfGradientParentStarts[--sdfGradientDepth];
    sdfGradientOverflow = (sdfGradientParentOverflows[sdfGradientDepth] && parentWeight != 0.0)
        || (sdfGradientOverflow && childWeight != 0.0);
    uint write = parentStart;
    [loop]
    for (uint read = parentStart; read < sdfGradientCount; read++) {
        float weight = sdfGradientWeights[read] * ((read < sdfGradientScope) ? parentWeight : childWeight);
        if (weight != 0.0) {
            if (write == parentStart + SDF_GRADIENT_CONTRIBUTORS) {
                sdfGradientOverflow = true;
                continue;
            }
            sdfGradientIds[write] = sdfGradientIds[read];
            sdfGradientWeights[write++] = weight;
        }
    }
    sdfGradientCount = write;
    sdfGradientScope = parentStart;
}

void sdfGradientCompose(float current, float candidate, uint blend, float smooth, uint shapeIndex, bool scope) {
    if (sdfGradientMode != 1u) return;
    float ignored;
    float3 weights;
    blendShapeDual(current, float3(1.0, 0.0, 0.0), candidate, float3(0.0, 1.0, 0.0), blend, smooth, ignored, weights);
    if (scope) {
        sdfGradientScopeWeights(weights.x, weights.y);
        return;
    }
    sdfGradientScale(weights.x);
    if (weights.y != 0.0) {
        if (sdfGradientCount == sdfGradientScope + SDF_GRADIENT_CONTRIBUTORS) {
            sdfGradientOverflow = true;
            return;
        }
        sdfGradientIds[sdfGradientCount] = shapeIndex;
        sdfGradientWeights[sdfGradientCount++] = weights.y;
    }
}

float sdfGradientWeight(uint shapeIndex) {
    [loop]
    for (uint index = 0u; index < sdfGradientCount; index++) {
        if (sdfGradientIds[index] == shapeIndex) return sdfGradientWeights[index];
    }
    return 0.0;
}
#endif
