using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // E8 in Bourbaki's Plate VII: Coxeter number 30, rank 8, 240 roots, and these exponents.
    private static readonly int[] PublishedE8Exponents = [1, 7, 11, 13, 17, 19, 23, 29];
    // Literal copies of the hand-written wheel tables, compared with the derivation.
    private static readonly byte[] LiteralWheelGaps = [6, 4, 2, 4, 2, 4, 6, 2];
    private static readonly byte[] LiteralNumericPrefixMasks = [
        0, 1, 1, 1, 1, 1, 1, 3, 3, 3, 3, 7, 7, 15, 15,
        15, 15, 31, 31, 63, 63, 63, 63, 127, 127, 127, 127, 127, 127, 255,
    ];
    // MarkPacketPrime's written forms: distance k uses gap g and multiplier m, offset k uses coefficient m - 1.
    private static readonly (int Phase, int Gap, int Multiplier)[] PacketDistanceForms = [(0, 6, 1), (1, 4, 7), (2, 2, 11), (3, 4, 13), (7, 2, 29)];
    private static readonly (int Phase, int Coefficient, int Multiplier)[] PacketOffsetForms = [(1, 6, 7), (2, 10, 11), (3, 12, 13), (4, 16, 17), (5, 18, 19), (6, 22, 23), (7, 28, 29)];
    // OEIS A048670, a(n) = Jacobsthal's function of the product of the first n primes, for n = 4..9 (7# through 23#).
    private static readonly int[] PublishedJacobsthalPrimorials = [10, 14, 22, 26, 34, 40];

    // OEIS A048670 a(38): 163 is the 38th prime, and Jacobsthal's function of 2·3·5·…·163 is 492.
    private const int PublishedJacobsthal163 = 492;
    private const int PresieveWitnessLength = 394;

    /// <summary>Pins the E8 Coxeter data SymmetryLattice declares against the lattice's own cycle and Bourbaki's plate.</summary>
    public static string? SymmetryLatticeCoxeterData() {
        var order = SymmetryLattice.CycleOrder;
        var rank = SymmetryLattice.Dimension;
        var exponents = SymmetryLattice.CycleExponents.ToArray().Select(selector: static exponent => ((int)exponent)).ToArray();
        var units = Oracles.UnitsModulo(modulus: order);

        if (!exponents.SequenceEqual(second: units)) { return $"CycleExponents [{string.Join(separator: ", ", values: exponents)}] are not the units modulo {order}"; }
        if (units.Length != rank) { return $"φ({order}) = {units.Length} differs from the rank {rank}: the exponents are not exactly the units"; }
        foreach (var unit in units) {
            var image = units.Select(selector: exponent => ((unit * exponent) % order)).Order().ToArray();

            if (!image.SequenceEqual(second: units)) { return $"multiplication by the unit {unit} does not permute the exponents"; }
        }
        if ((order != 30) || (rank != 8) || (SymmetryLattice.NodeCount != 240) || !exponents.SequenceEqual(second: PublishedE8Exponents)) {
            return "the Coxeter number, rank, root count or exponents differ from Bourbaki's Plate VII for E8";
        }
        if ((SymmetryLattice.NodeCount != (rank * order)) || (SymmetryLattice.RingCount != rank) || (SymmetryLattice.RingSize != order)
            || (CyclicRotation.Period != order) || ((2 * CyclicRotation.PlaneCount) != rank)) {
            return "NodeCount, RingCount, RingSize, CyclicRotation.Period or PlaneCount disagrees with rank × h, rank, h and rank / 2";
        }
        for (var plane = 0; (plane < CyclicRotation.PlaneCount); ++plane) {
            var speed = CyclicRotation.Step(plane: plane, tick: 1L);

            if (speed != units[plane]) { return $"plane {plane} turns at {speed}, not the exponent {units[plane]}"; }
        }

        // Φ_h(c) annihilates every node: ⟨Σ a_k c^k(v), w⟩ = Σ a_k InnerProduct(Cycle(v, k), w) vanishes for every w, and
        // the nodes span the space. Φ_h is irreducible of degree φ(h) = rank, so it is the cycle's characteristic
        // polynomial and the eigenvalues are exactly the primitive h-th roots of unity.
        var cyclotomic = Oracles.CyclotomicPolynomial(order: order);

        if ((cyclotomic.Length - 1) != rank) { return $"Φ_{order} has degree {(cyclotomic.Length - 1)}, not the rank {rank}"; }
        var images = new int[cyclotomic.Length];

        for (var node = 0; (node < SymmetryLattice.NodeCount); ++node) {
            images[0] = node;
            for (var power = 1; (power < images.Length); ++power) { images[power] = SymmetryLattice.Cycle(node: images[(power - 1)]); }
            for (var other = 0; (other < SymmetryLattice.NodeCount); ++other) {
                var pairing = BigInteger.Zero;

                for (var power = 0; (power < images.Length); ++power) { pairing += (cyclotomic[power] * SymmetryLattice.InnerProduct(first: images[power], second: other)); }
                if (!pairing.IsZero) { return $"Φ_{order}(Cycle) applied to node {node} pairs to {pairing} with node {other}"; }
            }
        }
        return null;
    }
    /// <summary>Pins every 30-wheel table and written form against a direct walk of multiples and the hand-written tables.</summary>
    public static string? PrimeWheelTablesMatchTheirDerivation() {
        var units = Oracles.UnitsModulo(modulus: 30);
        var walk = units.Append(element: 31).ToArray();

        if ((PrimeWheel30.Modulus != SymmetryLattice.CycleOrder) || (PrimeWheel30.ChannelCount != SymmetryLattice.Dimension)
            || (PrimeWheel30.ChannelCount != units.Length) || !PrimeWheel30.NumericResidues.SequenceEqual(other: SymmetryLattice.CycleExponents)) {
            return "Modulus, ChannelCount or NumericResidues is not the cycle order, the rank or the exponents";
        }
        var wordIntegers = PrimeWheel30.WordIntegers;

        if ((wordIntegers != (sizeof(ulong) * PrimeWheel30.Modulus)) || (wordIntegers != 240) || (wordIntegers != SymmetryLattice.NodeCount)) {
            return $"WordIntegers is {wordIntegers}, not eight bytes of thirty integers, 240, numerically NodeCount";
        }
        for (var phase = 0; (phase <= units.Length); ++phase) {
            if (PrimeWheel30.Multiplier(phase: phase) != walk[phase]) { return $"Multiplier({phase}) is not {walk[phase]}"; }
        }
        for (var phase = 0; (phase < units.Length); ++phase) {
            var gap = (walk[(phase + 1)] - walk[phase]);

            if ((PrimeWheel30.Gap(phase: phase) != gap) || (PrimeWheel30.Gaps[phase] != gap) || (LiteralWheelGaps[phase] != gap)) { return $"the gap at phase {phase} is not {gap}"; }
        }
        for (var remainder = 0; (remainder < 30); ++remainder) {
            var expected = 0;

            for (var bit = 0; (bit < units.Length); ++bit) {
                if (units[bit] <= remainder) { expected |= (1 << bit); }
            }
            if ((PrimeWheel30.PrefixMask(remainder: remainder) != expected) || (PrimeWheel30.PrefixMasks[remainder] != expected)
                || (LiteralNumericPrefixMasks[remainder] != expected) || (PrimeExploration.WheelUnitsThrough.Counts[remainder] != BitOperations.PopCount(value: ((uint)expected)))) {
                return $"the prefix mask or unit count through remainder {remainder} is not {expected}";
            }
        }
        var word = 0UL;

        for (var offset = 0; (offset < wordIntegers); ++offset) {
            var bit = Array.IndexOf(array: units, value: (offset % 30));

            if (bit >= 0) { word |= (1UL << (((offset / 30) * 8) + bit)); }
            if ((PrimeWheel30.WordPrefixMask(offset: offset) != word) || (PrimeExtensions.CombinatorialTables.PrefixMasks[offset] != word)) {
                return $"the word prefix mask at offset {offset} is not 0x{word:X16}";
            }
        }
        for (var channel = 0; (channel < units.Length); ++channel) {
            var residue = ((int)Oracles.PrimeExplorationResidues[channel]);

            foreach (var quotient in ((ReadOnlySpan<int>)[0, 1, 7, 1_000])) {
                var prime = ((30L * quotient) + residue);

                for (var phase = 0; (phase < units.Length); ++phase) {
                    var here = ((prime * walk[phase]) / 30);
                    var next = ((prime * walk[(phase + 1)]) / 30);
                    var gap = (walk[(phase + 1)] - walk[phase]);
                    var carry = ((int)((next - here) - (quotient * gap)));
                    var lift = ((int)(here - (quotient * walk[phase])));
                    var target = ((int)((prime * walk[phase]) % 30));
                    var targetBit = Array.IndexOf(array: units, value: target);
                    var row = ((channel * 8) + phase);
                    var step = PrimeExtensions.CombinatorialTables.Steps[row];

                    if ((PrimeWheel30.Carry(phase: phase, residue: residue) != carry) || (step.Carry != carry)) {
                        return $"the carry of residue {residue} at phase {phase} is not {carry}";
                    }
                    if (PrimeWheel30.Lift(multiplier: walk[phase], residue: residue) != lift) {
                        return $"the lift of residue {residue} at phase {phase} is not {lift}";
                    }
                    if ((PrimeWheel30.TargetBit(phase: phase, residue: residue) != targetBit) || (PrimeWheel30.NumericBit(residue: target) != targetBit) || (step.Bit != targetBit)
                        || (step.Gap != gap) || (PrimeExploration.ClearMasks[row] != ((byte)~(1 << targetBit)))) {
                        return $"residue {residue} at phase {phase} does not land on numeric bit {targetBit}";
                    }
                }
            }
        }
        foreach (var (residue, masks) in ((ReadOnlySpan<(uint, ulong)>)[
            PacketMasks<PrimeExploration.PacketResidue1>(), PacketMasks<PrimeExploration.PacketResidue7>(),
            PacketMasks<PrimeExploration.PacketResidue11>(), PacketMasks<PrimeExploration.PacketResidue13>(),
            PacketMasks<PrimeExploration.PacketResidue17>(), PacketMasks<PrimeExploration.PacketResidue19>(),
            PacketMasks<PrimeExploration.PacketResidue23>(), PacketMasks<PrimeExploration.PacketResidue29>()])) {
            for (var phase = 0; (phase < units.Length); ++phase) {
                var target = ((int)((residue * walk[phase]) % 30));

                if (((byte)(masks >> (8 * phase))) != ((byte)~(1 << Array.IndexOf(array: units, value: target)))) {
                    return $"the packet masks of residue {residue} do not clear the lane of {target} at phase {phase}";
                }
            }
        }
        foreach (var residue in units) {
            foreach (var (phase, gap, multiplier) in PacketDistanceForms) {
                if ((gap != (walk[(phase + 1)] - walk[phase])) || (multiplier != walk[phase])
                    || (((((residue * multiplier) % 30) + (residue * gap)) / 30) != PrimeWheel30.Carry(phase: phase, residue: residue))) {
                    return $"MarkPacketPrime's written distance {phase} disagrees with the derivation at residue {residue}";
                }
            }
            foreach (var (phase, coefficient, multiplier) in PacketOffsetForms) {
                if ((coefficient != (walk[phase] - walk[0])) || (multiplier != walk[phase])
                    || (((residue * multiplier) / 30) != PrimeWheel30.Lift(multiplier: walk[phase], residue: residue))) {
                    return $"MarkFullPacketRun's written offset {phase} disagrees with the derivation at residue {residue}";
                }
            }
            int Carry(int phase) => PrimeWheel30.Carry(phase: phase, residue: residue);
            var squareModFive = (((residue % 5) == 1) || ((residue % 5) == 4));

            // MarkPacketPrime reuses distances: phases 4, 5, 6 for every residue; phase 3 and phase 7 by residue class.
            if ((Carry(phase: 4) != Carry(phase: 2)) || (Carry(phase: 5) != Carry(phase: 1)) || (Carry(phase: 6) != Carry(phase: 0))
                || ((Carry(phase: 3) == Carry(phase: 1)) != squareModFive) || ((Carry(phase: 3) == Carry(phase: 1)) != (residue is 1 or 11 or 19 or 29))
                || ((Carry(phase: 7) == Carry(phase: 2)) != (residue is 7 or 13 or 17 or 23))) {
                return $"MarkPacketPrime's distance reuse does not hold at residue {residue}";
            }
        }
        return null;
    }

    private static (uint Residue, ulong Masks) PacketMasks<TResidue>() where TResidue : struct, PrimeExploration.IPacketResidue =>
        (TResidue.Residue, TResidue.Masks);

    /// <summary>Pins the presieve patterns as coprimality to the primes 7 through 163 and their runs to Jacobsthal's bound.</summary>
    public static string? PresieveJacobsthalRunBound() {
        var units = Oracles.UnitsModulo(modulus: 30);
        var presieved = Oracles.PrimesBetween(high: ((int)PrimeExploration.PreSievePrimeLimit), low: 7);
        var groups = PrimeExploration.SmallPrimeGroups;
        var patterns = PrimeExploration.SmallPrimePatterns.Periods;
        var grouped = groups.SelectMany(selector: static group => ((int[])[group.First, group.Second, group.Third])).Where(predicate: static prime => (prime != 1)).Order().ToArray();

        if (!grouped.SequenceEqual(second: presieved)) { return "the pattern groups do not hold each prime from 7 through PreSievePrimeLimit exactly once"; }
        if (Oracles.PrimesBetween(high: ((int)PrimeExploration.PreSievePrimeLimit), low: 2).Length != 38) { return "PreSievePrimeLimit is not the 38th prime, whose primorial A048670 a(38) describes"; }
        for (var group = 0; (group < groups.Length); ++group) {
            var (first, second, third) = groups[group];
            var pattern = patterns[group];

            if (pattern.Length != ((first * second) * third)) { return $"pattern {group} has length {pattern.Length}, not its period"; }
            for (var index = 0; (index < pattern.Length); ++index) {
                for (var bit = 0; (bit < units.Length); ++bit) {
                    var value = ((30L * index) + units[bit]);
                    var coprime = (((value % first) != 0) && ((value % second) != 0) && (((value % third) != 0) || (third == 1)));

                    if (((pattern[index] & (1 << bit)) != 0) != coprime) { return $"pattern {group} byte {index} bit {bit} does not record coprimality of {value} to ({first}, {second}, {third})"; }
                }
            }
        }

        var primorialPrimes = Oracles.PrimesBetween(high: 23, low: 2);

        for (var count = 4; (count <= primorialPrimes.Length); ++count) {
            var run = Oracles.LongestNonCoprimeRun(primes: primorialPrimes[..count]);

            if (run != (PublishedJacobsthalPrimorials[(count - 4)] - 1)) { return $"the longest non-coprime run of the product of the first {count} primes is {run}, not A048670 a({count}) − 1"; }
        }

        var bound = (PublishedJacobsthal163 - 1);
        var segment = new byte[(1 << 20)];

        foreach (var blockLow in ((ReadOnlySpan<ulong>)[0UL, 33_333_333_333_333UL, ((ulong.MaxValue / 30UL) - ((ulong)segment.Length))])) {
            PrimeExploration.FilterSmallPrimes(blockLow: blockLow, segment: segment);
            var previous = -1L;
            var zeroBytes = 0;
            var zeroWords = 0;

            for (var index = 0; (index < segment.Length); ++index) {
                var bits = segment[index];

                zeroBytes = ((bits == 0) ? (zeroBytes + 1) : 0);
                if (zeroBytes >= 17) { return $"seventeen consecutive wheel bytes end at block {(blockLow + ((ulong)index))} with no candidate"; }
                if ((index % 8) == 7) {
                    zeroWords = ((BitConverter.ToUInt64(startIndex: (index - 7), value: segment) == 0) ? (zeroWords + 1) : 0);
                    if (zeroWords >= 3) { return $"three consecutive 240-integer words end at block {(blockLow + ((ulong)index))} with no candidate"; }
                }
                for (var bit = 0; (bit < units.Length); ++bit) {
                    if ((bits & (1 << bit)) == 0) { continue; }
                    var value = ((30L * index) + units[bit]);

                    if ((previous >= 0) && (((value - previous) - 1) > bound)) { return $"a run of {((value - previous) - 1)} non-candidates ends at block {(blockLow + ((ulong)index))}"; }
                    previous = value;
                }
            }
        }

        // A run the greedy covering forces, placed by the Chinese remainder theorem and read from the real patterns.
        var coveringPrimes = Oracles.PrimesBetween(high: ((int)PrimeExploration.PreSievePrimeLimit), low: 2);
        var classes = Oracles.GreedyCovering(length: PresieveWitnessLength, primes: coveringPrimes);

        if (classes is null) { return $"the greedy covering no longer covers {PresieveWitnessLength} integers"; }
        var start = Oracles.ChineseRemainder(moduli: coveringPrimes, residues: coveringPrimes.Select(selector: (prime, index) => ((prime - classes[index]) % prime)).ToArray());

        bool Candidate(BigInteger value) {
            var bit = Array.IndexOf(array: units, value: ((int)(value % 30)));

            if (bit < 0) { return false; }
            var block = (value / 30);

            for (var group = 0; (group < patterns.Length); ++group) {
                if ((patterns[group][((int)(block % patterns[group].Length))] & (1 << bit)) == 0) { return false; }
            }
            return true;
        }
        for (var offset = 1; (offset <= PresieveWitnessLength); ++offset) {
            if (Candidate(value: (start + offset))) { return $"the patterns keep {(start + offset)}, which the covering strikes"; }
        }
        var before = 0;
        var after = 0;

        while (!Candidate(value: (start - before))) { if (++before > bound) { return "no candidate precedes the witness run within the bound"; } }
        while (!Candidate(value: (((start + PresieveWitnessLength) + 1) + after))) { if (++after > bound) { return "no candidate follows the witness run within the bound"; } }
        var witnessRun = ((before + PresieveWitnessLength) + after);

        if (witnessRun > bound) { return $"the witness run is {witnessRun} integers, beyond Jacobsthal's {bound}"; }
        return null;
    }
}
