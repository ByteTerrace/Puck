namespace Puck.Abstractions.Machines;

/// <summary>The semantics of an access to a machine's hardware.</summary>
public enum MachineAccessMode {
    /// <summary>A coherent side-effect-free observation at an execution boundary.</summary>
    Inspect,
    /// <summary>A deliberate state patch, independent of the hardware's bus write behavior.</summary>
    Patch,
    /// <summary>A hardware-visible access with the provider's actual bus or device side effects.</summary>
    Bus,
}

/// <summary>Availability of a hardware observation or operation. Failure never masquerades as a successful zero.</summary>
public enum MachineAccessStatus {
    /// <summary>The runtime has no currently accessible hardware instance.</summary>
    Unavailable,
    /// <summary>The access completed.</summary>
    Available,
    /// <summary>The provider does not support this space, width, or access mode.</summary>
    Unsupported,
    /// <summary>The request is outside an admitted range or violates an operation constraint.</summary>
    Refused,
    /// <summary>A machine or worker fault prevented the access.</summary>
    Faulted,
}

/// <summary>The provider's address-space contract. Individual configurations and operations can narrow admission.</summary>
/// <param name="Name">The stable space name.</param>
/// <param name="FirstAddress">The first admitted byte address.</param>
/// <param name="LastAddress">The last admitted byte address, inclusive.</param>
/// <param name="Widths">Supported scalar widths, in bytes.</param>
/// <param name="LittleEndian">Whether scalar values place their low byte at the first address.</param>
/// <param name="AccessModes">Supported access semantics.</param>
/// <param name="Description">The space's hardware meaning and restrictions.</param>
public sealed record MachineMemorySpaceDescriptor(
    string Name, ulong FirstAddress, ulong LastAddress, IReadOnlyList<int> Widths,
    bool LittleEndian, IReadOnlyList<MachineAccessMode> AccessModes, string Description
);

/// <summary>A scalar hardware address. A width belongs to the request, rather than to a host-wide bus assumption.</summary>
/// <param name="Space">The provider-owned address space.</param>
/// <param name="Address">The unsigned byte address.</param>
/// <param name="Width">The scalar width in bytes.</param>
public readonly record struct MachineMemoryAddress(string Space, ulong Address, int Width);

/// <summary>A hardware access outcome with explicit availability.</summary>
/// <param name="Status">Whether the operation completed, or why it could not.</param>
/// <param name="Value">The observed scalar when available. Undefined for failed reads and for writes.</param>
/// <param name="Reason">An author-facing explanation of a refusal or fault.</param>
public readonly record struct MachineAccessResult(MachineAccessStatus Status, ulong Value = 0, string? Reason = null);

/// <summary>Optional direct hardware access. Implementations drain queued execution before observing state, keep
/// inspection coherent across bytes, and validate a complete write before changing any state. Bus access follows
/// hardware ordering and side effects; it is never substituted with a debug patch.</summary>
public interface IMachineHardwareAccess {
    /// <summary>Gets the hardware spaces available in this runtime's configuration.</summary>
    IReadOnlyList<MachineMemorySpaceDescriptor> MemorySpaces { get; }
    /// <summary>Reads a scalar at a deterministic execution boundary.</summary>
    /// <param name="address">The provider space, address, and width.</param>
    /// <param name="mode">Inspect for side-effect-free reads, or Bus for a hardware-visible read.</param>
    /// <returns>The observed value with availability, or an explicit refusal.</returns>
    MachineAccessResult Read(MachineMemoryAddress address, MachineAccessMode mode = MachineAccessMode.Inspect);
    /// <summary>Writes a scalar through the declared access semantics, after complete validation.</summary>
    /// <param name="address">The provider space, address, and width.</param>
    /// <param name="value">The scalar, already converted under the authored checked/truncating policy.</param>
    /// <param name="mode">Patch for deliberate state edits, or Bus for hardware-visible writes.</param>
    /// <returns>Availability or a refusal; the host records and orders the operation.</returns>
    MachineAccessResult Write(MachineMemoryAddress address, ulong value, MachineAccessMode mode);
}
