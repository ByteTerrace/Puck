namespace Puck.Demo.Forge.Framework;

/// <summary>
/// The framework's serial LINK CABLE plumbing: start a one-byte send (internal clock, this side drives the shift
/// clock) or arm a one-byte receive (external clock, the side waits on a linked peer's clock — see
/// <c>SerialLinkSession</c> in <c>experimental/Puck.HumbleGamingBrick</c>), then poll SC's transfer bit to
/// completion with a caller-supplied, BOUNDED budget. An unconnected cable never completes an external-clock
/// transfer (real hardware: an unplugged link reads <c>0xFF</c> forever) and a bounded poll is the only way a
/// cartridge can tell "no partner" from "partner is slow" without hanging — on timeout, control falls through to a
/// caller-provided label so the game can narrate the disconnect (e.g. "NO LINK") instead of freezing.
/// <para>
/// The module owns no WRAM of its own: every helper here reads or writes a caller-supplied address, so a consumer
/// (a future link-fed game) reserves its own bytes in its <see cref="FrameworkMemoryMap.GameRam"/> region for
/// send/receive staging, retry counters, or protocol state. This module is stateless plumbing only — vocabulary
/// ("link", "cable"), never a protocol.
/// </para>
/// </summary>
internal sealed class LinkModule {
    private readonly Sm83Emitter m_emitter;

    /// <summary>Creates the module over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    public LinkModule(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        m_emitter = emitter;
    }

    /// <summary>Emits an internal-clock send: SB = the byte at <paramref name="sourceAddress"/>, then
    /// SC = start | internal-clock (<see cref="Hw.SerialTransferStart"/> | <see cref="Hw.SerialInternalClock"/>,
    /// optionally | <see cref="Hw.SerialFastClock"/>). This side drives the shift clock, so the transfer always
    /// completes (a linked peer receives every bit; an unlinked port shifts in ones). Returns immediately — pair
    /// with <see cref="EmitPollComplete"/> to wait for completion. Clobbers A.</summary>
    /// <param name="sourceAddress">The work-RAM (or ROM) address of the byte to send.</param>
    /// <param name="fastClock">Whether to drive the Color double-speed shift clock.</param>
    public void EmitStartInternalSend(ushort sourceAddress, bool fastClock = false) {
        m_emitter.LoadAFromAddress(address: sourceAddress);
        m_emitter.StoreAToHighPage(port: Hw.PortSerialData);
        m_emitter.LoadAImmediate(value: (byte)(Hw.SerialTransferStart | Hw.SerialInternalClock | (fastClock ? Hw.SerialFastClock : 0)));
        m_emitter.StoreAToHighPage(port: Hw.PortSerialControl);
    }

    /// <summary>Emits an external-clock arm: optionally SB = the byte at <paramref name="sourceAddress"/> (the
    /// shift register is full-duplex — a peer's internal-clock send simultaneously shifts THIS side's SB out to
    /// them, so a side wanting to talk back stages its own outgoing byte before arming, exactly as it would send
    /// one), then SC = start only (<see cref="Hw.SerialTransferStart"/>, clock-select bit clear) — this side waits
    /// on a linked peer's internal-clock edges. On real hardware (and on an unconnected cable in the emulator) this
    /// transfer never completes on its own; it only finishes if a peer is linked and drives its own send. Pair with
    /// <see cref="EmitPollComplete"/>, whose bounded budget is what makes "no partner" observable instead of an
    /// infinite wait. Clobbers A.</summary>
    /// <param name="sourceAddress">The work-RAM (or ROM) address of the byte to stage into SB before arming, or
    /// <see langword="null"/> to leave SB untouched (a pure listen that only cares about the incoming byte).</param>
    public void EmitArmExternalReceive(ushort? sourceAddress = null) {
        if (sourceAddress is { } address) {
            m_emitter.LoadAFromAddress(address: address);
            m_emitter.StoreAToHighPage(port: Hw.PortSerialData);
        }

        m_emitter.LoadAImmediate(value: Hw.SerialTransferStart);
        m_emitter.StoreAToHighPage(port: Hw.PortSerialControl);
    }

    /// <summary>Emits a BOUNDED poll on SC's transfer bit (the <c>ldh / and / jr</c> idiom in
    /// <c>experimental/Puck.HumbleGamingBrick.Post/SerialLinkRom.cs</c>): spins reading SC and testing
    /// <see cref="Hw.SerialTransferStart"/> up to <paramref name="iterationBudget"/> times, falling through to the
    /// next instruction the moment the bit clears (transfer complete). If the budget is exhausted first — the
    /// disconnect story, since an unconnected cable's external-clock arm never clears the bit — control jumps to
    /// <paramref name="timeoutLabel"/> instead of spinning forever. Choose the budget from the caller's own
    /// frame/iteration accounting (this helper does not itself consume frames); a single poll pass here is cheap
    /// (three instructions), so a caller wanting a multi-frame timeout should re-arm the poll across
    /// <c>halt</c>-synced frames rather than pass a huge iteration count into one call. Clobbers A and BC (the
    /// budget counter).</summary>
    /// <param name="iterationBudget">The maximum poll iterations before giving up (≥ 1).</param>
    /// <param name="timeoutLabel">Where control lands when the budget is exhausted without the transfer completing.</param>
    public void EmitPollComplete(ushort iterationBudget, int timeoutLabel) {
        if (iterationBudget < 1) {
            throw new ArgumentOutOfRangeException(paramName: nameof(iterationBudget));
        }

        var loop = m_emitter.NewLabel();
        var done = m_emitter.NewLabel();

        m_emitter.LoadImmediate(pair: Reg16.Bc, value: iterationBudget);
        m_emitter.MarkLabel(label: loop);
        m_emitter.LoadAFromHighPage(port: Hw.PortSerialControl);
        m_emitter.ArithmeticImmediate(op: AluOp.And, value: Hw.SerialTransferStart);
        m_emitter.JumpRelative(condition: Condition.Zero, label: done); // Transfer bit clear: complete.
        m_emitter.Decrement(pair: Reg16.Bc);
        m_emitter.Load(destination: Reg8.A, source: Reg8.B);
        m_emitter.Arithmetic(op: AluOp.Or, source: Reg8.C);
        m_emitter.JumpRelative(condition: Condition.Zero, label: timeoutLabel); // Budget exhausted: give up.
        m_emitter.JumpRelative(label: loop);
        m_emitter.MarkLabel(label: done);
    }

    /// <summary>Emits a read of SB into <paramref name="destAddress"/> — call after <see cref="EmitPollComplete"/>
    /// observes completion so the byte is the transfer's final shifted-in value (an unconnected external-clock
    /// receive that timed out never reaches this call, so a caller never mistakes a stale/mid-shift SB for a real
    /// byte). Clobbers A.</summary>
    /// <param name="destAddress">The caller-owned work-RAM address to receive the byte.</param>
    public void EmitReadByte(ushort destAddress) {
        m_emitter.LoadAFromHighPage(port: Hw.PortSerialData);
        m_emitter.StoreAToAddress(address: destAddress);
    }
}
