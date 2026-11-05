using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The mesh path's irradiance volume (SdfMeshIrradiance): SdfMeshIrradianceProbe.ProbeWords words a probe, the slots of the
// active tier's volume, written by the residency before the frame's upload and read by the views pass through the World set
// (sdfMeshIrradiance). It is staged, so it lives in device-local memory, and a frame no probe landed in writes nothing.
public sealed partial class SdfWorldTables {
    private const int MeshIrradianceRegionIndex = 11;
    private static readonly int MeshIrradianceBytes = ((SdfMeshIrradiance.MaxProbes * SdfMeshIrradianceProbe.ProbeWords) * sizeof(uint));

    private readonly GpuRegion m_meshIrradianceRegion;
    private IGpuBuffer? m_gpuMeshIrradiance;
    private IGpuBuffer? m_gpuIrradianceSource;
    private readonly List<int> m_gpuIrradianceSlots = [];

    /// <summary>Gets the irradiance region's actual device and upload-buffer bytes.</summary>
    public GpuMemoryBytes MeshIrradianceBufferBytes => m_meshIrradianceRegion.OwnedBytes + new GpuMemoryBytes(DeviceLocal: m_gpuMeshIrradiance?.SizeBytes ?? 0, HostVisible: 0);
    /// <summary>Gets the irradiance region's native host shadow and managed copy-header bytes.</summary>
    public ulong MeshIrradianceCpuBytes => (((ulong)m_meshIrradianceRegion.ShadowBytes) + m_meshIrradianceRegion.CpuScratchBytes);

    // Stage holds the volume's gate. Only records whose GPU commit completed and whose scene/window still stand
    // enter this list. The upload copies the ready levels before any view reads their newly published parameters.
    internal void PublishGpuMeshIrradiance(IGpuBuffer source, int wordCount, List<int> slots) {
        if (m_gpuMeshIrradiance is null || m_gpuMeshIrradiance.SizeBytes < (ulong)wordCount * 4) {
            if (m_gpuMeshIrradiance is not null) { Retire(m_gpuMeshIrradiance); }
            m_gpuMeshIrradiance = m_gpu.BufferFactory.CreateDeviceLocal(name: NameOf("probe-publication"), sizeBytes: (ulong)wordCount * 4, usage: GpuBufferUsage.Storage);
            m_bindingRevision++;
        }
        m_gpuIrradianceSource = source;
        m_gpuIrradianceSlots.AddRange(slots);
    }

    private void RecordGpuMeshIrradiance(nint command) {
        if (m_gpuIrradianceSlots.Count == 0) { return; }
        var source = m_gpuIrradianceSource!;
        var destination = m_gpuMeshIrradiance!;
        var recorder = m_gpu.Recorder;
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: source.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.TransferWrite, sourceStageMask: GpuStage.ComputeShader | GpuStage.Transfer,
            destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: destination.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderRead, sourceStageMask: TableReaders,
            destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
        m_gpuIrradianceSlots.Sort();
        for (var i = 0; i < m_gpuIrradianceSlots.Count;) {
            var first = m_gpuIrradianceSlots[i++];
            var end = first + 1;
            while (i < m_gpuIrradianceSlots.Count && m_gpuIrradianceSlots[i] <= end) { end = Math.Max(end, m_gpuIrradianceSlots[i++] + 1); }
            var offset = (ulong)first * SdfMeshIrradianceProbe.ProbeWords * 4;
            recorder.CopyBuffer(command, source.BufferHandle, destination.BufferHandle, (ulong)(end - first) * SdfMeshIrradianceProbe.ProbeWords * 4, offset, offset);
        }
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: destination.BufferHandle,
            sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: TableReaders);
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: source.BufferHandle,
            sourceAccessMask: GpuAccess.TransferRead, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader);
        m_gpuIrradianceSlots.Clear();
        m_gpuIrradianceSource = null;
    }

    /// <summary>Fits probe storage to the published window, retiring the replaced buffer through the tables' existing
    /// frame-ring retirement. The old window stays bound until its replacement is measured.</summary>
    /// <param name="wordCount">The words in the complete published window.</param>
    public void FitMeshIrradiance(int wordCount) {
        ArgumentOutOfRangeException.ThrowIfLessThan(wordCount, SdfMeshIrradianceProbe.ProbeWords);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(wordCount, (MeshIrradianceBytes / sizeof(uint)));
        var bytes = (((long)wordCount) * sizeof(uint));

        if (bytes == m_meshIrradianceRegion.ByteCount) { return; }
        Retire(replaced: ((bytes > m_meshIrradianceRegion.ByteCount) ? m_meshIrradianceRegion.Grow(byteCount: bytes) : m_meshIrradianceRegion.Shrink(byteCount: bytes)));
        m_bindingRevision++;
    }
    /// <summary>Writes the irradiance volume's probe slots from its first word; the upload of the frame sends what
    /// differs.</summary>
    /// <param name="words">The probe slots, <see cref="SdfMeshIrradianceProbe.ProbeWords"/> words each; at most the High
    /// tier's volume.</param>
    /// <param name="wordOffset">The first changed word in the table.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="words"/> holds more words than the table.</exception>
    public void WriteMeshIrradiance(ReadOnlySpan<uint> words, int wordOffset = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(wordOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: wordOffset, other: (MeshIrradianceBytes / sizeof(uint)));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: words.Length, other: ((MeshIrradianceBytes / sizeof(uint)) - wordOffset));

        if (words.IsEmpty) { return; }
        _ = m_meshIrradianceRegion.Write(bytes: MemoryMarshal.AsBytes(span: words), offset: (wordOffset * sizeof(uint)));
    }
}
