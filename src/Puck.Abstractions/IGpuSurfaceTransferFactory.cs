namespace Puck.Abstractions;

/// <summary>
/// Creates backend-neutral surface readback, upload, and import objects. Every create call returns a new
/// instance — both backends construct one unconditionally — that the caller owns and disposes when finished;
/// disposing it releases the GPU resources behind any handles or memory it returned.
/// </summary>
public interface IGpuSurfaceTransferFactory {
    /// <summary>Creates a surface readback object; the caller owns and disposes it.</summary>
    IGpuSurfaceReadback CreateReadback(IGpuDeviceContext deviceContext);
    /// <summary>Creates a surface upload object; the caller owns and disposes it.</summary>
    IGpuSurfaceUpload CreateUpload(IGpuDeviceContext deviceContext);
    /// <summary>Creates a surface import object; the caller owns and disposes it.</summary>
    IGpuSurfaceImport CreateImport(IGpuDeviceContext deviceContext);
}
