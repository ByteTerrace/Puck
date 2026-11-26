using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The host-written tables: program words, dynamic transforms, screen surfaces, screen mappings,
// volumes, glyph decals, mesh draws, the lights and the sky's block and layers, the staged
// shadow handoff controls and the frame's sampled values. Each table uses a GpuRegion under the policy GpuResidency.Select chooses for
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
    private const int RegionCount = 12;
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
    private readonly GpuRegion m_programRegion;

    // Whether the dynamic-transform region has been packed whole once: its first frame packs every slot.
    private bool m_dynamicTransformsPacked;
    // One more when packed transform contents or the active row count changes. Replacing a source collection with
    // identical rows only reseeds motion history; the cadence signature does not treat that copy as a geometry edit.
    private ulong m_dynamicTransformRevision;

    // The moved set these tables last consumed transforms from, the serial of that frame, and the scratch the rows owed
    // since then are unioned into.
    private readonly GpuUploadRuns m_owedTransforms = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuRowCursor m_dynamicCursor = new();
    // Names the regions' host copies, native storage outside the allocator, in the process account; each region reports
    // its own changes, and the owner outlives them (DisposeRegions).
    private readonly Puck.Hosting.Jobs.MemoryAccount.Owner m_regionHostCopies = Puck.Hosting.Jobs.MemoryAccount.Shared.CreateOwner(name: "sdf.world.region-host-copies");

    // The mesh region's slots: the upload ring's, then one per background GPU work slot, which lands meshes
    // (MeshLandingSlot).
    private const int MeshRegionSlotCount = (FrameRingSize + SdfGpuWork.SlotCount);

    // The pool of the mesh region's own copy sets, which the region creates with itself at construction.
    private static GpuDescriptorPoolSizes MeshRegionCopyPoolSizes =>
        GpuRegionCopyPool.SizesOf(
            regionCount: 1,
            slotCount: MeshRegionSlotCount
        );
    // The one pool of the copy sets the tables reserve for their regions (ReserveRegionCopyPool).
    private static GpuDescriptorPoolSizes RegionCopyPoolSizes =>
        GpuRegionCopyPool.SizesOf(
            regionCount: RegionCount,
            slotCount: FrameRingSize
        );

    // Region index's region of byteCount bytes under the policy the device's profile selects, its ring in the memory the
    // profile selects, named by its table's role and writing the copy sets reserved for it. The draw region is always
    // staged: every raster and every shaded pixel reads a draw's record, so the records belong in device-local memory
    // rather than in the small host-visible device-local heap every residency's rings share. Every other region chooses
    // its own residency (GpuRegion's profile constructor), again whenever it grows, so no table selects or re-selects a
    // policy itself.
    private GpuRegion CreateRegion(int region, int byteCount) => ((region is MeshRegionIndex)
        ? new GpuRegion(
            bindings: m_gpu.Bindings,
            buffers: m_gpu.BufferFactory,
            byteCount: byteCount,
            copyPipeline: m_regionCopyPipeline,
            copySets: m_regionCopyPool.Region(index: region),
            memory: GpuHostVisibleMemory.Host,
            name: RegionName(region: region),
            policy: GpuResidencyPolicy.Staged,
            recorder: m_gpu.Recorder,
            slotCount: FrameRingSize,
            hostCopies: m_regionHostCopies
        )
        : new GpuRegion(
            bindings: m_gpu.Bindings,
            buffers: m_gpu.BufferFactory,
            byteCount: byteCount,
            copyPipeline: m_regionCopyPipeline,
            copySets: m_regionCopyPool.Region(index: region),
            name: RegionName(region: region),
            profile: m_deviceContext.MemoryProfile,
            hostCopies: m_regionHostCopies,
            readersInFlight: true,
            recorder: m_gpu.Recorder,
            slotCount: FrameRingSize
        ));

    // Disposes every region, then the copy pool reserved for them.
    private void DisposeRegions() {
        for (var index = 0; (index < RegionCount); index++) {
            RegionAt(index: index)?.Dispose();
        }

        m_meshIrradiance.Dispose();
        m_shadowHandoffBuffer.Dispose();
        m_regionCopyPool.Dispose();
        m_regionHostCopies.Dispose();
    }
    // The upload's copies: sends this ring slot, whose previous readers have finished, what it owes of every region, and
    // records every staged region's copy through the one owed-copy recording: at the first copy the barrier ordering the
    // earlier views' reads of every staged destination before the copies write it, then the copies, then one transition
    // per copied buffer so the views' passes read what it wrote. Every host write and copy of the regions is counted in
    // this pass. An upload owing nothing writes and records nothing.
    private void RecordRegionCopies() {
        StageShadowHandoffs();
        CountFrameMeshStaging(bytes: m_meshRegion.OwedCopyBytes);
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
    /// (<see cref="GpuRegion.OwnedBytes"/>): every per-frame table, the previous transform tables and the mesh region,
    /// and the world's shadow layers (<see cref="SdfWorldShadows.Bytes"/>). Read at the time asked, since a region
    /// grows by replacing its buffers. View-local readbacks remain charged to their recorders.</summary>
    public GpuMemoryBytes TableBytes {
        get {
            var bytes = (new GpuMemoryBytes(DeviceLocal: ((((m_previousDynamicTransforms.Buffer!.SizeBytes + m_previousMeshTransforms.Buffer!.SizeBytes) + m_shadowHandoffBuffer.SizeBytes) + ScreenEmission.SizeBytes) + (ulong)(m_shadows?.Bytes ?? 0L)), HostVisible: 0));

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

    /// <summary>Describes the residency's fixed cost for <c>world.residency</c>: what its tables hold whatever detail is
    /// resident, every buffer <see cref="TableBytes"/> counts, the draw records among them, and the host copies of its
    /// regions; then the presentation's mesh region beside it, which holds every residency's meshes once and is the
    /// same figure in every residency's line.</summary>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public string DescribeFixedCost() {
        var tables = TableBytes;
        var mesh = m_meshStore.OwnedBytes;
        var copies = 0UL;

        for (var index = 0; (index < RegionCount); index++) {
            if (RegionAt(index: index) is { } region) { copies += region.CpuShadowBytes; }
        }

        return string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"fixed-cost device-local={tables.DeviceLocal} host-visible={tables.HostVisible} host-copies={copies} (tables, the draw records among them) mesh-store device-local={mesh.DeviceLocal} host-visible={mesh.HostVisible} (the presentation's, held once)");
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
        FrameSampledRegionIndex => m_frameSampledRegion,
        _ => m_shadowHandoffRegion,
    };
    // The debug name of region index's objects, its reserved copy sets included: its table's role.
    private static GpuObjectName RegionName(int region) => NameOf(part: RegionRole(region: region));
    // Region index's table's role, which names its objects and its line in DescribeRegionFlushes.
    private static string RegionRole(int region) => region switch {
        ProgramRegionIndex => "program",
        DynamicTransformRegionIndex => "dynamic-transforms",
        ScreenSurfaceRegionIndex => "screen-surfaces",
        VolumeRegionIndex => "volumes",
        DecalRegionIndex => "decals",
        ScreenMappingRegionIndex => "screen-mappings",
        MeshRegionIndex => "mesh-draws",
        LightRegionIndex => "lights",
        SkyRegionIndex => "sky",
        SkyLayerRegionIndex => "sky-layers",
        FrameSampledRegionIndex => "frame-sampled",
        _ => "shadow-handoffs",
    };
    /// <summary>Describes what the latest upload flushed of the tables' regions (<see cref="GpuRegion.LastFlushedBytes"/>):
    /// the bytes of region words it wrote into host-visible memory, then each region that wrote any, by its table's role,
    /// and the sum every upload has flushed. A still world's upload writes what moved and nothing else. Read on the frame's
    /// thread.</summary>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public string DescribeRegionFlushes() {
        var builder = new System.Text.StringBuilder();
        var last = 0L;
        var total = 0L;

        for (var index = 0; (index < RegionCount); index++) {
            if (RegionAt(index: index) is not { } region) { continue; }
            last += region.LastFlushedBytes;
            total += region.FlushedBytes;
            if (region.LastFlushedBytes == 0L) { continue; }
            _ = builder.Append(value: ((builder.Length == 0) ? string.Empty : ",")).Append(value: RegionRole(region: index)).Append(value: ':').Append(value: region.LastFlushedBytes);
        }

        return string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"region-flushes last={last}B [{builder}] total={total}B uploads={m_uploads}");
    }
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
    private void WriteProgramWords(SdfProgram program) {
        _ = m_programRegion.Write(
            bytes: MemoryMarshal.AsBytes(span: program.FieldWords),
            offset: 0
        );
        _ = m_programRegion.Write(
            bytes: MemoryMarshal.AsBytes(span: program.ShadingWords),
            offset: checked((program.FieldWords.Length * sizeof(uint)))
        );
    }
}
