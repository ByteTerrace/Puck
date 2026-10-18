using Puck.Abstractions.Gpu;

namespace Puck.Launcher;

public static partial class GpuCaptureTools {
    /// <summary>Loads a named capture tool for a host on a backend, unless <see cref="Refusal"/> refuses it.</summary>
    /// <param name="name">The tool's spelling (<see cref="Names"/>).</param>
    /// <param name="directX">Whether the host renders on Direct3D 12, which PIX requires.</param>
    /// <param name="tool">The loaded tool, or <see langword="null"/>.</param>
    /// <param name="failure">Why no tool loaded, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the tool loaded.</returns>
    public static bool TryLoad(string name, bool directX, out IGpuCaptureTool? tool, out string? failure) {
        tool = null;
        failure = Refusal(directX: directX, name: name);

        if (failure is not null) {
            return false;
        }

        if ((name == "pix") && OperatingSystem.IsWindows()) {
            var pixLoaded = PixGpuCaptureTool.TryLoad(failure: out failure, tool: out var pix);

            tool = pix;

            return pixLoaded;
        }

        var renderDocLoaded = RenderDocCaptureTool.TryLoad(failure: out failure, tool: out var renderDoc);

        tool = renderDoc;

        return renderDocLoaded;
    }
}
