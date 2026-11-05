using Puck.Vulkan;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The World build's shipped shaders require only capabilities the Vulkan device enables.</summary>
public sealed class WorldShaderCapabilitiesLawTests {
    [Fact]
    public void EveryModuleTheWorldShipsDeclaresOnlyEnabledCapabilities() {
        // This project's World reference builds and copies the shipped kernels into this output for every configuration.
        var shaders = Path.Combine(AppContext.BaseDirectory, "Assets/Shaders");
        var modules = Directory.EnumerateFiles(shaders, "*.spv", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(modules.Length >= 20, $"{modules.Length} SPIR-V modules under {shaders}");
        foreach (var path in modules) {
            VulkanShaderCapabilities.Require(
                module: Path.GetRelativePath(shaders, path).Replace('\\', '/'),
                spirv: File.ReadAllBytes(path));
        }
    }
}
