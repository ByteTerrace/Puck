using System.Collections.ObjectModel;

namespace Puck.Vulkan;

/// <summary>One binding of a planned descriptor set layout, in the fields of <c>VkDescriptorSetLayoutBinding</c>.</summary>
/// <param name="Binding">The binding number, equal to the neutral binding's.</param>
/// <param name="DescriptorType">The <c>VkDescriptorType</c> value (<see cref="VulkanDescriptorType"/>).</param>
/// <param name="Count">The descriptor count.</param>
public readonly record struct VulkanSetLayoutBinding(
    uint Binding,
    uint DescriptorType,
    uint Count
);
/// <summary>One planned descriptor set layout.</summary>
/// <param name="Set">The set number, equal to the group's ordinal.</param>
/// <param name="Bindings">The bindings in binding order; empty for a set number no group uses.</param>
public sealed record VulkanSetLayout(
    uint Set,
    IReadOnlyList<VulkanSetLayoutBinding> Bindings
);
/// <summary>
/// The descriptor set layouts and push range a pipeline layout needs, planned from a
/// <see cref="GpuPipelineLayoutDescription"/> with no device call.
/// <para>Each group is the descriptor set its ordinal names, and each binding keeps its number, kind and count.
/// Set numbers run from zero through the highest group's ordinal, because a pipeline layout lists its set layouts
/// by position; a set number no group uses is an empty layout. Every binding and the push range are visible to every
/// stage. Read-only and read-write buffers are both storage buffers here; only Direct3D 12 views them
/// differently.</para>
/// </summary>
public sealed class VulkanGroupLayouts {
    private VulkanGroupLayouts(IReadOnlyList<VulkanSetLayout> sets, uint pushRangeBytes) {
        PushRangeBytes = pushRangeBytes;
        Sets = sets;
    }

    /// <summary>Gets the push-constant range's size in bytes at offset 0:
    /// <see cref="GpuPipelineLayoutDescription.PushIndexBytes"/> when the pipeline pushes an index, otherwise
    /// zero.</summary>
    public uint PushRangeBytes { get; }
    /// <summary>Gets the set layouts, where a layout's position is its set number.</summary>
    public IReadOnlyList<VulkanSetLayout> Sets { get; }

    private static uint DescriptorType(GpuBindingKind kind) =>
        kind switch {
            GpuBindingKind.ConstantBuffer => VulkanDescriptorType.UniformBuffer,
            GpuBindingKind.ReadOnlyBuffer or GpuBindingKind.ReadWriteBuffer => VulkanDescriptorType.StorageBuffer,
            GpuBindingKind.SampledImage => VulkanDescriptorType.SampledImage,
            GpuBindingKind.StorageImage => VulkanDescriptorType.StorageImage,
            GpuBindingKind.Sampler => VulkanDescriptorType.Sampler,
            _ => throw new ArgumentOutOfRangeException(
                actualValue: kind,
                message: "The value is not a binding kind.",
                paramName: nameof(kind)
            ),
        };

    /// <summary>Plans the set layouts and push range a pipeline layout needs.</summary>
    /// <param name="description">The neutral pipeline layout.</param>
    /// <returns>The planned layouts.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="description"/> is <see langword="null"/>.</exception>
    public static VulkanGroupLayouts Plan(GpuPipelineLayoutDescription description) {
        ArgumentNullException.ThrowIfNull(argument: description);

        var setCount = ((description.Groups.Count == 0)
            ? 0u
            : (description.Groups[^1].Ordinal + 1));
        var sets = new VulkanSetLayout[setCount];

        for (var set = 0u; (set < setCount); set++) {
            sets[set] = new VulkanSetLayout(
                Bindings: [],
                Set: set
            );
        }

        foreach (var group in description.Groups) {
            sets[group.Ordinal] = new VulkanSetLayout(
                Bindings: group.Bindings.Select(selector: static binding => new VulkanSetLayoutBinding(
                    Binding: binding.Binding,
                    Count: binding.Count,
                    DescriptorType: DescriptorType(kind: binding.Kind)
                )).ToArray().AsReadOnly(),
                Set: group.Ordinal
            );
        }

        return new VulkanGroupLayouts(
            pushRangeBytes: (description.PushesIndex
                ? GpuPipelineLayoutDescription.PushIndexBytes
                : 0u),
            sets: new ReadOnlyCollection<VulkanSetLayout>(list: sets)
        );
    }
}
