using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    /// <summary>Gets the currently allocated program-word capacity, including any growth during live uploads.</summary>
    public int ProgramWordCapacity => m_programWordCapacity;
    /// <summary>Gets the bytes allocated for the visibility records: one record of <see cref="VisibilityRecordByteLength"/>
    /// bytes for every pixel of the full extent in every viewport the engine reserves.</summary>
    public ulong VisibilityRecordBytes => FrameBufferBytes(
        buffer: SdfFrameBuffer.PrimaryHits,
        capacity: FrameCapacity
    );
    /// <summary>Gets the bytes of one visibility record, the words <c>sdf-visibility.hlsli</c> lays out.</summary>
    public static int VisibilityRecordByteLength => PrimaryHitByteLength;

    private SdfFrameCapacity FrameCapacity => new(
        BrickPoolVoxels: m_brickPoolVoxelCapacity,
        DynamicTransforms: m_dynamicTransformCapacity,
        Height: m_height,
        Instances: m_instanceCapacity,
        Viewports: m_viewportCapacity,
        Width: m_width
    );

    /// <summary>Returns the bytes the engine allocates for one of its device-local frame buffers at a capacity, the one
    /// statement of each buffer's size that construction and program growth allocate by.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="capacity">The capacities the buffer is sized for.</param>
    /// <returns>The buffer's size in bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="buffer"/> is not a declared buffer.</exception>
    /// <exception cref="OverflowException">The size does not fit in 64 bits.</exception>
    public static ulong FrameBufferBytes(SdfFrameBuffer buffer, SdfFrameCapacity capacity) => buffer switch {
        SdfFrameBuffer.Viewports => checked((((ulong)capacity.Viewports) * ViewportByteLength)),
        SdfFrameBuffer.DynamicTransforms => checked((((ulong)capacity.DynamicTransforms) * DynamicTransformByteLength)),
        SdfFrameBuffer.InstanceGrid => checked((((ulong)SdfInstanceGrid.WordCapacity(maxInstances: capacity.Instances)) * sizeof(uint))),
        SdfFrameBuffer.BrickPool => checked((((ulong)Math.Max(
            val1: 1,
            val2: capacity.BrickPoolVoxels
        )) * sizeof(float))),
        SdfFrameBuffer.InstanceMasks => checked(((((ulong)capacity.Viewports) * capacity.Tiles) * (((ulong)SdfProgram.InstanceMaskStorageWordCountFor(instanceCount: capacity.Instances)) * sizeof(uint)))),
        // Four tile planes per tile, then the primary and AO part-bound bands per instance, per viewport.
        SdfFrameBuffer.Tiles => checked(((((ulong)capacity.Viewports) * ((TilePlaneCount * capacity.Tiles) + (((ulong)PartBoundFloatCount) * ((uint)capacity.Instances)))) * sizeof(float))),
        SdfFrameBuffer.ViewsArgs => ViewsArgsByteLength,
        SdfFrameBuffer.CullBounds => CullBoundsByteLength,
        SdfFrameBuffer.PrimaryHits => checked(((((ulong)capacity.Width) * capacity.Height) * (((ulong)capacity.Viewports) * PrimaryHitByteLength))),
        _ => throw new ArgumentOutOfRangeException(
            actualValue: buffer,
            message: "Unknown SDF frame buffer.",
            paramName: nameof(buffer)
        ),
    };

    // Called only by UploadProgram, with every frame-ring fence retired. No per-frame allocations.
    private void EnsureProgramCapacity(SdfProgram program) {
        if ((program.Words.Length <= m_programWordCapacity) && (program.Instances.Count <= m_instanceCapacity)) {
            return;
        }

        var words = GrowCapacity(m_programWordCapacity, program.Words.Length, (int.MaxValue / sizeof(uint)));
        var instances = GrowCapacity(m_instanceCapacity, program.Instances.Count, SdfProgramBuilder.MaxInstances);
        var growProgram = (words != m_programWordCapacity);
        var growInstances = (instances != m_instanceCapacity);
        var grown = (FrameCapacity with { Instances = instances });
        var gridWords = SdfInstanceGrid.WordCapacity(maxInstances: instances);
        var maskWords = SdfProgram.InstanceMaskWordCountFor(instanceCount: instances);
        var inputScratch = (growInstances ? new SdfInstanceGridInput[instances] : m_instanceGridInputScratch);
        var workspace = (growInstances ? new SdfInstanceGrid.Workspace(maxInstances: instances) : m_instanceGridWorkspace);
        var replacements = new List<IGpuBuffer>(capacity: (FrameRingSize + 4));
        var committed = false;

        IGpuStorageBuffer HostBuffer(ulong bytes) {
            var buffer = m_gpu.BufferFactory.CreateHostVisible(sizeBytes: bytes, usage: GpuBufferUsage.Storage);

            replacements.Add(item: buffer);
            return buffer;
        }
        IGpuBuffer DeviceBuffer(ulong bytes) {
            var buffer = m_gpu.BufferFactory.CreateDeviceLocal(sizeBytes: bytes, usage: GpuBufferUsage.Storage);

            replacements.Add(item: buffer);
            return buffer;
        }

        if (growInstances) {
            RequireOneCopyDispatch(
                byteLength: checked((((ulong)gridWords) * sizeof(uint))),
                table: "instance-grid"
            );
        }

        try {
            // Allocate the entire replacement before changing any binding or releasing an old buffer.
            var programBuffer = (growProgram ? HostBuffer(bytes: checked((((ulong)words) * sizeof(uint)))) : m_programBuffer);
            var grids = (growInstances ? new IGpuStorageBuffer[FrameRingSize] : m_instanceGridBuffers);

            if (growInstances) {
                for (var slot = 0; (slot < FrameRingSize); slot++) {
                    grids[slot] = HostBuffer(bytes: FrameUploadStagingBytes(tableBytes: checked((gridWords * sizeof(uint)))));
                }
            }
            var gridDevice = (growInstances ? DeviceBuffer(bytes: FrameBufferBytes(buffer: SdfFrameBuffer.InstanceGrid, capacity: grown)) : m_instanceGridDeviceBuffer);
            var masks = (growInstances ? DeviceBuffer(bytes: FrameBufferBytes(buffer: SdfFrameBuffer.InstanceMasks, capacity: grown)) : m_instanceMaskBuffer);
            var tiles = (growInstances ? DeviceBuffer(bytes: FrameBufferBytes(buffer: SdfFrameBuffer.Tiles, capacity: grown)) : m_tileBuffer);

            var oldGrids = ((IGpuStorageBuffer[])m_instanceGridBuffers.Clone());

            try {
                BindProgramCapacity(gridDevice: gridDevice, grids: grids, masks: masks, program: programBuffer, tiles: tiles);
            } catch {
                BindProgramCapacity(gridDevice: m_instanceGridDeviceBuffer, grids: m_instanceGridBuffers, masks: m_instanceMaskBuffer, program: m_programBuffer, tiles: m_tileBuffer);
                throw;
            }

            var oldProgram = m_programBuffer;
            var oldGridDevice = m_instanceGridDeviceBuffer;
            var oldMasks = m_instanceMaskBuffer;
            var oldTiles = m_tileBuffer;

            m_programBuffer = programBuffer;
            m_instanceGridDeviceBuffer = gridDevice;
            m_instanceMaskBuffer = masks;
            m_tileBuffer = tiles;
            grids.CopyTo(array: m_instanceGridBuffers, index: 0);
            m_instanceGridInputScratch = inputScratch;
            m_instanceGridWorkspace = workspace;
            m_programWordCapacity = words;
            m_instanceCapacity = instances;
            m_instanceGridWordCapacity = gridWords;
            m_instanceMaskWordCount = maskWords;
            committed = true;

            // A replaced buffer's contents are undefined: the next program write and grid stage start from nothing.
            if (growProgram) {
                m_programBufferCurrent = false;
                oldProgram.Dispose();
            }
            if (growInstances) {
                ForgetInstanceGridResidency(wordCapacity: gridWords);
                oldGridDevice.Dispose();
                oldMasks.Dispose();
                oldTiles.Dispose();
                foreach (var buffer in oldGrids) { buffer.Dispose(); }
            }
        } finally {
            if (!committed) {
                foreach (var buffer in replacements) { buffer.Dispose(); }
            }
        }
    }
    private static int GrowCapacity(int current, int required, int ceiling) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(required, ceiling);
        return ((required <= current) ? current : (int)Math.Max(val1: required, val2: Math.Min(val1: ceiling, val2: (((long)current) + Math.Max(val1: 1, val2: (current / 2))))));
    }
    // The same descriptor contracts as construction: views/primary/surface/ambient share viewsSets.
    private void BindProgramCapacity(IGpuStorageBuffer program, IGpuStorageBuffer[] grids, IGpuBuffer gridDevice, IGpuBuffer masks, IGpuBuffer tiles) {
        WriteStorageBufferReadOnly(binding: TileBindingIndex, buffer: tiles, set: m_cullArgsSet);
        for (var slot = 0; (slot < FrameRingSize); slot++) {
            var beam = m_beamSets[slot];
            var cull = m_instanceCullSets[slot];
            var views = m_viewsSets[slot];

            WriteStorageBuffer(binding: ProgramBindingIndex, buffer: program, set: beam);
            WriteStorageBuffer(binding: ProgramBindingIndex, buffer: program, set: cull);
            WriteStorageBuffer(binding: ProgramBindingIndex, buffer: program, set: views);
            WriteStorageBufferReadWrite(binding: TileBindingIndex, buffer: tiles, set: beam);
            WriteStorageBufferReadOnly(binding: TileBindingIndex, buffer: tiles, set: views);
            WriteStorageBufferReadOnly(binding: InstanceMaskBindingIndex, buffer: masks, set: beam);
            WriteStorageBufferReadWrite(binding: InstanceMaskBindingIndex, buffer: masks, set: cull);
            WriteStorageBufferReadOnly(binding: InstanceMaskBindingIndex, buffer: masks, set: views);
            WriteStorageBufferReadOnly(binding: FrameInstanceGridBindingIndex, buffer: gridDevice, set: cull);
            WriteStorageBufferReadOnly(binding: FrameInstanceGridBindingIndex, buffer: gridDevice, set: views);
            var upload = m_frameUploadSets[((slot * FrameUploadTableCount) + 2)];

            WriteStorageBufferReadOnly(binding: FrameUploadSourceBindingIndex, buffer: grids[slot], set: upload);
            WriteStorageBufferReadWrite(binding: FrameUploadDestinationBindingIndex, buffer: gridDevice, set: upload);
        }
    }
}
