namespace Puck.HumbleGamingDeck;

/// <summary>
/// The console's 2 KiB of nametable RAM (CIRAM). The PPU never addresses it directly: its $2000-$3EFF accesses reach the
/// cartridge, and the board decides which of the two 1 KiB pages answers by driving CIRAM A10, or answers from memory of
/// its own. That is why a board, not the PPU, implements mirroring.
/// </summary>
public sealed class HgdNametableRam : ISnapshotable {
    private readonly byte[] m_bytes = new byte[2048];

    /// <summary>Reads one byte of a page.</summary>
    /// <param name="page">The page the board selects through CIRAM A10, zero or one.</param>
    /// <param name="offset">The offset within the page; only its low ten bits address the page.</param>
    /// <returns>The stored byte.</returns>
    public byte Read(int page, int offset) =>
        m_bytes[((page & 1) << 10) | (offset & 0x3FF)];
    /// <summary>Writes one byte of a page.</summary>
    /// <param name="page">The page the board selects through CIRAM A10, zero or one.</param>
    /// <param name="offset">The offset within the page; only its low ten bits address the page.</param>
    /// <param name="value">The byte to store.</param>
    public void Write(int page, int offset, byte value) =>
        m_bytes[((page & 1) << 10) | (offset & 0x3FF)] = value;
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The reader does not contain all 2 KiB of nametable RAM.</exception>
    public void LoadState(StateReader reader) =>
        TransferState(transfer: new StateLoadTransfer(reader: reader));

    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer =>
        transfer.Block(values: m_bytes.AsSpan());
}
