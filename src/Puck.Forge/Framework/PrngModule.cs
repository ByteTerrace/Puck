namespace Puck.Forge.Framework;

/// <summary>
/// The framework PRNG: a 16-bit linear congruential generator (<c>state = state × 5 + 1</c>, output = the state's
/// high byte) — deterministic from its seed, cheap on the SM83 (two <c>add hl, hl</c> and an <c>add hl, de</c>).
/// The stock seeding discipline is D4: pure INPUT ENTROPY — sample the free-running frame counter at the title
/// screen's START press edge, whitened with an XOR constant so an early press never yields a near-zero state. No
/// wall clock and no hardware noise: two players pressing on the same frame replay the same game.
/// </summary>
internal sealed class PrngModule {
    // The D4 whitening constant (seed = FrameCounter16 XOR 0xA5C3).
    private const byte SeedWhitenLow = 0xC3;
    private const byte SeedWhitenHigh = 0xA5;

    private readonly Sm83Emitter m_emitter;
    private readonly int m_nextLabel;

    /// <summary>Creates the module over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    public PrngModule(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        m_emitter = emitter;
        m_nextLabel = emitter.NewLabel();
    }

    /// <summary>Emits a call to the advance subroutine; A returns the next output byte. Clobbers A, D, E, H, L.</summary>
    public void EmitNext() => m_emitter.Call(label: m_nextLabel);

    /// <summary>Emits a draw uniform-ish in [0, <paramref name="modulus"/>): advances the PRNG, then reduces the
    /// output byte by repeated subtraction. Clobbers A, D, E, H, L.</summary>
    /// <param name="modulus">The exclusive upper bound (≥ 2).</param>
    public void EmitNextInRange(byte modulus) {
        if (modulus < 2) {
            throw new ArgumentOutOfRangeException(paramName: nameof(modulus));
        }

        var reduce = m_emitter.NewLabel();
        var done = m_emitter.NewLabel();

        EmitNext();
        m_emitter.MarkLabel(label: reduce);
        m_emitter.ArithmeticImmediate(op: AluOp.Compare, value: modulus);
        m_emitter.JumpRelative(condition: Condition.Carry, label: done);
        m_emitter.ArithmeticImmediate(op: AluOp.Subtract, value: modulus);
        m_emitter.JumpRelative(label: reduce);
        m_emitter.MarkLabel(label: done);
    }

    /// <summary>Emits the D4 seeding: state = FrameCounter16 XOR 0xA5C3, sampled at the current instant (call it on
    /// the title screen's START press edge). Clobbers A.</summary>
    public void EmitSeedFromFrameCounter() {
        m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.FrameCounter);
        m_emitter.ArithmeticImmediate(op: AluOp.Xor, value: SeedWhitenLow);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.PrngState);
        m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.FrameCounterHigh);
        m_emitter.ArithmeticImmediate(op: AluOp.Xor, value: SeedWhitenHigh);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.PrngStateHigh);
    }

    /// <summary>Emits the module's library subroutines (the LCG advance). Called once by the framework facade.</summary>
    public void EmitLibrary() {
        // prngNext: state = state*5 + 1; A = high byte. Clobbers A, D, E, H, L.
        m_emitter.MarkLabel(label: m_nextLabel);
        m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.PrngState);
        m_emitter.Load(destination: Reg8.L, source: Reg8.A);
        m_emitter.LoadAFromAddress(address: FrameworkMemoryMap.PrngStateHigh);
        m_emitter.Load(destination: Reg8.H, source: Reg8.A);
        m_emitter.Load(destination: Reg8.E, source: Reg8.L);
        m_emitter.Load(destination: Reg8.D, source: Reg8.H);
        m_emitter.AddToHl(pair: Reg16.Hl);   // ×2
        m_emitter.AddToHl(pair: Reg16.Hl);   // ×4
        m_emitter.AddToHl(pair: Reg16.De);   // ×5
        m_emitter.Increment(pair: Reg16.Hl); // +1
        m_emitter.Load(destination: Reg8.A, source: Reg8.L);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.PrngState);
        m_emitter.Load(destination: Reg8.A, source: Reg8.H);
        m_emitter.StoreAToAddress(address: FrameworkMemoryMap.PrngStateHigh);
        m_emitter.Return();
    }
}
