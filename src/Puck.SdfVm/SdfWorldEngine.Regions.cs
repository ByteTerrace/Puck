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
// writes a region's buffer directly.
public sealed partial class SdfWorldEngine {
    private const int DynamicTransformWordCount = (DynamicTransformByteLength / sizeof(uint));
    // The per-frame regions RegionAt names, the mesh region last.
    private const int RegionCount = 9;
    private const int ViewportWordCount = (ViewportByteLength / sizeof(uint));

    private readonly GpuRegion m_viewportRegion;
    private readonly GpuRegion m_dynamicTransformRegion;
    private readonly GpuRegion m_screenSurfaceRegion;
    private readonly GpuRegion m_screenLightRegion;
    private readonly GpuRegion m_volumeRegion;
    // The glyph decal table (Stage 1 only): the leading per-screen descriptor band, then the shared cell region. All
    // zero (every descriptor's gridCols 0) is inert, so a program that declares no decal renders byte-identically.
    private readonly GpuRegion m_decalRegion;

    // The buffers the upload pass copied into, which it transitions for reading once every copy is recorded.
    private readonly IGpuBuffer?[] m_copiedRegions = new IGpuBuffer?[RegionCount];

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

    // The copy pools the engine's regions create under the staged policy, which its admission covers whatever policy
    // the device selects: one per per-frame region, and the brick staging's with a brick pool.
    private static GpuDescriptorPoolSizes[] RegionPoolSizes(bool brickPool) {
        var pools = new GpuDescriptorPoolSizes[(RegionCount + (brickPool ? 1 : 0))];

        Array.Fill(
            array: pools,
            value: GpuRegion.CopyPoolSizes(slotCount: FrameRingSize)
        );

        return pools;
    }
    // Binds each region's buffer for this ring slot into the slot's beam, instance-cull and views sets.
    private void BindRegions(int slot) {
        var beam = m_beamSets[slot];
        var cull = m_instanceCullSets[slot];
        var views = m_viewsSets[slot];

        foreach (var set in ((ReadOnlySpan<nint>)[beam, cull, views])) {
            WriteStorageBuffer(binding: ProgramBindingIndex, buffer: m_programRegion.Buffer(slot: slot), set: set);
            WriteStorageBuffer(binding: ViewportBindingIndex, buffer: m_viewportRegion.Buffer(slot: slot), set: set);
            WriteStorageBuffer(binding: DynamicTransformBindingIndex, buffer: m_dynamicTransformRegion.Buffer(slot: slot), set: set);
        }

        WriteStorageBufferReadOnly(binding: FrameInstanceGridBindingIndex, buffer: m_instanceGridRegion.Buffer(slot: slot), set: cull);
        WriteStorageBufferReadOnly(binding: FrameInstanceGridBindingIndex, buffer: m_instanceGridRegion.Buffer(slot: slot), set: views);
        // The views layout alone shades: screen surfaces (48-byte entries), screen lights and volumes (float4 rows) and
        // decals (uint4 cells), each read at a 16-byte stride.
        WriteStorageBuffer(binding: ScreenSurfaceBindingIndex, buffer: m_screenSurfaceRegion.Buffer(slot: slot), set: views);
        WriteStorageBuffer(binding: ScreenLightBindingIndex, buffer: m_screenLightRegion.Buffer(slot: slot), set: views);
        WriteStorageBuffer(binding: DecalCellsBindingIndex, buffer: m_decalRegion.Buffer(slot: slot), set: views);
        WriteStorageBuffer(binding: VolumeBindingIndex, buffer: m_volumeRegion.Buffer(slot: slot), set: views);
    }
    // A region of byteCount bytes under the policy the device's profile selects, its ring in the memory the profile
    // selects, named by its table's role.
    private GpuRegion CreateRegion(int byteCount, in GpuObjectName name) {
        var profile = m_deviceContext.MemoryProfile;

        return new GpuRegion(
            bindings: m_gpu.Bindings,
            buffers: m_gpu.BufferFactory,
            byteCount: byteCount,
            copyPipeline: m_regionCopyPipeline,
            memory: GpuResidency.RingMemory(profile: profile),
            name: in name,
            policy: GpuResidency.Select(
                byteCount: ((ulong)byteCount),
                profile: profile,
                readersInFlight: true
            ),
            recorder: m_gpu.Recorder,
            slotCount: FrameRingSize
        );
    }
    private void DisposeRegions() {
        for (var index = 0; (index < RegionCount); index++) {
            RegionAt(index: index)?.Dispose();
        }
    }
    // The upload pass: sends this ring slot, whose fence has retired, what it owes of every region, records every staged
    // region's copy, then makes each copied buffer readable by the passes after it. Every host write and copy of the
    // regions is counted in this pass. A frame owing nothing writes and records nothing.
    private void RecordRegionCopies(nint commandBuffer) {
        var recorder = m_gpu.Recorder;
        var copied = 0;

        for (var index = 0; (index < RegionCount); index++) {
            RegionAt(index: index)?.Flush(slot: m_currentSlot);
        }

        for (var index = 0; (index < RegionCount); index++) {
            if (RegionAt(index: index) is not { OwesCopy: true } region) {
                continue;
            }

            if (copied == 0) {
                recorder.BeginDebugGroup(
                    commandBufferHandle: commandBuffer,
                    label: "upload"
                );
            }

            region.RecordCopy(
                commandBuffer: commandBuffer,
                slot: m_currentSlot
            );
            m_copiedRegions[copied++] = region.Buffer(slot: m_currentSlot);
        }

        for (var index = 0; (index < copied); index++) {
            recorder.TransitionBuffer(
                bufferHandle: m_copiedRegions[index]!.BufferHandle,
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuAccess.ShaderRead,
                destinationStageMask: GpuStage.ComputeShader,
                sourceAccessMask: GpuAccess.ShaderWrite,
                sourceStageMask: GpuStage.ComputeShader
            );
            m_copiedRegions[index] = null;
        }

        if (copied > 0) {
            recorder.EndDebugGroup(
                commandBufferHandle: commandBuffer
            );
        }
    }
    private GpuRegion? RegionAt(int index) => index switch {
        0 => m_programRegion,
        1 => m_viewportRegion,
        2 => m_dynamicTransformRegion,
        3 => m_instanceGridRegion,
        4 => m_screenSurfaceRegion,
        5 => m_screenLightRegion,
        6 => m_volumeRegion,
        7 => m_decalRegion,
        _ => m_meshRegion,
    };
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
