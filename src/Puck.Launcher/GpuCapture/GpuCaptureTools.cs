namespace Puck.Launcher;

/// <summary>
/// Loads the capture tool a run names (<c>--capture-tool pix|renderdoc</c>) into the process, which a host does before
/// anything can create a graphics device: <see cref="PixGpuCaptureTool"/> for Direct3D 12 on Windows,
/// <see cref="RenderDocCaptureTool"/> for either backend. <see cref="Refusal"/> decides whether a name can load on a
/// host; <c>TryLoad</c> loads it.
/// </summary>
public static partial class GpuCaptureTools {
    /// <summary>Gets every tool's spelling.</summary>
    public static IReadOnlyList<string> Names { get; } = ["pix", "renderdoc"];

    /// <summary>Returns why a named tool cannot load on a host, before anything is loaded.</summary>
    /// <param name="name">The tool's spelling.</param>
    /// <param name="directX">Whether the host renders on Direct3D 12, which PIX requires.</param>
    /// <returns>The reason, or <see langword="null"/> when the tool may load.</returns>
    public static string? Refusal(string name, bool directX) => name switch {
        "pix" when (!directX || !OperatingSystem.IsWindows()) => "PIX captures Direct3D 12 only; use --backend directx, or --capture-tool renderdoc",
        "pix" or "renderdoc" => null,
        _ => $"'{name}' is not a capture tool; expected {string.Join(separator: " or ", values: Names)}",
    };
}
