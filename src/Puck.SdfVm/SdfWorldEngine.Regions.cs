using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The host-written tables: program words, viewport rows, dynamic transforms, the frame instance grid, screen surfaces,
// screen lights, volumes, glyph decals and mesh draws, each a GpuRegion under the policy GpuResidency.Select chooses for
// its size with the frame ring's reader in flight, a ring's buffers in the memory GpuResidency.RingMemory chooses. A
// frame writes each table into its region, which owes only the words that differ; the upload pass flushes this ring
// slot's share (its fence has retired), records each staged region's copy, then one transition per copied buffer so
// every later pass reads what it wrote. What the pass records follows the device's policy, so its counts are
// per-backend-deterministic. The top-of-frame barrier orders the copies after the previous frame's reads. Nothing
// writes a region's buffer directly. Construction reserves every region's copy sets (the brick staging's included) in
// one copy pool beside the engine's own, whatever policy the device selects, so a region created or grown at a later
// frame takes no descriptor range then.
public sealed partial class SdfWorldEngine {
    private const int DynamicTransformWordCount = (DynamicTransformByteLength / sizeof(uint));
    // The brick staging's index in the reserved copy pool, past the per-frame regions.
    private const int BrickStagingRegionIndex = RegionCount;
    private const int DecalRegionIndex = 7;
    private const int DynamicTransformRegionIndex = 2;
    private const int InstanceGridRegionIndex = 3;
    private const int MeshRegionIndex = 8;
    private const int ProgramRegionIndex = 0;
    // The per-frame regions RegionAt names, the mesh region last.
    private const int RegionCount = 9;
    private const int ScreenLightRegionIndex = 5;
    private const int ScreenSurfaceRegionIndex = 4;
    private const int ViewportRegionIndex = 1;
    private const int VolumeRegionIndex = 6;
    private const int ViewportWordCount = (ViewportByteLength / sizeof(uint));

    private readonly GpuRegion m_viewportRegion;
    private readonly GpuRegion m_dynamicTransformRegion;
    private readonly GpuRegion m_screenSurfaceRegion;
    private readonly GpuRegion m_screenLightRegion;
    private readonly GpuRegion m_volumeRegion;
    // The glyph decal table (Stage 1 only): the leading per-screen descriptor band, then the shared cell region. All
    // zero (every descriptor's gridCols 0) is inert, so a program that declares no decal renders byte-identically.
    private readonly GpuRegion m_decalRegion;
    // Every region's copy sets, by its index: RegionAt's, then the brick staging's with a brick pool.
    private readonly GpuRegionCopyPool m_regionCopyPool;
    // The upload pass's owed copies, recorded through the engine's counted recorder into the frame's command buffer.
    private readonly GpuRegionCopyRecording m_regionCopies;

    // Replaced, with every binding of them, when UploadProgram grows the program or instance capacity.
    private GpuRegion m_programRegion;
    private GpuRegion m_instanceGridRegion;
    // Whether the dynamic-transform region has been packed whole once: its first frame packs every slot.
    private bool m_dynamicTransformsPacked;
    // One more whenever a dynamic-transform slot is packed: the cadence signature folds this in place of hashing the
    // whole table.
    private ulong m_dynamicTransformRevision;

    // The moved set this engine last consumed transforms from, the serial of that frame, and the scratch the rows owed
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
    // The one pool of the copy sets the engine reserves for its regions (ReserveRegionCopyPool).
    private static GpuDescriptorPoolSizes RegionCopyPoolSizes(bool brickPool) =>
        GpuRegionCopyPool.SizesOf(
            regionCount: CopyRegionCount(brickPool: brickPool),
            slotCount: FrameRingSize
        );
    // Binds each region's buffer for this ring slot into every views set of the slot.
    private void BindRegions(int slot) {
        foreach (var views in m_viewsSets[slot]) {
            WriteWorldBuffer(buffer: m_programRegion.Buffer(slot: slot), member: SdfWorldInterfaces.ProgramWords, set: views);
            WriteWorldBuffer(buffer: m_viewportRegion.Buffer(slot: slot), member: SdfWorldInterfaces.Viewports, set: views);
            WriteWorldBuffer(buffer: m_dynamicTransformRegion.Buffer(slot: slot), member: SdfWorldInterfaces.DynamicTransforms, set: views);
            WriteWorldBuffer(buffer: m_instanceGridRegion.Buffer(slot: slot), member: SdfWorldInterfaces.FrameInstanceGrid, set: views);
            WriteWorldBuffer(buffer: m_screenSurfaceRegion.Buffer(slot: slot), member: SdfWorldInterfaces.ScreenSurfaces, set: views);
            WriteWorldBuffer(buffer: m_screenLightRegion.Buffer(slot: slot), member: SdfWorldInterfaces.ScreenLights, set: views);
            WriteWorldBuffer(buffer: m_decalRegion.Buffer(slot: slot), member: SdfWorldInterfaces.DecalCells, set: views);
            WriteWorldBuffer(buffer: m_volumeRegion.Buffer(slot: slot), member: SdfWorldInterfaces.Volumes, set: views);
        }
    }
    // Binds the device-local buffers program growth replaces into every views set of the slot: the cull buffer and the
    // instance masks, each read-write for its writer and read-only for its readers.
    private void BindProgramBuffers(int slot) {
        foreach (var views in m_viewsSets[slot]) {
            WriteWorldBuffer(buffer: m_tileBuffer, member: SdfWorldInterfaces.TilesWritten, set: views);
            WriteWorldBuffer(buffer: m_tileBuffer, member: SdfWorldInterfaces.Tiles, set: views);
            WriteWorldBuffer(buffer: m_instanceMaskBuffer, member: SdfWorldInterfaces.InstanceMasksWritten, set: views);
            WriteWorldBuffer(buffer: m_instanceMaskBuffer, member: SdfWorldInterfaces.InstanceMasks, set: views);
        }
    }
    // Region index's region of byteCount bytes under the policy the device's profile selects, its ring in the memory the
    // profile selects, named by its table's role and writing the copy sets reserved for it.
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
            policy: GpuResidency.Select(
                byteCount: ((ulong)byteCount),
                profile: profile,
                readersInFlight: true
            ),
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
    // The upload pass: sends this ring slot, whose fence has retired, what it owes of every region, and records every
    // staged region's copy through the one owed-copy recording the render node shares: at the first copy the barrier
    // ordering the earlier frames' reads of every staged destination before the copies write it, then the copies, then
    // one transition per copied buffer so the passes after it read what it wrote. Every host write and copy of the regions
    // is counted in this pass. A frame owing nothing writes and records nothing.
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
    // Opens the upload pass's debug group in the frame's command buffer, at the frame's first owed copy.
    private nint BeginUpload() {
        var commandBuffer = m_commandPools[m_currentSlot].CommandBufferHandle;

        m_gpu.Recorder.BeginDebugGroup(
            commandBufferHandle: commandBuffer,
            label: "upload"
        );

        return commandBuffer;
    }
    private GpuRegion? RegionAt(int index) => index switch {
        ProgramRegionIndex => m_programRegion,
        ViewportRegionIndex => m_viewportRegion,
        DynamicTransformRegionIndex => m_dynamicTransformRegion,
        InstanceGridRegionIndex => m_instanceGridRegion,
        ScreenSurfaceRegionIndex => m_screenSurfaceRegion,
        ScreenLightRegionIndex => m_screenLightRegion,
        VolumeRegionIndex => m_volumeRegion,
        DecalRegionIndex => m_decalRegion,
        _ => m_meshRegion,
    };
    // The debug name of region index's objects, its reserved copy sets included: its table's role.
    private static GpuObjectName RegionName(int region) => NameOf(part: region switch {
        ProgramRegionIndex => "program",
        ViewportRegionIndex => "viewports",
        DynamicTransformRegionIndex => "dynamic-transforms",
        InstanceGridRegionIndex => "instance-grid",
        ScreenSurfaceRegionIndex => "screen-surfaces",
        ScreenLightRegionIndex => "screen-lights",
        VolumeRegionIndex => "volumes",
        DecalRegionIndex => "decals",
        MeshRegionIndex => "mesh-region",
        _ => "brick-staging",
    });
    // Creates the one copy pool of every region the engine may create, the brick staging's with a brick pool, each
    // region's sets named as its region: the pool its admission states after its own (RegionCopyPoolSizes).
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
