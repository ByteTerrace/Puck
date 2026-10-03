using System.Buffers.Binary;

namespace Puck.Vulkan;

/// <summary>
/// The SPIR-V capabilities a shader module may declare on a Vulkan device Puck creates, and the check every module
/// passes before <c>vkCreateShaderModule</c> (<see cref="Factories.VulkanShaderModuleFactory"/>). A capability is
/// enabled when Vulkan 1.3 core grants it to every device, or when it needs a device feature that
/// <see cref="Factories.VulkanLogicalDeviceFactory"/> requires at creation and refuses a device without. A module
/// declaring any other capability is refused by name when it is created, so a shader can never reach a device that was
/// not created to run it, as a <c>discard</c> compiled to <c>OpDemoteToHelperInvocation</c> once did.
/// </summary>
public static class VulkanShaderCapabilities {
    /// <summary>The SPIR-V <c>Shader</c> capability, which every graphics and compute module declares.</summary>
    public const uint Shader = 1u;
    /// <summary>The SPIR-V <c>ImageQuery</c> capability, for an image's size and levels.</summary>
    public const uint ImageQuery = 50u;
    /// <summary>The SPIR-V <c>GroupNonUniform</c> capability, for the subgroup's lanes.</summary>
    public const uint GroupNonUniform = 61u;
    /// <summary>The SPIR-V <c>GroupNonUniformArithmetic</c> capability, for subgroup sums.</summary>
    public const uint GroupNonUniformArithmetic = 63u;
    /// <summary>The SPIR-V <c>GroupNonUniformBallot</c> capability, for subgroup ballots.</summary>
    public const uint GroupNonUniformBallot = 64u;
    /// <summary>The SPIR-V <c>DemoteToHelperInvocation</c> capability, which a fragment's <c>discard</c> compiles to; it
    /// needs the <c>shaderDemoteToHelperInvocation</c> feature.</summary>
    public const uint DemoteToHelperInvocation = 5379u;

    // The SPIR-V module's magic number, its first word: the specification's, not a format Puck versions, so
    // puck formats ledgers none.
    private const uint SpirvMagic = 0x07230203u;
    // The words of a module's header before its first instruction: magic, version, generator, bound and schema.
    private const int HeaderWords = 5;
    // The opcode of OpCapability.
    private const ushort CapabilityOpcode = 17;

    /// <summary>Gets every capability a module may declare, each with its name and what enables it on every device Puck
    /// creates.</summary>
    public static IReadOnlyList<(uint Capability, string Name, string EnabledBy)> Enabled { get; } = [
        (Shader, "Shader", "Vulkan core"),
        (ImageQuery, "ImageQuery", "Vulkan core"),
        (GroupNonUniform, "GroupNonUniform", "Vulkan 1.1 core"),
        (GroupNonUniformArithmetic, "GroupNonUniformArithmetic", "Vulkan 1.1 core"),
        (GroupNonUniformBallot, "GroupNonUniformBallot", "Vulkan 1.1 core"),
        (DemoteToHelperInvocation, "DemoteToHelperInvocation", "the shaderDemoteToHelperInvocation feature every device is created with"),
    ];

    /// <summary>Returns the capabilities a SPIR-V module declares, in declaration order.</summary>
    /// <param name="spirv">The module's bytes, little-endian words.</param>
    /// <returns>The declared capabilities.</returns>
    /// <exception cref="ArgumentException">The bytes are not a SPIR-V module: too short, not whole words, without its
    /// magic number, or with an instruction that runs past the end or has no words.</exception>
    public static IReadOnlyList<uint> Declared(ReadOnlySpan<byte> spirv) {
        if (((spirv.Length % sizeof(uint)) != 0) || (spirv.Length < (HeaderWords * sizeof(uint))) || (Word(index: 0, spirv: spirv) != SpirvMagic)) {
            throw new ArgumentException(message: "The bytes are not a little-endian SPIR-V module.", paramName: nameof(spirv));
        }

        var words = (spirv.Length / sizeof(uint));
        var declared = new List<uint>();

        for (var index = HeaderWords; (index < words);) {
            var instruction = Word(index: index, spirv: spirv);
            var count = ((int)(instruction >> 16));

            if ((count == 0) || ((index + count) > words)) {
                throw new ArgumentException(message: $"The SPIR-V module's instruction at word {index} has {count} words, which do not fit the module.", paramName: nameof(spirv));
            }
            if ((((ushort)instruction) == CapabilityOpcode) && (count == 2)) {
                declared.Add(item: Word(index: (index + 1), spirv: spirv));
            }

            index += count;
        }

        return declared;
    }
    /// <summary>Refuses a module that declares a capability no device Puck creates enables.</summary>
    /// <param name="spirv">The module's bytes.</param>
    /// <param name="module">The module's name, for the refusal.</param>
    /// <exception cref="ArgumentException">The bytes are not a SPIR-V module.</exception>
    /// <exception cref="NotSupportedException">The module declares a capability outside <see cref="Enabled"/>; the
    /// message names the module and the capability.</exception>
    public static void Require(ReadOnlySpan<byte> spirv, string module) {
        foreach (var capability in Declared(spirv: spirv)) {
            if (!IsEnabled(capability: capability)) {
                throw new NotSupportedException(message: $"The shader module '{module}' declares the SPIR-V capability {capability}, which no Vulkan device Puck creates enables; require the device feature it needs at device creation and list it in VulkanShaderCapabilities.Enabled.");
            }
        }
    }
    /// <summary>Indicates whether every device Puck creates enables a capability.</summary>
    /// <param name="capability">The SPIR-V capability.</param>
    /// <returns><see langword="true"/> when the capability is in <see cref="Enabled"/>.</returns>
    public static bool IsEnabled(uint capability) {
        foreach (var (enabled, _, _) in Enabled) {
            if (enabled == capability) {
                return true;
            }
        }

        return false;
    }

    private static uint Word(ReadOnlySpan<byte> spirv, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(source: spirv[(index * sizeof(uint))..]);
}
