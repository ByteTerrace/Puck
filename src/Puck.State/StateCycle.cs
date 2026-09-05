using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.State;

/// <summary>What a <see cref="StateCycle"/> cell reads: the rotation as a step count, a fraction of a turn or a
/// unit-rotation component, or the rotation carried along a symmetry-lattice orbit as a node index, its ring or a
/// projected coordinate. The integer outputs belong to <see cref="CellKind.Int"/> cells and the fixed outputs to
/// <see cref="CellKind.Fixed"/> cells.</summary>
[JsonConverter(typeof(StrictEnumConverter<CycleOutput>))]
public enum CycleOutput : byte {
    /// <summary>The rotation's step count in <c>[0, order)</c>, one full turn per <see cref="StateCycle.Order"/>
    /// steps — an int cell.</summary>
    Step,
    /// <summary>The rotation as a fraction of one turn, <c>⌊step · 2^16 / order⌋</c> in raw <c>FixedQ4816</c> bits, so
    /// the value wraps once per loop the way <c>render.cycle</c> keys read a row — a fixed cell.</summary>
    Turns,
    /// <summary>The unit rotation's cosine, the real part of the order's root of unity at the step — a fixed cell.</summary>
    Cos,
    /// <summary>The unit rotation's sine, the imaginary part of that root of unity — a fixed cell.</summary>
    Sin,
    /// <summary>The symmetry-lattice node the phase node has been carried to along its orbit under the generator, in
    /// <c>[0, 240)</c> — an int cell whose stored value is the node the walk starts from.</summary>
    Node,
    /// <summary>The current node's projected X coordinate on the plane of eight concentric rings — a fixed cell.</summary>
    ProjectionX,
    /// <summary>The current node's projected Y coordinate on that plane — a fixed cell.</summary>
    ProjectionY,
    /// <summary>The ring, 0..7, the current node lies on — an int cell; constant along the lattice's own cycle, and a
    /// value that moves under a word whose orbits cross rings.</summary>
    Ring,
}
/// <summary>
/// A row's or cell's tick-indexed rotation trait: the value is a pure function of the server tick through a
/// generator of the symmetry lattice's reflection group — <c>Puck.Maths.SymmetryWord</c>, the lattice's own
/// thirty-step cycle when no <see cref="Word"/> is authored — raised to <see cref="Power"/> once per step. The
/// generator's order is the loop's period, derived from the word rather than authored: a word of order twelve is a
/// twelve-position dial, one of order twenty-four a day. Nothing accumulates and nothing is rebased: the mapping is
/// tick-absolute, so a replay, a reconnect, or a fresh read at any tick lands on the same bits.
/// </summary>
/// <remarks>
/// <para>The trait advances one step every <see cref="TicksPerStep"/> ticks from <see cref="EpochTick"/>, so a loop
/// lasts <c>Order · TicksPerStep</c> ticks. The stored cell value is the phase: for the rotation outputs a whole
/// number of steps added to the rotation's own step count, for the lattice outputs the node the orbit walk starts
/// from (reduced into <c>[0, 240)</c>) — both read in the row's own displayed unit, so a <see cref="CellKind.Fixed"/>
/// row's phase is the whole part of its value. An explicit write therefore sets the phase — no epoch moves — and a
/// rule's <c>addState</c> against a cycling cell turns it by whole steps or nodes.</para>
/// <para>The rotation index is <c>(Power · steps + phase) mod Order</c>; <see cref="CycleOutput.Node"/> applies
/// the generator <c>Power · steps</c> times to the phase node. With no word, powers 1, 7, 11 and 13 are the lattice
/// cycle's four rotation planes. A declared envelope clamps the computed value on every read, exactly as it does an
/// advancing row's.</para>
/// <para><c>world.save</c> settles a cycling cell in the serialized projection only: the stored value becomes the
/// current rotation index (or node), the epoch returns to zero, and <see cref="SubstepTicks"/> carries the elapsed
/// portion of the current step, so both the first value and the next transition remain continuous after reload.</para>
/// </remarks>
/// <param name="Word">The generator as a word of reflections — mirror nodes, one to eight, applied first to last —
/// or <see langword="null"/> for the lattice's own cycle. A word that moves no node is refused: it loops nothing.</param>
/// <param name="Power">How many applications of the generator one step is; nonzero, and smaller in magnitude than
/// the generator's order, since a power reduces modulo the order and a multiple of it would be the identity.</param>
/// <param name="Output">What the cell reads; must suit the carrying row's <see cref="CellKind"/>.</param>
/// <param name="TicksPerStep">The server ticks one step lasts; refused at zero or below.</param>
/// <param name="EpochTick">The server tick the step count is measured from; a tick before it reads as step zero. A
/// negative value is refused.</param>
/// <param name="SubstepTicks">Elapsed ticks already accumulated toward the next step at <see cref="EpochTick"/>;
/// must be non-negative and less than <see cref="TicksPerStep"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateCycle(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? Word = null,
    int Power = 1,
    CycleOutput Output = CycleOutput.Step,
    long TicksPerStep = 1,
    long EpochTick = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long SubstepTicks = 0
) {
    // The baked generator and the word it was baked from, resolved on first use; a pure function of Word, so it
    // never enters equality, and a `with` copy that swaps the word re-bakes rather than answering from the copy.
    private SymmetryWord? m_generator;
    private IReadOnlyList<int>? m_generatorWord;

    /// <summary>Gets the baked generator: the authored <see cref="Word"/>, or the lattice's own cycle when none is
    /// authored.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The word is empty, longer than eight letters, or names a letter
    /// outside the node range — shapes the validator refuses first.</exception>
    [JsonIgnore]
    public SymmetryWord Generator {
        get {
            var word = Word;

            if ((m_generator is not { } baked) || !ReferenceEquals(objA: m_generatorWord, objB: word)) {
                baked = Bake(word: word);
                m_generator = baked;
                m_generatorWord = word;
            }

            return baked;
        }
    }
    /// <summary>Gets the loop's period in steps: the generator's order.</summary>
    [JsonIgnore]
    public int Order => Generator.Order;

    /// <summary>Gets a value indicating whether <paramref name="output"/> reads through the symmetry lattice rather
    /// than the bare rotation.</summary>
    public static bool IsLatticeOutput(CycleOutput output) => (output is CycleOutput.Node or CycleOutput.ProjectionX or CycleOutput.ProjectionY or CycleOutput.Ring);
    /// <summary>Gets a value indicating whether <paramref name="output"/> is read by an <see cref="CellKind.Int"/>
    /// cell; every other output is read by a <see cref="CellKind.Fixed"/> cell.</summary>
    public static bool IsIntegerOutput(CycleOutput output) => (output is CycleOutput.Step or CycleOutput.Node or CycleOutput.Ring);

    private static SymmetryWord Bake(IReadOnlyList<int>? word) =>
        ((word is null)
            ? SymmetryWord.Coxeter
            : SymmetryWord.Create(mirrors: [.. word]));

    /// <summary>Returns the stored phase of a cell in whole units — the raw value itself for an <see cref="CellKind.Int"/>
    /// row, the whole part of a <see cref="CellKind.Fixed"/> row's raw value.</summary>
    /// <param name="kind">The carrying row's kind.</param>
    /// <param name="baseValue">The stored raw cell value.</param>
    public static long Phase(CellKind kind, long baseValue) =>
        ((kind == CellKind.Fixed) ? (baseValue >> FixedQ4816.FractionBitCount) : baseValue);
    /// <summary>Resolves the generator without throwing, naming the authoring defect when the word cannot bake.</summary>
    /// <param name="generator">The baked generator, on success.</param>
    /// <param name="reason">Why the word was refused, on failure.</param>
    /// <returns><see langword="true"/> when the word bakes.</returns>
    public bool TryResolveGenerator([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SymmetryWord? generator, out string reason) {
        if (Word is { } letters) {
            if ((letters.Count < 1) || (letters.Count > SymmetryWord.MaximumLength)) {
                generator = null;
                reason = $"word holds {letters.Count} letters — a word is one to {SymmetryWord.MaximumLength} mirror nodes, or omitted for the lattice's own cycle";

                return false;
            }

            for (var index = 0; (index < letters.Count); index++) {
                if ((letters[index] < 0) || (letters[index] >= SymmetryLattice.NodeCount)) {
                    generator = null;
                    reason = $"word[{index}] {letters[index]} is not a symmetry-lattice node 0..{SymmetryLattice.NodeCount - 1}";

                    return false;
                }
            }
        }

        generator = Generator;
        reason = string.Empty;

        return true;
    }
    /// <summary>Returns the rotation index in <c>[0, <see cref="Order"/>)</c> the trait has reached at a tick from a
    /// stored phase — the step count the rotation outputs read.</summary>
    /// <param name="phase">The stored phase, in whole steps (see <see cref="Phase"/>).</param>
    /// <param name="currentTick">The tick to compute as of.</param>
    public int RotationIndex(long phase, ulong currentTick) =>
        ((int)(Rotation(currentTick: currentTick) + phase).FloorModulo(modulus: ((long)Order)));
    /// <summary>Returns the lattice node the trait has reached at a tick from a stored phase node.</summary>
    /// <param name="phaseNode">The stored phase (see <see cref="Phase"/>), a node index reduced into <c>[0, 240)</c>.</param>
    /// <param name="currentTick">The tick to compute as of.</param>
    public int CurrentNode(long phaseNode, ulong currentTick) =>
        Generator.Apply(
            node: ((int)phaseNode.FloorModulo(modulus: ((long)SymmetryLattice.NodeCount))),
            steps: Rotation(currentTick: currentTick)
        );
    /// <summary>Computes the cell's current raw value: the selected <see cref="Output"/> at <paramref name="currentTick"/>
    /// from the stored phase, clamped into the row's declared envelope.</summary>
    /// <param name="row">The carrying row (for its <see cref="CellKind"/> and envelope).</param>
    /// <param name="baseValue">The row's stored raw cell value — the phase.</param>
    /// <param name="currentTick">The tick to compute the value as of.</param>
    /// <returns>The computed, envelope-clamped raw value.</returns>
    public long ComputeCurrentValue(StateRow row, long baseValue, ulong currentTick) {
        ArgumentNullException.ThrowIfNull(argument: row);

        long value;
        var phase = Phase(baseValue: baseValue, kind: row.Kind);

        if (IsLatticeOutput(output: Output)) {
            var node = CurrentNode(currentTick: currentTick, phaseNode: phase);

            value = Output switch {
                CycleOutput.Node => node,
                CycleOutput.Ring => SymmetryLattice.Ring(node: node),
                CycleOutput.ProjectionX => SymmetryLattice.Project(node: node).X.Value,
                _ => SymmetryLattice.Project(node: node).Y.Value,
            };
        } else {
            var order = Order;
            var index = RotationIndex(currentTick: currentTick, phase: phase);

            value = Output switch {
                CycleOutput.Step => index,
                CycleOutput.Turns => ((((long)index) << FixedQ4816.FractionBitCount) / order),
                CycleOutput.Cos => CyclicRotation.Rotor(step: index, order: order).Real.Value,
                _ => CyclicRotation.Rotor(step: index, order: order).Imaginary.Value,
            };
        }

        return row.ClampToEnvelope(value: value);
    }
    /// <summary>Returns the raw cell value a settled projection stores so a boot from it reads the current value at
    /// its first tick: the current rotation index, or the current node for a lattice output, in the row's own
    /// encoding.</summary>
    /// <param name="row">The carrying row (for its <see cref="CellKind"/>).</param>
    /// <param name="baseValue">The stored raw cell value.</param>
    /// <param name="currentTick">The tick being settled at.</param>
    public long SettledPhase(StateRow row, long baseValue, ulong currentTick) {
        ArgumentNullException.ThrowIfNull(argument: row);

        var phase = Phase(baseValue: baseValue, kind: row.Kind);
        long settled = (IsLatticeOutput(output: Output)
            ? CurrentNode(currentTick: currentTick, phaseNode: phase)
            : RotationIndex(currentTick: currentTick, phase: phase));

        return ((row.Kind == CellKind.Fixed) ? (settled << FixedQ4816.FractionBitCount) : settled);
    }

    /// <summary>Returns the elapsed remainder within the current step when settling at a tick.</summary>
    public long SettledSubstep(ulong currentTick) {
        var duration = ((ulong)Math.Max(val1: TicksPerStep, val2: 1L));
        var elapsedRemainder = (Elapsed(currentTick: currentTick) % duration);
        var carried = Math.Min(val1: ((ulong)Math.Max(val1: SubstepTicks, val2: 0L)), val2: (duration - 1UL));

        return ((long)((carried + elapsedRemainder) % duration));
    }
    /// <summary>Determines whether another trait spells the same rotation: the same word letter for letter, power,
    /// output, step length, epoch and substep.</summary>
    /// <param name="other">The trait to compare with.</param>
    public bool Equals(StateCycle? other) =>
        (other is not null) &&
        (Power == other.Power) &&
        (Output == other.Output) &&
        (TicksPerStep == other.TicksPerStep) &&
        (EpochTick == other.EpochTick) &&
        (SubstepTicks == other.SubstepTicks) &&
        SameWord(left: Word, right: other.Word);
    /// <inheritdoc/>
    public override int GetHashCode() {
        var hash = new HashCode();

        hash.Add(value: Power);
        hash.Add(value: Output);
        hash.Add(value: TicksPerStep);
        hash.Add(value: EpochTick);
        hash.Add(value: SubstepTicks);

        if (Word is { } letters) {
            foreach (var letter in letters) { hash.Add(value: letter); }
        }

        return hash.ToHashCode();
    }

    private static bool SameWord(IReadOnlyList<int>? left, IReadOnlyList<int>? right) {
        if (ReferenceEquals(objA: left, objB: right)) { return true; }
        if ((left is null) || (right is null) || (left.Count != right.Count)) { return false; }

        for (var index = 0; (index < left.Count); index++) {
            if (left[index] != right[index]) { return false; }
        }

        return true;
    }
    // The generator applications reached at a tick: whole steps since the epoch (with the carried substep), reduced
    // modulo the order before the power multiplies them, so no tick count can overflow the arithmetic.
    private long Rotation(ulong currentTick) {
        var duration = ((ulong)Math.Max(val1: TicksPerStep, val2: 1L));
        var elapsed = Elapsed(currentTick: currentTick);
        var carried = Math.Min(val1: ((ulong)Math.Max(val1: SubstepTicks, val2: 0L)), val2: (duration - 1UL));
        var steps = ((elapsed / duration) + (((elapsed % duration) + carried) / duration));

        return (((long)(steps % ((ulong)Order))) * Power);
    }
    private ulong Elapsed(ulong currentTick) {
        var epoch = ((ulong)Math.Max(val1: EpochTick, val2: 0L));
        return ((currentTick <= epoch) ? 0UL : (currentTick - epoch));
    }
}
