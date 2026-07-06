using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge.Cards;

/// <summary>
/// The card layer's identity encoding and the deterministic deal. A card id is <c>suit × 13 + (rank − 1)</c>
/// (suit 0 = spades, 1 = hearts, 2 = diamonds, 3 = clubs; rank 1 = ace … 13 = king), so rank/suit/colour are one
/// compare or divide away on either side of the forge. The Fisher–Yates shuffle is emitted OVER the framework PRNG
/// (<see cref="PrngModule"/>) and consumes exactly 51 draws, so the settled seed doctrine (D4: the frame counter
/// XOR 0xA5C3 sampled at a press edge) makes the whole deal a deterministic function of one press frame — and
/// <see cref="ShuffleOracle"/> plus <see cref="StepBack"/> let a C# verifier recover the seed from the post-deal
/// PRNG state and predict the deal bit-for-bit.
/// </summary>
internal static class CardDeck {
    /// <summary>The number of cards in the deck.</summary>
    public const int CardCount = 52;
    /// <summary>The number of suits.</summary>
    public const int SuitCount = 4;
    /// <summary>The number of ranks per suit.</summary>
    public const int RankCount = 13;
    /// <summary>The ace's rank value.</summary>
    public const int RankAce = 1;
    /// <summary>The king's rank value.</summary>
    public const int RankKing = 13;

    /// <summary>Extracts a card's rank (1 = ace … 13 = king).</summary>
    /// <param name="id">The card id (0..51).</param>
    /// <returns>The rank.</returns>
    public static int RankOf(int id) => ((id % RankCount) + 1);

    /// <summary>Extracts a card's suit (0 = spades, 1 = hearts, 2 = diamonds, 3 = clubs).</summary>
    /// <param name="id">The card id (0..51).</param>
    /// <returns>The suit.</returns>
    public static int SuitOf(int id) => (id / RankCount);

    /// <summary>Whether a suit is red (hearts and diamonds).</summary>
    /// <param name="suit">The suit (0..3).</param>
    /// <returns><see langword="true"/> for hearts/diamonds.</returns>
    public static bool IsRedSuit(int suit) => ((suit == 1) || (suit == 2));

    /// <summary>Composes a card id from a suit and rank.</summary>
    /// <param name="suit">The suit (0..3).</param>
    /// <param name="rank">The rank (1..13).</param>
    /// <returns>The card id.</returns>
    public static byte CardId(int suit, int rank) => (byte)((suit * RankCount) + (rank - 1));

    /// <summary>Emits an inline deck initialization: <c>deck[k] = k</c> for the 52 bytes at
    /// <paramref name="deckBase"/>. Clobbers A, H, L.</summary>
    /// <param name="e">The routine emitter.</param>
    /// <param name="deckBase">The deck buffer's work-RAM base.</param>
    public static void EmitInitDeck(Sm83Emitter e, ushort deckBase) {
        ArgumentNullException.ThrowIfNull(e);

        var loop = e.NewLabel();

        e.LoadImmediate(pair: Reg16.Hl, value: deckBase);
        e.XorA();
        e.MarkLabel(label: loop);
        e.StoreAToHlIncrement();
        e.Increment(register: Reg8.A);
        e.ArithmeticImmediate(op: AluOp.Compare, value: CardCount);
        e.JumpRelative(condition: Condition.Carry, label: loop);
    }

    /// <summary>Emits the Fisher–Yates shuffle SUBROUTINE at <paramref name="label"/>: shuffles the 52 bytes at
    /// <paramref name="deckBase"/> in place, drawing exactly 51 times from the framework PRNG (i = 51 … 1; each
    /// draw reduced into [0, i] by repeated subtraction — the same reduction <see cref="PrngModule.EmitNextInRange"/>
    /// uses, with a runtime bound). Same seed → same deal, on the machine and in <see cref="ShuffleOracle"/>.
    /// Clobbers everything.</summary>
    /// <param name="e">The routine emitter.</param>
    /// <param name="prng">The framework PRNG module.</param>
    /// <param name="label">The subroutine's label (the game calls it).</param>
    /// <param name="deckBase">The deck buffer's work-RAM base.</param>
    /// <param name="indexScratch">A scratch byte for the loop index i.</param>
    /// <param name="drawScratch">A scratch byte for the drawn index j.</param>
    public static void EmitShuffleSubroutine(Sm83Emitter e, PrngModule prng, int label, ushort deckBase, ushort indexScratch, ushort drawScratch) {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(prng);

        var loop = e.NewLabel();
        var reduce = e.NewLabel();
        var reduced = e.NewLabel();

        e.MarkLabel(label: label);
        e.LoadAImmediate(value: (CardCount - 1));
        e.StoreAToAddress(address: indexScratch);

        e.MarkLabel(label: loop);

        // C = the bound (i + 1); the PRNG advance clobbers A/D/E/H/L but leaves C alone.
        e.LoadAFromAddress(address: indexScratch);
        e.Increment(register: Reg8.A);
        e.Load(destination: Reg8.C, source: Reg8.A);
        prng.EmitNext();

        // Reduce the raw byte into [0, i] by repeated subtraction (the module's own reduction, runtime bound).
        e.MarkLabel(label: reduce);
        e.Arithmetic(op: AluOp.Compare, source: Reg8.C);
        e.JumpRelative(condition: Condition.Carry, label: reduced);
        e.Arithmetic(op: AluOp.Subtract, source: Reg8.C);
        e.JumpRelative(label: reduce);
        e.MarkLabel(label: reduced);
        e.StoreAToAddress(address: drawScratch);

        // HL = &deck[i] (pushed), then HL = &deck[j], DE = &deck[i]; swap the two bytes through B.
        e.LoadAFromAddress(address: indexScratch);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: deckBase);
        e.AddToHl(pair: Reg16.De);
        e.Push(pair: StackPair.Hl);
        e.LoadAFromAddress(address: drawScratch);
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: deckBase);
        e.AddToHl(pair: Reg16.De);
        e.Pop(pair: StackPair.De);
        e.LoadAFromDe();
        e.Load(destination: Reg8.B, source: Reg8.A);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToDe();
        e.Load(destination: Reg8.Memory, source: Reg8.B);

        // i--; loop while i > 0.
        e.LoadAFromAddress(address: indexScratch);
        e.Decrement(register: Reg8.A);
        e.StoreAToAddress(address: indexScratch);
        e.JumpRelative(condition: Condition.NotZero, label: loop);
        e.Return();
    }

    /// <summary>The C# oracle of the emitted shuffle: byte-for-byte the SM83 semantics (the ×5+1 LCG, the high-byte
    /// output, the repeated-subtraction reduction, the same swap order), so a verifier can predict a machine's deal
    /// from a seed — or recover the seed from the post-deal state via <see cref="StepBack"/>.</summary>
    /// <param name="seed">The 16-bit PRNG seed (the state at the moment the shuffle starts).</param>
    /// <param name="finalState">The PRNG state after the shuffle's 51 draws.</param>
    /// <returns>The shuffled 52-card deck.</returns>
    public static byte[] ShuffleOracle(ushort seed, out ushort finalState) {
        var deck = new byte[CardCount];

        for (var index = 0; (index < CardCount); index++) {
            deck[index] = (byte)index;
        }

        var state = seed;

        for (var index = (CardCount - 1); (index > 0); index--) {
            state = NextState(state: state);

            var draw = (byte)(state >> 8);
            var bound = (byte)(index + 1);

            while (draw >= bound) {
                draw -= bound;
            }

            (deck[index], deck[draw]) = (deck[draw], deck[index]);
        }

        finalState = state;

        return deck;
    }

    /// <summary>Advances the framework LCG one step (<c>state × 5 + 1</c>).</summary>
    /// <param name="state">The current state.</param>
    /// <returns>The next state.</returns>
    public static ushort NextState(ushort state) => (ushort)(((state * 5) + 1) & 0xFFFF);

    /// <summary>Inverts the framework LCG one step (5⁻¹ mod 2¹⁶ = 52429), so a verifier can walk the observed
    /// post-deal state back 51 draws to the seed the machine sampled.</summary>
    /// <param name="state">The current state.</param>
    /// <returns>The previous state.</returns>
    public static ushort StepBack(ushort state) => (ushort)(((state - 1) * 52429) & 0xFFFF);
}
