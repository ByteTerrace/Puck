#ifndef SDF_INDIRECT_RUN_HLSLI
#define SDF_INDIRECT_RUN_HLSLI
// Indirect field work as resumable procedures. A procedure keeps its arguments, locals and results in its own static
// record and advances through its step function, which runs until it needs a field query (SdfIndirectStepQuery, posted
// through sdfIndirectAsk or sdfIndirectAskGradient and answered in sdfIndirectReply), calls another procedure
// (SdfIndirectStepCall, after the callee's Begin names it in sdfIndirectCallee) or returns (SdfIndirectStepReturn). One
// driver, sdfIndirectRun, steps the procedure on top of a small stack and answers every query through its one
// sdfIndirectServe call, so a kernel that runs all its indirect field work from one sdfIndirectRun call inlines the
// interpreter once however many procedures and query points it has. A procedure is on the stack at most once at a time,
// which its static record relies on. A kernel enables the procedures it reaches (SDF_INDIRECT_PROC_*): every enabled
// procedure is a case of the driver's dispatch whether or not a run reaches it. The including module defines the field
// query (sdfIndirectServe) first: the engine's (indirect/sdf-indirect-field.hlsli), or a probe fixture's own field.
// The kinds of indirect field query: the full-field sample at a position under a mask (its distance, material and
// walls) and the normalized full-field gradient there, each counting one evaluation and one work step and running the
// complete field with the tape off and indirect participation on, restoring the caller's masks and flags. A probe
// kernel that defines SDF_INDIRECT_PLAIN_QUERIES also asks for the plain field under the caller's own flags, uncounted:
// its independent reference, through the same one interpreter call site.
static const uint SdfIndirectQuerySample = 0u;
static const uint SdfIndirectQueryGradient = 1u;
static const uint SdfIndirectQueryPlain = 2u;
// The kernel's one indirect field query, which the including module defines.
void sdfIndirectServe(uint kind, float3 position, uint mask, out SdfHit hit, out float3 normal);

static const uint SdfIndirectStepQuery = 0u;
static const uint SdfIndirectStepCall = 1u;
static const uint SdfIndirectStepReturn = 2u;

static const uint SdfIndirectProcLaunch = 1u;
static const uint SdfIndirectProcSegment = 2u;
static const uint SdfIndirectProcProve = 3u;
static const uint SdfIndirectProcMarch = 4u;
static const uint SdfIndirectProcPlace = 5u;
static const uint SdfIndirectProcPartition = 6u;
static const uint SdfIndirectProcVisibilities = 7u;
static const uint SdfIndirectProcNearIncoming = 8u;
static const uint SdfIndirectProcConeBounce = 9u;
static const uint SdfIndirectProcAlternative = 10u;
static const uint SdfIndirectProcReceive = 11u;
static const uint SdfIndirectProcReceiver = 12u;
static const uint SdfIndirectProcKernel = 13u;
static const uint SdfIndirectProcDepth = 6u;

static float3 sdfIndirectQueryAt = 0.0;
static uint sdfIndirectQueryMask = 0u;
static uint sdfIndirectQueryKind = 0u;
static SdfHit sdfIndirectReply = (SdfHit)0;
static float3 sdfIndirectReplyGradient = 0.0;
static uint sdfIndirectCallee = 0u;

uint sdfIndirectAsk(float3 position, uint mask) {
    sdfIndirectQueryAt = position;
    sdfIndirectQueryMask = mask;
    sdfIndirectQueryKind = SdfIndirectQuerySample;
    return SdfIndirectStepQuery;
}
uint sdfIndirectAskGradient(float3 position) {
    sdfIndirectQueryAt = position;
    sdfIndirectQueryMask = SDF_INSTANCE_MASK_ALL;
    sdfIndirectQueryKind = SdfIndirectQueryGradient;
    return SdfIndirectStepQuery;
}
// A probe kernel's plain field query under its own flags (SDF_INDIRECT_PLAIN_QUERIES), answered in sdfIndirectReply.
uint sdfIndirectAskPlain(float3 position, uint mask) {
    sdfIndirectQueryAt = position;
    sdfIndirectQueryMask = mask;
    sdfIndirectQueryKind = SdfIndirectQueryPlain;
    return SdfIndirectStepQuery;
}
uint sdfIndirectCall(uint callee) {
    sdfIndirectCallee = callee;
    return SdfIndirectStepCall;
}

uint sdfIndirectStep(uint procedure);

// The driver's static trip ceiling. Every procedure's own loops carry static step ceilings and query budgets, which bound
// a run's turns (one per query, call and return) far below this: the longest run, the comparison receiver's four cone
// rays, each ending in six light slots' fallback marches, takes about four thousand. The ceiling only ends a run that a
// defective procedure never returns.
static const uint SdfIndirectRunTurns = 65536u;

// Runs `procedure`, already begun, to its return, answering every query the procedures on the stack ask through this
// one field call site. The deepest chain of calls (the comparison receiver's alternative, its cone bounce, the bounce's
// light visibilities and their march) is five procedures; the stack holds SdfIndirectProcDepth.
void sdfIndirectRun(uint procedure) {
    uint stack[SdfIndirectProcDepth];
    uint depth = 1u;
    stack[0] = procedure;
    [loop]
    for (uint turn = 0u; turn < SdfIndirectRunTurns && depth > 0u; turn++) {
        uint status = sdfIndirectStep(stack[depth - 1u]);
        if (status == SdfIndirectStepQuery) {
            sdfIndirectServe(sdfIndirectQueryKind, sdfIndirectQueryAt, sdfIndirectQueryMask, sdfIndirectReply, sdfIndirectReplyGradient);
        } else if (status == SdfIndirectStepCall) {
            stack[min(depth, SdfIndirectProcDepth - 1u)] = sdfIndirectCallee;
            depth = min(depth + 1u, SdfIndirectProcDepth);
        } else {
            depth--;
        }
    }
}
#endif
