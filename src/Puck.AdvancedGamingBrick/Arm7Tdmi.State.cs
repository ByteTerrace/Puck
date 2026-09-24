namespace Puck.AdvancedGamingBrick;

public sealed partial class Arm7Tdmi : ISnapshotable {
    /// <inheritdoc/>
    public void LoadState(StateReader reader) =>
        TransferState(transfer: new StateLoadTransfer(reader: reader));
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));

    // The entire architectural + micro-architectural CPU state: the visible register file, every banked set, CPSR and
    // the SPSR bank, the 3-stage fetch/decode/execute pipeline (words, addresses, Thumb/IRQ flags) plus its lazy
    // reload flag, and the 2-stage IRQ-recognition pipeline with the synchronizer line and the non-sequential-fetch
    // flag. Timing itself carries no CPU-side counter (it emerges from the bus), so this is complete.
    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Block(values: m_gpr);
        transfer.Block(values: m_bankR13);
        transfer.Block(values: m_bankR14);
        transfer.Block(values: m_bankSpsr);
        transfer.Block(values: m_fiqR8to12);
        transfer.Block(values: m_usrR8to12);

        transfer.UInt32(value: ref m_fetchWord);
        transfer.UInt32(value: ref m_decodeWord);
        transfer.UInt32(value: ref m_executeWord);
        transfer.UInt32(value: ref m_fetchAddress);
        transfer.UInt32(value: ref m_decodeAddress);
        transfer.UInt32(value: ref m_executeAddress);
        transfer.Boolean(value: ref m_decodeThumb);
        transfer.Boolean(value: ref m_executeThumb);
        transfer.Boolean(value: ref m_reload);

        transfer.UInt32(value: ref m_cpsr);
        transfer.Boolean(value: ref m_irqLine);
        transfer.Boolean(value: ref m_decodeIrq);
        transfer.Boolean(value: ref m_executeIrq);
        transfer.Boolean(value: ref m_nextFetchNonSequential);
    }
}
