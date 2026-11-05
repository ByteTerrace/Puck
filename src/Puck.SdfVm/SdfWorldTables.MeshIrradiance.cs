using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh path's published irradiance table (SdfMeshIrradiance): SdfMeshIrradianceProbe.ProbeWords words a probe, the
// slots of the active tier's volume, read by the views pass through the World set (sdfMeshIrradiance). The probe
// service's feedback table (SdfMeshProbeGpuWork) holds every record its relights committed; the frame's upload copies the
// slots the volume publishes from it on the device, so no frame uploads the table from the host. Until the volume first
// publishes into these tables there is no table, the World set binds the dummy buffer, and the volume's parameters are
// disabled (SdfMeshIrradiance.Publish).
public sealed partial class SdfWorldTables {
    private IGpuBuffer? m_meshIrradiance;
    private bool m_meshIrradianceFresh;
    private IGpuBuffer? m_meshIrradianceSource;
    private readonly List<int> m_meshIrradianceSlots = [];

    /// <summary>Gets the published irradiance table's device bytes.</summary>
    public GpuMemoryBytes MeshIrradianceBufferBytes => new(DeviceLocal: m_meshIrradiance?.SizeBytes ?? 0, HostVisible: 0);

    // Stage holds the volume's gate. Only records whose GPU commit completed and whose scene/window still stand enter
    // this list. The table fits the published volume exactly, growing or shrinking with its tier; a replaced table
    // retires through the frame ring, and a new one is cleared before its first copies, so no view reads uninitialized
    // memory. Slots still pending for a replaced table belonged to its layout and are dropped with it.
    internal void PublishMeshIrradiance(IGpuBuffer source, int wordCount, List<int> slots) {
        var bytes = (ulong)wordCount * sizeof(uint);
        if (m_meshIrradiance is null || m_meshIrradiance.SizeBytes != bytes) {
            if (m_meshIrradiance is not null) { Retire(m_meshIrradiance); }
            m_meshIrradiance = m_gpu.BufferFactory.CreateDeviceLocal(name: NameOf("mesh-irradiance"), sizeBytes: bytes, usage: GpuBufferUsage.Storage);
            m_meshIrradianceFresh = true;
            m_meshIrradianceSlots.Clear();
            m_bindingRevision++;
        }
        m_meshIrradianceSource = source;
        m_meshIrradianceSlots.AddRange(slots);
    }

    private void RecordMeshIrradiance(nint command) {
        if (m_meshIrradiance is not { } destination || (!m_meshIrradianceFresh && m_meshIrradianceSlots.Count == 0)) { return; }
        var recorder = m_gpu.Recorder;
        // A buffer copy writes as CopyWrite and a clear as TransferWrite: Direct3D 12 synchronizes each by its own scope.
        if (m_meshIrradianceFresh) {
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: destination.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderRead, sourceStageMask: TableReaders,
                destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
            recorder.ClearStorageBuffer(bufferHandle: destination.BufferHandle, commandBufferHandle: command, sizeBytes: destination.SizeBytes);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: destination.BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.CopyWrite, destinationStageMask: GpuStage.Transfer);
            m_meshIrradianceFresh = false;
        } else {
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: destination.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderRead, sourceStageMask: TableReaders,
                destinationAccessMask: GpuAccess.CopyWrite, destinationStageMask: GpuStage.Transfer);
        }
        if (m_meshIrradianceSlots.Count > 0) {
            var source = m_meshIrradianceSource!;
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: source.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.CopyWrite, sourceStageMask: GpuStage.ComputeShader | GpuStage.Transfer,
                destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
            m_meshIrradianceSlots.Sort();
            for (var i = 0; i < m_meshIrradianceSlots.Count;) {
                var first = m_meshIrradianceSlots[i++];
                var end = first + 1;
                while (i < m_meshIrradianceSlots.Count && m_meshIrradianceSlots[i] <= end) { end = Math.Max(end, m_meshIrradianceSlots[i++] + 1); }
                var offset = (ulong)first * SdfMeshIrradianceProbe.ProbeWords * sizeof(uint);
                recorder.CopyBuffer(command, source.BufferHandle, destination.BufferHandle, (ulong)(end - first) * SdfMeshIrradianceProbe.ProbeWords * sizeof(uint), offset, offset);
            }
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: source.BufferHandle,
                sourceAccessMask: GpuAccess.TransferRead, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader);
            m_meshIrradianceSlots.Clear();
        }
        m_meshIrradianceSource = null;
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: destination.BufferHandle,
            sourceAccessMask: GpuAccess.CopyWrite, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: TableReaders);
    }
}
