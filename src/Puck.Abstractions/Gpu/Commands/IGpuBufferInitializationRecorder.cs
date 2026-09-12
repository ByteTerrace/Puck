namespace Puck.Abstractions.Gpu;

/// <summary>Optional backend operation for initializing a storage buffer without a fake CPU-side placeholder.</summary>
public interface IGpuBufferInitializationRecorder {
    /// <summary>Records a zero fill for the complete storage buffer.</summary>
    void ClearStorageBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes);
}
