using System.Collections.ObjectModel;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// One frequency group's bindings. The group's ordinal is its Vulkan descriptor set and its Direct3D 12 register space
/// on both backends, and a binding's number is its Vulkan binding and its Direct3D 12 register.
/// <para>Bindings never overlap: an array of <c>n</c> descriptors at binding <c>b</c> takes bindings <c>b</c> through
/// <c>b + n - 1</c>, because Direct3D 12 gives it that many registers. A group may hold samplers beside other views;
/// Direct3D 12 then binds them through a second table, since a descriptor table cannot mix samplers with other
/// views.</para>
/// </summary>
public sealed class GpuGroupLayoutDescription {
    /// <summary>Initializes a new instance of the <see cref="GpuGroupLayoutDescription"/> class.</summary>
    /// <param name="ordinal">The group's ordinal: its descriptor set and register space, below
    /// <see cref="GpuPipelineLayoutDescription.GroupCount"/>.</param>
    /// <param name="bindings">The group's bindings in any order; at least one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The ordinal is not a group's, the group holds no binding, a binding holds an
    /// undefined kind or no descriptor, or two bindings overlap.</exception>
    public GpuGroupLayoutDescription(uint ordinal, IReadOnlyList<GpuGroupBinding> bindings) {
        ArgumentNullException.ThrowIfNull(argument: bindings);

        if (ordinal >= GpuPipelineLayoutDescription.GroupCount) {
            throw new ArgumentException(
                message: $"Group {ordinal} is not a frequency group; a group's ordinal is below {GpuPipelineLayoutDescription.GroupCount}, and space {GpuPipelineLayoutDescription.PushIndexSpace} holds the push index.",
                paramName: nameof(ordinal)
            );
        }
        if (bindings.Count == 0) {
            throw new ArgumentException(
                message: $"Group {ordinal} holds no binding.",
                paramName: nameof(bindings)
            );
        }

        var sorted = bindings.OrderBy(keySelector: static binding => binding.Binding).ToArray();

        for (var index = 0; (index < sorted.Length); index++) {
            var binding = sorted[index];

            if (
                !Enum.IsDefined(value: binding.Kind) ||
                (binding.Count == 0)
            ) {
                throw new ArgumentException(
                    message: $"Group {ordinal} binding {binding.Binding} holds an undefined kind or no descriptor.",
                    paramName: nameof(bindings)
                );
            }
            if (index == 0) {
                continue;
            }

            var previous = sorted[(index - 1)];

            if (binding.Binding < (((ulong)previous.Binding) + previous.Count)) {
                throw new ArgumentException(
                    message: ((binding.Binding == previous.Binding)
                        ? $"Group {ordinal} declares binding {binding.Binding} twice."
                        : $"Group {ordinal} binding {binding.Binding} lies inside binding {previous.Binding}'s array of {previous.Count}."),
                    paramName: nameof(bindings)
                );
            }
        }

        Bindings = new ReadOnlyCollection<GpuGroupBinding>(list: sorted);
        Ordinal = ordinal;
    }

    /// <summary>Gets the group's bindings in binding order.</summary>
    public IReadOnlyList<GpuGroupBinding> Bindings { get; }
    /// <summary>Gets whether the group holds a sampler.</summary>
    public bool HoldsSamplers =>
        Bindings.Any(predicate: static binding => (binding.Kind == GpuBindingKind.Sampler));
    /// <summary>Gets whether the group holds a binding other than a sampler.</summary>
    public bool HoldsViews =>
        Bindings.Any(predicate: static binding => (binding.Kind != GpuBindingKind.Sampler));
    /// <summary>Gets the group's ordinal: its Vulkan descriptor set and Direct3D 12 register space.</summary>
    public uint Ordinal { get; }
}
