namespace Puck.Abstractions.Gpu;

/// <summary>Describes one descriptor binding of a compute pipeline's descriptor set 0.</summary>
public readonly record struct GpuComputeBinding {
    /// <summary>Initializes one scalar or array descriptor binding.</summary>
    public GpuComputeBinding(uint Binding, GpuComputeBindingKind Kind, uint Count = 1) {
        if (!Enum.IsDefined(value: Kind)) {
            throw new ArgumentOutOfRangeException(
                nameof(Kind),
                Kind,
                "The descriptor kind is not defined."
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
    public GpuComputeBindingKind Kind { get; }

    /// <summary>Validates a whole set, including default values and duplicate binding indices.</summary>
    public static void ValidateSet(IReadOnlyList<GpuComputeBinding> bindings) {
        ArgumentNullException.ThrowIfNull(bindings);
        var seen = new HashSet<uint>();

        foreach (var binding in bindings) {
            if (
                !Enum.IsDefined(value: binding.Kind) ||
                (0 == binding.Count)
            ) {
                throw new ArgumentException(
                    message: "The descriptor set contains an invalid kind or zero descriptor count.",
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
