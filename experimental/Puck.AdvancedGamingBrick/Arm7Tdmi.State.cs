namespace Puck.AdvancedGamingBrick;

public sealed partial class Arm7Tdmi : IAgbSnapshotable {
    /// <inheritdoc/>
    // The entire architectural + micro-architectural CPU state: the visible register file, every banked set, CPSR and
    // the SPSR bank, the 3-stage fetch/decode/execute pipeline (words, addresses, Thumb/IRQ flags) plus its lazy
    // reload flag, and the 2-stage IRQ-recognition pipeline with the synchronizer line and the non-sequential-fetch
    // flag. Timing itself carries no CPU-side counter (it emerges from the bus), so this is complete.
    public void SaveState(AgbStateWriter writer) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteBlock<uint>(values: m_gpr);
        writer.WriteBlock<uint>(values: m_bankR13);
        writer.WriteBlock<uint>(values: m_bankR14);
        writer.WriteBlock<uint>(values: m_bankSpsr);
        writer.WriteBlock<uint>(values: m_fiqR8to12);
        writer.WriteBlock<uint>(values: m_usrR8to12);

        writer.WriteUInt32(value: m_fetchWord);
        writer.WriteUInt32(value: m_decodeWord);
        writer.WriteUInt32(value: m_executeWord);
        writer.WriteUInt32(value: m_fetchAddress);
        writer.WriteUInt32(value: m_decodeAddress);
        writer.WriteUInt32(value: m_executeAddress);
        writer.WriteBoolean(value: m_decodeThumb);
        writer.WriteBoolean(value: m_executeThumb);
        writer.WriteBoolean(value: m_reload);

        writer.WriteUInt32(value: m_cpsr);
        writer.WriteBoolean(value: m_irqLine);
        writer.WriteBoolean(value: m_decodeIrq);
        writer.WriteBoolean(value: m_executeIrq);
        writer.WriteBoolean(value: m_nextFetchNonSequential);
    }

    /// <inheritdoc/>
    public void LoadState(AgbStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        reader.ReadBlock<uint>(destination: m_gpr);
        reader.ReadBlock<uint>(destination: m_bankR13);
        reader.ReadBlock<uint>(destination: m_bankR14);
        reader.ReadBlock<uint>(destination: m_bankSpsr);
        reader.ReadBlock<uint>(destination: m_fiqR8to12);
        reader.ReadBlock<uint>(destination: m_usrR8to12);

        m_fetchWord = reader.ReadUInt32();
        m_decodeWord = reader.ReadUInt32();
        m_executeWord = reader.ReadUInt32();
        m_fetchAddress = reader.ReadUInt32();
        m_decodeAddress = reader.ReadUInt32();
        m_executeAddress = reader.ReadUInt32();
        m_decodeThumb = reader.ReadBoolean();
        m_executeThumb = reader.ReadBoolean();
        m_reload = reader.ReadBoolean();

        m_cpsr = reader.ReadUInt32();
        m_irqLine = reader.ReadBoolean();
        m_decodeIrq = reader.ReadBoolean();
        m_executeIrq = reader.ReadBoolean();
        m_nextFetchNonSequential = reader.ReadBoolean();
    }
}
