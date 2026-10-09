#define SDF_INDIRECT_PASS
#define SDF_SCREEN_SOURCES
#define SDF_GROUP_SHADOW_GATHER
#define SDF_DYNAMIC_TRANSFORMS
// The trace runs its field work as one procedure (sdfIndirectKernelStep below), so the kernel inlines the interpreter once.
#define SDF_INDIRECT_PROCS_CUSTOM
#define SDF_INDIRECT_PROC_MARCH
#define SDF_INDIRECT_PROC_PROVE
#define SDF_INDIRECT_PROC_SEGMENT
#define SDF_INDIRECT_PROC_LAUNCH
#define SDF_INDIRECT_PROC_KERNEL
#include "../indirect/sdf-indirect-cache.hlsli"

// One probe ray's transport: support-seeking march segments, each continued only past a full-field proof of its
// travel, then a hit's launch and feedback proof. Its marches, proofs and launch are the calls of one procedure.
struct SdfIndirectTraceProc {
    float3 origin;
    float3 direction;
    float reach;
    uint gather;
    uint level;
    uint budget;
    uint mask;
    uint proofLevel;
    SdfIndirectRay ray;
    float travel;
    uint segment;
    float3 surface;
    float spacing;
    uint feedback;
    uint phase;
};
static SdfIndirectTraceProc sdfIndirectTraceProc = (SdfIndirectTraceProc)0;

static const uint SdfIndirectTraceBegun = 0u;
static const uint SdfIndirectTraceMarched = 1u;
static const uint SdfIndirectTraceSupported = 2u;
static const uint SdfIndirectTraceLaunched = 3u;
static const uint SdfIndirectTraceFedBack = 4u;

uint sdfIndirectTraceBegin(float3 origin, float3 direction, float reach, uint gather, uint level) {
    sdfIndirectTraceProc.origin = origin;
    sdfIndirectTraceProc.direction = direction;
    sdfIndirectTraceProc.reach = reach;
    sdfIndirectTraceProc.gather = gather;
    sdfIndirectTraceProc.level = level;
    sdfIndirectTraceProc.phase = SdfIndirectTraceBegun;
    return SdfIndirectProcKernel;
}

uint sdfIndirectKernelStep() {
    uint level = sdfIndirectTraceProc.level;
    float3 origin = sdfIndirectTraceProc.origin;
    float3 direction = sdfIndirectTraceProc.direction;
    uint phase = sdfIndirectTraceProc.phase;
    bool marching = false;
    if (phase == SdfIndirectTraceBegun) {
        sdfIndirectTraceProc.budget = SdfIndirectTraceSteps;
        sdfIndirectTraceProc.mask = 0u;
        sdfIndirectTraceProc.proofLevel = level;
        sdfIndirectTraceProc.ray = (SdfIndirectRay)0;
        sdfIndirectTraceProc.ray.kind = SdfIndirectKindUnresolved;
        sdfIndirectTraceProc.travel = 0.0;
        sdfIndirectTraceProc.segment = 0u;
        marching = true;
    } else if (phase == SdfIndirectTraceMarched) {
        sdfIndirectTraceProc.budget = sdfIndirectMarchProc.budget;
        sdfIndirectTraceProc.ray = sdfIndirectMarchProc.result;
        sdfIndirectTraceProc.ray.distance += sdfIndirectTraceProc.travel;
        if (!(sdfIndirectTraceProc.ray.kind != SdfIndirectKindExit || sdfIndirectTraceProc.ray.distance >= passGroup.farDistance)) {
            sdfIndirectTraceProc.travel = sdfIndirectTraceProc.ray.distance;
            sdfIndirectTraceProc.proofLevel = level + 1u;
            sdfIndirectTraceProc.phase = SdfIndirectTraceSupported;
            return sdfIndirectCall(sdfIndirectProveBegin(origin + direction * sdfIndirectTraceProc.travel, sdfIndirectTraceProc.proofLevel, sdfIndirectTraceProc.budget, 0.0));
        }
    } else if (phase == SdfIndirectTraceSupported) {
        sdfIndirectTraceProc.budget = sdfIndirectProveProc.budget;
        uint component = sdfIndirectProveProc.mask;
        sdfIndirectTraceProc.mask = sdfIndirectSupport(origin + direction * sdfIndirectTraceProc.travel, direction, sdfIndirectTraceProc.proofLevel, component);
        if (sdfIndirectTraceProc.mask != 0u) {
            sdfIndirectTraceProc.ray.kind = SdfIndirectKindContinuation;
        } else {
            sdfIndirectTraceProc.ray.kind = SdfIndirectKindUnresolved;
            sdfIndirectTraceProc.segment++;
            marching = true;
        }
    } else if (phase == SdfIndirectTraceLaunched) {
        float3 position = sdfIndirectLaunchProc.position;
        float clearance = sdfIndirectLaunchProc.clearance;
        if (sdfIndirectLaunchProc.launched) {
            sdfIndirectTraceProc.ray.launchHeight = sdfIndirectQuantizeLaunch(sdfIndirectTraceProc.surface, sdfIndirectTraceProc.ray.normal, sdfIndirectTraceProc.spacing, position, clearance);
            if (clearance > 0.0) {
                sdfIndirectTraceProc.phase = SdfIndirectTraceFedBack;
                return sdfIndirectCall(sdfIndirectProveBegin(position, level, sdfIndirectTraceProc.feedback, clearance));
            }
        }
        return SdfIndirectStepReturn;
    } else {
        sdfIndirectTraceProc.mask = sdfIndirectProveProc.mask;
        return SdfIndirectStepReturn;
    }
    // Reach is a support-seeking boundary, never an unproved terminal. Between attempts the full field certifies travel.
    if (marching && sdfIndirectTraceProc.segment < SdfIndirectTraceSteps && sdfIndirectTraceProc.budget > 0u) {
        float travel = sdfIndirectTraceProc.travel;
        float reach = sdfIndirectTraceProc.reach;
        float segmentEnd = reach > 0.0 && level + 1u < sdfIndirectLevelCount()
            ? min(passGroup.farDistance, travel == 0.0 ? reach : travel + sdfIndirectSpacing(passGroup.indirectTier, level + 1u))
            : passGroup.farDistance;
        sdfIndirectTraceProc.phase = SdfIndirectTraceMarched;
        return sdfIndirectCall(sdfIndirectMarchBegin(origin + direction * travel, direction, travel == 0.0 ? reach : 0.0,
            segmentEnd - travel, travel == 0.0 ? sdfIndirectTraceProc.gather : SDF_INSTANCE_MASK_ALL, 0.0, sdfIndirectTraceProc.budget));
    }
    if (sdfIndirectTraceProc.ray.kind == SdfIndirectKindHit) {
        sdfIndirectTraceProc.feedback = SdfIndirectFeedbackSteps - SdfIndirectLaunchSteps;
        sdfIndirectTraceProc.proofLevel = level;
        sdfIndirectTraceProc.surface = origin + direction * sdfIndirectTraceProc.ray.distance;
        sdfIndirectTraceProc.spacing = sdfIndirectSpacing(passGroup.indirectTier, level);
        sdfIndirectTraceProc.phase = SdfIndirectTraceLaunched;
        return sdfIndirectCall(sdfIndirectLaunchBegin(sdfIndirectTraceProc.surface, sdfIndirectTraceProc.ray.normal, sdfIndirectTraceProc.spacing, SdfIndirectLaunchSteps));
    }
    return SdfIndirectStepReturn;
}
#include "../indirect/sdf-indirect-procedures.hlsli"

[numthreads(64, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    if (passGroup.indirectTier == SdfIndirectTierOff) { return; }
    // One existing transport dispatch resets the allowance shared by all subsequent view consumers, even at rest.
    if (group.x == 0u && lane == 0u) {
        sdfIndirectStore(sdfIndirectReceiverProofWordOffset(passGroup.indirectTier), 0u);
        puckCountDetail(4u, 0u, 1u, 0u, 0u, 0u);
        if (passGroup.workCounterRowDetail == 0u) { puckCountWork(0u, 1u); }
    }
    uint item = passGroup.indirectItemFirst + group.x;
    if (item >= passGroup.indirectTraceCount || item >= sdfIndirectTraceBudget(passGroup.indirectTier)
        || passGroup.indirectPlaceCount > sdfIndirectClassifyBudget(passGroup.indirectTier)
        || passGroup.indirectClassifyCount > sdfIndirectClassifyBudget(passGroup.indirectTier)
        || !sdfIndirectChunkTouches(group.x, 64u)) { return; }
    // Each lane is one admission unit, a ray of the stratum; every lane still joins the group's gather and barrier.
    bool active = sdfIndirectChunkUnit(group.x, lane, 64u);
    sdfProgramLayout = sdfLoadProgramLayout();
    uint4 update;
    int3 lattice;
    if (!sdfIndirectProbeUpdate(passGroup.indirectPlaceCount + passGroup.indirectClassifyCount + item, update, lattice)
        || update.y >= sdfIndirectRaysPerProbe(passGroup.indirectTier) / 64u) { return; }
    uint index = update.x;
    uint level = update.z;
    SdfIndirectPlacement probe = sdfIndirectReadProbe((int)index);
    float reach = sdfIndirectReach(passGroup.indirectTier, level);
    uint gather = sdfIndirectGather(probe.position, reach, lane);
    if (probe.classification != SdfIndirectClassActive && probe.classification != SdfIndirectClassRelocated) { return; }
    SdfIndirectRay ray = (SdfIndirectRay)0;
    ray.kind = SdfIndirectKindUnresolved;
    if (active) {
        uint ordinal = lane * (sdfIndirectRaysPerProbe(passGroup.indirectTier) / 64u) + update.y;
        sdfIndirectProofOwner = ordinal == 0u ? index : 0xffffffffu;
        float3 direction = sdfIndirectDirection(lattice, level, ordinal);
        sdfIndirectRun(sdfIndirectTraceBegin(probe.position, direction, reach, gather, level));
        ray = sdfIndirectTraceProc.ray;
        uint mask = sdfIndirectTraceProc.mask;
        uint proofLevel = sdfIndirectTraceProc.proofLevel;
        if (ray.kind == SdfIndirectKindUnresolved || ray.kind == SdfIndirectKindExit) { mask = 0u; }
        uint4 packed = sdfIndirectPackRay(ray, mask, proofLevel);
        uint address = sdfIndirectHitWordOffset(passGroup.indirectTier) + (index * sdfIndirectRaysPerProbe(passGroup.indirectTier) + ordinal) * SdfIndirectHitWords;
        sdfIndirectStore(address, packed.x);
        sdfIndirectStore(address + 1u, packed.y);
        sdfIndirectStore(address + 2u, packed.z);
        sdfIndirectStore(address + 3u, packed.w);
    }
    DeviceMemoryBarrierWithGroupSync();
    // The stratum's last ray marks it traced: every earlier ray was written by this group or an earlier chunk.
    if (active && lane == 63u) { InterlockedOr(indirectCacheRW[index * SdfIndirectProbeWords + 3u], 1u << (SdfIndirectTracedShift + update.y)); }
    uint detail = passGroup.indirectTier == SdfIndirectTierHigh ? level : level + 1u;
    sdfWorkTexels = active ? 1u : 0u;
    puckCountDetail(detail, sdfIndirectEvaluations - sdfIndirectLaunchEvaluations - sdfIndirectProofEvaluations, sdfWorkTexels, 0u, 0u, 0u);
    puckCountDetail(3u, sdfIndirectLaunchEvaluations, 0u, 0u, 0u, 0u);
    puckCountDetail(4u, sdfIndirectProofEvaluations, 0u, 0u, 0u, 0u);
    puckCountIndirect(detail, 0u, 0u, (active && ray.kind == SdfIndirectKindUnresolved) ? 1u : 0u);
    // A detail row holds this lane's work when the pass has detail rows; the plain row then adds none of it.
    if (passGroup.workCounterRowDetail != 0u) { sdfWorkSteps = 0u; sdfWorkTexels = 0u; }
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
