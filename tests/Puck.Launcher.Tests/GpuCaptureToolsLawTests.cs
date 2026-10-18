using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>Laws for which capture tool may load on a host (<see cref="GpuCaptureTools.Refusal"/>), decided before
/// anything loads: an unknown name and PIX on a Vulkan host are refused by name, RenderDoc loads on either backend.</summary>
public sealed class GpuCaptureToolsLawTests {
    [Fact]
    public void AnUnknownToolIsRefusedByName() =>
        Assert.Contains(expectedSubstring: "'nsight' is not a capture tool", actualString: GpuCaptureTools.Refusal(directX: true, name: "nsight"));
    [Fact]
    public void PixOnVulkanIsRefusedAndRenderDocIsNot() {
        Assert.Contains(expectedSubstring: "PIX captures Direct3D 12 only", actualString: GpuCaptureTools.Refusal(directX: false, name: "pix"));
        Assert.Null(@object: GpuCaptureTools.Refusal(directX: false, name: "renderdoc"));
        Assert.Equal(actual: (GpuCaptureTools.Refusal(directX: true, name: "pix") is null), expected: OperatingSystem.IsWindows());
    }
    [Fact]
    public void EveryToolIsNamed() =>
        Assert.Equal(actual: GpuCaptureTools.Names, expected: ["pix", "renderdoc"]);
}
