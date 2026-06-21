namespace Puck.Abstractions;

/// <summary>
/// Creates backend-neutral surface readback, upload, and import objects.
/// </summary>
public interface IGpuSurfaceTransferFactory {
    /// <summary>Creates a surface readback object, or <see langword="null"/> if readback is not needed.</summary>
    IGpuSurfaceReadback CreateReadback(IGpuDeviceContext deviceContext);
    /// <summary>Creates a surface upload object.</summary>
    IGpuSurfaceUpload CreateUpload(IGpuDeviceContext deviceContext);
    /// <summary>Creates a surface import object.</summary>
    IGpuSurfaceImport CreateImport(IGpuDeviceContext deviceContext);
}
