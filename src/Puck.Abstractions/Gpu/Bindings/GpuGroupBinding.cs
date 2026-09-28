namespace Puck.Abstractions.Gpu;

/// <summary>One binding of a group: its binding number, its kind, and its descriptor count. The binding number is the
/// Vulkan binding and the Direct3D 12 register number in the register class its kind takes, and an array of
/// <see cref="Count"/> descriptors takes that many registers from it on Direct3D 12.</summary>
public readonly record struct GpuGroupBinding {
    /// <summary>Initializes a new instance of the <see cref="GpuGroupBinding"/> struct.</summary>
    /// <param name="binding">The binding number within the group.</param>
    /// <param name="kind">The binding kind.</param>
    /// <param name="count">The descriptor count; one for a binding that is not an array.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a binding kind, or
    /// <paramref name="count"/> is zero.</exception>
    public GpuGroupBinding(uint binding, GpuBindingKind kind, uint count = 1) {
        if (!Enum.IsDefined(value: kind)) {
            throw new ArgumentOutOfRangeException(
                actualValue: kind,
                message: $"Binding {binding}'s kind is not a binding kind.",
                paramName: nameof(kind)
            );
        }
        if (count == 0) {
            throw new ArgumentOutOfRangeException(
                actualValue: count,
                message: $"Binding {binding} holds no descriptor.",
                paramName: nameof(count)
            );
        }

        Binding = binding;
        Count = count;
        Kind = kind;
    }

    /// <summary>Gets the binding number within the group.</summary>
    public uint Binding { get; }
    /// <summary>Gets the descriptor count; one for a binding that is not an array.</summary>
    public uint Count { get; }
    /// <summary>Gets the binding kind.</summary>
    public GpuBindingKind Kind { get; }
}
