namespace Puck.GamingBricks.Forge;

/// <summary>Shared byte layout for native state, initialization and persistence.</summary>
/// <remarks>Two-byte slots precede byte slots, preserving declaration order within each width. This keeps every
/// halfword aligned without spending padding from the variable window. Physical base addresses belong to each target.</remarks>
public sealed class CartridgeStateLayout {
    /// <summary>A state slot's relative location and representation.</summary>
    /// <param name="Offset">Byte offset from the target's variable base.</param>
    /// <param name="Width">One or two bytes, little-endian.</param>
    /// <param name="Initial">The declared initial value.</param>
    public readonly record struct Slot(int Offset, int Width, int Initial);

    /// <summary>Gets the named variable slots.</summary>
    public IReadOnlyDictionary<string, Slot> Slots { get; }

    /// <summary>Gets the bytes occupied by the variables.</summary>
    public int ByteCount { get; }

    /// <summary>Builds the representation of a validated document's state variables.</summary>
    /// <param name="document">The validated document.</param>
    public CartridgeStateLayout(CartridgeDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        var slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        foreach (var variable in document.Variables.OrderByDescending(static variable => variable.Width)) {
            slots.Add(variable.Name, new Slot(ByteCount, variable.Width, variable.Initial));
            ByteCount += variable.Width;
        }
        Slots = slots;
    }

    /// <summary>Gets a saved slot's initial bytes in payload order.</summary>
    /// <param name="name">The declared variable name.</param>
    /// <returns>The complete little-endian initial value.</returns>
    public byte[] InitialBytes(string name) {
        var slot = Slots[name];
        return slot.Width == 1 ? [(byte)slot.Initial] : [(byte)slot.Initial, (byte)(slot.Initial >> 8)];
    }
}
