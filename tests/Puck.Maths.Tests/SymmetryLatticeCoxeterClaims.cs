using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // E8 in Bourbaki's Plate VII: Coxeter number 30, rank 8, 240 roots, and these exponents.
    private static readonly int[] PublishedE8Exponents = [1, 7, 11, 13, 17, 19, 23, 29];

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
}
