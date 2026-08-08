namespace Puck.Demo.Forge.Framework;

/// <summary>The caller-owned work-RAM buffers a <see cref="LinkProtocolModule"/> drives, all in a consumer's
/// <see cref="FrameworkMemoryMap.GameRam"/> region (the module owns none of its own, mirroring <see cref="LinkModule"/>'s
/// no-WRAM contract). Seven single scratch bytes plus the two fixed-length exchange blocks: the game fills
/// <see cref="OutBlock"/> before the trade (the block it sends every round) and reads <see cref="InBlock"/> once the
/// phase reaches <see cref="LinkProtocolModule.PhaseDoneOk"/> (the partner's block, validated).</summary>
/// <param name="Phase">The protocol phase byte (one of the module's <c>Phase*</c> constants).</param>
/// <param name="Role">The negotiated role byte (<c>0</c> unknown, <c>1</c> master, <c>2</c> slave).</param>
/// <param name="Backoff">The remaining DIV-seeded listen frames before this side claims the master role.</param>
/// <param name="Attempts">The failed-round counter (a retry rerolls the backoff; <see cref="LinkProtocolModule.MaxAttempts"/>
/// exhausted falls to <see cref="LinkProtocolModule.PhaseNoLink"/>).</param>
/// <param name="ByteIndex">The current block byte being exchanged (0..block length).</param>
/// <param name="Stage">The one-byte outgoing-transfer staging slot (SB is loaded from here each transfer).</param>
/// <param name="ReadScratch">The one-byte landing slot for a completed transfer's shifted-in byte.</param>
/// <param name="OutBlock">The outgoing block's first byte (the game fills <see cref="LinkProtocolModule.BlockByteCount"/>
/// bytes: magic, payload, additive checksum).</param>
/// <param name="InBlock">The incoming block's first byte (the module writes the partner's block here).</param>
internal readonly record struct LinkProtocolRam(
    ushort Phase,
    ushort Role,
    ushort Backoff,
    ushort Attempts,
    ushort ByteIndex,
    ushort Stage,
    ushort ReadScratch,
    ushort OutBlock,
    ushort InBlock
);

/// <summary>
/// A small, reusable LINK PROTOCOL atop <see cref="LinkModule"/>: it turns the module's stateless SB/SC vocabulary into
/// a boot-order-proof, per-frame trade handshake two identical carts can run over a real <c>SerialLinkSession</c>. Each
/// call to <see cref="EmitTick"/> (once per displayed frame, from a consumer's link state) advances a work-RAM phase
/// machine:
/// <list type="number">
/// <item>ROLE NEGOTIATION — each side seeds a backoff from <c>DIV ^ FrameCounter</c> (the deterministic-yet-divergent
/// entropy two staggered machines differ on), then LISTENS for the magic with a bounded poll; whichever side's backoff
/// expires first CLAIMS the master role by sending the magic on its internal clock, and the still-listening peer hears
/// it and becomes the slave. A perfectly symmetric pair (identical DIV AND identical entry frame) cannot be broken by
/// entropy alone — it degrades through the retry cap to <see cref="PhaseNoLink"/> rather than livelocking, and staggered
/// input (a human, or a verify that offsets one side) breaks it cleanly on the first round.</item>
/// <item>BLOCK EXCHANGE — the full-duplex shift register carries each side's <see cref="LinkProtocolRam.OutBlock"/> to
/// the other one byte per frame; the master drives the clock, the slave arms an external-clock receive with its own byte
/// staged, so one poll-to-completion moves a byte BOTH ways.</item>
/// <item>VALIDATE + ACK/RETRY — each side checks the partner block's magic and additive checksum, then exchanges an
/// ACK/NAK byte; a clean round on both sides lands <see cref="PhaseDoneOk"/> (the consumer commits the swap), a bad round
/// rerolls the backoff and retries, and the retry cap lands <see cref="PhaseNoLink"/> — which is ALSO where a lone,
/// unlinked cart ends up (its external-clock listens and slave receives never complete, so every round times out): the
/// "NO LINK" narration the consumer shows instead of freezing.</item>
/// </list>
/// The module owns no WRAM (see <see cref="LinkProtocolRam"/>) and no rendering — it is transport + negotiation +
/// validation only; the consumer fills the outgoing block, reads the incoming one, and narrates each terminal phase.
/// </summary>
internal sealed class LinkProtocolModule {
    /// <summary>The phase seeds a fresh backoff and moves to <see cref="PhaseNegotiate"/> (also the retry re-entry).</summary>
    public const byte PhaseSeed = 0;
    /// <summary>The role-negotiation phase: listen-with-timeout, counting the backoff down to a master claim.</summary>
    public const byte PhaseNegotiate = 1;
    /// <summary>The block-exchange phase: one full-duplex byte per frame until the whole block has crossed.</summary>
    public const byte PhaseExchange = 2;
    /// <summary>The validate + ACK phase: check the partner block, exchange an ACK/NAK, then commit or retry.</summary>
    public const byte PhaseAck = 3;
    /// <summary>The terminal success phase: the partner block in <see cref="LinkProtocolRam.InBlock"/> is valid and
    /// acknowledged — the consumer commits the trade.</summary>
    public const byte PhaseDoneOk = 4;
    /// <summary>The terminal failure phase: no partner (or an unbreakable symmetric livelock) after
    /// <see cref="MaxAttempts"/> rounds — the consumer narrates "NO LINK".</summary>
    public const byte PhaseNoLink = 5;

    /// <summary>The role byte for the side that drives the shift clock (internal clock).</summary>
    public const byte RoleMaster = 1;
    /// <summary>The role byte for the side that waits on the peer's clock (external clock).</summary>
    public const byte RoleSlave = 2;

    /// <summary>The block's fixed byte count: a magic byte, two payload bytes (the critter's species + level, in the
    /// critter-swap consumer), and an additive checksum.</summary>
    public const int BlockByteCount = 4;
    /// <summary>The handshake/block magic byte — a distinctive non-zero value so an all-zero garbage block (the classic
    /// two-master collision result) fails validation instead of silently checksum-matching as zero.</summary>
    public const byte MagicByte = 0xC7;
    /// <summary>The positive acknowledgement byte a side sends when its received block validated.</summary>
    public const byte AckByte = 0x06;
    /// <summary>The negative acknowledgement byte a side sends when its received block failed validation.</summary>
    public const byte NakByte = 0x15;
    /// <summary>The failed-round cap before the protocol gives up to <see cref="PhaseNoLink"/> (also the bound that makes
    /// a lone cart's "NO LINK" arrive promptly rather than after an unbounded spin).</summary>
    public const byte MaxAttempts = 4;

    // The backoff: (DIV^FrameCounter & 0x07) + a floor, so a claim is BackoffFloor..BackoffFloor+7 listen frames out.
    // The masked entropy (0..7) is the symmetry-breaker two staggered machines differ on; the floor guarantees both
    // sides are co-negotiating (each has entered the trade and is listening) before the earlier one claims, so a small
    // input stagger yields a clean master/slave handshake on the first round rather than a missed rendezvous.
    private const byte BackoffMask = 0x07;
    private const byte BackoffFloor = 4;
    // The per-transfer poll budget (iterations of the three-instruction SC spin). A normal-rate byte is 8×512 T-cycles;
    // this comfortably covers one byte within a frame with headroom, and bounds a no-peer receive's timeout.
    private const ushort TransferBudget = 4096;
    // The listen poll budget: the same order, sized so a live master's magic is caught while a dead cable times out.
    private const ushort ListenBudget = 4096;

    private readonly Sm83Emitter m_emitter;
    private readonly LinkModule m_link;
    private readonly LinkProtocolRam m_ram;
    private readonly int m_transferByteLabel;
    private readonly int m_validateInLabel;

    /// <summary>Creates the protocol module over the shared emitter, the underlying link vocabulary, and the caller's
    /// work-RAM buffers.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="link">The stateless SB/SC link module this protocol builds on.</param>
    /// <param name="ram">The caller-owned work-RAM layout the protocol drives.</param>
    public LinkProtocolModule(Sm83Emitter emitter, LinkModule link, LinkProtocolRam ram) {
        ArgumentNullException.ThrowIfNull(emitter);
        ArgumentNullException.ThrowIfNull(link);

        m_emitter = emitter;
        m_link = link;
        m_ram = ram;
        m_transferByteLabel = emitter.NewLabel();
        m_validateInLabel = emitter.NewLabel();
    }

    /// <summary>Emits a reset of the protocol to its start (call from the consumer's link-state enter, AFTER the game has
    /// filled <see cref="LinkProtocolRam.OutBlock"/>): phase = <see cref="PhaseSeed"/>, attempts cleared.</summary>
    public void EmitBegin() {
        m_emitter.LoadAImmediate(value: PhaseSeed);
        m_emitter.StoreAToAddress(address: m_ram.Phase);
        m_emitter.XorA();
        m_emitter.StoreAToAddress(address: m_ram.Attempts);
    }

    /// <summary>Emits ONE per-frame advance of the phase machine (call from the consumer's link-state tick). Dispatches
    /// on the phase byte; the terminal phases (<see cref="PhaseDoneOk"/>/<see cref="PhaseNoLink"/>) are left for the
    /// consumer to read and act on. Clobbers A, B, C, D, E, H, L.</summary>
    public void EmitTick() {
        var e = m_emitter;

        var hSeed = e.NewLabel();
        var hNegotiate = e.NewLabel();
        var hExchange = e.NewLabel();
        var hAck = e.NewLabel();
        var retry = e.NewLabel();
        var giveUp = e.NewLabel();
        var end = e.NewLabel();

        // `cp` never modifies A, so each compare tests the same loaded phase byte against an absolute phase constant.
        e.LoadAFromAddress(address: m_ram.Phase);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PhaseSeed);
        e.JumpAbsolute(condition: Condition.Zero, label: hSeed);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PhaseNegotiate);
        e.JumpAbsolute(condition: Condition.Zero, label: hNegotiate);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PhaseExchange);
        e.JumpAbsolute(condition: Condition.Zero, label: hExchange);
        e.ArithmeticImmediate(op: AluOp.Compare, value: PhaseAck);
        e.JumpAbsolute(condition: Condition.Zero, label: hAck);
        e.JumpAbsolute(label: end); // DoneOk / NoLink: nothing to do — the consumer handles the terminal phases.

        // SEED: back off a DIV^FrameCounter-derived number of listen frames before claiming master. Two machines that
        // entered the trade at different frames (a human's two presses, or a staggered verify) differ here and negotiate
        // distinct roles on the first round; a symmetric pair falls through the retry cap to NO LINK instead of hanging.
        EmitSeedHandler(label: hSeed, end: end);

        // NEGOTIATE: while the backoff remains, LISTEN for the magic (become slave on hearing it, else count down);
        // when it hits zero, CLAIM the master role by sending the magic on the internal clock.
        EmitNegotiateHandler(label: hNegotiate, end: end);

        // EXCHANGE: one full-duplex block byte per frame (master clocks, slave arms) until the whole block has crossed.
        EmitExchangeHandler(label: hExchange, end: end, retry: retry);

        // ACK: validate the partner block, exchange an ACK/NAK, and commit (DoneOk) only when BOTH this side validated
        // and the peer acknowledged; otherwise retry.
        EmitAckHandler(label: hAck, end: end, retry: retry);

        // RETRY: another failed round — reroll and re-negotiate, unless the cap is hit (then NO LINK).
        e.MarkLabel(label: retry);
        e.LoadAFromAddress(address: m_ram.Attempts);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: m_ram.Attempts);
        e.ArithmeticImmediate(op: AluOp.Compare, value: MaxAttempts);
        e.JumpAbsolute(condition: Condition.NoCarry, label: giveUp); // attempts >= cap → give up.
        e.LoadAImmediate(value: PhaseSeed);
        e.StoreAToAddress(address: m_ram.Phase);
        e.JumpAbsolute(label: end);

        e.MarkLabel(label: giveUp);
        e.LoadAImmediate(value: PhaseNoLink);
        e.StoreAToAddress(address: m_ram.Phase);

        e.MarkLabel(label: end);
    }

    /// <summary>Emits the protocol's library subroutines (the per-byte transfer + the block validator). Call once from
    /// the consumer's game-library emission (after the framework module libraries, before the state bodies).</summary>
    public void EmitLibrary() {
        EmitTransferByteSubroutine();
        EmitValidateInSubroutine();
    }

    private void EmitSeedHandler(int label, int end) {
        var e = m_emitter;

        e.MarkLabel(label: label);
        // backoff = ((FrameCounterLow ^ DIV) & mask) + 1.
        e.LoadAFromAddress(address: FrameworkMemoryMap.FrameCounter);
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.LoadAFromHighPage(port: Hw.PortDivider);
        e.Arithmetic(op: AluOp.Xor, source: Reg8.B);
        e.ArithmeticImmediate(op: AluOp.And, value: BackoffMask);
        e.ArithmeticImmediate(op: AluOp.Add, value: BackoffFloor);
        e.StoreAToAddress(address: m_ram.Backoff);
        // Fresh negotiation: role unknown, byte cursor at the block start.
        e.XorA();
        e.StoreAToAddress(address: m_ram.Role);
        e.StoreAToAddress(address: m_ram.ByteIndex);
        e.LoadAImmediate(value: PhaseNegotiate);
        e.StoreAToAddress(address: m_ram.Phase);
        e.JumpAbsolute(label: end);
    }
    private void EmitNegotiateHandler(int label, int end) {
        var e = m_emitter;

        var claim = e.NewLabel();
        var noMaster = e.NewLabel();
        var claimProceed = e.NewLabel();

        e.MarkLabel(label: label);
        e.LoadAFromAddress(address: m_ram.Backoff);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpAbsolute(condition: Condition.Zero, label: claim); // backoff spent → become master.

        // LISTEN: arm an external-clock receive (SB staged 0 — we care only about the incoming magic) with a bounded
        // poll. A live master's magic completes it; a dead cable times out to noMaster.
        e.XorA();
        e.StoreAToAddress(address: m_ram.Stage);
        m_link.EmitArmExternalReceive(sourceAddress: m_ram.Stage);
        m_link.EmitPollComplete(iterationBudget: ListenBudget, timeoutLabel: noMaster);
        m_link.EmitReadByte(destAddress: m_ram.ReadScratch);
        e.LoadAFromAddress(address: m_ram.ReadScratch);
        e.ArithmeticImmediate(op: AluOp.Compare, value: MagicByte);
        e.JumpAbsolute(condition: Condition.NotZero, label: noMaster); // heard something, but not the magic — keep waiting.

        // Heard the master's magic: become the slave and start the exchange next.
        e.LoadAImmediate(value: RoleSlave);
        e.StoreAToAddress(address: m_ram.Role);
        e.XorA();
        e.StoreAToAddress(address: m_ram.ByteIndex);
        e.LoadAImmediate(value: PhaseExchange);
        e.StoreAToAddress(address: m_ram.Phase);
        e.JumpAbsolute(label: end);

        e.MarkLabel(label: noMaster);
        e.LoadAFromAddress(address: m_ram.Backoff);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: m_ram.Backoff);
        e.JumpAbsolute(label: end);

        // CLAIM: drive the magic on the internal clock (which always completes, peer or not), then move to the exchange.
        e.MarkLabel(label: claim);
        e.LoadAImmediate(value: RoleMaster);
        e.StoreAToAddress(address: m_ram.Role);
        e.LoadAImmediate(value: MagicByte);
        e.StoreAToAddress(address: m_ram.Stage);
        m_link.EmitStartInternalSend(sourceAddress: m_ram.Stage);
        m_link.EmitPollComplete(iterationBudget: TransferBudget, timeoutLabel: claimProceed);
        e.MarkLabel(label: claimProceed); // internal-clock transfer completes on its own; success and timeout both land here.
        e.XorA();
        e.StoreAToAddress(address: m_ram.ByteIndex);
        e.LoadAImmediate(value: PhaseExchange);
        e.StoreAToAddress(address: m_ram.Phase);
        e.JumpAbsolute(label: end);
    }
    private void EmitExchangeHandler(int label, int end, int retry) {
        var e = m_emitter;

        var exchangeDone = e.NewLabel();

        e.MarkLabel(label: label);
        e.LoadAFromAddress(address: m_ram.ByteIndex);
        e.ArithmeticImmediate(op: AluOp.Compare, value: BlockByteCount);
        e.JumpAbsolute(condition: Condition.Zero, label: exchangeDone);

        // Stage OutBlock[byteIndex] for this transfer.
        EmitLoadBlockByte(block: m_ram.OutBlock, into: Reg8.A);
        e.StoreAToAddress(address: m_ram.Stage);

        // One full-duplex byte (the subroutine picks internal/external clock from the role); carry set = timed out.
        e.Call(label: m_transferByteLabel);
        e.JumpAbsolute(condition: Condition.Carry, label: retry);

        // Land the shifted-in byte at InBlock[byteIndex].
        EmitBlockByteAddress(block: m_ram.InBlock);
        e.LoadAFromAddress(address: m_ram.ReadScratch);
        e.Load(destination: Reg8.Memory, source: Reg8.A);

        e.LoadAFromAddress(address: m_ram.ByteIndex);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: m_ram.ByteIndex);
        e.JumpAbsolute(label: end);

        e.MarkLabel(label: exchangeDone);
        e.LoadAImmediate(value: PhaseAck);
        e.StoreAToAddress(address: m_ram.Phase);
        e.JumpAbsolute(label: end);
    }
    private void EmitAckHandler(int label, int end, int retry) {
        var e = m_emitter;

        var sendNak = e.NewLabel();
        var staged = e.NewLabel();

        e.MarkLabel(label: label);
        // Validate the partner block; keep the verdict in D across the transfer (the transfer clobbers only A/B/C).
        e.Call(label: m_validateInLabel);
        e.Load(destination: Reg8.D, source: Reg8.A);

        // Stage ACK when this side validated, NAK otherwise.
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpAbsolute(condition: Condition.Zero, label: sendNak);
        e.LoadAImmediate(value: AckByte);
        e.JumpAbsolute(label: staged);
        e.MarkLabel(label: sendNak);
        e.LoadAImmediate(value: NakByte);
        e.MarkLabel(label: staged);
        e.StoreAToAddress(address: m_ram.Stage);

        // Exchange the ACK/NAK byte; a timeout is a failed round.
        e.Call(label: m_transferByteLabel);
        e.JumpAbsolute(condition: Condition.Carry, label: retry);

        // Commit only when BOTH this side validated (D) AND the peer acknowledged (ReadScratch == ACK).
        e.Load(destination: Reg8.A, source: Reg8.D);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpAbsolute(condition: Condition.Zero, label: retry);
        e.LoadAFromAddress(address: m_ram.ReadScratch);
        e.ArithmeticImmediate(op: AluOp.Compare, value: AckByte);
        e.JumpAbsolute(condition: Condition.NotZero, label: retry);
        e.LoadAImmediate(value: PhaseDoneOk);
        e.StoreAToAddress(address: m_ram.Phase);
        e.JumpAbsolute(label: end);
    }

    // transferByte: SB = Stage already loaded by the caller; the role picks the clock (master drives, slave arms), one
    // poll-to-completion moves a byte both ways, and the shifted-in byte lands in ReadScratch. Carry SET on a poll
    // timeout (a dropped peer), CLEAR on success. Clobbers A, B, C.
    private void EmitTransferByteSubroutine() {
        var e = m_emitter;

        var asMaster = e.NewLabel();
        var doPoll = e.NewLabel();
        var timedOut = e.NewLabel();

        e.MarkLabel(label: m_transferByteLabel);
        e.LoadAFromAddress(address: m_ram.Role);
        e.ArithmeticImmediate(op: AluOp.Compare, value: RoleMaster);
        e.JumpRelative(condition: Condition.Zero, label: asMaster);
        m_link.EmitArmExternalReceive(sourceAddress: m_ram.Stage);
        e.JumpRelative(label: doPoll);
        e.MarkLabel(label: asMaster);
        m_link.EmitStartInternalSend(sourceAddress: m_ram.Stage);
        e.MarkLabel(label: doPoll);
        m_link.EmitPollComplete(iterationBudget: TransferBudget, timeoutLabel: timedOut);
        m_link.EmitReadByte(destAddress: m_ram.ReadScratch);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A); // OR clears the carry — success.
        e.Return();
        e.MarkLabel(label: timedOut);
        e.SetCarryFlag();
        e.Return();
    }

    // validateIn: A = 1 when InBlock is a valid partner block (InBlock[0] == magic AND the additive checksum of the
    // magic + payload bytes equals the trailing checksum byte), else 0. Rejects an all-zero garbage block (magic check)
    // and an all-ones no-peer block (checksum check). Clobbers A, B, C; preserves D (the ACK verdict rides there).
    private void EmitValidateInSubroutine() {
        var e = m_emitter;

        var bad = e.NewLabel();

        e.MarkLabel(label: m_validateInLabel);
        e.LoadAFromAddress(address: m_ram.InBlock);
        e.ArithmeticImmediate(op: AluOp.Compare, value: MagicByte);
        e.JumpRelative(condition: Condition.NotZero, label: bad);

        // C = sum of InBlock[0..BlockByteCount-2].
        e.LoadAFromAddress(address: m_ram.InBlock);
        e.Load(destination: Reg8.C, source: Reg8.A);

        for (var index = 1; (index < (BlockByteCount - 1)); index++) {
            e.LoadAFromAddress(address: (ushort)(m_ram.InBlock + index));
            e.Arithmetic(op: AluOp.Add, source: Reg8.C);
            e.Load(destination: Reg8.C, source: Reg8.A);
        }

        e.LoadAFromAddress(address: (ushort)(m_ram.InBlock + (BlockByteCount - 1)));
        e.Arithmetic(op: AluOp.Compare, source: Reg8.C);
        e.JumpRelative(condition: Condition.NotZero, label: bad);
        e.LoadAImmediate(value: 1);
        e.Return();

        e.MarkLabel(label: bad);
        e.XorA();
        e.Return();
    }

    // Loads a block's [ByteIndex] byte into A (HL = block + ByteIndex; A = (HL)). Clobbers A, D, E, H, L.
    private void EmitLoadBlockByte(ushort block, Reg8 into) {
        EmitBlockByteAddress(block: block);
        m_emitter.Load(destination: into, source: Reg8.Memory);
    }

    // Points HL at a block's [ByteIndex] byte (HL = block + ByteIndex). Clobbers A, D, E, H, L.
    private void EmitBlockByteAddress(ushort block) {
        var e = m_emitter;

        e.LoadAFromAddress(address: m_ram.ByteIndex);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: block);
        e.AddToHl(pair: Reg16.De);
    }
}
