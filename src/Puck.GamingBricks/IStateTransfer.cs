namespace Puck.GamingBricks;

/// <summary>
/// One direction of a component's snapshot: <see cref="StateSaveTransfer"/> writes each field it is handed to a
/// <see cref="StateWriter"/>, and <see cref="StateLoadTransfer"/> reads each field it is handed back from a
/// <see cref="StateReader"/>. A component lists its snapshotted fields once, in a method generic over this interface,
/// and implements <see cref="ISnapshotable.SaveState"/> and <see cref="ISnapshotable.LoadState"/> by running that one
/// list in each direction — so the order and width a save writes are, by construction, the order and width a load
/// reads.
/// </summary>
/// <remarks>Implementations are structs, so a generic field list constrained to one specializes per direction and each
/// call reaches the writer or reader directly. Each scalar member is named for the width it moves, so retyping a field
/// fails to compile rather than silently changing the snapshot layout.</remarks>
public interface IStateTransfer {
    /// <summary>Moves a run of unmanaged values verbatim as their raw bytes, in host byte order (Puck targets
    /// little-endian hosts); the run's length is the span's length and is not itself recorded.</summary>
    /// <typeparam name="T">The unmanaged element type.</typeparam>
    /// <param name="values">The values to save from or load into; a load fills the span completely.</param>
    void Block<T>(Span<T> values) where T : unmanaged;
    /// <summary>Moves a boolean as one byte (<c>0</c> or <c>1</c>).</summary>
    /// <param name="value">The field to save from or load into.</param>
    void Boolean(ref bool value);
    /// <summary>Moves a single byte.</summary>
    /// <param name="value">The field to save from or load into.</param>
    void Byte(ref byte value);
    /// <summary>Moves a 32-bit signed integer, little-endian.</summary>
    /// <param name="value">The field to save from or load into.</param>
    void Int32(ref int value);
    /// <summary>Moves a 64-bit signed integer, little-endian.</summary>
    /// <param name="value">The field to save from or load into.</param>
    void Int64(ref long value);
    /// <summary>Moves a 16-bit unsigned integer, little-endian.</summary>
    /// <param name="value">The field to save from or load into.</param>
    void UInt16(ref ushort value);
    /// <summary>Moves a 32-bit unsigned integer, little-endian.</summary>
    /// <param name="value">The field to save from or load into.</param>
    void UInt32(ref uint value);
    /// <summary>Moves a 64-bit unsigned integer, little-endian.</summary>
    /// <param name="value">The field to save from or load into.</param>
    void UInt64(ref ulong value);
}
