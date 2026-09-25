namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral surface readback, upload, and import objects, bound to the device context the factory
/// belongs to (<see cref="IGpuDeviceContext.Services"/>), so none of their calls takes a device. Every create call
/// returns a new instance — both backends construct one unconditionally — that the caller owns and disposes when
/// finished, before the device goes; disposing it releases the GPU resources behind any handles or memory it returned.
/// </summary>
public interface IGpuSurfaceTransferFactory {
    /// <summary>Creates a surface readback object; the caller owns and disposes it.</summary>
    IGpuSurfaceReadback CreateReadback();
    /// <summary>Creates a surface upload object; the caller owns and disposes it.</summary>
    IGpuSurfaceUpload CreateUpload();
    /// <summary>Creates a surface import object; the caller owns and disposes it.</summary>
    IGpuSurfaceImport CreateImport();
}
