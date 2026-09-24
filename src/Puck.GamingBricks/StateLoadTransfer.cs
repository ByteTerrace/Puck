namespace Puck.GamingBricks;

/// <summary>The loading direction of <see cref="IStateTransfer"/>: every field it is handed is overwritten with the next
/// value read from a <see cref="StateReader"/>.</summary>
public readonly struct StateLoadTransfer : IStateTransfer {
    private readonly StateReader m_reader;

    /// <summary>Initializes a new instance of the <see cref="StateLoadTransfer"/> struct over a reader.</summary>
    /// <param name="reader">The source the fields are read from; the transfer does not own it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is <see langword="null"/>.</exception>
    public StateLoadTransfer(StateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        m_reader = reader;
    }

    /// <inheritdoc/>
    public void Block<T>(Span<T> values) where T : unmanaged => m_reader.ReadBlock<T>(destination: values);
    /// <inheritdoc/>
    public void Boolean(ref bool value) => value = m_reader.ReadBoolean();
    /// <inheritdoc/>
    public void Byte(ref byte value) => value = m_reader.ReadByte();
    /// <inheritdoc/>
    public void Int32(ref int value) => value = m_reader.ReadInt32();
    /// <inheritdoc/>
    public void Int64(ref long value) => value = m_reader.ReadInt64();
    /// <inheritdoc/>
    public void UInt16(ref ushort value) => value = m_reader.ReadUInt16();
    /// <inheritdoc/>
    public void UInt32(ref uint value) => value = m_reader.ReadUInt32();
    /// <inheritdoc/>
    public void UInt64(ref ulong value) => value = m_reader.ReadUInt64();
}
