// A High receiver replaces one incoming cosine sample; resolved colour and temporal history never supply radiance.
#ifndef SDF_INDIRECT_NEAR_HLSLI
#define SDF_INDIRECT_NEAR_HLSLI
#include "sdf-indirect-near-policy.hlsli"
#include "sdf-indirect-near-incoming.hlsli"
#include "sdf-indirect-alternatives.hlsli"

static uint sdfIndirectNearOutcome = SdfIndirectNearOutcomeNotAttempted;
static float3 sdfIndirectNearDirection = 0.0;

// The receiver's near-field sample replaces its cache answer when answered (sdfIndirectNearResult) and keeps every cache
// source when not admitted or unresolved. The receiver procedure runs it (sdfIndirectReceiverStep): the outcome and
// direction above are what the pick reports.
#endif

