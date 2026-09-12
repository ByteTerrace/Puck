namespace Puck.Abstractions.Gpu;

/// <summary>Optional backend operation for initializing a storage image without a fake CPU-side placeholder.</summary>
public interface IGpuImageInitializationRecorder {
    /// <summary>Records a zero clear for a color image in <see cref="GpuImageLayout.General"/>.</summary>
    void ClearStorageImage(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuPixelFormat format);
}