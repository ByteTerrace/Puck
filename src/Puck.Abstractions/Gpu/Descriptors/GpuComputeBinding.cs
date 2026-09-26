namespace Puck.Abstractions.Gpu;

/// <summary>Describes one descriptor binding of a compute pipeline's descriptor set 0: a buffer or a storage image, the
/// kinds a pipeline created without groups binds. A sampled image, a sampler or a constant buffer belongs to a group
/// (<see cref="GpuPipelineLayoutDescription"/>).</summary>
public readonly record struct GpuComputeBinding {
    /// <summary>Initializes one scalar or array descriptor binding.</summary>
    /// <param name="Binding">The binding index within descriptor set 0.</param>
    /// <param name="Kind">The descriptor kind: <see cref="GpuBindingKind.ReadOnlyBuffer"/>,
    /// <see cref="GpuBindingKind.ReadWriteBuffer"/> or <see cref="GpuBindingKind.StorageImage"/>.</param>
    /// <param name="Count">The descriptor-array length, at least one.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="Kind"/> is not a kind a binding list holds, or
    /// <paramref name="Count"/> is zero.</exception>
    public GpuComputeBinding(uint Binding, GpuBindingKind Kind, uint Count = 1) {
        if (!IsListKind(kind: Kind)) {
            throw new ArgumentOutOfRangeException(
                nameof(Kind),
                Kind,
                "A compute binding list holds buffers and storage images; a sampled image, a sampler or a constant buffer belongs to a group."
            );
        }

        ArgumentOutOfRangeException.ThrowIfZero(value: Count);

        this.Binding = Binding;
        this.Kind = Kind;
        this.Count = Count;
    }

    /// <summary>Gets the binding index within descriptor set 0.</summary>
    public uint Binding { get; }
    /// <summary>Gets the descriptor-array length at this binding.</summary>
    public uint Count { get; }
    /// <summary>Gets the descriptor kind.</summary>
    public GpuBindingKind Kind { get; }

    private static bool IsListKind(GpuBindingKind kind) =>
        (kind is GpuBindingKind.ReadOnlyBuffer or GpuBindingKind.ReadWriteBuffer or GpuBindingKind.StorageImage);

    /// <summary>Validates a whole set, including default values and duplicate binding indices.</summary>
    public static void ValidateSet(IReadOnlyList<GpuComputeBinding> bindings) {
        ArgumentNullException.ThrowIfNull(bindings);

        var seen = new HashSet<uint>();

        foreach (var binding in bindings) {
            if (
                !IsListKind(kind: binding.Kind) ||
                (0 == binding.Count)
            ) {
                throw new ArgumentException(
                    message: "The descriptor set contains a kind a binding list does not hold or a zero descriptor count.",
                    paramName: nameof(bindings)
                );
            }
            if (!seen.Add(item: binding.Binding)) {
                throw new ArgumentException(
                    message: $"Descriptor binding {binding.Binding} is declared more than once in set 0.",
                    paramName: nameof(bindings)
                );
            }
        }
    }
}
