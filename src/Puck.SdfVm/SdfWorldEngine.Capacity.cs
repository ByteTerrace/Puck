using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    /// <summary>Gets the currently allocated program-word capacity, including any growth during live uploads.</summary>
    public int ProgramWordCapacity => m_programWordCapacity;

    // Called only by UploadProgram, with every frame-ring fence retired. No per-frame allocations.
    private void EnsureProgramCapacity(SdfProgram program) {
        if ((program.Words.Length <= m_programWordCapacity) && (program.Instances.Count <= m_instanceCapacity)) {
            return;
        }

        var words = GrowCapacity(m_programWordCapacity, program.Words.Length, int.MaxValue / sizeof(uint));
        var instances = GrowCapacity(m_instanceCapacity, program.Instances.Count, SdfProgramBuilder.MaxInstances);
        var growProgram = words != m_programWordCapacity;
        var growInstances = instances != m_instanceCapacity;
        var gridWords = SdfInstanceGrid.WordCapacity(maxInstances: instances);
        var maskWords = SdfProgram.InstanceMaskWordCountFor(instanceCount: instances);
        var maskStorageWords = checked(maskWords + ((maskWords + 31) / 32));
        var inputScratch = growInstances ? new SdfInstanceGridInput[instances] : m_instanceGridInputScratch;
        var workspace = growInstances ? new SdfInstanceGrid.Workspace(maxInstances: instances) : m_instanceGridWorkspace;
        var replacements = new List<IGpuBuffer>(capacity: FrameRingSize + 4);
        var committed = false;

        IGpuStorageBuffer HostBuffer(ulong bytes) {
            var buffer = m_gpu.StorageBufferFactory.Create(deviceContext: m_deviceContext, sizeBytes: bytes);
            replacements.Add(buffer);
            return buffer;
        }
        IGpuBuffer DeviceBuffer(ulong bytes) {
            var buffer = m_gpu.StorageBufferFactory.CreateDeviceLocal(deviceContext: m_deviceContext, sizeBytes: bytes);
            replacements.Add(buffer);
            return buffer;
        }

        try {
            // Allocate the entire replacement before changing any binding or releasing an old buffer.
            var programBuffer = growProgram ? HostBuffer(checked((ulong)words * sizeof(uint))) : m_programBuffer;
            var grids = growInstances ? new IGpuStorageBuffer[FrameRingSize] : m_instanceGridBuffers;
            if (growInstances) {
                for (var slot = 0; slot < FrameRingSize; slot++) {
                    grids[slot] = HostBuffer(checked((ulong)gridWords * sizeof(uint)));
                }
            }
            var gridDevice = growInstances ? DeviceBuffer(checked((ulong)gridWords * sizeof(uint))) : m_instanceGridDeviceBuffer;
            var masks = growInstances ? DeviceBuffer(checked((ulong)m_viewportCapacity * m_tileGridX * m_tileGridY * (uint)maskStorageWords * sizeof(uint))) : m_instanceMaskBuffer;
            var tiles = growInstances ? DeviceBuffer(checked((ulong)m_viewportCapacity *
                (((ulong)TilePlaneCount * m_tileGridX * m_tileGridY) + ((ulong)PartBoundFloatCount * (uint)instances)) * sizeof(float))) : m_tileBuffer;

            var oldGrids = (IGpuStorageBuffer[])m_instanceGridBuffers.Clone();
            try {
                BindProgramCapacity(programBuffer, grids, gridDevice, masks, tiles);
            } catch {
                BindProgramCapacity(m_programBuffer, m_instanceGridBuffers, m_instanceGridDeviceBuffer, m_instanceMaskBuffer, m_tileBuffer);
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
            grids.CopyTo(m_instanceGridBuffers, 0);
            m_instanceGridInputScratch = inputScratch;
            m_instanceGridWorkspace = workspace;
            m_programWordCapacity = words;
            m_instanceCapacity = instances;
            m_instanceGridWordCapacity = gridWords;
            m_instanceMaskWordCount = maskWords;
            committed = true;

            if (growProgram) { oldProgram.Dispose(); }
            if (growInstances) {
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
        return required <= current ? current : (int)Math.Max(required, Math.Min(ceiling, (long)current + Math.Max(1, current / 2)));
    }

    // The same descriptor contracts as construction: views/primary/surface/ambient share viewsSets.
    private void BindProgramCapacity(IGpuStorageBuffer program, IGpuStorageBuffer[] grids, IGpuBuffer gridDevice, IGpuBuffer masks, IGpuBuffer tiles) {
        WriteStorageBufferReadOnly(binding: TileBindingIndex, buffer: tiles, set: m_cullArgsSet);
        for (var slot = 0; slot < FrameRingSize; slot++) {
            var beam = m_beamSets[slot];
            var cull = m_instanceCullSets[slot];
            var views = m_viewsSets[slot];
            WriteStorageBuffer(binding: ProgramBindingIndex, buffer: program, set: beam);
            WriteStorageBuffer(binding: ProgramBindingIndex, buffer: program, set: cull);
            WriteStorageBuffer(binding: ProgramBindingIndex, buffer: program, set: views);
            WriteStorageBufferReadWrite(binding: TileBindingIndex, buffer: tiles, set: beam);
            WriteStorageBufferReadWrite(binding: TileBindingIndex, buffer: tiles, set: views);
            WriteStorageBufferReadOnly(binding: TileBindingIndex, buffer: tiles, set: m_compositeSets[slot]);
            WriteStorageBufferReadOnly(binding: InstanceMaskBindingIndex, buffer: masks, set: beam);
            WriteStorageBufferReadWrite(binding: InstanceMaskBindingIndex, buffer: masks, set: cull);
            WriteStorageBufferReadOnly(binding: InstanceMaskBindingIndex, buffer: masks, set: views);
            WriteStorageBufferReadOnly(binding: FrameInstanceGridBindingIndex, buffer: gridDevice, set: cull);
            WriteStorageBufferReadOnly(binding: FrameInstanceGridBindingIndex, buffer: gridDevice, set: views);
            var upload = m_frameUploadSets[(slot * FrameUploadTableCount) + 2];
            WriteStorageBufferReadOnly(binding: FrameUploadSourceBindingIndex, buffer: grids[slot], set: upload);
            WriteStorageBufferReadWrite(binding: FrameUploadDestinationBindingIndex, buffer: gridDevice, set: upload);
        }
    }
}
