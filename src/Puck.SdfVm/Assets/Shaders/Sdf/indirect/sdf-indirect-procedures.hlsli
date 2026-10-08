// The indirect procedures' dispatch (sdfIndirectRun, indirect/sdf-indirect-field.hlsli): one case per procedure the
// kernel enables. A kernel includes this after every procedure it enables, and enables only the procedures it reaches,
// since an enabled procedure is compiled into the dispatch whether or not a run reaches it.
#ifndef SDF_INDIRECT_PROCEDURES_HLSLI
#define SDF_INDIRECT_PROCEDURES_HLSLI
// A kernel without the indirect field module has no procedures.
#ifdef SDF_INDIRECT_FIELD_HLSLI

#ifdef SDF_INDIRECT_PROC_KERNEL
// The step of the kernel's own procedure, which a kernel that enables SDF_INDIRECT_PROC_KERNEL defines.
uint sdfIndirectKernelStep();
#endif

uint sdfIndirectStep(uint procedure) {
    [branch]
    switch (procedure) {
#ifdef SDF_INDIRECT_PROC_LAUNCH
        case SdfIndirectProcLaunch: return sdfIndirectLaunchStep();
#endif
#ifdef SDF_INDIRECT_PROC_SEGMENT
        case SdfIndirectProcSegment: return sdfIndirectSegmentStep();
#endif
#ifdef SDF_INDIRECT_PROC_PROVE
        case SdfIndirectProcProve: return sdfIndirectProveStep();
#endif
#ifdef SDF_INDIRECT_PROC_MARCH
        case SdfIndirectProcMarch: return sdfIndirectMarchStep();
#endif
#ifdef SDF_INDIRECT_PROC_PLACE
        case SdfIndirectProcPlace: return sdfIndirectPlaceStep();
#endif
#ifdef SDF_INDIRECT_PROC_PARTITION
        case SdfIndirectProcPartition: return sdfIndirectPartitionStep();
#endif
#ifdef SDF_INDIRECT_PROC_VISIBILITIES
        case SdfIndirectProcVisibilities: return sdfIndirectVisibilitiesStep();
#endif
#ifdef SDF_INDIRECT_PROC_NEAR_INCOMING
        case SdfIndirectProcNearIncoming: return sdfIndirectNearIncomingStep();
#endif
#ifdef SDF_INDIRECT_PROC_CONE_BOUNCE
        case SdfIndirectProcConeBounce: return sdfIndirectConeBounceStep();
#endif
#ifdef SDF_INDIRECT_PROC_ALTERNATIVE
        case SdfIndirectProcAlternative: return sdfIndirectAlternativeStep();
#endif
#ifdef SDF_INDIRECT_PROC_RECEIVE
        case SdfIndirectProcReceive: return sdfIndirectReceiveStep();
#endif
#ifdef SDF_INDIRECT_PROC_RECEIVER
        case SdfIndirectProcReceiver: return sdfIndirectReceiverStep();
#endif
#ifdef SDF_INDIRECT_PROC_KERNEL
        case SdfIndirectProcKernel: return sdfIndirectKernelStep();
#endif
        default: return SdfIndirectStepReturn;
    }
}

#endif
#endif
