namespace Puck.GamingBricks;

/// <summary>The saving direction of <see cref="IStateTransfer"/>: every field it is handed is written to a
/// <see cref="StateWriter"/> and left unchanged.</summary>
public readonly struct StateSaveTransfer : IStateTransfer {
    private readonly StateWriter m_writer;

    /// <summary>Initializes a new instance of the <see cref="StateSaveTransfer"/> struct over a writer.</summary>
    /// <param name="writer">The sink the fields are written to; the transfer does not own it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public StateSaveTransfer(StateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        m_writer = writer;
    }

    /// <inheritdoc/>
    public void Block<T>(Span<T> values) where T : unmanaged => m_writer.WriteBlock<T>(values: values);
    /// <inheritdoc/>
    public void Boolean(ref bool value) => m_writer.WriteBoolean(value: value);
    /// <inheritdoc/>
    public void Byte(ref byte value) => m_writer.WriteByte(value: value);
    /// <inheritdoc/>
    public void Int32(ref int value) => m_writer.WriteInt32(value: value);
    /// <inheritdoc/>
    public void Int64(ref long value) => m_writer.WriteInt64(value: value);
    /// <inheritdoc/>
    public void UInt16(ref ushort value) => m_writer.WriteUInt16(value: value);
    /// <inheritdoc/>
    public void UInt32(ref uint value) => m_writer.WriteUInt32(value: value);
    /// <inheritdoc/>
    public void UInt64(ref ulong value) => m_writer.WriteUInt64(value: value);
}
