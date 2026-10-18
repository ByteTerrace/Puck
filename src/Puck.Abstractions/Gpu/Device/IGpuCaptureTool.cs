namespace Puck.Abstractions.Gpu;

/// <summary>
/// An in-process GPU capture tool loaded before the device is created (the World's <c>--capture-tool pix|renderdoc</c>),
/// which captures the device's work between a start and an end into a file its own application opens. A tool is driven
/// on the host's frame thread by <see cref="GpuFrameCapture"/>.
/// </summary>
public interface IGpuCaptureTool {
    /// <summary>Gets the tool's one spelling: <c>pix</c> or <c>renderdoc</c>.</summary>
    string Name { get; }
    /// <summary>Gets whether one capture may span several frames (PIX); a tool that captures one frame at a time
    /// (RenderDoc) is started and ended once per frame.</summary>
    bool CapturesSpanFrames { get; }

    /// <summary>Starts capturing the device's work into a file named from <paramref name="path"/>.</summary>
    /// <param name="path">The absolute path the capture is written to, without the tool's extension when the tool names
    /// its own files.</param>
    /// <returns><see langword="null"/> when the capture started; otherwise why it did not.</returns>
    string? Start(string path);
    /// <summary>Ends the capture <see cref="Start"/> began.</summary>
    /// <returns>The outcome: written, with the file's path; still being written, so the host calls again on a later
    /// frame; or failed, with the reason.</returns>
    GpuCaptureEnd End();
}
/// <summary>What ending a capture did (<see cref="IGpuCaptureTool.End"/>).</summary>
/// <param name="Pending">Whether the tool is still writing the capture and is to be asked again.</param>
/// <param name="Path">The written file's path, or <see langword="null"/> while pending or on failure.</param>
/// <param name="Failure">Why the capture failed, or <see langword="null"/>.</param>
public readonly record struct GpuCaptureEnd(bool Pending, string? Path, string? Failure) {
    /// <summary>Returns the outcome of a written capture.</summary>
    /// <param name="path">The written file's path.</param>
    /// <returns>The outcome.</returns>
    public static GpuCaptureEnd Written(string path) => new(Failure: null, Path: path, Pending: false);
    /// <summary>Returns the outcome of a capture the tool is still writing.</summary>
    /// <returns>The outcome.</returns>
    public static GpuCaptureEnd StillWriting() => new(Failure: null, Path: null, Pending: true);
    /// <summary>Returns the outcome of a failed capture.</summary>
    /// <param name="reason">Why it failed.</param>
    /// <returns>The outcome.</returns>
    public static GpuCaptureEnd Failed(string reason) => new(Failure: reason, Path: null, Pending: false);
}
