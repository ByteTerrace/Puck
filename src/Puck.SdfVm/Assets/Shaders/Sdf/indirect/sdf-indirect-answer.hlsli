// The receiver pass's answer for one shaded pixel, which views reads from the same render-grid slot: the receiver's
// status, whether a near-field or comparison answer replaces the cache's, the receiver's field evaluations for the
// evals heatmap, and the replacing answer's source-masked total. A pixel views does not shade has no answer.
#ifndef SDF_INDIRECT_ANSWER_HLSLI
#define SDF_INDIRECT_ANSWER_HLSLI

static const uint SdfIndirectAnswerStatusMask = 7u;
static const uint SdfIndirectAnswerReplaced = 8u;
static const uint SdfIndirectAnswerEvaluationShift = 8u;
static const uint SdfIndirectAnswerEvaluationLimit = 0xffffffu;

uint sdfIndirectAnswerIndex(uint2 pixel, uint viewIndex) {
    return ((viewIndex * passGroup.imageExtent.y + pixel.y) * passGroup.imageExtent.x) + pixel.x;
}
// Whether views shades a pixel's hit through the material path, which alone applies indirect light: a final or
// indirect debug mode, and no screen (a bound screen or its unbound glass). Both passes decide it from the same record.
bool sdfIndirectAnswers(SdfPixel p, SdfSurfaceSample s) {
    return p.active && s.hit && (worldFinalShadingMode(p.viewMode) || (p.viewMode == DebugViewModeIndirect))
        && (s.material < SDF_SCREEN_MATERIAL);
}

#endif
