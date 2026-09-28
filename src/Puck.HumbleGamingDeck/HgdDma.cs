namespace Puck.HumbleGamingDeck;

/// <summary>
/// The 2A03's DMA unit, which owns the bus while OAM DMA or DMC DMA runs. A request halts the CPU through RDY on the
/// CPU's next read cycle (RDY cannot stop a write); from then on each CPU cycle is either a DMA transfer or a cycle on
/// which the halted CPU repeats its pending read, with every side effect that read has. Transfers alternate between get
/// cycles, which read, and put cycles, which write: an OAM DMA copies 256 bytes from the requested page to $2004 in
/// get/put pairs, and a DMC DMA spends one dummy cycle before it reads its sample byte on a get cycle, taking that get
/// ahead of OAM DMA when both want it.
/// <para>
/// The halt, dummy and alignment cycles and the get/put alternation are modelled. Which CPU cycle parity is a get cycle
/// after power-on, the separate DMC load and reload timings, and aborts belong to the NTSC accuracy work that pins them
/// against the DMA test suites.
/// </para>
/// https://www.nesdev.org/wiki/DMA
/// </summary>
public sealed class HgdDma : ISnapshotable {
    private bool m_getCycle;
    private bool m_halted;
    private bool m_oamRequested;
    private byte m_oamPage;
    private int m_oamIndex;
    private bool m_oamHoldsByte;
    private byte m_oamByte;
    private bool m_dmcRequested;
    private bool m_dmcDummyDone;

    /// <summary>Gets whether a DMA holds the CPU halted.</summary>
    public bool Halted => m_halted;

    /// <summary>Requests an OAM DMA from a CPU page, the effect of a $4014 write.</summary>
    /// <param name="page">The source page; the transfer reads <c>page</c>·$100 through <c>page</c>·$100+$FF.</param>
    public void RequestObjectMemory(byte page) {
        m_oamRequested = true;
        m_oamPage = page;
        m_oamIndex = 0;
        m_oamHoldsByte = false;
    }
    /// <summary>Runs one CPU cycle: the CPU's own step when no DMA is pending or the CPU is about to write, otherwise a
    /// halt, dummy or alignment cycle on which the CPU repeats its read, or a DMA transfer.</summary>
    /// <param name="cpu">The CPU the unit halts.</param>
    /// <param name="bus">The CPU bus DMA transfers use.</param>
    /// <param name="apu">The APU whose DMC requests sample bytes.</param>
    public void Step(HgdCpu<HgdCpuBus> cpu, HgdSystemBus bus, HgdApu apu) {
        m_getCycle = !m_getCycle;
        if (!m_dmcRequested && apu.DmcNeedsSample) {
            m_dmcRequested = true;
            m_dmcDummyDone = false;
        }
        if (!m_oamRequested && !m_dmcRequested) {
            cpu.StepCycle();

            return;
        }
        if (!m_halted) {
            if (cpu.NextAccessIsWrite) {
                cpu.StepCycle();

                return;
            }

            m_halted = true;
            cpu.Ready = false;
            cpu.StepCycle();

            return;
        }

        var dmcMayGet = (m_dmcRequested && m_dmcDummyDone);

        if (m_dmcRequested) {
            m_dmcDummyDone = true;
        }
        if (m_getCycle && dmcMayGet) {
            apu.CompleteDmcFetch(value: bus.Read(address: apu.DmcAddress));
            m_dmcRequested = false;
        } else if (m_getCycle && m_oamRequested && !m_oamHoldsByte) {
            m_oamByte = bus.Read(address: ((ushort)((m_oamPage << 8) | m_oamIndex)));
            m_oamHoldsByte = true;
        } else if (!m_getCycle && m_oamRequested && m_oamHoldsByte) {
            bus.Write(
                address: 0x2004,
                value: m_oamByte
            );
            m_oamHoldsByte = false;
            ++m_oamIndex;
            if (m_oamIndex == 256) {
                m_oamRequested = false;
            }
        } else {
            cpu.StepCycle();
        }
        if (!m_oamRequested && !m_dmcRequested) {
            m_halted = false;
            cpu.Ready = true;
        }
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The reader does not contain a complete DMA state.</exception>
    /// <exception cref="InvalidDataException">The serialized OAM transfer index is out of range.</exception>
    public void LoadState(StateReader reader) {
        TransferState(transfer: new StateLoadTransfer(reader: reader));
        if (((uint)m_oamIndex) > 256) {
            throw new InvalidDataException(message: "Snapshot OAM DMA index is out of range.");
        }
    }

    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Boolean(value: ref m_getCycle);
        transfer.Boolean(value: ref m_halted);
        transfer.Boolean(value: ref m_oamRequested);
        transfer.Byte(value: ref m_oamPage);
        transfer.Int32(value: ref m_oamIndex);
        transfer.Boolean(value: ref m_oamHoldsByte);
        transfer.Byte(value: ref m_oamByte);
        transfer.Boolean(value: ref m_dmcRequested);
        transfer.Boolean(value: ref m_dmcDummyDone);
    }
}
