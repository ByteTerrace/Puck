namespace Puck.Machines.Post;

/// <summary>One disagreement between a vector's expected RAM and the memory after stepping.</summary>
/// <param name="Address">The memory address.</param>
/// <param name="Actual">The byte held by memory.</param>
/// <param name="Expected">The vector's expected byte.</param>
public readonly record struct SstRamMismatch(ushort Address, byte Actual, byte Expected);
/// <summary>The independent fields that disagree in a bus transaction.</summary>
[Flags]
public enum SstAccessMismatch {
    /// <summary>Every connected pin matches.</summary>
    None = 0,
    /// <summary>The address pins disagree.</summary>
    Address = 1,
    /// <summary>The data pins disagree.</summary>
    Value = 2,
    /// <summary>The write strobe disagrees.</summary>
    Write = 4,
}
/// <summary>Compares flat-memory instruction vectors independently of CPU registers and stepping policy.</summary>
public static class SstComparison {
    /// <summary>Compares a declared RAM byte with memory after stepping.</summary>
    /// <param name="address">The memory address.</param>
    /// <param name="actual">The byte held by memory.</param>
    /// <param name="expected">The vector's expected byte.</param>
    /// <returns>The mismatch, or null when the bytes agree.</returns>
    public static SstRamMismatch? CompareRam(ushort address, byte actual, byte expected) =>
        ((actual == expected) ? null : new(Actual: actual, Address: address, Expected: expected));
    /// <summary>Compares one transaction, ignoring address or data pins the vector leaves disconnected.</summary>
    /// <param name="actual">The recorded bus transaction.</param>
    /// <param name="address">The expected address, or null for disconnected pins.</param>
    /// <param name="value">The expected data, or null for disconnected pins.</param>
    /// <param name="write">Whether the vector asserts the write strobe.</param>
    /// <returns>The fields that disagree.</returns>
    public static SstAccessMismatch CompareAccess(CpuAccess actual, ushort? address, byte? value, bool write) =>
        (((address is not null) && (actual.Address != address.Value)) ? SstAccessMismatch.Address : SstAccessMismatch.None) |
        (((value is not null) && (actual.Value != value.Value)) ? SstAccessMismatch.Value : SstAccessMismatch.None) |
        ((actual.Write != write) ? SstAccessMismatch.Write : SstAccessMismatch.None);
}
