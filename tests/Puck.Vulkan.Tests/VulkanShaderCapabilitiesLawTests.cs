using System.Buffers.Binary;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Laws for the SPIR-V capabilities a Vulkan device runs (<see cref="VulkanShaderCapabilities"/>): a module's
/// declared capabilities read in order, a capability outside <see cref="VulkanShaderCapabilities.Enabled"/> refused by
/// name before the module is created, and a malformed module refused.</summary>
public sealed class VulkanShaderCapabilitiesLawTests {
    // OpCapability, OpMemoryModel and the Logical addressing and GLSL450 memory models.
    private const uint CapabilityOpcode = 17u;
    private const uint MemoryModelOpcode = 14u;

    [Fact]
    public void A_modules_capabilities_read_in_declaration_order() {
        var module = Module(VulkanShaderCapabilities.Shader, VulkanShaderCapabilities.GroupNonUniform);

        Assert.Equal(
            actual: VulkanShaderCapabilities.Declared(spirv: module),
            expected: [VulkanShaderCapabilities.Shader, VulkanShaderCapabilities.GroupNonUniform]
        );
    }
    [Fact]
    public void A_capability_no_device_enables_is_refused_naming_the_module_and_the_capability() {
        // Float16 (SPIR-V capability 9) needs shaderFloat16, which no device is required to have.
        var refusal = Assert.Throws<NotSupportedException>(testCode: () => VulkanShaderCapabilities.Require(
            module: "half.comp.spv",
            spirv: Module(VulkanShaderCapabilities.Shader, 9u)
        ));

        Assert.Contains(actualString: refusal.Message, expectedSubstring: "half.comp.spv");
        Assert.Contains(actualString: refusal.Message, expectedSubstring: "capability 9");
    }
    [Fact]
    public void Every_enabled_capability_is_accepted() {
        foreach (var (capability, _, _) in VulkanShaderCapabilities.Enabled) {
            VulkanShaderCapabilities.Require(module: "enabled", spirv: Module(capability));
        }
    }
    [Fact]
    public void ShadowStorageFormatsRequireTheirEnabledCapability() {
        Assert.True(condition: VulkanShaderCapabilities.IsEnabled(capability: VulkanShaderCapabilities.StorageImageExtendedFormats));
        VulkanShaderCapabilities.Require(module: "shadow-fade", spirv: Module(49u));
    }
    [Fact]
    public void A_malformed_module_is_refused() {
        var module = Module(VulkanShaderCapabilities.Shader);

        module[0] ^= 0xFF;
        _ = Assert.Throws<ArgumentException>(testCode: () => VulkanShaderCapabilities.Declared(spirv: module));
        // An instruction claiming more words than the module holds.
        var truncated = Module(VulkanShaderCapabilities.Shader);

        BinaryPrimitives.WriteUInt32LittleEndian(destination: truncated.AsSpan(start: (truncated.Length - 12)), value: (5u << 16) | CapabilityOpcode);
        _ = Assert.Throws<ArgumentException>(testCode: () => VulkanShaderCapabilities.Declared(spirv: truncated));
        _ = Assert.Throws<ArgumentException>(testCode: () => VulkanShaderCapabilities.Declared(spirv: new byte[6]));
    }

    // A minimal module: the header, one OpCapability per capability, and OpMemoryModel Logical GLSL450.
    private static byte[] Module(params uint[] capabilities) {
        var words = new List<uint> { 0x07230203u, 0x00010600u, 0u, 1u, 0u };

        foreach (var capability in capabilities) {
            words.Add(item: (2u << 16) | CapabilityOpcode);
            words.Add(item: capability);
        }
        words.Add(item: (3u << 16) | MemoryModelOpcode);
        words.Add(item: 0u);
        words.Add(item: 1u);
        var bytes = new byte[(words.Count * sizeof(uint))];

        for (var index = 0; (index < words.Count); index++) {
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes.AsSpan(start: (index * sizeof(uint))), value: words[index]);
        }

        return bytes;
    }
}
