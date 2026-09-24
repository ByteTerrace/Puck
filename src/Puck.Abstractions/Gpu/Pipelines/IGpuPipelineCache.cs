namespace Puck.Abstractions.Gpu;

/// <summary>
/// A device's persistent pipeline cache: the driver's native code for the pipelines the device has created, loaded
/// from disk when the device is created and handed to every compute and graphics pipeline creation, so a warm start
/// translates nothing. A device context that keeps one implements this interface; a caller probes for it rather than
/// requiring it. <see cref="Persist"/> is how the code that just built a batch of pipelines gets them onto disk
/// before the device ends.
/// </summary>
public interface IGpuPipelineCache {
    /// <summary>Writes the cache to its file when pipelines missed it since the last write; otherwise does nothing. Safe
    /// on any thread, including a pipeline build thread. A write that fails leaves the previous file whole and is
    /// reported, never thrown.</summary>
    void Persist();
}
