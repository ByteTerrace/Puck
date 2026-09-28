namespace Puck.Abstractions.Gpu;

/// <summary>
/// Raised by a <see cref="GpuCreationFaults"/> decorator in place of the creation an operator armed it to fail, before
/// the call reaches the device, so nothing is created. Its message starts with
/// <c>[<see cref="GpuCreationFaults.RefusalCode"/>]</c> and names the kind and the creation's number.
/// </summary>
public sealed class GpuCreationFaultException : Exception {
    /// <summary>Initializes a new instance of the <see cref="GpuCreationFaultException"/> class.</summary>
    /// <param name="kind">The kind of object whose creation failed.</param>
    /// <param name="creation">The creation's one-based number among its kind's creations since the faults were last
    /// disarmed.</param>
    public GpuCreationFaultException(GpuCreationKind kind, long creation)
        : base(message: $"[{GpuCreationFaults.RefusalCode}] The {GpuCreationFaults.NameOf(kind: kind)} creation {creation} was failed by gpu.faults before it reached the device.") {
        Creation = creation;
        Kind = kind;
    }

    /// <summary>Gets the failed creation's one-based number among its kind's creations since the faults were last
    /// disarmed.</summary>
    public long Creation { get; }
    /// <summary>Gets the kind of object whose creation failed.</summary>
    public GpuCreationKind Kind { get; }
}
