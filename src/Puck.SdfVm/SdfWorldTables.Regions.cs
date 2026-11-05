using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The host-written tables: program words, dynamic transforms, screen surfaces, screen mappings,
// volumes, glyph decals, mesh draws, the lights and the sky's block and layers, plus the staged
// shadow handoff controls. Each table uses a GpuRegion under the policy GpuResidency.Select chooses for
// its size with the upload ring's readers in flight, a ring's buffers in the memory GpuResidency.RingMemory chooses. A
// frame writes each table into its region, which owes only the words that differ; the upload flushes its ring slot's
// share (the previous upload's fence has retired the views that read it), records each staged region's copy, then one
// transition per copied buffer so the views' passes read what it wrote. What an upload records follows the device's
// policy, so its counts are per-backend-deterministic. Nothing writes a region's buffer directly. Construction reserves
// every region's copy sets in one copy pool, whatever policy the device selects, so a
// region created or grown at a later frame takes no descriptor range then.
public sealed partial class SdfWorldTables {
    private const int DynamicTransformWordCount = (DynamicTransformByteLength / sizeof(uint));
    private const int DecalRegionIndex = 4;
    private const int DynamicTransformRegionIndex = 1;
    private const int MeshRegionIndex = 6;
    private const int ProgramRegionIndex = 0;
    // The per-frame regions RegionAt names.
    private const int RegionCount = 11;
    private const int ScreenMappingRegionIndex = 5;
    private const int ScreenSurfaceRegionIndex = 2;
    private const int VolumeRegionIndex = 3;

    private readonly GpuRegion m_dynamicTransformRegion;
    private readonly GpuRegion m_screenSurfaceRegion;
    private readonly GpuRegion m_screenMappingRegion;
    private readonly GpuRegion m_volumeRegion;
    // The glyph decal table (Stage 1 only): the leading per-screen descriptor band, then the shared cell region. All
    // zero (every descriptor's gridCols 0) is inert, so a program that declares no decal renders byte-identically.
    private readonly GpuRegion m_decalRegion;
    // Every region's copy sets, by its index (RegionAt's).
    private readonly GpuRegionCopyPool m_regionCopyPool;
    // The upload's owed copies, recorded through the tables' counted recorder into the upload's command buffer.
    private readonly GpuRegionCopyRecording m_regionCopies;

    // Replaced when UploadProgram grows the program capacity.
    private GpuRegion m_programRegion;
    // Whether the dynamic-transform region has been packed whole once: its first frame packs every slot.
    private bool m_dynamicTransformsPacked;
    // One more when packed transform contents or the active row count changes. Replacing a source collection with
    // identical rows only reseeds motion history; the cadence signature does not treat that copy as a geometry edit.
    private ulong m_dynamicTransformRevision;

    // The moved set these tables last consumed transforms from, the serial of that frame, and the scratch the rows owed
    // since then are unioned into.
    private readonly GpuUploadRuns m_owedTransforms = new(capacity: GpuRegion.MaxCopyRuns);

    private SdfMovedTransforms? m_movedTransformsSource;
    private long m_movedTransformsSerial;
    private IReadOnlyList<DynamicTransform>? m_movedTransformsTable;

    // The one pool of the copy sets the tables reserve for their regions (ReserveRegionCopyPool).
    private static GpuDescriptorPoolSizes RegionCopyPoolSizes =>
        GpuRegionCopyPool.SizesOf(
            regionCount: RegionCount,
            slotCount: FrameRingSize
        );
    // Region index's region of byteCount bytes under the policy the device's profile selects, its ring in the memory the
    // profile selects, named by its table's role and writing the copy sets reserved for it. The mesh region is always
    // staged: it holds every drawn mesh's vertices and is rewritten only when the draws change, so it belongs in
    // device-local memory rather than in the small host-visible device-local heap every residency's rings share.
    private GpuRegion CreateRegion(int region, int byteCount) {
        var profile = m_deviceContext.MemoryProfile;

        return new GpuRegion(
            bindings: m_gpu.Bindings,
            buffers: m_gpu.BufferFactory,
            byteCount: byteCount,
            copyPipeline: m_regionCopyPipeline,
            copySets: m_regionCopyPool.Region(index: region),
            memory: GpuResidency.RingMemory(profile: profile),
            name: RegionName(region: region),
            policy: ((region is MeshRegionIndex)
                ? GpuResidencyPolicy.Staged
                : GpuResidency.Select(
                    byteCount: ((ulong)byteCount),
                    profile: profile,
                    readersInFlight: true
                )),
            recorder: m_gpu.Recorder,
            slotCount: FrameRingSize
        );
    }
    // Disposes every region, then the copy pool reserved for them.
    private void DisposeRegions() {
        for (var index = 0; (index < RegionCount); index++) {
            RegionAt(index: index)?.Dispose();
        }

        m_meshIrradiance?.Dispose();
        m_shadowHandoffBuffer.Dispose();
        m_regionCopyPool.Dispose();
    }
    // The upload's copies: sends this ring slot, whose previous readers have finished, what it owes of every region, and
    // records every staged region's copy through the one owed-copy recording: at the first copy the barrier ordering the
    // earlier views' reads of every staged destination before the copies write it, then the copies, then one transition
    // per copied buffer so the views' passes read what it wrote. Every host write and copy of the regions is counted in
    // this pass. An upload owing nothing writes and records nothing.
    private void RecordRegionCopies() {
        StageShadowHandoffs();
        for (var index = 0; (index < RegionCount); index++) {
            if (RegionAt(index: index) is { } region) {
                m_regionCopies.Record(
                    handsToReaders: true,
                    region: region,
                    slot: m_currentSlot
                );
            }
        }

        var commandBuffer = m_regionCopies.Finish();

        if (commandBuffer != 0) {
            m_gpu.Recorder.EndDebugGroup(commandBufferHandle: commandBuffer);
        }
    }
    // Opens the upload pass's debug group in the upload's command buffer, at its first owed copy.
    private nint BeginUpload() {
        var commandBuffer = m_commandPools[m_currentSlot].CommandBufferHandle;

        m_gpu.Recorder.BeginDebugGroup(
            commandBufferHandle: commandBuffer,
            label: "upload"
        );

        return commandBuffer;
    }

    /// <summary>Gets the bytes of the buffers the tables' regions hold, by the memory they live in
    /// (<see cref="GpuRegion.OwnedBytes"/>): every per-frame table, the previous transform tables and the mesh region.
    /// Read at the time asked, since a region
    /// grows by being replaced. View-local readbacks remain charged to their recorders.</summary>
    public GpuMemoryBytes TableBytes {
        get {
            var bytes = (new GpuMemoryBytes(DeviceLocal: (((m_previousDynamicTransforms.SizeBytes + m_previousMeshTransforms.SizeBytes) + m_shadowHandoffBuffer.SizeBytes) + ScreenEmission.SizeBytes), HostVisible: 0));
            bytes += m_meshHistoryCopies.OwnedBytes;
            bytes += MeshIrradianceBufferBytes;

            for (var index = 0; (index < RegionCount); index++) {
                if (RegionAt(index: index) is { } region) {
                    bytes += region.OwnedBytes;
                }
            }

            return bytes;
        }
    }

    private GpuRegion? RegionAt(int index) => index switch {
        ProgramRegionIndex => m_programRegion,
        DynamicTransformRegionIndex => m_dynamicTransformRegion,
        ScreenSurfaceRegionIndex => m_screenSurfaceRegion,
        VolumeRegionIndex => m_volumeRegion,
        DecalRegionIndex => m_decalRegion,
        ScreenMappingRegionIndex => m_screenMappingRegion,
        MeshRegionIndex => m_meshRegion,
        LightRegionIndex => m_lightRegion,
        SkyRegionIndex => m_skyRegion,
        SkyLayerRegionIndex => m_skyLayerRegion,
        _ => m_shadowHandoffRegion,
    };
    // The debug name of region index's objects, its reserved copy sets included: its table's role.
    private static GpuObjectName RegionName(int region) => NameOf(part: region switch {
        ProgramRegionIndex => "program",
        DynamicTransformRegionIndex => "dynamic-transforms",
        ScreenSurfaceRegionIndex => "screen-surfaces",
        VolumeRegionIndex => "volumes",
        DecalRegionIndex => "decals",
        ScreenMappingRegionIndex => "screen-mappings",
        MeshRegionIndex => "mesh-region",
        LightRegionIndex => "lights",
        SkyRegionIndex => "sky",
        SkyLayerRegionIndex => "sky-layers",
        _ => "shadow-handoffs",
    });
    // Creates the one copy pool of every region the tables may create, each region's sets named as its region: the pool its admission states (RegionCopyPoolSizes).
    private GpuRegionCopyPool ReserveRegionCopyPool(GpuCreationScope scope) {
        var regions = new GpuObjectName[RegionCount];

        for (var region = 0; (region < regions.Length); region++) {
            regions[region] = RegionName(region: region);
        }

        return scope.Own(created: new GpuRegionCopyPool(
            bindings: m_gpu.Bindings,
            copyPipeline: m_regionCopyPipeline,
            name: NameOf(part: "region-copies"),
            regions: regions,
            slotCount: FrameRingSize
        ));
    }
    // Writes the program's words into the program region, which owes only the words that differ from the last program.
    private void WriteProgramWords(SdfProgram program) =>
        _ = m_programRegion.Write(
            bytes: MemoryMarshal.AsBytes(span: program.Words),
            offset: 0
        );
}
