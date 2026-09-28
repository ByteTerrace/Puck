using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The host-written tables: program words, dynamic transforms, the frame instance grid, screen surfaces, screen mappings,
// screen lights, volumes, glyph decals and mesh draws, each a GpuRegion under the policy GpuResidency.Select chooses for
// its size with the upload ring's readers in flight, a ring's buffers in the memory GpuResidency.RingMemory chooses. A
// frame writes each table into its region, which owes only the words that differ; the upload flushes its ring slot's
// share (the previous upload's fence has retired the views that read it), records each staged region's copy, then one
// transition per copied buffer so the views' passes read what it wrote. What an upload records follows the device's
// policy, so its counts are per-backend-deterministic. Nothing writes a region's buffer directly. Construction reserves
// every region's copy sets (the brick staging's included) in one copy pool, whatever policy the device selects, so a
// region created or grown at a later frame takes no descriptor range then.
public sealed partial class SdfWorldTables {
    private const int DynamicTransformWordCount = (DynamicTransformByteLength / sizeof(uint));
    // The brick staging's index in the reserved copy pool, past the per-frame regions.
    private const int BrickStagingRegionIndex = RegionCount;
    private const int DecalRegionIndex = 6;
    private const int DynamicTransformRegionIndex = 1;
    private const int InstanceGridRegionIndex = 2;
    private const int MeshRegionIndex = 8;
    private const int ProgramRegionIndex = 0;
    // The per-frame regions RegionAt names, the mesh region last.
    private const int RegionCount = 9;
    private const int ScreenLightRegionIndex = 4;
    private const int ScreenMappingRegionIndex = 7;
    private const int ScreenSurfaceRegionIndex = 3;
    private const int VolumeRegionIndex = 5;

    private readonly GpuRegion m_dynamicTransformRegion;
    private readonly GpuRegion m_screenSurfaceRegion;
    private readonly GpuRegion m_screenMappingRegion;
    private readonly GpuRegion m_screenLightRegion;
    private readonly GpuRegion m_volumeRegion;
    // The glyph decal table (Stage 1 only): the leading per-screen descriptor band, then the shared cell region. All
    // zero (every descriptor's gridCols 0) is inert, so a program that declares no decal renders byte-identically.
    private readonly GpuRegion m_decalRegion;
    // Every region's copy sets, by its index: RegionAt's, then the brick staging's with a brick pool.
    private readonly GpuRegionCopyPool m_regionCopyPool;
    // The upload's owed copies, recorded through the tables' counted recorder into the upload's command buffer.
    private readonly GpuRegionCopyRecording m_regionCopies;

    // Replaced when UploadProgram grows the program or instance capacity.
    private GpuRegion m_programRegion;
    private GpuRegion m_instanceGridRegion;
    // Whether the dynamic-transform region has been packed whole once: its first frame packs every slot.
    private bool m_dynamicTransformsPacked;
    // One more whenever a dynamic-transform slot is packed: the cadence signature folds this in place of hashing the
    // whole table.
    private ulong m_dynamicTransformRevision;

    // The moved set these tables last consumed transforms from, the serial of that frame, and the scratch the rows owed
    // since then are unioned into.
    private readonly GpuUploadRuns m_owedTransforms = new(capacity: GpuRegion.MaxCopyRuns);

    private SdfMovedTransforms? m_movedTransformsSource;
    private long m_movedTransformsSerial;
    private IReadOnlyList<DynamicTransform>? m_movedTransformsTable;
    // A per-frame grid rebuild owed whether or not a transform moved: set by UploadProgram for a moving-instance program.
    private bool m_instanceGridRebuildOwed;

    // The regions the copy pool serves: the per-frame regions, and the brick staging with a brick pool.
    private static int CopyRegionCount(bool brickPool) =>
        (RegionCount + (brickPool ? 1 : 0));
    // The one pool of the copy sets the tables reserve for their regions (ReserveRegionCopyPool).
    private static GpuDescriptorPoolSizes RegionCopyPoolSizes(bool brickPool) =>
        GpuRegionCopyPool.SizesOf(
            regionCount: CopyRegionCount(brickPool: brickPool),
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
            policy: ((region == MeshRegionIndex)
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

        m_brickRegion?.Dispose();
        m_regionCopyPool.Dispose();
    }
    // The upload's copies: sends this ring slot, whose previous readers have finished, what it owes of every region, and
    // records every staged region's copy through the one owed-copy recording: at the first copy the barrier ordering the
    // earlier views' reads of every staged destination before the copies write it, then the copies, then one transition
    // per copied buffer so the views' passes read what it wrote. Every host write and copy of the regions is counted in
    // this pass. An upload owing nothing writes and records nothing.
    private void RecordRegionCopies() {
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
    /// (<see cref="GpuRegion.OwnedBytes"/>): every per-frame table, the previous transform tables, the mesh region, and the brick staging with a brick
    /// pool. Read at the time asked, since a region grows by being replaced.</summary>
    public GpuMemoryBytes TableBytes {
        get {
            var bytes = new GpuMemoryBytes(DeviceLocal: (m_previousDynamicTransforms.SizeBytes + m_previousMeshTransforms.SizeBytes), HostVisible: 0);

            for (var index = 0; (index < RegionCount); index++) {
                if (RegionAt(index: index) is { } region) {
                    bytes += region.OwnedBytes;
                }
            }

            return ((m_brickRegion is { } brick)
                ? (bytes + brick.OwnedBytes)
                : bytes
            );
        }
    }

    private GpuRegion? RegionAt(int index) => index switch {
        ProgramRegionIndex => m_programRegion,
        DynamicTransformRegionIndex => m_dynamicTransformRegion,
        InstanceGridRegionIndex => m_instanceGridRegion,
        ScreenSurfaceRegionIndex => m_screenSurfaceRegion,
        ScreenLightRegionIndex => m_screenLightRegion,
        VolumeRegionIndex => m_volumeRegion,
        DecalRegionIndex => m_decalRegion,
        ScreenMappingRegionIndex => m_screenMappingRegion,
        _ => m_meshRegion,
    };
    // The debug name of region index's objects, its reserved copy sets included: its table's role.
    private static GpuObjectName RegionName(int region) => NameOf(part: region switch {
        ProgramRegionIndex => "program",
        DynamicTransformRegionIndex => "dynamic-transforms",
        InstanceGridRegionIndex => "instance-grid",
        ScreenSurfaceRegionIndex => "screen-surfaces",
        ScreenLightRegionIndex => "screen-lights",
        VolumeRegionIndex => "volumes",
        DecalRegionIndex => "decals",
        ScreenMappingRegionIndex => "screen-mappings",
        MeshRegionIndex => "mesh-region",
        _ => "brick-staging",
    });
    // Creates the one copy pool of every region the tables may create, the brick staging's with a brick pool, each
    // region's sets named as its region: the pool its admission states (RegionCopyPoolSizes).
    private GpuRegionCopyPool ReserveRegionCopyPool(GpuCreationScope scope) {
        var regions = new GpuObjectName[CopyRegionCount(brickPool: m_brickPoolEnabled)];

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
    // Writes a freshly built instance grid into its region, which owes only the words that differ.
    private void StageInstanceGrid(ReadOnlySpan<uint> words) =>
        _ = m_instanceGridRegion.Write(
            bytes: MemoryMarshal.AsBytes(span: words),
            offset: 0
        );
    // Writes the program's words into the program region, which owes only the words that differ from the last program.
    private void WriteProgramWords(SdfProgram program) =>
        _ = m_programRegion.Write(
            bytes: MemoryMarshal.AsBytes(span: program.Words),
            offset: 0
        );
}
