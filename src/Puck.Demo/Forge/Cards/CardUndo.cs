namespace Puck.Demo.Forge.Cards;

/// <summary>
/// The card layer's undo mechanism: a fixed-size work-RAM ring of fixed-stride move records. The game fills the
/// staging area and calls push; pop copies the most recent record back into staging (A = 1) or reports empty
/// (A = 0) — the game interprets the record bytes (it owns the move encoding). The ring saturates at its capacity
/// (the oldest record silently ages out), the head wraps with an AND mask, and everything is plain bytes in
/// game-owned work RAM — deterministic by construction, and both card games share the same mechanism instead of
/// growing two.
/// </summary>
internal sealed class CardUndo {
    private readonly int m_capacity;
    private readonly ushort m_countAddress;
    private readonly Sm83Emitter m_emitter;
    private readonly ushort m_headAddress;
    private readonly int m_popLabel;
    private readonly int m_pushLabel;
    private readonly ushort m_ringBase;
    private readonly ushort m_stagingBase;
    private readonly int m_stride;

    /// <summary>Creates the ring over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="headAddress">One work-RAM byte: the next write slot (0 .. capacity − 1).</param>
    /// <param name="countAddress">One work-RAM byte: the live record count (saturates at the capacity).</param>
    /// <param name="ringBase">The ring's first byte (capacity × stride bytes; must fit one 256-byte page).</param>
    /// <param name="stagingBase">The staging record the game reads/writes (stride bytes).</param>
    /// <param name="stride">Bytes per record (1..32).</param>
    /// <param name="capacity">Records in the ring — a power of two (the head wraps with an AND).</param>
    public CardUndo(Sm83Emitter emitter, ushort headAddress, ushort countAddress, ushort ringBase, ushort stagingBase, int stride, int capacity) {
        ArgumentNullException.ThrowIfNull(emitter);

        if ((capacity < 2) || ((capacity & (capacity - 1)) != 0)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(capacity), message: "The ring capacity must be a power of two.");
        }

        if ((stride < 1) || (stride > 32)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(stride));
        }

        if (((ringBase & 0xFF) + (capacity * stride)) > 0x100) {
            throw new ArgumentException(message: "The ring must fit one 256-byte page (the slot math is 8-bit).", paramName: nameof(ringBase));
        }

        m_capacity = capacity;
        m_countAddress = countAddress;
        m_emitter = emitter;
        m_headAddress = headAddress;
        m_popLabel = emitter.NewLabel();
        m_pushLabel = emitter.NewLabel();
        m_ringBase = ringBase;
        m_stagingBase = stagingBase;
        m_stride = stride;
    }

    /// <summary>The staging record's base address (the game's move encoding lives here).</summary>
    public ushort StagingBase => m_stagingBase;

    /// <summary>Emits an inline ring reset (head = count = 0). Clobbers A.</summary>
    public void EmitReset() {
        m_emitter.XorA();
        m_emitter.StoreAToAddress(address: m_headAddress);
        m_emitter.StoreAToAddress(address: m_countAddress);
    }

    /// <summary>Emits a call to the push subroutine (staging → the head slot; count saturates). Clobbers all.</summary>
    public void EmitPush() => m_emitter.Call(label: m_pushLabel);

    /// <summary>Emits a call to the pop subroutine (the newest slot → staging; A = 1, or A = 0 when empty).
    /// Clobbers all.</summary>
    public void EmitPop() => m_emitter.Call(label: m_popLabel);

    /// <summary>Emits the ring's library subroutines. Call once from the game's library emission.</summary>
    public void EmitLibrary() {
        EmitPushSubroutine();
        EmitPopSubroutine();
    }

    private void EmitPushSubroutine() {
        var noSaturate = m_emitter.NewLabel();

        m_emitter.MarkLabel(label: m_pushLabel);

        // HL = the head slot, DE = staging; copy stride bytes staging → slot.
        m_emitter.LoadAFromAddress(address: m_headAddress);
        EmitSlotPointer();
        m_emitter.LoadImmediate(pair: Reg16.De, value: m_stagingBase);
        EmitCopy(fromDeToHl: true);

        // head = (head + 1) & mask; count = min(count + 1, capacity).
        m_emitter.LoadAFromAddress(address: m_headAddress);
        m_emitter.Increment(register: Reg8.A);
        m_emitter.ArithmeticImmediate(op: AluOp.And, value: (byte)(m_capacity - 1));
        m_emitter.StoreAToAddress(address: m_headAddress);
        m_emitter.LoadAFromAddress(address: m_countAddress);
        m_emitter.ArithmeticImmediate(op: AluOp.Compare, value: (byte)m_capacity);
        m_emitter.JumpRelative(condition: Condition.NoCarry, label: noSaturate);
        m_emitter.Increment(register: Reg8.A);
        m_emitter.StoreAToAddress(address: m_countAddress);
        m_emitter.MarkLabel(label: noSaturate);
        m_emitter.Return();
    }
    private void EmitPopSubroutine() {
        var empty = m_emitter.NewLabel();

        m_emitter.MarkLabel(label: m_popLabel);
        m_emitter.LoadAFromAddress(address: m_countAddress);
        m_emitter.Arithmetic(op: AluOp.Or, source: Reg8.A);
        m_emitter.JumpRelative(condition: Condition.Zero, label: empty);
        m_emitter.Decrement(register: Reg8.A);
        m_emitter.StoreAToAddress(address: m_countAddress);

        // head = (head − 1) & mask; HL = that slot, DE = staging; copy stride bytes slot → staging.
        m_emitter.LoadAFromAddress(address: m_headAddress);
        m_emitter.Decrement(register: Reg8.A);
        m_emitter.ArithmeticImmediate(op: AluOp.And, value: (byte)(m_capacity - 1));
        m_emitter.StoreAToAddress(address: m_headAddress);
        EmitSlotPointer();
        m_emitter.LoadImmediate(pair: Reg16.De, value: m_stagingBase);
        EmitCopy(fromDeToHl: false);
        m_emitter.LoadAImmediate(value: 1);
        m_emitter.Return();

        m_emitter.MarkLabel(label: empty);
        m_emitter.XorA();
        m_emitter.Return();
    }

    // HL := ringBase + A × stride (A = the slot; the whole ring shares one page, so the math stays 8-bit).
    private void EmitSlotPointer() {
        m_emitter.Load(destination: Reg8.B, source: Reg8.A);
        m_emitter.XorA();

        for (var bit = 0; (bit < m_stride); bit++) {
            // A += B, stride times (stride ≤ 32 keeps this a handful of adds for the small strides games use).
            m_emitter.Arithmetic(op: AluOp.Add, source: Reg8.B);
        }

        m_emitter.ArithmeticImmediate(op: AluOp.Add, value: (byte)(m_ringBase & 0xFF));
        m_emitter.Load(destination: Reg8.L, source: Reg8.A);
        m_emitter.LoadImmediate(destination: Reg8.H, value: (byte)(m_ringBase >> 8));
    }

    // Copies stride bytes between the slot (HL) and staging (DE), unrolled.
    private void EmitCopy(bool fromDeToHl) {
        for (var index = 0; (index < m_stride); index++) {
            if (fromDeToHl) {
                m_emitter.LoadAFromDe();
                m_emitter.StoreAToHlIncrement();
            } else {
                m_emitter.LoadAFromHlIncrement();
                m_emitter.StoreAToDe();
            }

            if (index < (m_stride - 1)) {
                m_emitter.Increment(pair: Reg16.De);
            }
        }
    }
}
