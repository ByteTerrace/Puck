using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Vulkan;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The World build's shipped shaders require only capabilities the Vulkan device enables: every device's, or,
/// for an SDF kernel variant that depends on an optional capability (<see cref="SdfKernelSet.RequiresOf"/>), what that
/// capability enables, and nothing more.</summary>
public sealed class WorldShaderCapabilitiesLawTests {
    [Fact]
    public void EveryModuleTheWorldShipsDeclaresOnlyEnabledCapabilities() {
        // This project's World reference builds and copies the shipped kernels into this output for every configuration.
        var shaders = Path.Combine(AppContext.BaseDirectory, "Assets/Shaders");
        var modules = Directory.EnumerateFiles(shaders, "*.spv", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var variants = SdfKernelSet.Kernels.ToDictionary(kernel => $"{SdfKernelSet.StemOf(kernel)}.comp.spv", SdfKernelSet.RequiresOf, StringComparer.Ordinal);

        Assert.True(modules.Length >= 20, $"{modules.Length} SPIR-V modules under {shaders}");
        foreach (var path in modules) {
            VulkanShaderCapabilities.Require(
                module: Path.GetRelativePath(shaders, path).Replace('\\', '/'),
                optional: variants.GetValueOrDefault(Path.GetFileName(path), GpuOptionalCapabilities.None),
                spirv: File.ReadAllBytes(path));
        }
    }
}
